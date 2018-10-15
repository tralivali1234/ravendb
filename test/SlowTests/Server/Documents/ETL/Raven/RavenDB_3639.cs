// -----------------------------------------------------------------------
//  <copyright file="RavenDB_3639.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------

using System;
using Raven.Tests.Core.Utils.Entities;
using Xunit;

namespace SlowTests.Server.Documents.ETL.Raven
{
    public class RavenDB_3639 : EtlTestBase
    {
        [Fact]
        public void Docs_are_transformed_according_to_provided_collection_specific_scripts()
        {
            using (var master = GetDocumentStore())
            using (var slave = GetDocumentStore())
            {
                var etlDone = WaitForEtl(master, (n, statistics) => statistics.LoadSuccesses != 0);

                AddEtl(master, slave, "users",
                    @"this.Name = 'patched ' + this.Name;
                      loadToUsers(this)");

                using (var session = master.OpenSession())
                {
                    session.Store(new Person
                    {
                        Name = "Arek"
                    }, "people/1");

                    session.Store(new User
                    {
                        Name = "Arek"
                    }, "users/1");

                    session.SaveChanges();
                }

                etlDone.Wait(TimeSpan.FromSeconds(30));

                using (var session = slave.OpenSession())
                {
                    var user = session.Load<User>("users/1");

                    Assert.Equal("patched Arek", user.Name);

                    var person = session.Load<Person>("people/1");

                    Assert.Null(person);
                }
            }
        }
    }
}
