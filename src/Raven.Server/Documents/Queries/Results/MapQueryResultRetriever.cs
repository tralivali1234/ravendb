﻿using System;
using Lucene.Net.Store;
using Raven.Client;
using Raven.Server.Documents.Includes;
using Raven.Server.Documents.Queries.Timings;
using Raven.Server.ServerWide.Context;
using Sparrow.Json.Parsing;

namespace Raven.Server.Documents.Queries.Results
{
    public class MapQueryResultRetriever : QueryResultRetrieverBase
    {
        private readonly DocumentsOperationContext _context;
        private QueryTimingsScope _storageScope;

        public MapQueryResultRetriever(DocumentDatabase database, IndexQueryServerSide query, QueryTimingsScope queryTimings, DocumentsStorage documentsStorage, DocumentsOperationContext context, FieldsToFetch fieldsToFetch, IncludeDocumentsCommand includeDocumentsCommand)
            : base(database, query, queryTimings, fieldsToFetch, documentsStorage, context, false, includeDocumentsCommand)
        {
            _context = context;
        }

        public override Document Get(Lucene.Net.Documents.Document input, float score, IState state)
        {
            using (RetrieverScope?.Start())
            {
                if (TryGetKey(input, state, out string id) == false)
                    throw new InvalidOperationException($"Could not extract '{Constants.Documents.Indexing.Fields.DocumentIdFieldName}' from index.");

                if (FieldsToFetch.IsProjection)
                    return GetProjection(input, score, id, state);

                using (_storageScope = _storageScope?.Start() ?? RetrieverScope?.For(nameof(QueryTimingsScope.Names.Storage)))
                {
                    var doc = DirectGet(null, id, state);

                    if (doc != null)
                        doc.IndexScore = score;

                    return doc;
                }
            }
        }

        public override bool TryGetKey(Lucene.Net.Documents.Document input, IState state, out string key)
        {
            key = input.Get(Constants.Documents.Indexing.Fields.DocumentIdFieldName, state);
            return key != null;
        }

        protected override Document DirectGet(Lucene.Net.Documents.Document input, string id, IState state)
        {
            return DocumentsStorage.Get(_context, id);
        }

        protected override Document LoadDocument(string id)
        {
            return DocumentsStorage.Get(_context, id);
        }

        protected override long? GetCounter(string docId, string name)
        {
            return DocumentsStorage.CountersStorage.GetCounterValue(_context, docId, name);
        }

        protected override DynamicJsonValue GetCounterRaw(string docId, string name)
        {
            var djv = new DynamicJsonValue();

            foreach (var (cv, val) in DocumentsStorage.CountersStorage.GetCounterValues(_context, docId, name))
            {
                djv[cv] = val;
            }

            return djv;
        }
    }
}
