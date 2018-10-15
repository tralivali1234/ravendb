﻿using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Raven.Client.Documents.Changes;
using Raven.Server.Documents;
using Raven.Server.Routing;
using Raven.Server.ServerWide.Context;
using Raven.Server.TrafficWatch;
using Sparrow.Json;

namespace Raven.Server.Web.Operations
{
    public class OperationsHandler : DatabaseRequestHandler
    {
        [RavenAction("/databases/*/operations/next-operation-id", "GET", AuthorizationStatus.ValidUser)]
        public Task GetNextOperationId()
        {
            var nextId = Database.Operations.GetNextOperationId();

            using (ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
            {
                using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("Id");
                    writer.WriteInteger(nextId);
                    writer.WriteEndObject();
                }
            }

            return Task.CompletedTask;
        }

        [RavenAction("/databases/*/operations/kill", "POST", AuthorizationStatus.ValidUser)]
        public Task Kill()
        {
            var id = GetLongQueryString("id");
            // ReSharper disable once PossibleInvalidOperationException
            Database.Operations.KillOperation(id);

            return NoContent();
        }

        [RavenAction("/databases/*/operations", "GET", AuthorizationStatus.ValidUser)]
        public Task GetAll()
        {
            var id = GetLongQueryString("id", required: false);

            using (ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
            {
                IEnumerable<Documents.Operations.Operations.Operation> operations;
                if (id.HasValue == false)
                    operations = Database.Operations.GetAll();
                else
                {
                    var operation = Database.Operations.GetOperation(id.Value);
                    if (operation == null)
                    {
                        HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                        return Task.CompletedTask;
                    }

                    operations = new List<Documents.Operations.Operations.Operation> { operation };
                }

                using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
                {
                    writer.WriteStartObject();
                    writer.WriteArray(context, "Results", operations, (w, c, operation) =>
                    {
                        c.Write(w, operation.ToJson());
                    });
                    writer.WriteEndObject();
                }
            }

            return Task.CompletedTask;
        }

        [RavenAction("/databases/*/operations/state", "GET", AuthorizationStatus.ValidUser)]
        public Task State()
        {
            var id = GetLongQueryString("id");
            // ReSharper disable once PossibleInvalidOperationException
            var state = Database.Operations.GetOperation(id)?.State;

            if (state == null)
            {
                HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                return Task.CompletedTask;
            }

            using (ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
            {
                using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
                {
                    context.Write(writer, state.ToJson());
                    // writes Patch response
                    if (TrafficWatchManager.HasRegisteredClients)
                        AddStringToHttpContext(writer.ToString(), TrafficWatchChangeType.Operations);
                }
            }

            return Task.CompletedTask;
        }
    }
}
