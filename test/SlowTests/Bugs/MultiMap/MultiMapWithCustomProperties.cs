using System.Linq;
using FastTests;
using Raven.Client.Documents.Indexes;
using Xunit;

namespace SlowTests.Bugs.MultiMap
{
    public class MultiMapWithCustomProperties : RavenTestBase
    {
        [Fact]
        public void Can_create_index()
        {
            using (var store = GetDocumentStore())
            {
                using (var session = store.OpenSession())
                {
                    session.Store(new Cat { Name = "Tom", CatsOnlyProperty = "Miau" });
                    session.Store(new Dog { Name = "Oscar" });

                    session.SaveChanges();
                }

                new CatsAndDogs().Execute(store);

                WaitForIndexing(store);

                var db = GetDocumentDatabaseInstanceFor(store).Result;
                var errorsCount = db.IndexStore.GetIndexes().Sum(index => index.GetErrorCount());

                Assert.Equal(errorsCount, 0);

                using (var s = store.OpenSession())
                {
                    Assert.NotEmpty(s.Query<Cat, CatsAndDogs>()
                                        .Where(x => x.CatsOnlyProperty == "Miau")
                                        .ToList());

                    Assert.NotEmpty(s.Query<Dog, CatsAndDogs>()
                                        .Where(x => x.Name == "Oscar")
                                        .ToList());
                }
            }
        }

        private class CatsAndDogs : AbstractMultiMapIndexCreationTask
        {
            public CatsAndDogs()
            {
                AddMap<Cat>(cats => from cat in cats
                                    select new { cat.Name, cat.CatsOnlyProperty });

                AddMap<Dog>(dogs => from dog in dogs
                                    select new { dog.Name, CatsOnlyProperty = (string)null });
            }
        }

        private interface IHaveName
        {
            string Name { get; }
        }

        private class Cat : IHaveName
        {
            public string Name { get; set; }
            public string CatsOnlyProperty { get; set; }
        }

        private class Dog : IHaveName
        {
            public string Name { get; set; }
        }
    }
}
