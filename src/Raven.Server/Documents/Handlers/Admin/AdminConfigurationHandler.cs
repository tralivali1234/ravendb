﻿using System;
using System.Net;
using System.Threading.Tasks;
using Raven.Client;
using Raven.Client.ServerWide;
using Raven.Server.Json;
using Raven.Server.Routing;
using Raven.Server.ServerWide.Context;

namespace Raven.Server.Documents.Handlers.Admin
{
    public class AdminConfigurationHandler : DatabaseRequestHandler
    {
        [RavenAction("/databases/*/admin/configuration/studio", "PUT", AuthorizationStatus.DatabaseAdmin)]
        public async Task PutStudioConfiguration()
        {
            ServerStore.EnsureNotPassive();

            using (ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            {
                var studioConfigurationJson = context.ReadForDisk(RequestBodyStream(), Constants.Configuration.StudioId);
                var studioConfiguration = JsonDeserializationServer.StudioConfiguration(studioConfigurationJson);

                await UpdateDatabaseRecord(context, record =>
                {
                    record.Studio = studioConfiguration;
                });
            }

            NoContentStatus();
            HttpContext.Response.StatusCode = (int)HttpStatusCode.Created;
        }

        [RavenAction("/databases/*/admin/configuration/client", "PUT", AuthorizationStatus.DatabaseAdmin)]
        public async Task PutClientConfiguration()
        {
            ServerStore.EnsureNotPassive();

            using (ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            {
                var clientConfigurationJson = context.ReadForDisk(RequestBodyStream(), Constants.Configuration.ClientId);
                var clientConfiguration = JsonDeserializationServer.ClientConfiguration(clientConfigurationJson);

                await UpdateDatabaseRecord(context, record =>
                {
                    record.Client = clientConfiguration;
                    record.Client.Etag++; // we don't care _what_ the value is, just that it is changing
                });
            }

            NoContentStatus();
            HttpContext.Response.StatusCode = (int)HttpStatusCode.Created;
        }

        private async Task UpdateDatabaseRecord(TransactionOperationContext context, Action<DatabaseRecord> action)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));
            if (action == null)
                throw new ArgumentNullException(nameof(action));

            using (context.OpenReadTransaction())
            {
                var record = ServerStore.Cluster.ReadDatabase(context, Database.Name, out long index);

                action(record);

                var result = await ServerStore.WriteDatabaseRecordAsync(Database.Name, record, index);
                await Database.RachisLogIndexNotifications.WaitForIndexNotification(result.Index, ServerStore.Engine.OperationTimeout);
            }
        }
    }
}
