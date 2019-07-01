using System;

namespace Hangfire.ElasticStorage
{
    public class ElasticStorageOptions
    {
        public ElasticStorageOptions()
        {
            FetchNextJobTimeout = TimeSpan.FromSeconds(30);
        }

        public TimeSpan FetchNextJobTimeout { get; set; }

        public string IndexPrefix { get; set; }

        public Uri ServerUrl { get; set; }
    }
}