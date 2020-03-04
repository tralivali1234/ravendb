//-----------------------------------------------------------------------
// <copyright file="DocumentSession.cs" company="Hibernating Rhinos LTD">
//     Copyright (c) Hibernating Rhinos LTD. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Raven.Client.Documents.Commands;
using Raven.Client.Documents.Commands.MultiGet;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session.Operations;
using Raven.Client.Documents.Session.Operations.Lazy;
using Raven.Client.Http;

namespace Raven.Client.Documents.Session
{
    /// <summary>
    /// Implements Unit of Work for accessing the RavenDB server
    /// </summary>
    public partial class DocumentSession : InMemoryDocumentSessionOperations, IDocumentQueryGenerator, IAdvancedSessionOperations, IDocumentSessionImpl
    {
        /// <summary>
        /// Get the accessor for advanced operations
        /// </summary>
        /// <remarks>
        /// Those operations are rarely needed, and have been moved to a separate 
        /// property to avoid cluttering the API
        /// </remarks>
        public IAdvancedSessionOperations Advanced => this;

        /// <summary>
        /// Access the eager operations
        /// </summary>
        public IEagerSessionOperations Eagerly => this;

        /// <summary>
        /// Access the lazy operations
        /// </summary>
        public ILazySessionOperations Lazily => this;

        /// <summary>
        /// Access the attachments operations
        /// </summary>
        public IAttachmentsSessionOperations Attachments { get; }

        /// <summary>
        /// Access the revisions operations
        /// </summary>
        public IRevisionsSessionOperations Revisions { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="DocumentSession"/> class.
        /// </summary>
        public DocumentSession(string dbName, DocumentStore documentStore, Guid id, RequestExecutor requestExecutor)
            : base(dbName, documentStore, requestExecutor, id)
        {
            Attachments = new DocumentSessionAttachments(this);
            Revisions = new DocumentSessionRevisions(this);
        }

        /// <summary>
        /// Saves all the changes to the Raven server.
        /// </summary>
        public void SaveChanges()
        {
            var saveChangesOperation = new BatchOperation(this);

            using (var command = saveChangesOperation.CreateRequest())
            {
                if (command == null)
                    return;

                RequestExecutor.Execute(command, Context, sessionInfo: SessionInfo);
                saveChangesOperation.SetResult(command.Result);
            }
        }

        /// <summary>
        /// Check if document exists without loading it
        /// </summary>
        /// <param name="id">Document id.</param>
        /// <returns></returns>
        public bool Exists(string id)
        {
            if (id == null)
                throw new ArgumentNullException(nameof(id));

            if (_knownMissingIds.Contains(id))
                return false;

            if (DocumentsById.TryGetValue(id, out _))
                return true;

            var command = new HeadDocumentCommand(id, null);
            RequestExecutor.Execute(command, Context, sessionInfo: SessionInfo);

            return command.Result != null;
        }

        /// <summary>
        /// Refreshes the specified entity from Raven server.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="entity">The entity.</param>
        public void Refresh<T>(T entity)
        {
            DocumentInfo documentInfo;
            if (DocumentsByEntity.TryGetValue(entity, out documentInfo) == false)
                throw new InvalidOperationException("Cannot refresh a transient instance");
            IncrementRequestCount();

            var command = new GetDocumentsCommand(new[] { documentInfo.Id }, includes: null, metadataOnly: false);
            RequestExecutor.Execute(command, Context, sessionInfo: SessionInfo);

            RefreshInternal(entity, command, documentInfo);
        }

        /// <summary>
        /// Generates the document ID.
        /// </summary>
        /// <param name="entity">The entity.</param>
        /// <returns></returns>
        protected override string GenerateId(object entity)
        {
            return Conventions.GenerateDocumentId(DatabaseName, entity);
        }

        /// <summary>
        /// Not supported on sync session.
        /// </summary>
        /// <param name="entity">The entity.</param>
        /// <returns></returns>
        protected override Task<string> GenerateIdAsync(object entity)
        {
            throw new NotSupportedException("Cannot use async operation in sync session");
        }

        public ResponseTimeInformation ExecuteAllPendingLazyOperations()
        {
            var requests = new List<GetRequest>();
            for (int i = 0; i < PendingLazyOperations.Count; i++)
            {
                var req = PendingLazyOperations[i].CreateRequest(Context);
                if (req == null)
                {
                    PendingLazyOperations.RemoveAt(i);
                    i--; // so we'll recheck this index
                    continue;
                }
                requests.Add(req);
            }

            if (requests.Count == 0)
                return new ResponseTimeInformation();

            try
            {
                var sw = Stopwatch.StartNew();

                IncrementRequestCount();

                var responseTimeDuration = new ResponseTimeInformation();

                while (ExecuteLazyOperationsSingleStep(responseTimeDuration, requests))
                {
                    Thread.Sleep(100);
                }

                responseTimeDuration.ComputeServerTotal();


                foreach (var pendingLazyOperation in PendingLazyOperations)
                {
                    Action<object> value;
                    if (OnEvaluateLazy.TryGetValue(pendingLazyOperation, out value))
                        value(pendingLazyOperation.Result);
                }
                responseTimeDuration.TotalClientDuration = sw.Elapsed;
                return responseTimeDuration;
            }
            finally
            {
                PendingLazyOperations.Clear();
            }
        }

        private bool ExecuteLazyOperationsSingleStep(ResponseTimeInformation responseTimeInformation,
            List<GetRequest> requests)
        {
            var multiGetOperation = new MultiGetOperation(this);
            var multiGetCommand = multiGetOperation.CreateRequest(requests);
            RequestExecutor.Execute(multiGetCommand, Context, sessionInfo: SessionInfo);
            var responses = multiGetCommand.Result;

            for (var i = 0; i < PendingLazyOperations.Count; i++)
            {
                long totalTime;
                string tempReqTime;
                var response = responses[i];

                response.Headers.TryGetValue(Constants.Headers.RequestTime, out tempReqTime);

                long.TryParse(tempReqTime, out totalTime);

                responseTimeInformation.DurationBreakdown.Add(new ResponseTimeItem
                {
                    Url = requests[i].UrlAndQuery,
                    Duration = TimeSpan.FromMilliseconds(totalTime)
                });

                if (response.RequestHasErrors())
                    throw new InvalidOperationException("Got an error from server, status code: " + (int)response.StatusCode + Environment.NewLine + response.Result);

                PendingLazyOperations[i].HandleResponse(response);
                if (PendingLazyOperations[i].RequiresRetry)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
