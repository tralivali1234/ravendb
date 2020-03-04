using Sparrow;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Voron;
using Voron.Data.Tables;
using Voron.Util.Conversion;

namespace FastTests.Voron.Tables
{
    public class TableStorageTest : StorageTest
    {
        public static readonly Slice EtagsSlice;
        public static readonly Slice EtagAndCollectionSlice;

        protected TableSchema DocsSchema;

        static TableStorageTest()
        {
            using (StorageEnvironment.GetStaticContext(out var ctx))
            {
                Slice.From(ctx, "Etags", ByteStringType.Immutable, out EtagsSlice);
                Slice.From(ctx, "Etag&Collection", ByteStringType.Immutable, out EtagAndCollectionSlice);
            }
        }

        protected override void Configure(StorageEnvironmentOptions options)
        {
            base.Configure(options);

            DocsSchema = new TableSchema()
                .DefineIndex(new TableSchema.SchemaIndexDef
                {
                    StartIndex = 2,
                    Count = 1,
                    Name = EtagsSlice
                })
                .DefineIndex(new TableSchema.SchemaIndexDef
                {
                    StartIndex = 1,
                    Count = 2,
                    Name = EtagAndCollectionSlice
                })
                .DefineKey(new TableSchema.SchemaIndexDef
                {
                    StartIndex = 0,
                    Count = 1
                });
        }

        public unsafe void SetHelper(Table table, params object[] args)
        {
            var handles1 = new List<GCHandle>();

            var builder = new TableValueBuilder();
            foreach (var o in args)
            {
                byte[] buffer;
                GCHandle gcHandle;

                var s = o as string;
                if (s != null)
                {
                    buffer = Encoding.UTF8.GetBytes(s);
                    gcHandle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                    builder.Add((byte*)gcHandle.AddrOfPinnedObject(), buffer.Length);
                    handles1.Add(gcHandle);
                    continue;
                }

                if (o is Slice)
                {
                    var slice = (Slice)o;
                    builder.Add(slice.Content.Ptr, slice.Content.Length);

                    continue;
                }

                var stream = o as MemoryStream;
                if (stream != null)
                {
                    buffer = stream.ToArray();
                    gcHandle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                    builder.Add((byte*)gcHandle.AddrOfPinnedObject(), buffer.Length);
                    handles1.Add(gcHandle);

                    continue;
                }

                var l = (long)o;
                buffer = EndianBitConverter.Big.GetBytes(l);
                gcHandle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                builder.Add((byte*)gcHandle.AddrOfPinnedObject(), buffer.Length);
                handles1.Add(gcHandle);
            }

            var handles = handles1;

            table.Set(builder);

            foreach (var gcHandle in handles)
            {
                gcHandle.Free();
            }
        }
    }
}
