﻿using System.Linq;
using FastTests;
using Orders;
using Xunit;

namespace SlowTests.Issues
{
    public class RavenDB_10986 : RavenTestBase
    {
        [Fact]
        public void WhereLuceneShouldAllowLeadingWildcards()
        {
            using (var store = GetDocumentStore())
            {
                using (var session = store.OpenSession())
                {
                    session.Store(new Company { Name = "Microsoft" });
                    session.Store(new Company { Name = "Othersoft" });
                    session.Store(new Company { Name = "Hibernating Rhinos" });

                    session.SaveChanges();
                }

                using (var session = store.OpenSession())
                {
                    var companies = session.Advanced.DocumentQuery<Company>()
                        .WhereLucene("Name", "*soft")
                        .ToList();

                    Assert.Equal(2, companies.Count);
                    Assert.Contains("Microsoft", companies.Select(x => x.Name));
                    Assert.Contains("Othersoft", companies.Select(x => x.Name));

                    companies = session.Advanced.DocumentQuery<Company>()
                        .WhereLucene("Name", "*soft OR *inos")
                        .ToList();

                    Assert.Equal(3, companies.Count);
                    Assert.Contains("Microsoft", companies.Select(x => x.Name));
                    Assert.Contains("Othersoft", companies.Select(x => x.Name));
                    Assert.Contains("Hibernating Rhinos", companies.Select(x => x.Name));
                }
            }
        }
    }
}
