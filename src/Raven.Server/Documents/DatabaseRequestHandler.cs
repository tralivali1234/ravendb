﻿using System;
using System.Net;
using System.Threading.Tasks;
using Raven.Server.Documents.Indexes;
using Raven.Server.ServerWide;
using Raven.Server.ServerWide.Context;
using Raven.Server.Web;
using Raven.Client;
using Raven.Client.Documents.Changes;
using Raven.Client.Exceptions;
using Raven.Client.ServerWide;
using Raven.Server.NotificationCenter.Notifications.Details;
using Raven.Server.Web.System;
using Sparrow.Json;
using Sparrow.Json.Parsing;
using Sparrow.Logging;

namespace Raven.Server.Documents
{
    public abstract class DatabaseRequestHandler : RequestHandler
    {
        protected DocumentsContextPool ContextPool;
        protected DocumentDatabase Database;
        protected IndexStore IndexStore;
        protected Logger Logger;

        public override void Init(RequestHandlerContext context)
        {
            base.Init(context);

            Database = context.Database;
            ContextPool = Database.DocumentsStorage.ContextPool;
            IndexStore = context.Database.IndexStore;
            Logger = LoggingSource.Instance.GetLogger(Database.Name, GetType().FullName);

            var topologyEtag = GetLongFromHeaders(Constants.Headers.TopologyEtag);
            if (topologyEtag.HasValue && Database.HasTopologyChanged(topologyEtag.Value))
                context.HttpContext.Response.Headers[Constants.Headers.RefreshTopology] = "true";

            var clientConfigurationEtag = GetLongFromHeaders(Constants.Headers.ClientConfigurationEtag);
            if (clientConfigurationEtag.HasValue && Database.HasClientConfigurationChanged(clientConfigurationEtag.Value))
                context.HttpContext.Response.Headers[Constants.Headers.RefreshClientConfiguration] = "true";
        }

        protected delegate void RefAction(string databaseName, ref BlittableJsonReaderObject configuration, JsonOperationContext context);

        protected async Task DatabaseConfigurations(Func<TransactionOperationContext, string,
           BlittableJsonReaderObject, Task<(long, object)>> setupConfigurationFunc,
           string debug,
           RefAction beforeSetupConfiguration = null,
           Action<DynamicJsonValue, BlittableJsonReaderObject, long> fillJson = null,
           HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            
            if (TryGetAllowedDbs(Database.Name, out var _, requireAdmin: true) == false)
                return;

            if (ResourceNameValidator.IsValidResourceName(Database.Name, ServerStore.Configuration.Core.DataDirectory.FullPath, out string errorMessage) == false)
                throw new BadRequestException(errorMessage);

            ServerStore.EnsureNotPassive();
            using (ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            {
                var configurationJson = await context.ReadForMemoryAsync(RequestBodyStream(), debug);
                beforeSetupConfiguration?.Invoke(Database.Name, ref configurationJson, context);

                var (index, _) = await setupConfigurationFunc(context, Database.Name, configurationJson);
                DatabaseRecord dbRecord;
                using (context.OpenReadTransaction())
                {
                    dbRecord = ServerStore.Cluster.ReadDatabase(context, Database.Name);
                }
                if (dbRecord.Topology.RelevantFor(ServerStore.NodeTag))
                {
                    var db = await ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(Database.Name);
                    await db.RachisLogIndexNotifications.WaitForIndexNotification(index, ServerStore.Engine.OperationTimeout);
                }
                else
                {
                    await ServerStore.Cluster.WaitForIndexNotification(index);
                }
                HttpContext.Response.StatusCode = (int)statusCode;

                using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
                {
                    var json = new DynamicJsonValue
                    {
                        ["RaftCommandIndex"] = index
                    };
                    fillJson?.Invoke(json, configurationJson, index);
                    context.Write(writer, json);
                    writer.Flush();
                }
            }
        }
        /// <summary>
        /// puts the given string in TrafficWatch property of HttpContext.Items
        /// puts the given type in TrafficWatchChangeType property of HttpContext.Items
        /// </summary>
        /// <param name="str"></param>
        /// <param name="type"></param>
        public void AddStringToHttpContext(string str, TrafficWatchChangeType type)
        {
            HttpContext.Items["TrafficWatch"] = (str, type);
        }

        protected OperationCancelToken CreateTimeLimitedOperationToken()
        {
            return new OperationCancelToken(Database.Configuration.Databases.OperationTimeout.AsTimeSpan, Database.DatabaseShutdown);
        }

        protected OperationCancelToken CreateTimeLimitedQueryToken()
        {
            return new OperationCancelToken(Database.Configuration.Databases.QueryTimeout.AsTimeSpan, Database.DatabaseShutdown);
        }

        protected OperationCancelToken CreateTimeLimitedCollectionOperationToken()
        {
            return new OperationCancelToken(Database.Configuration.Databases.CollectionOperationTimeout.AsTimeSpan, Database.DatabaseShutdown);
        }

        protected OperationCancelToken CreateTimeLimitedQueryOperationToken()
        {
            return new OperationCancelToken(Database.Configuration.Databases.QueryOperationTimeout.AsTimeSpan, Database.DatabaseShutdown);
        }

        protected OperationCancelToken CreateOperationToken()
        {
            return new OperationCancelToken(Database.DatabaseShutdown);
        }

        protected void AddPagingPerformanceHint(PagingOperationType operation, string action, string details, int numberOfResults, int pageSize, long duration)
        {
            if (numberOfResults <= Database.Configuration.PerformanceHints.MaxNumberOfResults)
                return;

            Database.NotificationCenter.Paging.Add(operation, action, details, numberOfResults, pageSize, duration);
        }
    }
}
