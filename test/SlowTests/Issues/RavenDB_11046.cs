﻿using FastTests;
using Raven.Client.Documents.Queries;
using Tests.Infrastructure;
using Xunit;

namespace SlowTests.Issues
{
    public class RavenDB_11046 : RavenTestBase
    {
        [Fact]
        public void FacetRqlShouldSupportAliasNotation()
        {
            using (var store = GetDocumentStore())
            {
                store.Maintenance.Send(new CreateSampleDataOperation());

                WaitForIndexing(store);

                using (var commands = store.Commands())
                {
                    var results1 = commands
                        .Query(new IndexQuery
                        {
                            Query = @"from index 'Orders/Totals' select facet(Total >= 50)"
                        });

                    var results2 = commands
                        .Query(new IndexQuery
                        {
                            Query = @"from index 'Orders/Totals' as o select facet(o.Total >= 50)"
                        });

                    Assert.True(results1.Results.Equals(results2.Results));
                }
            }
        }
    }
}
