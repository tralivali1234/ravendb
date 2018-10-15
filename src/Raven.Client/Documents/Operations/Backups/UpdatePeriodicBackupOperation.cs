﻿using System.Net.Http;
using Raven.Client.Documents.Conventions;
using Raven.Client.Documents.Session;
using Raven.Client.Http;
using Raven.Client.Json;
using Raven.Client.Json.Converters;
using Sparrow.Json;

namespace Raven.Client.Documents.Operations.Backups
{
    public class UpdatePeriodicBackupOperation : IMaintenanceOperation<UpdatePeriodicBackupOperationResult>
    {
        private readonly PeriodicBackupConfiguration _configuration;

        public UpdatePeriodicBackupOperation(PeriodicBackupConfiguration configuration)
        {
            _configuration = configuration;
        }

        public RavenCommand<UpdatePeriodicBackupOperationResult> GetCommand(DocumentConventions conventions, JsonOperationContext ctx)
        {
            return new UpdatePeriodicBackupCommand(conventions, _configuration);
        }

        private class UpdatePeriodicBackupCommand : RavenCommand<UpdatePeriodicBackupOperationResult>
        {
            private readonly DocumentConventions _conventions;
            private readonly PeriodicBackupConfiguration _configuration;

            public UpdatePeriodicBackupCommand(DocumentConventions conventions, PeriodicBackupConfiguration configuration)
            {
                _conventions = conventions;
                _configuration = configuration;
            }

            public override bool IsReadRequest => false;

            public override HttpRequestMessage CreateRequest(JsonOperationContext ctx, ServerNode node, out string url)
            {
                url = $"{node.Url}/databases/{node.Database}/admin/periodic-backup";

                var request = new HttpRequestMessage
                {
                    Method = HttpMethod.Post,
                    Content = new BlittableJsonContent(stream =>
                    {
                        var config = EntityToBlittable.ConvertCommandToBlittable(_configuration, ctx);
                        ctx.Write(stream, config);
                    })
                };

                return request;
            }

            public override void SetResponse(JsonOperationContext context, BlittableJsonReaderObject response, bool fromCache)
            {
                if (response == null)
                    ThrowInvalidResponse();

                Result = JsonDeserializationClient.ConfigurePeriodicBackupOperationResult(response);
            }
        }
    }
}
