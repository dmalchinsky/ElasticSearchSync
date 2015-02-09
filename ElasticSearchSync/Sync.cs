﻿using Elasticsearch.Net;
using ElasticSearchSync.Helpers;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;

namespace ElasticSearchSync
{
    public class Sync
    {
        private SyncConfiguration Config;
        private const string LogIndex = "sqlserver_es_sync";
        private const string LogType = "log";
        private const string BulkLogType = "bulk_log";

        public Sync(SyncConfiguration config)
        {
            Config = config;
        }

        public SyncResponse Exec()
        {
            log4net.Config.BasicConfigurator.Configure();
            log4net.ILog log = log4net.LogManager.GetLogger("SQLSERVER-ES Sync");

            var startedOn = DateTime.UtcNow;
            var client = new ElasticsearchClient(this.Config.ElasticSearchConfiguration);

            if (this.Config.IgnoreFieldsUpToDate)
            {
                var lastSyncResponse = client.Search(LogIndex, LogType,@"{
                  ""filter"" : {
                    ""match_all"" : { }
                  },
                  ""sort"": [
                    {
                      ""startedOn"": {
                        ""order"": ""desc""
                      }
                    }
                  ],
                  ""size"": 1
                }");
                var lastSyncDate = lastSyncResponse.Response["hits"].HasValue
                    ? DateTime.Parse(lastSyncResponse.Response["hits"]["hits"]["_index"][0]["_source"]["startedOn"]).ToUniversalTime()
                    : new DateTime();
                this.Config.SqlCommand.CommandText = this.Config.SqlCommand.CommandText.Replace("{LASTSYNC}", lastSyncDate);

                throw new Exception("sarasa");
            }

            var data = GetSerializedObject();
            log.Debug(String.Format("{0} objects have been serialized.", data.Count()));

            var syncResponse = new SyncResponse();
            string partialbulk = string.Empty;
            var c = 0;
            while (c < data.Count())
            {
                var bulkStartedOn = DateTime.UtcNow;
                var partialData = data.Skip(c).Take(this.Config.BulkSize).ToList();
                foreach (var bulkData in partialData)
                    partialbulk = partialbulk + GetPartialIndexBulk(bulkData.Key, bulkData.Value);

                //bulk request
                var response = client.Bulk(partialbulk);

                //log
                var indexedDocuments = response.Response["items"].HasValue ? ((object[])response.Response["items"].Value).Length : 0;
                var bulkResponse = new BulkResponse
                {
                    Success = response.Success,
                    HttpStatusCode = response.HttpStatusCode,
                    DocumentsIndexed = indexedDocuments,
                    ESexception = response.OriginalException,
                    StartedOn = bulkStartedOn,
                    Duration = Math.Truncate((DateTime.UtcNow - bulkStartedOn).TotalMilliseconds)
                };
                syncResponse.BulkResponses.Add(bulkResponse);
                syncResponse.DocumentsIndexed += indexedDocuments;
                syncResponse.Success = syncResponse.Success && response.Success;

                client.IndexAsync(LogIndex, BulkLogType, new
                {
                    success = bulkResponse.Success,
                    httpStatusCode = bulkResponse.HttpStatusCode,
                    documentsIndexed = bulkResponse.DocumentsIndexed,
                    startedOn = bulkResponse.StartedOn,
                    duration = bulkResponse.Duration + "ms",
                    exception = bulkResponse.ESexception != null ? ((Exception)bulkResponse.ESexception).Message : null
                });
                log.Debug(String.Format("bulk duration: {0}ms. so far {1} documents have been indexed successfully.", bulkResponse.Duration, syncResponse.DocumentsIndexed));

                partialbulk = string.Empty;
                c += this.Config.BulkSize;
            }

            //EXTRACT METHOD
            if (this.Config.DeleteSqlCommand != null)
            {
                this.Config.SqlConnection.Open();
                Dictionary<object, Dictionary<string, object>> deleteData = null;
                using (SqlDataReader rdr = this.Config.DeleteSqlCommand.ExecuteReader())
                {
                    deleteData = rdr.Serialize();
                }
                this.Config.SqlConnection.Close();

                var d = 0;
                while (d < deleteData.Count())
                {
                    var bulkStartedOn = DateTime.UtcNow;
                    var partialData = deleteData.Skip(d).Take(this.Config.BulkSize).ToList();
                    foreach (var bulkData in partialData)
                        partialbulk = partialbulk + GetPartialDeleteBulk(bulkData.Key);

                    //bulk request
                    var response = client.Bulk(partialbulk);

                    //log
                    var deletedDocuments = response.Response["items"].HasValue ? ((object[])response.Response["items"].Value).Length : 0;
                    var bulkResponse = new BulkResponse
                    {
                        Success = response.Success,
                        HttpStatusCode = response.HttpStatusCode,
                        DocumentsDeleted = deletedDocuments,
                        ESexception = response.OriginalException,
                        StartedOn = bulkStartedOn,
                        Duration = Math.Truncate((DateTime.UtcNow - bulkStartedOn).TotalMilliseconds)
                    };
                    syncResponse.BulkResponses.Add(bulkResponse);
                    client.IndexAsync(LogIndex, BulkLogType, new
                    {
                        success = bulkResponse.Success,
                        httpStatusCode = bulkResponse.HttpStatusCode,
                        documentsDeleted = bulkResponse.DocumentsDeleted,
                        startedOn = bulkResponse.StartedOn,
                        duration = bulkResponse.Duration + "ms",
                        exception = bulkResponse.ESexception != null ? ((Exception)bulkResponse.ESexception).Message : null
                    });
                    log.Debug(String.Format("bulk duration: {0}ms. so far {1} documents have been deleted successfully.", bulkResponse.Duration, syncResponse.DocumentsDeleted));

                    syncResponse.DocumentsDeleted += deletedDocuments;
                    syncResponse.Success = syncResponse.Success && response.Success;

                    partialbulk = string.Empty;
                    d += this.Config.BulkSize;
                }        
            }

            client.Bulk(LogIndex, new object[]
            { 
                new { create = new { _type = LogType  } },
                new
                { 
                    startedOn = startedOn,
                    endedOn = DateTime.UtcNow,
                    success = syncResponse.Success,
                    indexedDocuments = syncResponse.DocumentsIndexed,
                    deletedDocuments = syncResponse.DocumentsDeleted,
                    bulks = syncResponse.BulkResponses.Select(x => new {
                        success = x.Success,
                        httpStatusCode = x.HttpStatusCode,
                        indexedDocuments = x.DocumentsIndexed,
                        deletedDocuments = x.DocumentsDeleted,
                        duration = x.Duration + "ms",
                        exception = x.ESexception != null ? ((Exception)x.ESexception).Message : null
                    })
                }
            });

            return syncResponse;
        }

        private Dictionary<object, Dictionary<string, object>> GetSerializedObject()
        {
            try
            {
                this.Config.SqlConnection.Open();
                Dictionary<object, Dictionary<string, object>> data = null;
                this.Config.SqlCommand.CommandTimeout = 0;
                using (SqlDataReader rdr = this.Config.SqlCommand.ExecuteReader())
                {
                    data = rdr.Serialize();
                }

                string[] dataIds = null;
                if (this.Config.FilterArrayByObjectsIds)
                    dataIds = data.Select(x => "'" + x.Key + "'").ToArray();
                foreach (var cmd in this.Config.ArraySqlCommands)
                {
                    cmd.CommandTimeout = 0;
                    if (this.Config.FilterArrayByObjectsIds)
                        cmd.CommandText = cmd.CommandText.Replace("{OBJECTS_IDS}", String.Join(",", dataIds));

                    using (SqlDataReader rdr = cmd.ExecuteReader())
                    {
                        data = rdr.SerializeArray(data);
                    }
                }

                return data;
            }
            finally
            {
                this.Config.SqlConnection.Close();
            }
        }

        private string GetPartialDeleteBulk(object key)
        {
            return String.Format("{0}\n",
                JsonConvert.SerializeObject(new { delete = new { _index = this.Config._Index, _type = this.Config._Type, _id = key } }, Formatting.None));
        }

        private string GetPartialIndexBulk(object key, Dictionary<string, object> value)
        {
            return String.Format("{0}\n{1}\n",
                JsonConvert.SerializeObject(new { index = new { _index = this.Config._Index, _type = this.Config._Type, _id = key } }, Formatting.None),
                JsonConvert.SerializeObject(value, Formatting.None));
        }
    }
}