using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Transactions;
using Hangfire.Annotations;
using Hangfire.ElasticStorage.JobQueue;
using Hangfire.Logging;
using Hangfire.MySql;
using Hangfire.MySql.JobQueue;
using Hangfire.MySql.Monitoring;
using Hangfire.Server;
using Hangfire.Storage;
using Nest;
using IsolationLevel = System.Transactions.IsolationLevel;

namespace Hangfire.ElasticStorage
{
    public class ElasticStorage : JobStorage, IDisposable
    {
        private readonly IElasticClient _client;
        private readonly ElasticStorageOptions _storageOptions;

        public virtual PersistentJobQueueProviderCollection QueueProviders { get; private set; }

        public ElasticStorage(IElasticClient client, ElasticStorageOptions storageOptions = null)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (storageOptions == null)
            {
                storageOptions = new ElasticStorageOptions
                {
                    IndexPrefix = string.Empty,
                    ServerUrl = new Uri("http://localhost:19200"),
                    FetchNextJobTimeout = TimeSpan.FromSeconds(3)
                };
            }

            _client = client;
            _storageOptions = storageOptions;
            InitializeQueueProviders();
        }

        private string ApplyAllowUserVariablesProperty(string connectionString)
        {
            if (connectionString.ToLower().Contains("allow user variables"))
            {
                return connectionString;
            }

            return connectionString + ";Allow User Variables=True;";
        }

        private void InitializeQueueProviders()
        {
            QueueProviders =
                new PersistentJobQueueProviderCollection(
                    new MySqlJobQueueProvider(this, _storageOptions));
        }

        public override IEnumerable<IServerComponent> GetComponents()
        {
            yield return new ExpirationManager(this, _storageOptions);
            yield return new CountersAggregator(this, _storageOptions);
        }

        public override void WriteOptionsToLog(ILog logger)
        {
            logger.Info("Using the following options for SQL Server job storage:");
            logger.InfoFormat("    Queue poll interval: {0}.", _storageOptions.QueuePollInterval);
        }

        public override string ToString()
        {
            const string canNotParseMessage = "<Connection string can not be parsed>";

            try
            {
                var parts = _connectionString.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Split(new[] { '=' }, StringSplitOptions.RemoveEmptyEntries))
                    .Select(x => new { Key = x[0].Trim(), Value = x[1].Trim() })
                    .ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);

                var builder = new StringBuilder();

                foreach (var alias in new[] { "Data Source", "Server", "Address", "Addr", "Network Address" })
                {
                    if (parts.ContainsKey(alias))
                    {
                        builder.Append(parts[alias]);
                        break;
                    }
                }

                if (builder.Length != 0) builder.Append("@");

                foreach (var alias in new[] { "Database", "Initial Catalog" })
                {
                    if (parts.ContainsKey(alias))
                    {
                        builder.Append(parts[alias]);
                        break;
                    }
                }

                return builder.Length != 0
                    ? String.Format("Server: {0}", builder)
                    : canNotParseMessage;
            }
            catch (Exception ex)
            {
                Logger.ErrorException(ex.Message, ex);
                return canNotParseMessage;
            }
        }

        public override IMonitoringApi GetMonitoringApi()
        {
            return new MySqlMonitoringApi(this, _storageOptions);
        }

        public override IStorageConnection GetConnection()
        {
            return new MySqlStorageConnection(this, _storageOptions);
        }

        private bool IsConnectionString(string nameOrConnectionString)
        {
            return nameOrConnectionString.Contains(";");
        }

        internal void UseTransaction([InstantHandle] Action<MySqlConnection> action)
        {
            UseTransaction(connection =>
            {
                action(connection);
                return true;
            }, null);
        }

        internal T UseTransaction<T>(
            [InstantHandle] Func<MySqlConnection, T> func, IsolationLevel? isolationLevel)
        {
            using (var tScope = new TransactionScope(TransactionScopeOption.Required,
                new TransactionOptions
                {
                    IsolationLevel = isolationLevel ?? IsolationLevel.ReadCommitted
                },
                TransactionScopeAsyncFlowOption.Enabled))
            {

                MySqlConnection connection = null;

                try
                {
                    connection = CreateAndOpenConnection();
                    T result = func(connection);
                    tScope.Complete();

                    return result;
                }
                finally
                {
                    ReleaseConnection(connection);
                }
            }
        }

        internal void UseConnection([InstantHandle] Action<MySqlConnection> action)
        {
            UseConnection(connection =>
            {
                action(connection);
                return true;
            });
        }

        internal T UseConnection<T>([InstantHandle] Func<MySqlConnection, T> func)
        {
            MySqlConnection connection = null;

            try
            {
                connection = CreateAndOpenConnection();
                return func(connection);
            }
            finally
            {
                ReleaseConnection(connection);
            }
        }

        internal MySqlConnection CreateAndOpenConnection()
        {
            if (_existingConnection != null)
            {
                return _existingConnection;
            }

            var connection = new MySqlConnection(_connectionString);
            connection.Open();

            return connection;
        }

        internal void ReleaseConnection(IDbConnection connection)
        {
            if (connection != null && !ReferenceEquals(connection, _existingConnection))
            {
                connection.Dispose();
            }
        }
        public void Dispose()
        {
        }
    }
}
