﻿using System;
using FastTests;
using Raven.Client.Documents;
using Xunit;

namespace SlowTests.Issues
{
    public class RDoc_1318 : RavenTestBase
    {
        [Fact]
        public void OpeningSessionWithoutDatabaseShouldThrowMeaningfulException()
        {
            using (var store = new DocumentStore
            {
                Urls = new[] { Server.WebUrl }
            })
            {
                store.Initialize();

                var e = Assert.Throws<InvalidOperationException>(() =>
                {
                    using (store.OpenSession())
                    {
                    }
                });

                Assert.Contains("Cannot open a Session without specifying a name of a database to operate on", e.Message);

                e = Assert.Throws<InvalidOperationException>(() =>
                {
                    using (store.OpenAsyncSession())
                    {
                    }
                });

                Assert.Contains("Cannot open a Session without specifying a name of a database to operate on", e.Message);
            }
        }
    }
}
