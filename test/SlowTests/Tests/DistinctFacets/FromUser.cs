// -----------------------------------------------------------------------
//  <copyright file="FromUser.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------

using System.Collections.Generic;
using System.Linq;
using FastTests;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Xunit;

namespace SlowTests.Tests.DistinctFacets
{
    public class FromUser : RavenTestBase
    {
        [Fact]
        public void ShouldFacetsWork()
        {
            using (var documentStore = GetDocumentStore())
            {
                CreateSampleData(documentStore);
                WaitForIndexing(documentStore);

                using (var session = documentStore.OpenSession())
                {
                    var result = session.Advanced.DocumentQuery<SampleData, SampleData_Index>()
                        .Distinct()
                        .SelectFields<SampleData_Index.Result>("Name")
                        .AggregateBy("Tag")
                        .AndAggregateOn("TotalCount")
                        .Execute();

                    Assert.Equal(3, result["Tag"].Values.Count);

                    Assert.Equal(5, result["TotalCount"].Values[0].Hits);

                    Assert.Equal(5, result["Tag"].Values.First(x => x.Range == "0").Hits);
                    Assert.Equal(5, result["Tag"].Values.First(x => x.Range == "1").Hits);
                    Assert.Equal(5, result["Tag"].Values.First(x => x.Range == "2").Hits);
                }
            }
        }
        private static void CreateSampleData(IDocumentStore documentStore)
        {
            var names = new List<string>() { "Raven", "MSSQL", "NoSQL", "MYSQL", "BlaaBlaa" };

            new SampleData_Index().Execute(documentStore);

            using (var session = documentStore.OpenSession())
            {
                for (int i = 0; i < 600; i++)
                {
                    session.Store(new SampleData
                    {
                        Name = names[i % 5],
                        Tag = i % 3
                    });
                }

                session.SaveChanges();
            }

        }
        private class SampleData
        {
            public string Name { get; set; }
            public int Tag { get; set; }
        }

        private class SampleData_Index : AbstractIndexCreationTask<SampleData>
        {
            public SampleData_Index()
            {
                Map = docs => from doc in docs
                              select new
                              {
                                  doc.Name,
                                  Tag = doc.Tag,
                                  TotalCount = 1
                              };
                Store(x => x.Name, FieldStorage.Yes);
            }

            public class Result
            {
#pragma warning disable 169,649
                public string Name;
#pragma warning restore 169,649
            }
        }
    }
}
