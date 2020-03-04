﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Raven.Client;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;
using Raven.Server.Background;
using Raven.Server.Documents;
using Raven.Server.Json;
using Raven.Server.NotificationCenter;
using Raven.Server.ServerWide;
using Raven.Server.ServerWide.Context;
using Raven.Server.Utils;
using Sparrow;
using Sparrow.Collections;
using Sparrow.Json;
using Sparrow.Platform;
using Sparrow.Utils;

namespace Raven.Server.Dashboard
{
    public class DatabasesInfoNotificationSender : BackgroundWorkBase
    {
        private readonly ServerStore _serverStore;
        private readonly ConcurrentSet<ConnectedWatcher> _watchers;
        private readonly TimeSpan _notificationsThrottle;
        private DateTime _lastSentNotification = DateTime.MinValue;

        public DatabasesInfoNotificationSender(string resourceName, ServerStore serverStore,
            ConcurrentSet<ConnectedWatcher> watchers, TimeSpan notificationsThrottle, CancellationToken shutdown) 
            : base(resourceName, shutdown)
        {
            _serverStore = serverStore;
            _watchers = watchers;
            _notificationsThrottle = notificationsThrottle;
        }

        protected override async Task DoWork()
        {
            var now = DateTime.UtcNow;
            var timeSpan = now - _lastSentNotification;
            if (timeSpan < _notificationsThrottle)
            {
                await WaitOrThrowOperationCanceled(_notificationsThrottle - timeSpan);
            }

            try
            {
                if (CancellationToken.IsCancellationRequested)
                    return;

                if (_watchers.Count == 0)
                    return;

                var databasesInfo = FetchDatabasesInfo(_serverStore, null, Cts).ToList();
                foreach (var watcher in _watchers)
                {
                    foreach (var info in databasesInfo)
                    {
                        // serialize to avoid race conditions
                        // please notice we call ToJson inside a loop since DynamicJsonValue is not thread-safe
                        watcher.NotificationsQueue.Enqueue(info.ToJson());
                    }
                }
            }
            finally
            {
                _lastSentNotification = DateTime.UtcNow;
            }
        }

        public static IEnumerable<AbstractDashboardNotification> FetchDatabasesInfo(ServerStore serverStore, Func<string, bool> isValidFor, CancellationTokenSource cts)
        {
            var databasesInfo = new DatabasesInfo();
            var indexingSpeed = new IndexingSpeed();
            var trafficWatch = new TrafficWatch();
            var drivesUsage = new DrivesUsage();

            using (serverStore.ContextPool.AllocateOperationContext(out TransactionOperationContext transactionContext))
            using (transactionContext.OpenReadTransaction())
            {
                foreach (var databaseTuple in serverStore.Cluster.ItemsStartingWith(transactionContext, Constants.Documents.Prefix, 0, int.MaxValue))
                {
                    var databaseName = databaseTuple.ItemName.Substring(Constants.Documents.Prefix.Length);
                    if (cts.IsCancellationRequested)
                        yield break;

                    if (isValidFor != null && isValidFor(databaseName) == false)
                        continue;

                    if (serverStore.DatabasesLandlord.DatabasesCache.TryGetValue(databaseName, out var databaseTask) == false)
                    {
                        // database does not exist on this server or disabled
                        SetOfflineDatabaseInfo(serverStore, transactionContext, databaseName, databasesInfo, drivesUsage, disabled: true);
                        continue;
                    }

                    try
                    {
                        var databaseOnline = IsDatabaseOnline(databaseTask, out var database);
                        if (databaseOnline == false)
                        {
                            SetOfflineDatabaseInfo(serverStore, transactionContext, databaseName, databasesInfo, drivesUsage, disabled: false);
                            continue;
                        }

                        var indexingSpeedItem = new IndexingSpeedItem
                        {
                            Database = database.Name,
                            IndexedPerSecond = database.Metrics.MapIndexes.IndexedPerSec.OneSecondRate,
                            MappedPerSecond = database.Metrics.MapReduceIndexes.MappedPerSec.OneSecondRate,
                            ReducedPerSecond = database.Metrics.MapReduceIndexes.ReducedPerSec.OneSecondRate
                        };
                        indexingSpeed.Items.Add(indexingSpeedItem);

                        var replicationFactor = GetReplicationFactor(databaseTuple.Value);
                        var documentsStorage = database.DocumentsStorage;
                        var indexStorage = database.IndexStore;
                        using (documentsStorage.ContextPool.AllocateOperationContext(out DocumentsOperationContext documentsContext))
                        using (documentsContext.OpenReadTransaction())
                        {
                            var databaseInfoItem = new DatabaseInfoItem
                            {
                                Database = databaseName,
                                DocumentsCount = documentsStorage.GetNumberOfDocuments(documentsContext),
                                IndexesCount = database.IndexStore.Count,
                                AlertsCount = database.NotificationCenter.GetAlertCount(),
                                ReplicationFactor = replicationFactor,
                                ErroredIndexesCount = indexStorage.GetIndexes().Count(index => index.GetErrorCount() > 0),
                                Online = true
                            };
                            databasesInfo.Items.Add(databaseInfoItem);
                        }

                        var writesPerSecond = (int)database.Metrics.Docs.PutsPerSec.OneSecondRate +
                                              (int)database.Metrics.Attachments.PutsPerSec.OneSecondRate;
                        var writeBytesPerSecond = database.Metrics.Docs.BytesPutsPerSec.OneSecondRate +
                                                  database.Metrics.Attachments.BytesPutsPerSec.OneSecondRate;
                        var trafficWatchItem = new TrafficWatchItem
                        {
                            Database = databaseName,
                            RequestsPerSecond = (int)database.Metrics.Requests.RequestsPerSec.OneSecondRate,
                            WritesPerSecond = writesPerSecond,
                            WriteBytesPerSecond = writeBytesPerSecond
                        };
                        trafficWatch.Items.Add(trafficWatchItem);

                        foreach (var mountPointUsage in database.GetMountPointsUsage())
                        {
                            if (cts.IsCancellationRequested)
                                yield break;

                            UpdateMountPoint(mountPointUsage, databaseName, drivesUsage);
                        }
                    }
                    catch (Exception)
                    {
                        SetOfflineDatabaseInfo(serverStore, transactionContext, databaseName, databasesInfo, drivesUsage, disabled: false);
                    }
                }
            }

            yield return databasesInfo;
            yield return indexingSpeed;
            yield return trafficWatch;
            yield return drivesUsage;
        }

        private static void UpdateMountPoint(
            Client.ServerWide.Operations.MountPointUsage mountPointUsage, 
            string databaseName, 
            DrivesUsage drivesUsage)
        {
            var mountPoint = mountPointUsage.DiskSpaceResult.DriveName;
            var usage = drivesUsage.Items.FirstOrDefault(x => x.MountPoint == mountPoint);
            if (usage == null)
            {
                usage = new MountPointUsage
                {
                    MountPoint = mountPoint,
                    VolumeLabel = mountPointUsage.DiskSpaceResult.VolumeLabel,
                    FreeSpace = mountPointUsage.DiskSpaceResult.TotalFreeSpaceInBytes,
                    TotalCapacity = mountPointUsage.DiskSpaceResult.TotalSizeInBytes
                };
                drivesUsage.Items.Add(usage);
            }

            usage.VolumeLabel = mountPointUsage.DiskSpaceResult.VolumeLabel;
            usage.FreeSpace = mountPointUsage.DiskSpaceResult.TotalFreeSpaceInBytes;
            usage.TotalCapacity = mountPointUsage.DiskSpaceResult.TotalSizeInBytes;

            var existingDatabaseUsage = usage.Items.FirstOrDefault(x => x.Database == databaseName);
            if (existingDatabaseUsage == null)
            {
                existingDatabaseUsage = new DatabaseDiskUsage
                {
                    Database = databaseName
                };
                usage.Items.Add(existingDatabaseUsage);
            }

            existingDatabaseUsage.Size += mountPointUsage.UsedSpace;
            existingDatabaseUsage.TempBuffersSize += mountPointUsage.UsedSpaceByTempBuffers;
        }

        private static void SetOfflineDatabaseInfo(
            ServerStore serverStore,
            TransactionOperationContext context,
            string databaseName, 
            DatabasesInfo existingDatabasesInfo, 
            DrivesUsage existingDrivesUsage, 
            bool disabled)
        {
            var databaseRecord = serverStore.Cluster.ReadDatabase(context, databaseName, out var _);
            if (databaseRecord == null)
            {
                // database doesn't exist
                return;
            }

            var irrelevant = databaseRecord.Topology == null || 
                             databaseRecord.Topology.AllNodes.Contains(serverStore.NodeTag) == false;
            var databaseInfoItem = new DatabaseInfoItem
            {
                Database = databaseName,
                Online = false,
                Disabled = disabled,
                Irrelevant = irrelevant
            };

            if (irrelevant == false)
            {
                // nothing to fetch if irrelevant on this node
                UpdateDatabaseInfo(databaseRecord, serverStore, databaseName, existingDrivesUsage, databaseInfoItem);
            }

            existingDatabasesInfo.Items.Add(databaseInfoItem);
        }

        private static void UpdateDatabaseInfo(DatabaseRecord databaseRecord, ServerStore serverStore, string databaseName, DrivesUsage existingDrivesUsage,
            DatabaseInfoItem databaseInfoItem)
        {
            DatabaseInfo databaseInfo = null;
            if (serverStore.DatabaseInfoCache.TryGet(databaseName,
                databaseInfoJson => databaseInfo = JsonDeserializationServer.DatabaseInfo(databaseInfoJson)) == false)
                return;

            Debug.Assert(databaseInfo != null);

            databaseInfoItem.DocumentsCount = databaseInfo.DocumentsCount ?? 0;
            databaseInfoItem.IndexesCount = databaseInfo.IndexesCount ?? databaseRecord.Indexes.Count;
            databaseInfoItem.ReplicationFactor = databaseRecord.Topology?.ReplicationFactor ?? databaseInfo.ReplicationFactor;
            databaseInfoItem.ErroredIndexesCount = databaseInfo.IndexingErrors ?? 0;

            if (databaseInfo.MountPointsUsage == null)
                return;

            foreach (var mountPointUsage in databaseInfo.MountPointsUsage)
            {
                var driveName = mountPointUsage.DiskSpaceResult.DriveName;
                var diskSpaceResult = DiskSpaceChecker.GetDiskSpaceInfo(
                    mountPointUsage.DiskSpaceResult.DriveName,
                    new DriveInfoBase
                    {
                        DriveName = driveName
                    });

                if (diskSpaceResult != null)
                {
                    // update the latest drive info
                    mountPointUsage.DiskSpaceResult = new Client.ServerWide.Operations.DiskSpaceResult
                    {
                        DriveName = diskSpaceResult.DriveName,
                        VolumeLabel = diskSpaceResult.VolumeLabel,
                        TotalFreeSpaceInBytes = diskSpaceResult.TotalFreeSpace.GetValue(SizeUnit.Bytes),
                        TotalSizeInBytes = diskSpaceResult.TotalSize.GetValue(SizeUnit.Bytes)
                    };
                }

                UpdateMountPoint(mountPointUsage, databaseName, existingDrivesUsage);
            }
        }

        private static int GetReplicationFactor(BlittableJsonReaderObject databaseRecordBlittable)
        {
            if (databaseRecordBlittable.TryGet("Topology", out BlittableJsonReaderObject topology) == false)
                return 1;

            if (topology.TryGet("ReplicationFactor", out int replicationFactor) == false)
                return 1;

            return replicationFactor;
        }

        private static bool IsDatabaseOnline(Task<DocumentDatabase> databaseTask, out DocumentDatabase database)
        {
            if (databaseTask.IsCanceled || databaseTask.IsFaulted || databaseTask.IsCompleted == false)
            {
                database = null;
                return false;
            }

            database = databaseTask.Result;
            return database.DatabaseShutdown.IsCancellationRequested == false;
        }
    }
}
