﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Raven.Client.Documents.Commands;
using Raven.Client.Documents.Conventions;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;
using Raven.Client.Documents.Session.Operations;
using Raven.Client.Documents.Session.Operations.Lazy;
using Sparrow.Json;

namespace Raven.Client.Documents.Queries.Suggestions
{
    internal class SuggestionQuery<T> : SuggestionQueryBase, ISuggestionQuery<T>
    {
        private readonly IQueryable<T> _source;

        public SuggestionQuery(IQueryable<T> source) 
            : base(((IRavenQueryInspector)source).Session)
        {
            _source = source;
        }

        protected override IndexQuery GetIndexQuery(bool isAsync)
        {
            var inspector = (IRavenQueryInspector)_source;
            return inspector.GetIndexQuery(isAsync);
        }

        protected override void InvokeAfterQueryExecuted(QueryResult result)
        {
            var provider = (RavenQueryProvider<T>)_source.Provider;
            provider.InvokeAfterQueryExecuted(result);
        }
    }

    internal abstract class SuggestionQueryBase
    {
        private readonly InMemoryDocumentSessionOperations _session;
        private IndexQuery _query;
        private Stopwatch _duration;

        protected SuggestionQueryBase(InMemoryDocumentSessionOperations session)
        {
            _session = session;
        }

        public Dictionary<string, SuggestionResult> Execute()
        {
            var command = GetCommand(isAsync: false);

            _duration = Stopwatch.StartNew();
            _session.IncrementRequestCount();
            _session.RequestExecutor.Execute(command, _session.Context, sessionInfo:_session.SessionInfo);

            return ProcessResults(command.Result, _session.Conventions);
        }

        public async Task<Dictionary<string, SuggestionResult>> ExecuteAsync(CancellationToken token = default)
        {
            var command = GetCommand(isAsync: true);

            _duration = Stopwatch.StartNew();
            _session.IncrementRequestCount();
            await _session.RequestExecutor.ExecuteAsync(command, _session.Context, _session.SessionInfo, token).ConfigureAwait(false);

            return ProcessResults(command.Result, _session.Conventions);
        }

        private Dictionary<string, SuggestionResult> ProcessResults(QueryResult queryResult, DocumentConventions conventions)
        {
            InvokeAfterQueryExecuted(queryResult);

            var results = new Dictionary<string, SuggestionResult>();
            foreach (BlittableJsonReaderObject result in queryResult.Results)
            {
                var suggestionResult = (SuggestionResult)EntityToBlittable.ConvertToEntity(typeof(SuggestionResult), "suggestion/result", result, conventions);
                results[suggestionResult.Name] = suggestionResult;
            }

            QueryOperation.EnsureIsAcceptable(queryResult, _query.WaitForNonStaleResults, _duration, _session);

            return results;
        }

        public Lazy<Dictionary<string, SuggestionResult>> ExecuteLazy(Action<Dictionary<string, SuggestionResult>> onEval = null)
        {
            _query = GetIndexQuery(isAsync: false);
            return ((DocumentSession)_session).AddLazyOperation(new LazySuggestionQueryOperation(_session.Conventions, _query, InvokeAfterQueryExecuted, ProcessResults), onEval);
        }

        public Lazy<Task<Dictionary<string, SuggestionResult>>> ExecuteLazyAsync(Action<Dictionary<string, SuggestionResult>> onEval = null, CancellationToken token = default)
        {
            _query = GetIndexQuery(isAsync: true);
            return ((AsyncDocumentSession)_session).AddLazyOperation(new LazySuggestionQueryOperation(_session.Conventions, _query, InvokeAfterQueryExecuted, ProcessResults), onEval, token);
        }

        protected abstract IndexQuery GetIndexQuery(bool isAsync);

        protected abstract void InvokeAfterQueryExecuted(QueryResult result);

        private QueryCommand GetCommand(bool isAsync)
        {
            _query = GetIndexQuery(isAsync);

            return new QueryCommand(_session, _query);
        }

        public override string ToString()
        {
            var iq = GetIndexQuery(_session is AsyncDocumentSession);
            return iq.ToString();
        }
    }
}
