﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Raven.Client.Documents.Indexes;
using Raven.Client.Extensions;
using Raven.Server.ServerWide;
using Raven.Server.ServerWide.Context;

using Sparrow.Json;

using Voron;
using Sparrow;
using Voron.Impl;

namespace Raven.Server.Documents.Indexes
{
    public abstract class IndexDefinitionBase
    {
        public string Name { get; protected set; }

        public HashSet<string> Collections { get; protected set; }

        public IndexLockMode LockMode { get; set; }

        public IndexPriority Priority { get; set; }

        public virtual bool HasDynamicFields => false;

        public void Rename(string name, TransactionOperationContext context, StorageEnvironmentOptions options)
        {
            Name = name;
            Persist(context, options);
        }

        public abstract void Persist(TransactionOperationContext context, StorageEnvironmentOptions options);

        protected abstract void PersistMapFields(JsonOperationContext context, BlittableJsonTextWriter writer);

        public static readonly byte[] EncryptionContext = Encoding.UTF8.GetBytes("Indexes!");

        public static string GetIndexNameSafeForFileSystem(string name)
        {
            foreach (var invalidPathChar in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(invalidPathChar, '_');
            }

            if (name.Length < 64)
                return name;
            // RavenDB-8220 To avoid giving the same path to indexes with the 
            // same 64 chars prefix, we hash the full name. Note that this is
            // a persistent value and should NOT be changed. 
            return name.Substring(0, 64) + "." + Hashing.XXHash32.Calculate(name);
        }

        public void Persist(JsonOperationContext context, BlittableJsonTextWriter writer)
        {
            writer.WriteStartObject();

            writer.WritePropertyName(nameof(Name));
            writer.WriteString(Name);
            writer.WriteComma();

            writer.WritePropertyName(nameof(Collections));
            writer.WriteStartArray();
            var isFirst = true;
            foreach (var collection in Collections)
            {
                if (isFirst == false)
                    writer.WriteComma();

                isFirst = false;
                writer.WriteString((collection));
            }

            writer.WriteEndArray();
            writer.WriteComma();

            writer.WritePropertyName(nameof(LockMode));
            writer.WriteInteger((int)LockMode);
            writer.WriteComma();

            writer.WritePropertyName(nameof(Priority));
            writer.WriteInteger((int)Priority);
            writer.WriteComma();

            PersistFields(context, writer);

            writer.WriteEndObject();
        }

        public bool ContainsField(string name)
        {
            return MapFields.ContainsKey(name);
        }

        protected abstract void PersistFields(JsonOperationContext context, BlittableJsonTextWriter writer);

        protected internal abstract IndexDefinition GetOrCreateIndexDefinitionInternal();

        public abstract IndexDefinitionCompareDifferences Compare(IndexDefinitionBase indexDefinition);

        public abstract IndexDefinitionCompareDifferences Compare(IndexDefinition indexDefinition);

        public Dictionary<string, IndexFieldBase> MapFields { get; protected set; }

        public Dictionary<string, IndexField> IndexFields { get;  set; }
    }

    public abstract class IndexDefinitionBase<T> : IndexDefinitionBase where T : IndexFieldBase 
    {
        protected const string MetadataFileName = "metadata";

        protected static readonly Slice DefinitionSlice;

        private int? _cachedHashCode;

        protected IndexDefinitionBase(string name, HashSet<string> collections, IndexLockMode lockMode, IndexPriority priority, T[] mapFields)
        {
            Name = name;
            Collections = collections;

            MapFields = new Dictionary<string, IndexFieldBase>(StringComparer.Ordinal);
            IndexFields = new Dictionary<string, IndexField>(StringComparer.Ordinal);

            foreach (var field in mapFields)
            {
                MapFields.Add(field.Name, field);

                if ((object)field is AutoIndexField autoField)
                {
                    foreach (var indexField in autoField.ToIndexFields())
                    {
                        IndexFields.Add(indexField.Name, indexField);
                    }
                }
                else if ((object)field is IndexField indexField)
                    IndexFields.Add(indexField.Name, indexField);
            }
            
            LockMode = lockMode;
            Priority = priority;
        }

        static IndexDefinitionBase()
        {
            using (StorageEnvironment.GetStaticContext(out var ctx))
            {
                Slice.From(ctx, "Definition", ByteStringType.Immutable, out DefinitionSlice);
            }
        }

        public override void Persist(TransactionOperationContext context, StorageEnvironmentOptions options)
        {
            var tree = context.Transaction.InnerTransaction.CreateTree("Definition");
            using (var stream = new MemoryStream())
            using (var writer = new BlittableJsonTextWriter(context, stream))
            {
                Persist(context, writer);

                writer.Flush();

                stream.Position = 0;

                if (options is StorageEnvironmentOptions.DirectoryStorageEnvironmentOptions)
                {
                    using (var metadata = File.Open(options.BasePath.Combine(MetadataFileName).FullPath, FileMode.Create))
                    {
                        if (options.EncryptionEnabled)
                        {
                            EncryptStream(options, stream);
                        }

                        stream.CopyTo(metadata);
                        stream.Position = 0;
                    }
                }

                using (Slice.From(context.Allocator, stream.ToArray(), out Slice val))
                {
                    tree.Add(DefinitionSlice, val);
                }
            }
        }

        private static unsafe void EncryptStream(StorageEnvironmentOptions options, MemoryStream stream)
        {
            var data = stream.ToArray();
            var nonce = Sodium.GenerateRandomBuffer((int)Sodium.crypto_aead_xchacha20poly1305_ietf_npubbytes()); // 192-bit
            var encryptedData = new byte[data.Length + (int)Sodium.crypto_aead_xchacha20poly1305_ietf_abytes()]; // data length + 128-bit mac 

            fixed (byte* ctx = EncryptionContext)
            fixed (byte* pData = data)
            fixed (byte* pEncryptedData = encryptedData)
            fixed (byte* pNonce = nonce)
            fixed (byte* pKey = options.MasterKey)
            {
                var subKeyLen = Sodium.crypto_aead_xchacha20poly1305_ietf_keybytes();
                var subKey = stackalloc byte[(int)subKeyLen ];
            
                if (Sodium.crypto_kdf_derive_from_key(subKey, subKeyLen, (ulong)SodiumSubKeyId.IndexDef, ctx, pKey) != 0)
                    throw new InvalidOperationException("Unable to generate derived key");

                ulong cLen;
                var rc = Sodium.crypto_aead_xchacha20poly1305_ietf_encrypt(
                    pEncryptedData,
                    &cLen,
                    pData,
                    (ulong)data.Length,
                    null,
                    0,
                    null,
                    pNonce,
                    subKey
                );

                Debug.Assert(cLen <= (ulong)data.Length + (ulong)Sodium.crypto_aead_xchacha20poly1305_ietf_abytes());

                if (rc != 0)
                    throw new InvalidOperationException($"Unable to encrypt stream, rc={rc}");
            }

            // reset the stream and write the encrypted data to it
            stream.SetLength(0);
            stream.Write(encryptedData, 0, encryptedData.Length);
            stream.Write(nonce, 0, nonce.Length);
            stream.Position = 0;
        }

        protected static Stream GetIndexDefinitionStream(StorageEnvironment environment, Transaction tx)
        {
            var tree = tx.CreateTree("Definition");
            var result = tree.Read(DefinitionSlice);
            if (result == null)
                return null;

            var stream = result.Reader.AsStream();
            if (environment.Options.EncryptionEnabled)
            {
                using (stream)
                {
                    var ms = new MemoryStream();
                    result.Reader.AsStream().CopyTo(ms);
                    ms.Position = 0;
                    DecryptStream(environment.Options, ms);
                    return ms;
                }
            }
            return stream;
        }

        private static unsafe void DecryptStream(StorageEnvironmentOptions options, MemoryStream stream)
        {
            var buffer = stream.ToArray();
            var nonce = new byte[(int)Sodium.crypto_aead_xchacha20poly1305_ietf_npubbytes()];
            var data = new byte[buffer.Length - nonce.Length];

            Array.Copy(buffer, 0, data, 0, buffer.Length - nonce.Length);
            Array.Copy(buffer, buffer.Length - nonce.Length, nonce, 0, nonce.Length);

            var decryptedData = new byte[data.Length - (int)Sodium.crypto_aead_xchacha20poly1305_ietf_abytes()];

            fixed (byte* ctx = EncryptionContext)
            fixed (byte* pData = data)
            fixed (byte* pDecryptedData = decryptedData)
            fixed (byte* pNonce = nonce)
            fixed (byte* pKey = options.MasterKey)
            {
                var subKeyLen = Sodium.crypto_aead_xchacha20poly1305_ietf_keybytes();
                var subKey = stackalloc byte[(int)subKeyLen ];
            
                if (Sodium.crypto_kdf_derive_from_key(subKey, subKeyLen, (ulong)SodiumSubKeyId.IndexDef, ctx, pKey) != 0)
                    throw new InvalidOperationException("Unable to generate derived key");
                
                ulong mLen;
                var rc = Sodium.crypto_aead_xchacha20poly1305_ietf_decrypt(
                    pDecryptedData,
                    &mLen,
                    null,
                    pData,
                    (ulong)data.Length,
                    null,
                    0,
                    pNonce,
                    subKey
                );

                Debug.Assert(mLen <= (ulong)data.Length - (ulong)Sodium.crypto_aead_xchacha20poly1305_ietf_abytes());

                if (rc != 0)
                    throw new InvalidOperationException($"Unable to decrypt stream, rc={rc}");
            }

            // reset the stream and write the decrypted data to it
            stream.SetLength(0);
            stream.Write(decryptedData, 0, decryptedData.Length);
            stream.Position = 0;
        }

        public virtual bool TryGetField(string field, out T value)
        {
            if (MapFields.TryGetValue(field, out var mapField))
            {
                value = mapField.As<T>();

                return true;
            }

            value = null;
            return false;
        }

        public virtual T GetField(string field)
        {
            return MapFields[field].As<T>();
        }

        public override int GetHashCode()
        {
            if (_cachedHashCode != null)
                return _cachedHashCode.Value;

            unchecked
            {
                var hashCode = MapFields?.GetDictionaryHashCode() ?? 0;
                hashCode = (hashCode * 397) ^ (Name?.GetHashCode() ?? 0);
                hashCode = (hashCode * 397) ^ (Collections?.GetEnumerableHashCode() ?? 0);

                hashCode = ComputeRestOfHash(hashCode);

                _cachedHashCode = hashCode;

                return hashCode;
            }
        }

        protected abstract int ComputeRestOfHash(int hashCode);

        public bool TryReadNameFromMetadataFile(TransactionOperationContext context, StorageEnvironmentOptions options, out string name)
        {
            var metadata = ReadMetadataFile(options);

            var metadataJson = context.ReadForDisk(metadata, string.Empty);

            return metadataJson.TryGet("Name", out name);
        }

        public Stream ReadMetadataFile(StorageEnvironmentOptions options)
        {
            try
            {
                var metadata = File.ReadAllBytes(options.BasePath.Combine(MetadataFileName).FullPath);
                var stream = new MemoryStream(metadata);

                if (options.EncryptionEnabled)
                {
                    DecryptStream(options, stream);
                }
                return stream;
            }
            catch (Exception e)
            {
                throw new InvalidOperationException($"Unable to read metadata file for index '{Name}' at {options.BasePath.Combine(MetadataFileName).FullPath}", e);
            }
        }

        public static bool TryReadIdFromDirectory(DirectoryInfo directory, out int indexId, out string indexName)
        {
            var index = directory.Name.IndexOf('-');
            var maybeId = index >= 0
                ? directory.Name.Substring(0, index)
                : directory.Name;

            if (int.TryParse(maybeId, out indexId) == false)
            {
                indexId = -1;
                indexName = null;
                return false;
            }

            indexName = directory.Name.Substring(index + 1);
            return true;
        }

        protected static string ReadName(BlittableJsonReaderObject reader)
        {
            if (reader.TryGet(nameof(Name), out string name) == false || String.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException("No persisted name");

            return name;
        }

        protected static string[] ReadCollections(BlittableJsonReaderObject reader)
        {
            if (reader.TryGet(nameof(Collections), out BlittableJsonReaderArray jsonArray) == false || jsonArray.Length == 0)
                throw new InvalidOperationException("No persisted collections");

            var result = new string[jsonArray.Length];
            for (var i = 0; i < jsonArray.Length; i++)
                result[i] = jsonArray.GetStringByIndex(i);

            return result;
        }

        protected static IndexLockMode ReadLockMode(BlittableJsonReaderObject reader)
        {
            if (reader.TryGet(nameof(LockMode), out int lockModeAsInt) == false)
                throw new InvalidOperationException("No persisted lock mode");

            return (IndexLockMode)lockModeAsInt;
        }

        protected static IndexPriority ReadPriority(BlittableJsonReaderObject reader)
        {
            if (reader.TryGet(nameof(Priority), out int priorityAsInt) == false)
                throw new InvalidOperationException("No persisted priority");

            return (IndexPriority)priorityAsInt;
        }
    }
}
