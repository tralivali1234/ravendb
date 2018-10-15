using System.Linq;
using FastTests;
using Raven.Client;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.Documents.Queries;
using Sparrow.Json;
using Xunit;

namespace SlowTests.Issues
{
    public class RavenDB_554 : RavenTestBase
    {
        private class Person
        {
            public string FirstName { get; set; }

            public string LastName { get; set; }

            public string MiddleName { get; set; }
        }

        [Fact]
        public void IndexEntryFieldShouldNotContainNullValues()
        {
            const string IndexName = "Index1";

            using (var docStore = GetDocumentStore())
            {
                docStore.Maintenance.Send(new PutIndexesOperation(new IndexDefinition
                {
                    Name = IndexName,
                    Maps =
                    {
                        "from doc in docs.People select new { doc.FirstName, doc.LastName, Query = new[] { doc.FirstName, doc.LastName, doc.MiddleName } }"
                    },
                    Fields =
                    {
                        {
                            "Query", new IndexFieldOptions
                            {
                                Indexing = FieldIndexing.Search
                            }
                        }
                    }
                }));

                using (var session = docStore.OpenSession())
                {
                    session.Store(new Person { FirstName = "John", MiddleName = null, LastName = null });
                    session.Store(new Person { FirstName = "William", MiddleName = "Edgard", LastName = "Smith" });
                    session.Store(new Person { FirstName = "Paul", MiddleName = null, LastName = "Smith" });
                    session.SaveChanges();
                }

                using (var session = docStore.OpenSession())
                {
                    session.Query<Person>(IndexName)
                        .Customize(x => x.WaitForNonStaleResults())
                        .ToList();

                    using (var commands = docStore.Commands())
                    {
                        var queryResult = commands.Query(new IndexQuery { Query = $"FROM INDEX '{IndexName}'" }, false, true);
                        foreach (BlittableJsonReaderObject result in queryResult.Results)
                        {
                            Assert.True(result.TryGet("Query", out object obj));

                            var q = obj?.ToString();
                            Assert.NotNull(q);
                            Assert.False(q.Contains(Constants.Documents.Indexing.Fields.NullValue));
                        }
                    }
                }
            }
        }
    }
}
