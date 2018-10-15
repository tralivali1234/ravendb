﻿using System;
using System.Linq;
using FastTests;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Tests.Core.Utils.Entities;
using Xunit;

namespace SlowTests.Issues
{
    public class RavenDB_10505 : RavenTestBase
    {
        [Fact]
        public void ChangingCollectionNameByPutAndDeleteShouldNotDeleteTheOriginalTheTombstone()
        {
            using (var store = GetDocumentStore())
            {
                using (var session = store.OpenSession())
                {
                    session.Store(new User()
                    {
                        Name = "Arek"
                    }, "users/1");

                    session.SaveChanges();

                    session.Query<User>().Customize(x => x.WaitForNonStaleResults())
                        .Where(x => x.Name == "Arek").ToList();
                }

                store.Operations.Send(new PatchByQueryOperation(@"
from Users as u
update
{
    del(id(u));
    this[""@metadata""][""@collection""] = ""People"";
    put(id(u), this);
}
")).WaitForCompletion(TimeSpan.FromSeconds(30));


                WaitForIndexing(store);

                var indexStats = store.Maintenance.Send(new GetIndexStatisticsOperation("Auto/Users/ByName"));

                Assert.Equal(0, indexStats.EntriesCount);
            }
        }
    }
}
