﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Raven.Client;
using Raven.Client.Documents.Conventions;
using Raven.Client.Documents.Operations.Configuration;
using Raven.Client.Documents.Session;
using Raven.Client.Documents.Smuggler;
using Raven.Client.Exceptions.Database;
using Raven.Client.Extensions;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;
using Raven.Client.Util;
using Raven.Server.Config;
using Raven.Server.Documents.ETL;
using Raven.Server.Documents.Expiration;
using Raven.Server.Documents.Indexes;
using Raven.Server.Documents.Patch;
using Raven.Server.Documents.PeriodicBackup;
using Raven.Server.Documents.Queries;
using Raven.Server.Documents.Replication;
using Raven.Server.Documents.Subscriptions;
using Raven.Server.Documents.TcpHandlers;
using Raven.Server.NotificationCenter.Notifications;
using Raven.Server.NotificationCenter.Notifications.Details;
using Raven.Server.Routing;
using Raven.Server.ServerWide;
using Raven.Server.ServerWide.Context;
using Raven.Server.Smuggler.Documents;
using Raven.Server.Smuggler.Documents.Data;
using Raven.Server.Utils;
using Sparrow;
using Sparrow.Collections;
using Sparrow.Json;
using Sparrow.Json.Parsing;
using Sparrow.Logging;
using Sparrow.Utils;
using Voron;
using Voron.Exceptions;
using Voron.Impl;
using Voron.Impl.Backup;
using DatabaseInfo = Raven.Client.ServerWide.Operations.DatabaseInfo;
using DatabaseSmuggler = Raven.Server.Smuggler.Documents.DatabaseSmuggler;
using DiskSpaceResult = Raven.Client.ServerWide.Operations.DiskSpaceResult;
using Size = Raven.Client.Util.Size;

namespace Raven.Server.Documents
{
    public class DocumentDatabase : IDisposable
    {
        private readonly ServerStore _serverStore;
        private readonly Action<string> _addToInitLog;
        private readonly Logger _logger;

        private readonly CancellationTokenSource _databaseShutdown = new CancellationTokenSource();

        private readonly object _idleLocker = new object();
        /// <summary>
        /// The current lock, used to make sure indexes have a unique names
        /// </summary>
        private Task _indexStoreTask;
        private long _usages;
        private readonly ManualResetEventSlim _waitForUsagesOnDisposal = new ManualResetEventSlim(false);
        private long _lastIdleTicks = DateTime.UtcNow.Ticks;
        private long _lastTopologyIndex = -1;
        private long _lastClientConfigurationIndex = -1;

        public void ResetIdleTime()
        {
            _lastIdleTicks = DateTime.MinValue.Ticks;
        }

        public DocumentDatabase(string name, RavenConfiguration configuration, ServerStore serverStore, Action<string> addToInitLog)
        {
            Name = name;
            _logger = LoggingSource.Instance.GetLogger<DocumentDatabase>(Name);
            _serverStore = serverStore;
            _addToInitLog = addToInitLog;
            StartTime = Time.GetUtcNow();
            LastAccessTime = Time.GetUtcNow();
            Configuration = configuration;
            Scripts = new ScriptRunnerCache(this, Configuration);

            try
            {
                using (_serverStore.ContextPool.AllocateOperationContext(out TransactionOperationContext ctx))
                using (ctx.OpenReadTransaction())
                {
                    MasterKey = serverStore.GetSecretKey(ctx, Name);

                    var databaseRecord = _serverStore.Cluster.ReadDatabase(ctx, Name);
                    if (databaseRecord != null)
                    {
                        // can happen when we are in the process of restoring a database
                        if (databaseRecord.Encrypted && MasterKey == null)
                            throw new InvalidOperationException($"Attempt to create encrypted db {Name} without supplying the secret key");
                        if (databaseRecord.Encrypted == false && MasterKey != null)
                            throw new InvalidOperationException($"Attempt to create a non-encrypted db {Name}, but a secret key exists for this db.");
                    }
                }

                QueryMetadataCache = new QueryMetadataCache();
                IoChanges = new IoChangesNotifications();
                Changes = new DocumentsChanges();
                DocumentTombstoneCleaner = new DocumentTombstoneCleaner(this);
                DocumentsStorage = new DocumentsStorage(this, addToInitLog);
                IndexStore = new IndexStore(this, serverStore);
                QueryRunner = new QueryRunner(this);
                EtlLoader = new EtlLoader(this, serverStore);
                ReplicationLoader = new ReplicationLoader(this, serverStore);
                SubscriptionStorage = new SubscriptionStorage(this, serverStore);
                Metrics = new MetricCounters();
                TxMerger = new TransactionOperationsMerger(this, DatabaseShutdown);
                HugeDocuments = new HugeDocuments(configuration.PerformanceHints.HugeDocumentsCollectionSize,
                    configuration.PerformanceHints.HugeDocumentSize.GetValue(SizeUnit.Bytes));
                ConfigurationStorage = new ConfigurationStorage(this);
                NotificationCenter = new NotificationCenter.NotificationCenter(ConfigurationStorage.NotificationsStorage, Name, _databaseShutdown.Token);
                Operations = new Operations.Operations(Name, ConfigurationStorage.OperationsStorage, NotificationCenter, Changes);
                DatabaseInfoCache = serverStore.DatabaseInfoCache;
                RachisLogIndexNotifications = new RachisLogIndexNotifications(DatabaseShutdown);
                CatastrophicFailureNotification = new CatastrophicFailureNotification(e =>
                {
                    serverStore.DatabasesLandlord.UnloadResourceOnCatastrophicFailure(name, e);
                });
            }
            catch (Exception)
            {
                Dispose();
                throw;
            }
        }

        public ServerStore ServerStore => _serverStore;

        public DateTime LastIdleTime => new DateTime(_lastIdleTicks);

        public DateTime LastAccessTime;

        public DatabaseInfoCache DatabaseInfoCache { get; set; }

        public readonly SystemTime Time = new SystemTime();

        public ScriptRunnerCache Scripts;

        public readonly TransactionOperationsMerger TxMerger;

        public SubscriptionStorage SubscriptionStorage { get; }

        public string Name { get; }

        public Guid DbId => DocumentsStorage.Environment?.DbId ?? Guid.Empty;

        public string DbBase64Id => DocumentsStorage.Environment?.Base64Id ?? "";

        public RavenConfiguration Configuration { get; }

        public QueryRunner QueryRunner { get; }

        public CancellationToken DatabaseShutdown => _databaseShutdown.Token;

        public AsyncManualResetEvent DatabaseShutdownCompleted { get; } = new AsyncManualResetEvent();

        public DocumentsStorage DocumentsStorage { get; private set; }

        public ExpiredDocumentsCleaner ExpiredDocumentsCleaner { get; private set; }

        public PeriodicBackupRunner PeriodicBackupRunner { get; private set; }

        public DocumentTombstoneCleaner DocumentTombstoneCleaner { get; private set; }

        public DocumentsChanges Changes { get; }

        public IoChangesNotifications IoChanges { get; }

        public CatastrophicFailureNotification CatastrophicFailureNotification { get; }

        public NotificationCenter.NotificationCenter NotificationCenter { get; private set; }

        public Operations.Operations Operations { get; private set; }

        public HugeDocuments HugeDocuments { get; }

        public MetricCounters Metrics { get; }

        public IndexStore IndexStore { get; private set; }

        public ConfigurationStorage ConfigurationStorage { get; }

        public ReplicationLoader ReplicationLoader { get; private set; }

        public EtlLoader EtlLoader { get; private set; }

        public readonly ConcurrentSet<TcpConnectionOptions> RunningTcpConnections = new ConcurrentSet<TcpConnectionOptions>();

        public readonly DateTime StartTime;

        public readonly RachisLogIndexNotifications RachisLogIndexNotifications;

        public readonly byte[] MasterKey;

        public ClientConfiguration ClientConfiguration { get; private set; }

        public long LastDatabaseRecordIndex { get; private set; }

        public readonly QueryMetadataCache QueryMetadataCache;

        public void Initialize(InitializeOptions options = InitializeOptions.None)
        {
            try
            {
                _addToInitLog("Initializing NotificationCenter");
                NotificationCenter.Initialize(this);

                _addToInitLog("Initializing DocumentStorage");
                DocumentsStorage.Initialize((options & InitializeOptions.GenerateNewDatabaseId) == InitializeOptions.GenerateNewDatabaseId);
                _addToInitLog("Starting Transaction Merger");
                TxMerger.Start();
                _addToInitLog("Initializing ConfigurationStorage");
                ConfigurationStorage.Initialize();

                if ((options & InitializeOptions.SkipLoadingDatabaseRecord) == InitializeOptions.SkipLoadingDatabaseRecord)
                    return;

                _addToInitLog("Loading Database");
                long index;
                DatabaseRecord record;
                using (_serverStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
                using (context.OpenReadTransaction())
                    record = _serverStore.Cluster.ReadDatabase(context, Name, out index);

                if (record == null)
                    DatabaseDoesNotExistException.Throw(Name);

                PeriodicBackupRunner = new PeriodicBackupRunner(this, _serverStore);

                _addToInitLog("Initializing IndexStore (async)");
                _indexStoreTask = IndexStore.InitializeAsync(record);
                _addToInitLog("Initializing Replication");
                ReplicationLoader?.Initialize(record);
                _addToInitLog("Initializing ETL");
                EtlLoader.Initialize(record);

                DocumentTombstoneCleaner.Start();

                try
                {
                    _indexStoreTask.Wait(DatabaseShutdown);
                }
                finally
                {
                    _addToInitLog("Initializing IndexStore completed");
                    _indexStoreTask = null;
                }

                SubscriptionStorage.Initialize();
                _addToInitLog("Initializing SubscriptionStorage completed");

                TaskExecutor.Execute((state) =>
                {
                    NotifyFeaturesAboutStateChange(record, index);
                }, null);
            }
            catch (Exception)
            {
                Dispose();
                throw;
            }
        }

        public DatabaseUsage DatabaseInUse(bool skipUsagesCount)
        {
            return new DatabaseUsage(this, skipUsagesCount);
        }

        internal void ThrowDatabaseShutdown(Exception e = null)
        {
            throw new DatabaseDisabledException("The database " + Name + " is shutting down", e);
        }

        public struct DatabaseUsage : IDisposable
        {
            private readonly DocumentDatabase _parent;
            private readonly bool _skipUsagesCount;

            public DatabaseUsage(DocumentDatabase parent, bool skipUsagesCount)
            {
                _parent = parent;
                _skipUsagesCount = skipUsagesCount;

                if (_skipUsagesCount == false)
                    Interlocked.Increment(ref _parent._usages);

                if (_parent.DatabaseShutdown.IsCancellationRequested)
                {
                    Dispose();
                    _parent.ThrowDatabaseShutdown();
                }
            }

            public void Dispose()
            {
                if (_skipUsagesCount)
                    return;

                var currentUsagesCount = Interlocked.Decrement(ref _parent._usages);

                if (_parent._databaseShutdown.IsCancellationRequested && currentUsagesCount == 0)
                    _parent._waitForUsagesOnDisposal.Set();
            }
        }

        public unsafe void Dispose()
        {
            if (_databaseShutdown.IsCancellationRequested)
                return; // double dispose?

            lock (this)
            {
                if (_databaseShutdown.IsCancellationRequested)
                    return; // double dispose?

                //before we dispose of the database we take its latest info to be displayed in the studio
                try
                {
                    var databaseInfo = GenerateDatabaseInfo();
                    if (databaseInfo != null)
                        DatabaseInfoCache?.InsertDatabaseInfo(databaseInfo, Name);
                }
                catch (Exception e)
                {
                    // if we encountered a catastrophic failure we might not be able to retrieve database info

                    if (_logger.IsInfoEnabled)
                        _logger.Info("Failed to generate and store database info", e);
                }

                _databaseShutdown.Cancel();

                // we'll wait for 1 minute to drain all the requests
                // from the database

                var sp = Stopwatch.StartNew();
                while (sp.ElapsedMilliseconds < 60 * 1000)
                {
                    if (Interlocked.Read(ref _usages) == 0)
                        break;

                    if (_waitForUsagesOnDisposal.Wait(1000))
                        _waitForUsagesOnDisposal.Reset();
                }

                var exceptionAggregator = new ExceptionAggregator(_logger, $"Could not dispose {nameof(DocumentDatabase)} {Name}");

                foreach (var connection in RunningTcpConnections)
                {
                    exceptionAggregator.Execute(() =>
                    {
                        connection.Dispose();
                    });
                }

                exceptionAggregator.Execute(() =>
                {
                    TxMerger?.Dispose();
                });

                if (_indexStoreTask != null)
                {
                    exceptionAggregator.Execute(() =>
                    {
                        _indexStoreTask.Wait(DatabaseShutdown);
                    });
                }

                exceptionAggregator.Execute(() =>
                {
                    IndexStore?.Dispose();
                });

                exceptionAggregator.Execute(() =>
                {
                    ExpiredDocumentsCleaner?.Dispose();
                });

                exceptionAggregator.Execute(() =>
                {
                    PeriodicBackupRunner?.Dispose();
                });

                exceptionAggregator.Execute(() =>
                {
                    DocumentTombstoneCleaner?.Dispose();
                });

                exceptionAggregator.Execute(() =>
                {
                    ReplicationLoader?.Dispose();
                });

                exceptionAggregator.Execute(() =>
                {
                    EtlLoader?.Dispose();
                });

                exceptionAggregator.Execute(() =>
                {
                    Operations?.Dispose(exceptionAggregator);
                });

                exceptionAggregator.Execute(() =>
                {
                    NotificationCenter?.Dispose();
                });

                exceptionAggregator.Execute(() =>
                {
                    SubscriptionStorage?.Dispose();
                });

                exceptionAggregator.Execute(() =>
                {
                    ConfigurationStorage?.Dispose();
                });

                exceptionAggregator.Execute(() =>
                {
                    DocumentsStorage?.Dispose();
                });

                exceptionAggregator.Execute(() =>
                {
                    _databaseShutdown.Dispose();
                });

                exceptionAggregator.Execute(() =>
                {
                    if (MasterKey == null)
                        return;
                    fixed (byte* pKey = MasterKey)
                    {
                        Sodium.sodium_memzero(pKey, (UIntPtr)MasterKey.Length);
                    }
                });

                exceptionAggregator.ThrowIfNeeded();
            }
        }

        public DynamicJsonValue GenerateDatabaseInfo()
        {
            var envs = GetAllStoragesEnvironment().ToList();
            if (envs.Any(x => x.Environment == null))
                return null;

            var size = new Size(envs.Sum(env => env.Environment.Stats().AllocatedDataFileSizeInBytes));
            var databaseInfo = new DynamicJsonValue
            {
                [nameof(DatabaseInfo.HasRevisionsConfiguration)] = DocumentsStorage.RevisionsStorage.Configuration != null,
                [nameof(DatabaseInfo.HasExpirationConfiguration)] = ExpiredDocumentsCleaner != null,
                [nameof(DatabaseInfo.IsAdmin)] = true, //TODO: implement me!
                [nameof(DatabaseInfo.IsEncrypted)] = DocumentsStorage.Environment.Options.EncryptionEnabled,
                [nameof(DatabaseInfo.Name)] = Name,
                [nameof(DatabaseInfo.Disabled)] = false, //TODO: this value should be overwritten by the studio since it is cached
                [nameof(DatabaseInfo.TotalSize)] = new DynamicJsonValue
                {
                    [nameof(Size.HumaneSize)] = size.HumaneSize,
                    [nameof(Size.SizeInBytes)] = size.SizeInBytes
                },
                [nameof(DatabaseInfo.IndexingErrors)] = IndexStore.GetIndexes().Sum(index => index.GetErrorCount()),
                [nameof(DatabaseInfo.Alerts)] = NotificationCenter.GetAlertCount(),
                [nameof(DatabaseInfo.UpTime)] = null, //it is shutting down
                [nameof(DatabaseInfo.BackupInfo)] = PeriodicBackupRunner?.GetBackupInfo(),
                [nameof(DatabaseInfo.MountPointsUsage)] = new DynamicJsonArray(GetMountPointsUsage().Select(x => x.ToJson())),
                [nameof(DatabaseInfo.DocumentsCount)] = DocumentsStorage.GetNumberOfDocuments(),
                [nameof(DatabaseInfo.IndexesCount)] = IndexStore.GetIndexes().Count(),
                [nameof(DatabaseInfo.RejectClients)] = false, //TODO: implement me!
                [nameof(DatabaseInfo.IndexingStatus)] = IndexStore.Status.ToString(),
                ["CachedDatabaseInfo"] = true
            };
            return databaseInfo;
        }

        public DatabaseSummary GetDatabaseSummary()
        {
            using (DocumentsStorage.ContextPool.AllocateOperationContext(out DocumentsOperationContext documentsContext))
            using (documentsContext.OpenReadTransaction())
            {
                return new DatabaseSummary
                {
                    DocumentsCount = DocumentsStorage.GetNumberOfDocuments(),
                    AttachmentsCount = DocumentsStorage.AttachmentsStorage.GetNumberOfAttachments(documentsContext).AttachmentCount,
                    RevisionsCount = DocumentsStorage.RevisionsStorage.GetNumberOfRevisionDocuments(documentsContext),
                    ConflictsCount = DocumentsStorage.ConflictsStorage.GetNumberOfConflicts(documentsContext)
                };
            }
        }

        public void RunIdleOperations()
        {
            if (Monitor.TryEnter(_idleLocker) == false)
                return;

            try
            {
                _lastIdleTicks = DateTime.UtcNow.Ticks;
                IndexStore?.RunIdleOperations();
                Operations?.CleanupOperations();
                PeriodicBackupRunner?.RemoveInactiveCompletedTasks();
                DocumentsStorage.Environment.ScratchBufferPool.Cleanup();
            }
            finally
            {
                Monitor.Exit(_idleLocker);
            }
        }

        public IEnumerable<StorageEnvironmentWithType> GetAllStoragesEnvironment()
        {
            // TODO :: more storage environments ?
            var documentsStorage = DocumentsStorage;
            if (documentsStorage != null)
                yield return
                    new StorageEnvironmentWithType(Name, StorageEnvironmentWithType.StorageEnvironmentType.Documents,
                        documentsStorage.Environment);
            var configurationStorage = ConfigurationStorage;
            if (configurationStorage != null)
                yield return
                    new StorageEnvironmentWithType("Configuration",
                        StorageEnvironmentWithType.StorageEnvironmentType.Configuration, configurationStorage.Environment);

            //check for null to prevent NRE when disposing the DocumentDatabase
            foreach (var index in (IndexStore?.GetIndexes()).EmptyIfNull())
            {
                var env = index?._indexStorage?.Environment();
                if (env != null)
                    yield return
                        new StorageEnvironmentWithType(index.Name,
                            StorageEnvironmentWithType.StorageEnvironmentType.Index, env);
            }
        }

        private IEnumerable<FullBackup.StorageEnvironmentInformation> GetAllStoragesForBackup()
        {
            foreach (var storageEnvironmentWithType in GetAllStoragesEnvironment())
            {
                switch (storageEnvironmentWithType.Type)
                {
                    case StorageEnvironmentWithType.StorageEnvironmentType.Documents:
                        yield return new FullBackup.StorageEnvironmentInformation
                        {
                            Name = string.Empty,
                            Folder = Constants.Documents.PeriodicBackup.Folders.Documents,
                            Env = storageEnvironmentWithType.Environment
                        };
                        break;
                    case StorageEnvironmentWithType.StorageEnvironmentType.Index:
                        yield return new FullBackup.StorageEnvironmentInformation
                        {
                            Name = IndexDefinitionBase.GetIndexNameSafeForFileSystem(storageEnvironmentWithType.Name),
                            Folder = Constants.Documents.PeriodicBackup.Folders.Indexes,
                            Env = storageEnvironmentWithType.Environment
                        };
                        break;
                    case StorageEnvironmentWithType.StorageEnvironmentType.Configuration:
                        yield return new FullBackup.StorageEnvironmentInformation
                        {
                            Name = string.Empty,
                            Folder = Constants.Documents.PeriodicBackup.Folders.Configuration,
                            Env = storageEnvironmentWithType.Environment
                        };
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
        }

        public void FullBackupTo(string backupPath)
        {
            using (var file = SafeFileStream.Create(backupPath, FileMode.Create))
            using (var package = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
            using (_serverStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            using (context.OpenReadTransaction())
            {
                var databaseRecord = _serverStore.Cluster.ReadDatabase(context, Name);
                Debug.Assert(databaseRecord != null);

                var zipArchiveEntry = package.CreateEntry(RestoreSettings.SmugglerValuesFileName, CompressionLevel.Optimal);
                using (var zipStream = zipArchiveEntry.Open())
                {
                    var smugglerSource = new DatabaseSource(this, 0);
                    using (DocumentsStorage.ContextPool.AllocateOperationContext(out DocumentsOperationContext ctx))
                    using (ctx.OpenReadTransaction())
                    {
                        var smugglerDestination = new StreamDestination(zipStream, ctx, smugglerSource);
                        var databaseSmugglerOptionsServerSide = new DatabaseSmugglerOptionsServerSide
                        {
                            AuthorizationStatus = AuthorizationStatus.DatabaseAdmin,
                            OperateOnTypes = DatabaseItemType.CompareExchange | DatabaseItemType.Identities
                        };
                        var smuggler = new DatabaseSmuggler(this,
                            smugglerSource,
                            smugglerDestination,
                            this.Time,
                            options: databaseSmugglerOptionsServerSide);

                        smuggler.Execute();
                    }
                }

                zipArchiveEntry = package.CreateEntry(RestoreSettings.SettingsFileName, CompressionLevel.Optimal);
                using (var zipStream = zipArchiveEntry.Open())
                using (var writer = new BlittableJsonTextWriter(context, zipStream))
                {
                    //TODO: encrypt this file using the MasterKey
                    //http://issues.hibernatingrhinos.com/issue/RavenDB-7546

                    writer.WriteStartObject();

                    // save the database record
                    writer.WritePropertyName(nameof(RestoreSettings.DatabaseRecord));
                    var databaseRecordBlittable = EntityToBlittable.ConvertEntityToBlittable(databaseRecord, DocumentConventions.Default, context);
                    context.Write(writer, databaseRecordBlittable);

                    // save the database values (subscriptions, periodic backups statuses, etl states...)
                    writer.WriteComma();
                    writer.WritePropertyName(nameof(RestoreSettings.DatabaseValues));
                    writer.WriteStartObject();

                    var first = true;
                    var prefix = Helpers.ClusterStateMachineValuesPrefix(Name);
                    foreach (var keyValue in ClusterStateMachine.ReadValuesStartingWith(context, prefix))
                    {
                        if (first == false)
                            writer.WriteComma();

                        first = false;

                        var key = keyValue.Key.ToString().Substring(prefix.Length);
                        writer.WritePropertyName(key);
                        context.Write(writer, keyValue.Value);
                    }

                    writer.WriteEndObject();
                    // end of values

                    writer.WriteEndObject();
                }

                BackupMethods.Full.ToFile(GetAllStoragesForBackup(), package);

                file.Flush(true); // make sure that we fully flushed to disk
            }
        }

        /// <summary>
        /// this event is intended for entities that are not singletons 
        /// per database and still need to be informed on changes to the database record.
        /// </summary>
        public event Action<DatabaseRecord> DatabaseRecordChanged;

        public void ValueChanged(long index)
        {
            try
            {
                if (_databaseShutdown.IsCancellationRequested)
                    ThrowDatabaseShutdown();

                DatabaseRecord record;
                using (_serverStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
                using (context.OpenReadTransaction())
                {
                    record = _serverStore.Cluster.ReadDatabase(context, Name);
                }

                NotifyFeaturesAboutValueChange(record);
                RachisLogIndexNotifications.NotifyListenersAbout(index, null);

            }
            catch (Exception e)
            {
                RachisLogIndexNotifications.NotifyListenersAbout(index, e);

                if (_databaseShutdown.IsCancellationRequested)
                    ThrowDatabaseShutdown();

                throw;
            }
        }

        public void StateChanged(long index)
        {
            try
            {
                if (_databaseShutdown.IsCancellationRequested)
                    ThrowDatabaseShutdown();

                DatabaseRecord record;
                using (_serverStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
                using (context.OpenReadTransaction())
                {
                    record = _serverStore.Cluster.ReadDatabase(context, Name);
                }

                if (_lastTopologyIndex < record.Topology.Stamp.Index)
                    _lastTopologyIndex = record.Topology.Stamp.Index;

                ClientConfiguration = record.Client;
                _lastClientConfigurationIndex = record.Client?.Etag ?? -1;

                NotifyFeaturesAboutStateChange(record, index);
                RachisLogIndexNotifications.NotifyListenersAbout(index, null);
            }
            catch (Exception e)
            {
                RachisLogIndexNotifications.NotifyListenersAbout(index, e);

                if (_logger.IsInfoEnabled)
                    _logger.Info($"Got exception during StateChanged({index}).", e);

                if (_databaseShutdown.IsCancellationRequested)
                    ThrowDatabaseShutdown(e);

                throw;
            }
        }

        private void NotifyFeaturesAboutStateChange(DatabaseRecord record, long index)
        {
            lock (this)
            {
                Debug.Assert(string.Equals(Name, record.DatabaseName, StringComparison.OrdinalIgnoreCase),
                    $"{Name} != {record.DatabaseName}");

                // index and LastDatabaseRecordIndex could have equal values when we transit from/to passive and want to update the tasks. 
                if (LastDatabaseRecordIndex > index)
                {
                    if (_logger.IsInfoEnabled)
                        _logger.Info($"Skipping record {index} (current {LastDatabaseRecordIndex}) for {record.DatabaseName} because it was already precessed.");
                    return;
                }

                if (_logger.IsInfoEnabled)
                    _logger.Info($"Starting to process record {index} (current {LastDatabaseRecordIndex}) for {record.DatabaseName}.");

                try
                {
                    InitializeFromDatabaseRecord(record);
                    LastDatabaseRecordIndex = index;
                    IndexStore.HandleDatabaseRecordChange(record, index);
                    ReplicationLoader?.HandleDatabaseRecordChange(record);
                    EtlLoader?.HandleDatabaseRecordChange(record);
                    OnDatabaseRecordChanged(record);
                    SubscriptionStorage?.HandleDatabaseValueChange(record);

                    if (_logger.IsInfoEnabled)
                        _logger.Info($"Finish to process record {index} for {record.DatabaseName}.");
                }
                catch (Exception e)
                {
                    if (_logger.IsInfoEnabled)
                        _logger.Info($"Encounter an error while processing record {index} for {record.DatabaseName}.", e);
                    throw;
                }
            }
        }

        private void NotifyFeaturesAboutValueChange(DatabaseRecord record)
        {
            lock (this)
            {
                SubscriptionStorage?.HandleDatabaseValueChange(record);
                EtlLoader?.HandleDatabaseValueChanged(record);
            }
        }

        public void RefreshFeatures()
        {
            if (_databaseShutdown.IsCancellationRequested)
                ThrowDatabaseShutdown();

            DatabaseRecord record;
            long index;
            using (_serverStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            using (context.OpenReadTransaction())
            {
                record = _serverStore.Cluster.ReadDatabase(context, Name, out index);
            }
            NotifyFeaturesAboutStateChange(record, index);
        }

        private void InitializeFromDatabaseRecord(DatabaseRecord record)
        {
            if (record == null || DocumentsStorage == null)
                return;

            ClientConfiguration = record.Client;
            DocumentsStorage.RevisionsStorage.InitializeFromDatabaseRecord(record);
            ExpiredDocumentsCleaner = ExpiredDocumentsCleaner.LoadConfigurations(this, record, ExpiredDocumentsCleaner);
            PeriodicBackupRunner.UpdateConfigurations(record);
        }

        public string WhoseTaskIsIt(
            DatabaseTopology databaseTopology,
            IDatabaseTask configuration,
            IDatabaseTaskStatus taskStatus,
            bool useLastResponsibleNodeIfNoAvailableNodes = false)
        {
            var whoseTaskIsIt = databaseTopology.WhoseTaskIsIt(
                ServerStore.Engine.CurrentState, configuration,
                getLastReponsibleNode:
                () => ServerStore.LicenseManager.GetLastResponsibleNodeForTask(
                    taskStatus,
                    databaseTopology,
                    configuration,
                    NotificationCenter));

            if (whoseTaskIsIt == null && useLastResponsibleNodeIfNoAvailableNodes)
                return taskStatus.NodeTag;

            return whoseTaskIsIt;
        }

        public IEnumerable<DatabasePerformanceMetrics> GetAllPerformanceMetrics()
        {
            yield return TxMerger.GeneralWaitPerformanceMetrics;
            yield return TxMerger.TransactionPerformanceMetrics;
        }

        private void OnDatabaseRecordChanged(DatabaseRecord record)
        {
            DatabaseRecordChanged?.Invoke(record);
        }

        public bool HasTopologyChanged(long index)
        {
            return _lastTopologyIndex != index;
        }

        public bool HasClientConfigurationChanged(long index)
        {
            var actual = Hashing.Combine(_lastClientConfigurationIndex, ServerStore.LastClientConfigurationIndex);

            return index != actual;
        }

        public long GetSizeOnDiskInBytes()
        {
            var storageEnvironments = GetAllStoragesEnvironment();
            if (storageEnvironments == null)
                return 0;

            long sizeOnDiskInBytes = 0;
            foreach (var environment in storageEnvironments)
            {
                if (environment == null)
                    continue;

                Transaction tx = null;
                try
                {
                    try
                    {
                        tx = environment.Environment.ReadTransaction();
                    }
                    catch (OperationCanceledException)
                    {
                        continue;
                    }

                    sizeOnDiskInBytes += GetSizeOnDisk(environment, tx);
                }
                finally
                {
                    tx?.Dispose();
                }
            }

            return sizeOnDiskInBytes;
        }

        public IEnumerable<MountPointUsage> GetMountPointsUsage()
        {
            var storageEnvironments = GetAllStoragesEnvironment();
            if (storageEnvironments == null)
                yield break;

            var drives = DriveInfo.GetDrives();
            foreach (var environment in storageEnvironments)
            {
                if (environment == null)
                    continue;

                Transaction tx = null;
                try
                {
                    try
                    {
                        tx = environment.Environment.ReadTransaction();
                    }
                    catch (OperationCanceledException)
                    {
                        continue;
                    }

                    var fullPath = environment.Environment.Options.BasePath.FullPath;
                    if (fullPath == null)
                        continue;

                    var diskSpaceResult = DiskSpaceChecker.GetFreeDiskSpace(fullPath, drives);
                    if (diskSpaceResult == null)
                        continue;

                    var sizeOnDiskInBytes = GetSizeOnDisk(environment, tx);
                    if (sizeOnDiskInBytes == 0)
                        continue;

                    yield return new MountPointUsage
                    {
                        UsedSpace = sizeOnDiskInBytes,
                        DiskSpaceResult = new DiskSpaceResult
                        {
                            DriveName = diskSpaceResult.DriveName,
                            VolumeLabel = diskSpaceResult.VolumeLabel,
                            TotalFreeSpaceInBytes = diskSpaceResult.TotalFreeSpace.GetValue(SizeUnit.Bytes),
                            TotalSizeInBytes = diskSpaceResult.TotalSize.GetValue(SizeUnit.Bytes)
                        }
                    };
                }
                finally
                {
                    tx?.Dispose();
                }
            }
        }

        private static long GetSizeOnDisk(StorageEnvironmentWithType environment, Transaction tx)
        {
            var storageReport = environment.Environment.GenerateReport(tx);
            if (storageReport == null)
                return 0;

            var journalSize = storageReport.Journals.Sum(j => j.AllocatedSpaceInBytes);
            return storageReport.DataFile.AllocatedSpaceInBytes + journalSize;
        }

        public DatabaseRecord ReadDatabaseRecord()
        {
            using (ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            using (context.OpenReadTransaction())
            {
                return ServerStore.Cluster.ReadDatabase(context, Name);
            }
        }

        internal void HandleNonDurableFileSystemError(object sender, NonDurabilitySupportEventArgs e)
        {
            _serverStore?.NotificationCenter.Add(AlertRaised.Create(
                Name,
                $"Non Durable File System - {Name ?? "Unknown Database"}",
                e.Message,
                AlertType.NonDurableFileSystem,
                NotificationSeverity.Warning,
                Name,
                details: new MessageDetails { Message = e.Details }));
        }

        internal void HandleOnDatabaseRecoveryError(object sender, RecoveryErrorEventArgs e)
        {
            HandleOnRecoveryError(StorageEnvironmentWithType.StorageEnvironmentType.Documents, Name, sender, e);
        }

        internal void HandleOnConfigurationRecoveryError(object sender, RecoveryErrorEventArgs e)
        {
            HandleOnRecoveryError(StorageEnvironmentWithType.StorageEnvironmentType.Configuration, Name, sender, e);
        }

        internal void HandleOnIndexRecoveryError(string indexName, object sender, RecoveryErrorEventArgs e)
        {
            HandleOnRecoveryError(StorageEnvironmentWithType.StorageEnvironmentType.Index, indexName, sender, e);
        }

        private void HandleOnRecoveryError(StorageEnvironmentWithType.StorageEnvironmentType type, string resourceName, object environment, RecoveryErrorEventArgs e)
        {
            NotificationCenter.NotificationCenter nc;
            string title;

            switch (type)
            {
                case StorageEnvironmentWithType.StorageEnvironmentType.Configuration:
                case StorageEnvironmentWithType.StorageEnvironmentType.Documents:
                    nc = _serverStore?.NotificationCenter;
                    title = $"Database Recovery Error - {resourceName ?? "Unknown Database"}";

                    if (type == StorageEnvironmentWithType.StorageEnvironmentType.Configuration)
                        title += " (configuration storage)";
                    break;
                case StorageEnvironmentWithType.StorageEnvironmentType.Index:
                    nc = NotificationCenter;
                    title = $"Index Recovery Error - {resourceName ?? "Unknown Index"}";
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(type), type.ToString());
            }

            nc?.Add(AlertRaised.Create(Name, 
                title, 
                $"{e.Message}{Environment.NewLine}{Environment.NewLine}Environment: {environment}", 
                AlertType.RecoveryError,
                NotificationSeverity.Error,
                key: resourceName));
        }
    }

    public class StorageEnvironmentWithType
    {
        public string Name { get; set; }
        public StorageEnvironmentType Type { get; set; }
        public StorageEnvironment Environment { get; set; }

        public StorageEnvironmentWithType(string name, StorageEnvironmentType type, StorageEnvironment environment)
        {
            Name = name;
            Type = type;
            Environment = environment;
        }

        public enum StorageEnvironmentType
        {
            Documents,
            Index,
            Configuration
        }
    }
}
