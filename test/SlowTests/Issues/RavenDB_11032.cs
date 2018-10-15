﻿using System;
using System.Collections.Generic;
using System.Text;
using FastTests;
using FastTests.Server.Basic.Entities;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;
using Xunit;
using Raven.Client;
using System.Threading.Tasks;

namespace SlowTests.Issues
{
    public class RavenDB_11032:RavenTestBase
    {
        [Fact]
        public async Task PatchByIndexShouldSupportDeclaredFunctions()
        {
            using (var store = GetDocumentStore())
            {
                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(new Order
                    {
                        Company = "companies/1"
                    }, "orders/1");
                    await session.SaveChangesAsync();
                }

                var operation = await store
    .Operations
    .SendAsync(new PatchByQueryOperation(new IndexQuery
    {
        QueryParameters = new Parameters
        {
            {"newCompany", "companies/2" }
        },
        Query = @"
declare function UpdateCompany(o, newVal)
{
o.Company = newVal;
}
from Orders as o                  
update
{
    UpdateCompany(o,$newCompany);
    
}"
    }));
                await operation.WaitForCompletionAsync();

                using (var session = store.OpenAsyncSession())
                {
                    var doc = await session.LoadAsync<Order>("orders/1");
                    Assert.Equal("companies/2", doc.Company);
                }

                
            }
        }
    }
}
