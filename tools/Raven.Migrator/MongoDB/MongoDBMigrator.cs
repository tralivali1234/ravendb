﻿using System;
using System.Collections.Generic;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.GridFS;
using Newtonsoft.Json;

namespace Raven.Migrator.MongoDB
{
    public class MongoDBMigrator : INoSqlMigrator
    {
        private readonly MongoDBConfiguration _configuration;
        private readonly BsonDocument _filterDefinition = new BsonDocument();

        private const string MongoDocumentId = "_id";
        private readonly List<string> _propertiesToRemove = new List<string>
        {
            MongoDocumentId
        };

        public MongoDBMigrator(MongoDBConfiguration configuration)
        {
            _configuration = configuration;
        }

        public async Task GetDatabases()
        {
            AssertConnectionString();

            var databases = new List<string>();

            var client = CreateNewMongoClient();
            AssertOnline(client, client.GetDatabase("database"));

            using (var cursor = await client.ListDatabasesAsync())
            {
                while (await cursor.MoveNextAsync())
                {
                    foreach (var database in cursor.Current)
                    {
                        var name = database["name"].ToString();
                        if (name.Equals("admin") || name.Equals("local") || name.Equals("config"))
                            continue;

                        databases.Add(name);
                    }
                }
            }

            MigrationHelpers.OutputClass(_configuration,
                new DatabasesInfo
                {
                    Databases = databases
                });
        }

        public async Task GetCollectionsInfo()
        {
            AssertDatabaseName();

            var client = CreateNewMongoClient();
            var database = client.GetDatabase(_configuration.DatabaseName);
            AssertOnline(client, database);

            MigrationHelpers.OutputClass(_configuration,
                new ExtendedCollectionsInfo
                {
                    Collections = await GetCollections(database),
                    HasGridFS = await HasGridFs(database)
                });
        }

        private static async Task<List<string>> GetCollections(IMongoDatabase database)
        {
            var collections = new List<string>();

            using (var cursor = await database.ListCollectionsAsync())
            {
                while (await cursor.MoveNextAsync())
                {
                    foreach (var collectionDocument in cursor.Current)
                    {
                        var collectionName = collectionDocument["name"].ToString();
                        if (collectionName.EndsWith(".chunks", StringComparison.OrdinalIgnoreCase) || 
                            collectionName.EndsWith(".files", StringComparison.OrdinalIgnoreCase))
                            continue; // grid fs files will be handled separately

                        collections.Add(collectionName);
                    }
                }
            }

            return collections;
        }

        private static async Task<bool> HasGridFs(IMongoDatabase database)
        {
            var bucket = new GridFSBucket(database);

            var filter = Builders<GridFSFileInfo>.Filter.Empty;
            using (var cursor = await bucket.FindAsync(filter))
            {
                return await cursor.MoveNextAsync();
            }
        }

        public async Task MigrateDatabase()
        {
            AssertDatabaseName();

            var client = CreateNewMongoClient();
            var database = client.GetDatabase(_configuration.DatabaseName);
            AssertOnline(client, database);

            if (_configuration.CollectionsToMigrate == null || 
                _configuration.CollectionsToMigrate.Count == 0 && _configuration.MigrateGridFS == false)
            {
                _configuration.CollectionsToMigrate = await GetCollectionsToMigrate(database);
            }

            await MigrationHelpers.MigrateNoSqlDatabase(
                _configuration,
                async (mongoCollectionName, ravenCollectionName, jsonTextWriter, streamWriter) => 
                    await MigrateSingleCollection(database, mongoCollectionName, ravenCollectionName, jsonTextWriter),
                async (jsonTextWriter, streamWriter) =>
                {
                    if (_configuration.MigrateGridFS)
                        await MigrateGridFS(database, jsonTextWriter, streamWriter);
                });
        }

        private async Task MigrateSingleCollection(
            IMongoDatabase database, 
            string mongoCollectionName,
            string ravenCollectionName,
            JsonTextWriter jsonTextWriter)
        {
            var collection = database.GetCollection<ExpandoObject>(mongoCollectionName);

            using (var documentsCursor = await collection.Find(_filterDefinition).ToCursorAsync())
            {
                while (await documentsCursor.MoveNextAsync())
                {
                    foreach (var document in documentsCursor.Current)
                    {
                        var dictionary = (IDictionary<string, object>)document;
                        var documentId = dictionary[MongoDocumentId].ToString();

                        MigrationHelpers.WriteDocument(
                            document,
                            documentId,
                            ravenCollectionName,
                            _propertiesToRemove,
                            jsonTextWriter);
                    }
                }
            }
        }

        private static async Task<List<Collection>> GetCollectionsToMigrate(IMongoDatabase database)
        {
            var collectionsToMigrate = await GetCollections(database);
            return collectionsToMigrate
                .Select(x => new Collection
                {
                    Name = x,
                    NewName = x
                }).ToList();
        }

        private static async Task MigrateGridFS(
            IMongoDatabase database, 
            JsonTextWriter jsonTextWriter, 
            StreamWriter streamWriter)
        {
            var bucket = new GridFSBucket(database);
            var collectionName = bucket.Options.BucketName == "fs" ? "Files" : bucket.Options.BucketName;
            var filter = Builders<GridFSFileInfo>.Filter.Empty;
            var attachmentNumber = 0;

            using (var cursor = await bucket.FindAsync(filter))
            {
                while (await cursor.MoveNextAsync())
                {
                    foreach (var fileInfo in cursor.Current)
                    {
                        var document = ToExpandoObject(fileInfo.Metadata);
                        var dictionary = (IDictionary<string, object>)document;
                        var documentId = fileInfo.Id.ToString();
                        var totalSize = fileInfo.Length;
                        var contentType = GetContentType(fileInfo, dictionary);

                        using (var stream = new MemoryStream())
                        {
                            //can't OpenDownloadStreamAsync because it's not a seekable stream
                            await bucket.DownloadToStreamAsync(fileInfo.Id, stream);
                            stream.Position = 0;

                            attachmentNumber++;
                            var attachmentInfo = await MigrationHelpers.WriteAttachment(
                                stream, totalSize, documentId, collectionName, contentType,
                                attachmentNumber, jsonTextWriter, streamWriter);

                            MigrationHelpers.WriteDocument(
                                document,
                                documentId,
                                collectionName,
                                null,
                                jsonTextWriter,
                                new List<Dictionary<string, object>>
                                {
                                    attachmentInfo
                                });
                        }
                    }
                }
            }
        }

        private static string GetContentType(GridFSFileInfo fileInfo, IDictionary<string, object> dictionary)
        {
#pragma warning disable CS0618 // Type or member is obsolete
            var contentType = fileInfo.ContentType;
#pragma warning restore CS0618 // Type or member is obsolete
            if (dictionary.TryGetValue("contentType", out object value))
            {
                contentType = (string)value;
            }
            else if (dictionary.TryGetValue("Content-Type", out value))
            {
                contentType = (string)value;
            }
            else if (dictionary.TryGetValue("ContentType", out value))
            {
                contentType = (string)value;
            }

            return contentType;
        }

        private static readonly System.Text.RegularExpressions.Regex ObjectIdReplace = new System.Text.RegularExpressions.Regex(@"ObjectId\((.[a-f0-9]{24}.)\)", System.Text.RegularExpressions.RegexOptions.Compiled);

        private static dynamic ToExpandoObject(BsonDocument bsonDocument)
        {
            var input = bsonDocument.ToJson();
            var json = ObjectIdReplace.Replace(input, s => s.Groups[1].Value);
            return JsonConvert.DeserializeObject<ExpandoObject>(json);
        }

        private void AssertConnectionString()
        {
            if (string.IsNullOrWhiteSpace(_configuration.ConnectionString))
                throw new ArgumentException("ConnectionString cannot be null or empty");
        }

        private void AssertDatabaseName()
        {
            if (string.IsNullOrWhiteSpace(_configuration.DatabaseName))
                throw new ArgumentException("DatabaseName cannot be null or empty");
        }

        private MongoClient CreateNewMongoClient()
        {
            return new MongoClient(_configuration.ConnectionString);
        }

        public void AssertOnline(MongoClient client, IMongoDatabase database)
        {
            var online = database.RunCommandAsync((Command<BsonDocument>)"{ping:1}").Wait(TimeSpan.FromSeconds(5));
            if (online == false)
                throw new InvalidOperationException($"Couldn't connect to: {_configuration.ConnectionString}");
        }
    }
}
