﻿using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using FastTests;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Raven.Client.Documents.Commands;
using Raven.Client.Documents.Operations;
using Raven.Client.Http;
using Sparrow.Json;
using Xunit;

namespace SlowTests.Issues
{
    public class RavenDB_9519 : RavenTestBase
    {
        [Fact]
        public async Task NestedObjectShouldBeExportedAndImportedProperly()
        {
            const string id = "companies/1";

            using (var store = GetDocumentStore())
            {
                string cv;
                using (var session = store.OpenSession())
                {
                    session.Store(_testCompany, id);
                    session.SaveChanges();
                    cv = session.Advanced.GetChangeVectorFor(_testCompany);
                }

                var client = new HttpClient();
                var stream = await client.GetStreamAsync($"{store.Urls[0]}/databases/{store.Database}/streams/queries?query=From%20companies&format=csv");

                using (var commands = store.Commands())
                {
                    var getOperationIdCommand = new GetNextOperationIdCommand();
                    await commands.RequestExecutor.ExecuteAsync(getOperationIdCommand, commands.Context);
                    var operationId = getOperationIdCommand.Result;

                    {
                        var csvImportCommand = new CsvImportCommand(stream, null, operationId);

                        await commands.ExecuteAsync(csvImportCommand);

                        var operation = new Operation(commands.RequestExecutor, () => store.Changes(), store.Conventions, operationId);

                        await operation.WaitForCompletionAsync();
                    }
                }

                using (var session = store.OpenSession())
                {
                    var res = session.Load<Company>(id);
                    Assert.NotEqual(session.Advanced.GetChangeVectorFor(res), cv);

                    try
                    {
                        Assert.Equal(res, _testCompany);
                    }
                    catch (Exception)
                    {
                        var sb = new StringBuilder();
                        sb.AppendLine("Expected:");
                        sb.AppendLine(JObject.FromObject(res).ToString(Formatting.Indented));
                        sb.AppendLine();
                        sb.AppendLine("Actual:");
                        sb.AppendLine(JObject.FromObject(_testCompany).ToString(Formatting.Indented));

                        Console.WriteLine(sb);

                        throw;
                    }

                }
            }
        }

        [Fact]
        public async Task ExportingAndImportingCsvUsingQueryFromDocumentShouldWork()
        {
            using (var store = GetDocumentStore())
            {
                using (var session = store.OpenSession())
                {
                    session.Store(_testCompany, "companies/1");
                    session.Store(new { Query = "From%20companies" }, "queries/1");
                    session.SaveChanges();
                }

                var client = new HttpClient();
                var stream = await client.GetStreamAsync($"{store.Urls[0]}/databases/{store.Database}/streams/queries?fromDocument=queries%2F1&format=csv");

                using (var commands = store.Commands())
                {
                    var getOperationIdCommand = new GetNextOperationIdCommand();
                    await commands.RequestExecutor.ExecuteAsync(getOperationIdCommand, commands.Context);
                    var operationId = getOperationIdCommand.Result;
                    var csvImportCommand = new CsvImportCommand(stream, null, operationId);

                    await commands.ExecuteAsync(csvImportCommand);

                    var operation = new Operation(commands.RequestExecutor, () => store.Changes(), store.Conventions, operationId);

                    await operation.WaitForCompletionAsync();
                }

                using (var session = store.OpenSession())
                {
                    var res = session.Query<Company>().ToList();
                    Assert.Equal(1, res.Count);
                }
            }
        }

        private readonly Company _testCompany = new Company
        {
            ExternalId = "WOLZA",
            Name = "Wolski  Zajazd",
            Contact = new Contact
            {
                Name = "Zbyszek Piestrzeniewicz",
                Title = "Owner"
            },
            Address = new Address
            {
                City = "Warszawa",
                Country = "Poland",
                Line1 = "ul. Filtrowa 68",
                Line2 = null,
                Location = new Location
                {
                    Latitude = 52.21956300000001,
                    Longitude = 20.985878
                },
                PostalCode = "01-012",
                Region = null
            },
            Phone = "(26) 642-7012",
            Fax = "(26) 642-7012",
        };

        private class Company
        {
            public string Id { get; set; }
            public string ExternalId { get; set; }
            public string Name { get; set; }
            public Contact Contact { get; set; }
            public Address Address { get; set; }
            public string Phone { get; set; }
            public string Fax { get; set; }
            public override bool Equals(object obj)
            {
                if (!(obj is Company other))
                    return false;

                if (ReferenceEquals(this, other))
                    return true;

                return ExternalId == other.ExternalId && Name == other.Name && Contact?.Name == other.Contact?.Name
                       && Contact?.Title == other.Contact?.Title && Address?.City == other.Address?.City && Address?.Country == other.Address?.Country
                       && Address?.Line1 == other.Address?.Line1 && Address?.Line2 == other.Address?.Line2 && Address?.PostalCode == other.Address?.PostalCode
                       && Address?.Region == other.Address?.Region && Address?.Location?.Latitude == other.Address?.Location?.Latitude
                       && Address?.Location?.Longitude == other.Address?.Location?.Longitude && Phone == other.Phone && Fax == other.Fax;
            }

            public override int GetHashCode()
            {
                return 1;
            }
        }

        private class Address
        {
            public string Line1 { get; set; }
            public string Line2 { get; set; }
            public string City { get; set; }
            public string Region { get; set; }
            public string PostalCode { get; set; }
            public string Country { get; set; }
            public Location Location { get; set; }
        }

        private class Location
        {
            public double Latitude { get; set; }
            public double Longitude { get; set; }
        }

        private class Contact
        {
            public string Name { get; set; }
            public string Title { get; set; }
        }

        private class CsvImportCommand : RavenCommand
        {
            private readonly Stream _stream;
            private readonly string _collection;
            private readonly long _operationId;

            public override bool IsReadRequest => false;

            public CsvImportCommand(Stream stream, string collection, long operationId)
            {
                _stream = stream ?? throw new ArgumentNullException(nameof(stream));

                _collection = collection;
                _operationId = operationId;
            }

            public override HttpRequestMessage CreateRequest(JsonOperationContext ctx, ServerNode node, out string url)
            {
                url = $"{node.Url}/databases/{node.Database}/smuggler/import/csv?operationId={_operationId}&collection={_collection}";

                var form = new MultipartFormDataContent
                {
                    {new StreamContent(_stream), "file", "name"}
                };

                return new HttpRequestMessage
                {
                    Method = HttpMethod.Post,
                    Content = form
                };
            }
        }
    }
}
