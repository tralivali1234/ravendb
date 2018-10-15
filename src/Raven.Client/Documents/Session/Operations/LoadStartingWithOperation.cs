using System;
using System.Collections.Generic;
using Raven.Client.Documents.Commands;
using Sparrow.Json;
using Sparrow.Logging;

namespace Raven.Client.Documents.Session.Operations
{
    internal class LoadStartingWithOperation
    {
        private static readonly Logger Logger = LoggingSource.Instance.GetLogger<LoadStartingWithOperation>("Client");
        private readonly InMemoryDocumentSessionOperations _session;

        private string _startWith;
        private string _matches;
        private int _start;
        private int _pageSize;
        private string _exclude;
        private string _startAfter;

        private readonly List<string> _returnedIds = new List<string>();
        private GetDocumentsResult _currentLoadResults;

        public LoadStartingWithOperation(InMemoryDocumentSessionOperations session)
        {
            _session = session;
        }

        public GetDocumentsCommand CreateRequest()
        {
            _session.IncrementRequestCount();
            if (Logger.IsInfoEnabled)
                Logger.Info($"Requesting documents with ids starting with '{_startWith}' from {_session.StoreIdentifier}");

            return new GetDocumentsCommand(_startWith, _startAfter, _matches, _exclude, _start, _pageSize, metadataOnly: false);
        }

        public void WithStartWith(string idPrefix, string matches = null, int start = 0, int pageSize = 25,
            string exclude = null, string startAfter = null)
        {
            _startWith = idPrefix;
            _matches = matches;
            _start = start;
            _pageSize = pageSize;
            _exclude = exclude;
            _startAfter = startAfter;
        }

        public void SetResult(GetDocumentsResult result)
        {
            if (_session.NoTracking)
            {
                _currentLoadResults = result;
                return;
            }

            foreach (var document in GetDocumentsFromResult(result))
            {
                _session.DocumentsById.Add(document);
                _returnedIds.Add(document.Id);
            }
        }

        public T[] GetDocuments<T>()
        {
            var i = 0;
            T[] finalResults;

            if (_session.NoTracking)
            {
                if (_currentLoadResults == null)
                    throw new InvalidOperationException($"Cannot execute '{nameof(GetDocuments)}' before operation execution.");

                finalResults = new T[_currentLoadResults.Results.Length];
                foreach (var document in GetDocumentsFromResult(_currentLoadResults))
                    finalResults[i++] = _session.TrackEntity<T>(document);
            }
            else
            {
                finalResults = new T[_returnedIds.Count];
                foreach (var id in _returnedIds)
                    finalResults[i++] = GetDocument<T>(id);
            }

            return finalResults;
        }

        private T GetDocument<T>(string id)
        {
            if (id == null)
                return default;

            if (_session.IsDeleted(id))
                return default;

            if (_session.DocumentsById.TryGetValue(id, out var doc))
                return _session.TrackEntity<T>(doc);

            return default;
        }

        private static IEnumerable<DocumentInfo> GetDocumentsFromResult(GetDocumentsResult result)
        {
            foreach (BlittableJsonReaderObject document in result.Results)
            {
                if (document == null)
                    continue;

                yield return DocumentInfo.GetNewDocumentInfo(document);
            }
        }
    }
}
