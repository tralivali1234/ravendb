﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using Raven.Client.Documents.Conventions;
using Raven.Client.Documents.Session;
using Raven.Client.Extensions;
using Raven.Client.Http;
using Raven.Client.Json;
using Raven.Client.Json.Converters;
using Sparrow.Json;

namespace Raven.Client.Documents.Operations.Counters
{
    public class GetCountersOperation : IOperation<CountersDetail>
    {
        private readonly string _docId;
        private readonly string[] _counters;
        private readonly bool _returnFullResults;

        public GetCountersOperation(string docId, string[] counters, bool returnFullResults = false)
        {
            _docId = docId;
            _counters = counters;
            _returnFullResults = returnFullResults;
        }

        public GetCountersOperation(string docId, string counter, bool returnFullResults = false)
        {
            _docId = docId;
            _counters = new[] { counter };
            _returnFullResults = returnFullResults;
        }

        public GetCountersOperation(string docId,  bool returnFullResults = false)
        {
            _docId = docId;
            _counters = Array.Empty<string>();
            _returnFullResults = returnFullResults;
        }

        public RavenCommand<CountersDetail> GetCommand(IDocumentStore store, DocumentConventions conventions, JsonOperationContext context, HttpCache cache)
        {
            return new GetCounterValuesCommand(_docId, _counters, _returnFullResults);
        }

        private class GetCounterValuesCommand : RavenCommand<CountersDetail>
        {
            private readonly string _docId;
            private readonly string[] _counters;
            private readonly bool _returnFullResults;


            public GetCounterValuesCommand(string docId, string[] counters, bool returnFullResults)
            {
                _docId = docId ?? throw new ArgumentNullException(nameof(docId));
                _counters = counters;
                _returnFullResults = returnFullResults;
            }

            public override HttpRequestMessage CreateRequest(JsonOperationContext ctx, ServerNode node, out string url)
            {
                var pathBuilder = new StringBuilder(node.Url);
                pathBuilder.Append("/databases/")
                    .Append(node.Database)
                    .Append("/counters?")
                    .Append("docId=")
                    .Append(Uri.EscapeDataString(_docId));

                if (_returnFullResults)
                    pathBuilder.Append("&full=").Append(true);

                var request = new HttpRequestMessage
                {
                    Method = HttpMethod.Get
                };

                if (_counters.Length > 0)
                {
                    if (_counters.Length > 1)
                    {
                        PrepareRequestWithMultipleCounters(pathBuilder, request, ctx);
                    }
                    else
                    {
                        pathBuilder.Append("&counter=").Append(Uri.EscapeDataString(_counters[0]));
                    }
                }

                url = pathBuilder.ToString();

                return request;
            }

            public override void SetResponse(JsonOperationContext context, BlittableJsonReaderObject response, bool fromCache)
            {
                if (response == null)
                    return;

                Result = JsonDeserializationClient.CountersDetail(response);
            }

            private void PrepareRequestWithMultipleCounters(StringBuilder pathBuilder, HttpRequestMessage request, JsonOperationContext ctx)
            {
                var uniqueNames = new HashSet<string>(_counters);
                // if it is too big, we drop to POST (note that means that we can't use the HTTP cache any longer)
                // we are fine with that, requests to load more than 1024 counters are going to be rare
                if (uniqueNames.Sum(x => x.Length) < 1024)
                {
                    uniqueNames.ApplyIfNotNull(counter => pathBuilder.Append("&counter=").Append(Uri.EscapeDataString(counter)));
                }
                else
                {
                    request.Method = HttpMethod.Post;

                    var docOps = new DocumentCountersOperation
                    {
                        DocumentId = _docId,
                        Operations = new List<CounterOperation>()
                    };

                    foreach (var counter in _counters)
                    {
                        docOps.Operations.Add(new CounterOperation
                        {
                            Type = CounterOperationType.Get,
                            CounterName = counter
                        });
                    }

                    var batch = new CounterBatch
                    {
                        Documents = new List<DocumentCountersOperation>
                        {
                            docOps
                        }
                    };

                    request.Content = new BlittableJsonContent(stream =>
                    {
                        var config = EntityToBlittable.ConvertCommandToBlittable(batch, ctx);

                        ctx.Write(stream, config);
                    });
                }
            }

            public override bool IsReadRequest => true;
        }
    }
}
