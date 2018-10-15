﻿using System;
using System.Net.Http;
using Raven.Client.Documents.Conventions;
using Raven.Client.Documents.Session;
using Raven.Client.Http;
using Raven.Client.Json;
using Raven.Client.Json.Converters;
using Sparrow.Json;

namespace Raven.Client.Documents.Operations.Counters
{
    public class CounterBatchOperation : IOperation<CountersDetail>
    {
        private readonly CounterBatch _counterBatch;

        public CounterBatchOperation(CounterBatch counterBatch)
        {
            _counterBatch = counterBatch;
        }

        public RavenCommand<CountersDetail> GetCommand(IDocumentStore store, DocumentConventions conventions, JsonOperationContext context, HttpCache cache)
        {
            return new CounterBatchCommand(_counterBatch, conventions);
        }

        public class CounterBatchCommand : RavenCommand<CountersDetail>
        {
            private readonly DocumentConventions _conventions;
            private readonly CounterBatch _counterBatch;

            public CounterBatchCommand(CounterBatch counterBatch, DocumentConventions conventions)
            {
                _counterBatch = counterBatch ?? throw new ArgumentNullException(nameof(counterBatch));
                _conventions = conventions;
            }


            public override HttpRequestMessage CreateRequest(JsonOperationContext ctx, ServerNode node, out string url)
            {
                url = $"{node.Url}/databases/{node.Database}/counters";

                return new HttpRequestMessage
                {
                    Method = HttpMethod.Post,

                    Content = new BlittableJsonContent(stream =>
                    {
                        var config = EntityToBlittable.ConvertCommandToBlittable(_counterBatch, ctx);
                        ctx.Write(stream, config);
                    })
                };
            }

            public override void SetResponse(JsonOperationContext context, BlittableJsonReaderObject response, bool fromCache)
            {
                if (response == null)
                    return;

                Result = JsonDeserializationClient.CountersDetail(response);
            }

            public override bool IsReadRequest => false;

        }


    }
}
