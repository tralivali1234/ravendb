﻿using Voron;
using Voron.Data;
using Voron.Data.Tables;

namespace Raven.Server.Storage.Schema.Updates.Index
{
    public class From11 : ISchemaUpdate
    {
        public bool Update(UpdateStep step)
        {
            using (Slice.From(step.ReadTx.Allocator, "ErrorTimestamps", out var errorTimestampsSlice))
            {
                var tableSchema = new TableSchema();

                tableSchema.DefineIndex(new TableSchema.SchemaIndexDef
                {
                    StartIndex = 0,
                    IsGlobal = false,
                    Name = errorTimestampsSlice
                });

                var tableTree = step.WriteTx.CreateTree("Errors", RootObjectType.Table);
                tableSchema.SerializeSchemaIntoTableTree(tableTree);

                return true;
            }
        }
    }
}