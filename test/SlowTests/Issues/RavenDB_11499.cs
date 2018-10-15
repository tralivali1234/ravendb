﻿using FastTests;
using Xunit;

namespace SlowTests.Issues
{
    public class RavenDB_11499 : RavenTestBase
    {
        private class User
        {
            public sbyte Sbyte { get; set; }

            public ushort Ushort { get; set; }
        }

        [Fact]
        public void CanPatchSbyteAndUshort()
        {
            using (var store = GetDocumentStore())
            {
                using (var session = store.OpenSession())
                {
                    session.Store(new User
                    {
                        Sbyte = 33,
                        Ushort = 55
                    }, "users/1");

                    session.SaveChanges();
                }

                using (var session = store.OpenSession())
                {
                    session.Advanced.Patch<User, sbyte>("users/1", u => u.Sbyte, 22);
                    session.SaveChanges();
                }

                using (var session = store.OpenSession())
                {
                    session.Advanced.Patch<User, ushort>("users/1", u => u.Ushort, 11);
                    session.SaveChanges();
                }

                using (var session = store.OpenSession())
                {
                    var user = session.Load<User>("users/1");

                    Assert.Equal((sbyte)22, user.Sbyte);
                    Assert.Equal((ushort)11, user.Ushort);
                }
            }
        }
    }
}
