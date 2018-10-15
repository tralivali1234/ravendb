﻿using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Raven.Client;
using Raven.Server.Background;
using Raven.Server.ServerWide.Context;
using Sparrow.Json;
using Sparrow.Logging;

namespace Raven.Server.Documents
{
    public class TombstoneCleaner : BackgroundWorkBase
    {
        private readonly SemaphoreSlim _subscriptionsLocker = new SemaphoreSlim(1, 1);

        private readonly DocumentDatabase _documentDatabase;

        private readonly HashSet<ITombstoneAware> _subscriptions = new HashSet<ITombstoneAware>();

        public TombstoneCleaner(DocumentDatabase documentDatabase) : base(documentDatabase.Name, documentDatabase.DatabaseShutdown)
        {
            _documentDatabase = documentDatabase;
        }

        public void Subscribe(ITombstoneAware subscription)
        {
            _subscriptionsLocker.Wait();

            try
            {
                _subscriptions.Add(subscription);
            }
            finally
            {
                _subscriptionsLocker.Release();
            }
        }

        public void Unsubscribe(ITombstoneAware subscription)
        {
            _subscriptionsLocker.Wait();

            try
            {
                _subscriptions.Remove(subscription);
            }
            finally
            {
                _subscriptionsLocker.Release();
            }
        }

        protected override async Task DoWork()
        {
            await WaitOrThrowOperationCanceled(_documentDatabase.Configuration.Tombstones.CleanupInterval.AsTimeSpan);

            await ExecuteCleanup();
        }

        internal async Task ExecuteCleanup()
        {
            try
            {
                if (CancellationToken.IsCancellationRequested)
                    return;

                var storageEnvironment = _documentDatabase?.DocumentsStorage?.Environment;
                if (storageEnvironment == null) // doc storage was disposed before us?
                    return;

                var tombstones = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

                using (var tx = storageEnvironment.ReadTransaction())
                {
                    foreach (var tombstoneCollection in _documentDatabase.DocumentsStorage.GetTombstoneCollections(tx))
                    {
                        tombstones[tombstoneCollection] = long.MaxValue;
                    }
                }

                if (tombstones.Count == 0)
                    return;

                long minAllDocsEtag = long.MaxValue;

                await _subscriptionsLocker.WaitAsync();

                try
                {
                    foreach (var subscription in _subscriptions)
                    {
                        foreach (var tombstone in subscription.GetLastProcessedTombstonesPerCollection())
                        {
                            if (tombstone.Key == Constants.Documents.Collections.AllDocumentsCollection)
                            {
                                minAllDocsEtag = Math.Min(tombstone.Value, minAllDocsEtag);
                                break;
                            }

                            if (tombstones.TryGetValue(tombstone.Key, out long v) == false)
                                tombstones[tombstone.Key] = tombstone.Value;
                            else
                                tombstones[tombstone.Key] = Math.Min(tombstone.Value, v);
                        }
                    }
                }
                finally
                {
                    _subscriptionsLocker.Release();
                }

                await _documentDatabase.TxMerger.Enqueue(new DeleteTombstonesCommand(tombstones, minAllDocsEtag, _documentDatabase, Logger));
            }
            catch (Exception e)
            {
                if (Logger.IsInfoEnabled)
                    Logger.Info($"Failed to execute tombstone cleanup on {_documentDatabase.Name}", e);
            }
        }

        internal class DeleteTombstonesCommand : TransactionOperationsMerger.MergedTransactionCommand
        {
            private readonly Dictionary<string, long> _tombstones;
            private readonly long _minAllDocsEtag;
            private readonly DocumentDatabase _database;
            private readonly Logger _logger;

            public DeleteTombstonesCommand(Dictionary<string, long> tombstones, long minAllDocsEtag, DocumentDatabase database, Logger logger)
            {
                _tombstones = tombstones;
                _minAllDocsEtag = minAllDocsEtag;
                _database = database;
                _logger = logger;
            }

            protected override int ExecuteCmd(DocumentsOperationContext context)
            {
                var deletionCount = 0;

                foreach (var tombstone in _tombstones)
                {
                    var minTombstoneValue = Math.Min(tombstone.Value, _minAllDocsEtag);
                    if (minTombstoneValue <= 0)
                        continue;

                    if (_database.DatabaseShutdown.IsCancellationRequested)
                        break;

                    deletionCount++;

                    try
                    {
                        _database.DocumentsStorage.DeleteTombstonesBefore(tombstone.Key, minTombstoneValue, context);
                    }
                    catch (Exception e)
                    {
                        if (_logger.IsInfoEnabled)
                            _logger.Info( $"Could not delete tombstones for '{tombstone.Key}' collection before '{minTombstoneValue}' etag.", e);

                        throw;
                    }
                }

                return deletionCount;
            }

            public override TransactionOperationsMerger.IReplayableCommandDto<TransactionOperationsMerger.MergedTransactionCommand> ToDto(JsonOperationContext context)
            {
                return new DeleteTombstonesCommandDto
                {
                    Tombstones = _tombstones,
                    MinAllDocsEtag = _minAllDocsEtag
                };
            }
        }
    }

    internal class DeleteTombstonesCommandDto : TransactionOperationsMerger.IReplayableCommandDto<TombstoneCleaner.DeleteTombstonesCommand>
    {
        public Dictionary<string, long> Tombstones;
        public long MinAllDocsEtag;

        public TombstoneCleaner.DeleteTombstonesCommand ToCommand(DocumentsOperationContext context, DocumentDatabase database)
        {
            var log = LoggingSource.Instance.GetLogger<TombstoneCleaner.DeleteTombstonesCommand>(database.Name);
            var command = new TombstoneCleaner.DeleteTombstonesCommand(Tombstones, MinAllDocsEtag, database, log);
            return command;
        }
    }

    public interface ITombstoneAware
    {
        Dictionary<string, long> GetLastProcessedTombstonesPerCollection();
    }
}
