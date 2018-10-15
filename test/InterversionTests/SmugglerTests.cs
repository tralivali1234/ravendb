﻿using System.IO;
using System.Threading.Tasks;
using FastTests.Server.Basic.Entities;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Smuggler;
using Tests.Infrastructure;
using Xunit;

namespace InterversionTests
{
    public class SmugglerTests : InterversionTestBase
    {
        [Fact]
        public async Task CanExportFrom40AndImportTo41()
        {
            var file = Path.GetTempFileName();
            long countOfDocuments;
            long countOfAttachments;
            long countOfIndexes;
            long countOfRevisions;

            try
            {
                using (var store40 = await GetDocumentStoreAsync("4.0.6-patch-40047"))
                {
                    store40.Maintenance.Send(new CreateSampleDataOperation());

                    var options = new DatabaseSmugglerExportOptions();
                    options.OperateOnTypes &= ~DatabaseItemType.Counters;

                    await store40.Smuggler.ExportAsync(options, file);

                    var stats = await store40.Maintenance.SendAsync(new GetStatisticsOperation());

                    countOfDocuments = stats.CountOfDocuments;
                    countOfAttachments = stats.CountOfAttachments;
                    countOfIndexes = stats.CountOfIndexes;
                    countOfRevisions = stats.CountOfRevisionDocuments;

                }

                using (var store41 = GetDocumentStore())
                {
                    var options = new DatabaseSmugglerImportOptions
                    {
                        SkipRevisionCreation = true
                    };

                    await store41.Smuggler.ImportAsync(options, file);

                    var stats = await store41.Maintenance.SendAsync(new GetStatisticsOperation());

                    Assert.Equal(countOfDocuments, stats.CountOfDocuments);
                    Assert.Equal(countOfAttachments, stats.CountOfAttachments);
                    Assert.Equal(countOfIndexes, stats.CountOfIndexes);
                    Assert.Equal(countOfRevisions, stats.CountOfRevisionDocuments);
                }
            }
            finally
            {
                File.Delete(file);
            }

        }

        [Fact]
        public async Task CanExportFrom41AndImportTo40()
        {
            var file = Path.GetTempFileName();
            long countOfDocuments;
            long countOfAttachments;
            long countOfIndexes;
            long countOfRevisions;

            try
            {
                using (var store41 = GetDocumentStore())
                {
                    store41.Maintenance.Send(new CreateSampleDataOperation());

                    using (var session = store41.OpenSession())
                    {
                        var o = session.Load<Order>("orders/1-A");
                        Assert.NotNull(o);
                        session.CountersFor(o).Increment("downloads", 100);
                        session.SaveChanges();
                    }

                    await store41.Smuggler.ExportAsync(new DatabaseSmugglerExportOptions(), file);

                    var stats = await store41.Maintenance.SendAsync(new GetStatisticsOperation());

                    countOfDocuments = stats.CountOfDocuments;
                    countOfAttachments = stats.CountOfAttachments;
                    countOfIndexes = stats.CountOfIndexes;
                    countOfRevisions = stats.CountOfRevisionDocuments;

                    Assert.Equal(1, stats.CountOfCounters);
                }

                using (var store40 = await GetDocumentStoreAsync("4.0.6-patch-40047"))
                {

                    var options = new DatabaseSmugglerImportOptions();
                    options.OperateOnTypes &= ~DatabaseItemType.Counters;
                    options.SkipRevisionCreation = true;

                    await store40.Smuggler.ImportAsync(options, file);

                    var stats = await store40.Maintenance.SendAsync(new GetStatisticsOperation());

                    Assert.Equal(countOfDocuments, stats.CountOfDocuments);
                    Assert.Equal(countOfAttachments, stats.CountOfAttachments);
                    Assert.Equal(countOfIndexes, stats.CountOfIndexes);
                    Assert.Equal(countOfRevisions, stats.CountOfRevisionDocuments);

                    Assert.Equal(0, stats.CountOfCounters);

                }
            }
            finally
            {
                File.Delete(file);
            }

        }
    }
}
