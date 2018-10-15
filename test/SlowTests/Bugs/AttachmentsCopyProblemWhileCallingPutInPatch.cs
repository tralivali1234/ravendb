﻿using System.IO;
using FastTests;
using Orders;
using Raven.Client;
using Raven.Client.Documents.Commands.Batches;
using Raven.Client.Documents.Operations;
using Xunit;

namespace SlowTests.Bugs
{
    public class RavenDB_11391_PtachPutWithAttachmentsOrCounters : RavenTestBase
    {
        [Fact]
        public void PatchPut_WhileContainAttachments_TheNewDocumentShouldNotContainThem()
        {
            var expectedAttachmentStream = new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6 });
            var employee = new Employee { FirstName = "Avi" };
            const string newId = "second/1-A";
            const string attachmentName = "Profile Picture";

            bool doesHaveAttachments;

            using (var store = GetDocumentStore())
            {
                //Store employee with attachment
                using (var session = store.OpenSession())
                {
                    session.Store(employee);

                    session.Advanced.Attachments.Store(employee.Id, attachmentName, expectedAttachmentStream);

                    session.SaveChanges();
                }

                //Put new employee by patch
                using (var session = store.OpenSession())
                {
                    session.Advanced.Defer(new PatchCommandData(
                        id: employee.Id,
                        changeVector: null,
                        patch: new PatchRequest
                        {
                            Script = $"put('{newId}', this)"
                        },
                        patchIfMissing: null));

                    session.SaveChanges();
                }

                //Try get attachment metadata properties
                using (var session = store.OpenSession())
                {
                    var newEmployee = session.Load<Employee>(newId);
                    var newEmployeeMetadata = session.Advanced.GetMetadataFor(newEmployee);
                    doesHaveAttachments = newEmployeeMetadata.TryGetValue(Constants.Documents.Metadata.Attachments, out object at);
                }
            }

            //Assert
            Assert.False(doesHaveAttachments, "The new employee should have no attachment properties in metadata");
        }

        [Fact]
        public void PatchPut_WhileContainCounters_TheNewDocumentShouldNotContainThem()
        {
            var employee = new Employee { FirstName = "Avi" };
            const string newId = "second/1-A";
            const string counterName = "Likes";

            bool doesHaveCounters;

            using (var store = GetDocumentStore())
            {
                //Store employee with counter
                using (var session = store.OpenSession())
                {
                    session.Store(employee);

                    session.CountersFor(employee).Increment(counterName);

                    session.SaveChanges();
                }

                //Put new employee by patch
                using (var session = store.OpenSession())
                {
                    session.Advanced.Defer(new PatchCommandData(
                        id: employee.Id,
                        changeVector: null,
                        patch: new PatchRequest
                        {
                            Script = $"put('{newId}', this)"
                        },
                        patchIfMissing: null));

                    session.SaveChanges();
                }

                //Try get counter metadata properties
                using (var session = store.OpenSession())
                {
                    var newEmployee = session.Load<Employee>(newId);
                    var newEmployeeMetadata = session.Advanced.GetMetadataFor(newEmployee);
                    doesHaveCounters = newEmployeeMetadata.TryGetValue(Constants.Documents.Metadata.Counters, out object at);
                }
            }

            //Assert
            Assert.False(doesHaveCounters, "The new employee should have no counter properties in metadata");
        }
    }
}
