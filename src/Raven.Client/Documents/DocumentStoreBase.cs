using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Raven.Client.Documents.BulkInsert;
using Raven.Client.Documents.Changes;
using Raven.Client.Documents.Conventions;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.Documents.Session;
using Raven.Client.Documents.Smuggler;
using Raven.Client.Documents.Subscriptions;
using Raven.Client.Http;
using Raven.Client.Util;

namespace Raven.Client.Documents
{
    /// <summary>
    /// Contains implementation of some IDocumentStore operations shared by DocumentStore implementations
    /// </summary>
    public abstract class DocumentStoreBase : IDocumentStore
    {
        protected DocumentStoreBase()
        {

            Subscriptions = new DocumentSubscriptions(this);
        }

        public abstract void Dispose();

        public abstract event EventHandler AfterDispose;
        public abstract event EventHandler BeforeDispose;

        /// <summary>
        /// Whether the instance has been disposed
        /// </summary>
        public bool WasDisposed { get; protected set; }

        /// <summary>
        /// Subscribe to change notifications from the server
        /// </summary>

        public abstract IDisposable AggressivelyCacheFor(TimeSpan cacheDuration, string database = null);

        public abstract IDatabaseChanges Changes(string database = null);

        public abstract IDisposable DisableAggressiveCaching(string database = null);

        public abstract string Identifier { get; set; }
        public abstract IDocumentStore Initialize();
        public abstract IAsyncDocumentSession OpenAsyncSession();
        public abstract IAsyncDocumentSession OpenAsyncSession(string database);
        public abstract IAsyncDocumentSession OpenAsyncSession(SessionOptions sessionOptions);

        public abstract IDocumentSession OpenSession();
        public abstract IDocumentSession OpenSession(string database);
        public abstract IDocumentSession OpenSession(SessionOptions sessionOptions);

        /// <inheritdoc />
        public virtual void ExecuteIndex(AbstractIndexCreationTask task, string database = null)
        {
            AsyncHelpers.RunSync(() => ExecuteIndexAsync(task, database));
        }

        /// <inheritdoc />
        public virtual Task ExecuteIndexAsync(AbstractIndexCreationTask task, string database = null, CancellationToken token = default)
        {
            AssertInitialized();
            return task.ExecuteAsync(this, Conventions, database, token);
        }

        /// <inheritdoc />
        public virtual void ExecuteIndexes(IEnumerable<AbstractIndexCreationTask> tasks, string database = null)
        {
            AsyncHelpers.RunSync(() => ExecuteIndexesAsync(tasks, database));
        }

        /// <inheritdoc />
        public virtual Task ExecuteIndexesAsync(IEnumerable<AbstractIndexCreationTask> tasks, string database = null, CancellationToken token = default)
        {
            AssertInitialized();
            var indexesToAdd = IndexCreation.CreateIndexesToAdd(tasks, Conventions);

            return Maintenance.ForDatabase(database ?? Database).SendAsync(new PutIndexesOperation(indexesToAdd), token);
        }

        private DocumentConventions _conventions;

        /// <summary>
        /// Gets the conventions.
        /// </summary>
        /// <value>The conventions.</value>
        public virtual DocumentConventions Conventions
        {
            get => _conventions ?? (_conventions = new DocumentConventions());
            set
            {
                AssertNotInitialized(nameof(Conventions));

                _conventions = value;
            }
        }

        /// <summary>
        /// Gets or sets the URLs.
        /// </summary>
        private string[] _urls = Array.Empty<string>();

        /// <summary>
        /// Gets or sets the Urls
        /// </summary>
        public string[] Urls
        {
            get => _urls;
            set
            {
                AssertNotInitialized(nameof(Urls));

                if (value == null)
                    throw new ArgumentNullException(nameof(value));

                for (var i = 0; i < value.Length; i++)
                {
                    if (value[i] == null)
                        throw new ArgumentNullException(nameof(value), "Urls cannot contain null");

                    if (Uri.TryCreate(value[i], UriKind.Absolute, out _) == false)
                        throw new ArgumentException(value[i] + " is no a valid url");
                    value[i] = value[i].TrimEnd('/');
                }
                _urls = value;
            }
        }

        protected bool Initialized;
        private X509Certificate2 _certificate;

        private string _database;


        public abstract BulkInsertOperation BulkInsert(string database = null);

        public DocumentSubscriptions Subscriptions { get; }

        protected void EnsureNotClosed()
        {
            if (WasDisposed)
                throw new ObjectDisposedException(GetType().Name, "The document store has already been disposed and cannot be used");
        }

        protected internal void AssertInitialized()
        {
            if (Initialized == false)
                throw new InvalidOperationException("You cannot open a session or access the database commands before initializing the document store. Did you forget calling Initialize()?");
        }

        private void AssertNotInitialized(string property)
        {
            if (Initialized)
                throw new InvalidOperationException($"You cannot set '{property}' after the document store has been initialized.");
        }

        public event EventHandler<BeforeStoreEventArgs> OnBeforeStore;
        public event EventHandler<AfterSaveChangesEventArgs> OnAfterSaveChanges;
        public event EventHandler<BeforeDeleteEventArgs> OnBeforeDelete;
        public event EventHandler<BeforeQueryEventArgs> OnBeforeQuery;
        public event EventHandler<SessionCreatedEventArgs> OnSessionCreated;

        /// <summary>
        /// The default database name
        /// </summary>
        public string Database
        {
            get => _database;
            set
            {
                AssertNotInitialized(nameof(Database));

                _database = value;
            }
        }

        /// <summary>
        /// The client certificate to use for authentication
        /// </summary>
        public X509Certificate2 Certificate
        {
            get => _certificate;
            set
            {
                AssertNotInitialized(nameof(Certificate));

                _certificate = value;
            }
        }

        public abstract RequestExecutor GetRequestExecutor(string databaseName = null);

        public abstract DatabaseSmuggler Smuggler { get; }

        public abstract IDisposable SetRequestTimeout(TimeSpan timeout, string database = null);

        /// <summary>
        /// Setup the context for aggressive caching.
        /// </summary>
        public IDisposable AggressivelyCache(string database = null)
        {
            return AggressivelyCacheFor(TimeSpan.FromDays(1), database);
        }

        protected void RegisterEvents(InMemoryDocumentSessionOperations session)
        {
            session.OnBeforeStore += OnBeforeStore;
            session.OnAfterSaveChanges += OnAfterSaveChanges;
            session.OnBeforeDelete += OnBeforeDelete;
            session.OnBeforeQuery += OnBeforeQuery;
        }

        protected void AfterSessionCreated(InMemoryDocumentSessionOperations session)
        {
            OnSessionCreated?.Invoke(this, new SessionCreatedEventArgs(session));
        }

        public abstract MaintenanceOperationExecutor Maintenance { get; }
        public abstract OperationExecutor Operations { get; }
    }
}
