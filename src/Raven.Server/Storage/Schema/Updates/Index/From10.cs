﻿using Voron;
using Voron.Data;
using Voron.Data.BTrees;
using Voron.Data.Tables;

namespace Raven.Server.Storage.Schema.Updates.Index
{
    public unsafe class From10 : ISchemaUpdate
    {
        public bool Update(UpdateStep step)
        {
            return UpdateErrorTimestampsTreeInErrorsTable(step);
        }

        private static bool UpdateErrorTimestampsTreeInErrorsTable(UpdateStep step)
        {
            using (Slice.From(step.ReadTx.Allocator, "ErrorTimestamps", out var errorTimestampsSlice))
            {
                var oldErrorsTable = GetOldErrorsTable(step, errorTimestampsSlice, out var oldTableSchema);
                var oldErrorTimestampsTree = oldErrorsTable?.GetTree(oldTableSchema.Indexes[errorTimestampsSlice]);
                if (oldErrorTimestampsTree == null)
                {
                    // old tree doesn't exist
                    return true;
                }

                var newErrorTimestampsTree = GetNewErrorTimestampsTreeFromErrorsTable(step, errorTimestampsSlice);
                using (var oldErrorsTreeIterator = oldErrorTimestampsTree.Iterate(false))
                {
                    if (oldErrorsTreeIterator.Seek(Slices.BeforeAllKeys))
                    {
                        do
                        {
                            newErrorTimestampsTree.Add(
                                oldErrorsTreeIterator.CurrentKey,
                                oldErrorsTreeIterator.CreateReaderForCurrent().AsStream());

                        } while (oldErrorsTreeIterator.MoveNext());
                    }
                }

                step.WriteTx.DeleteTree(oldErrorTimestampsTree.Name);

                return true;
            }
        }

        private static Tree GetNewErrorTimestampsTreeFromErrorsTable(UpdateStep step, Slice errorTimestampsSlice)
        {
            var newTableSchema = new TableSchema();
            var indexDef = new TableSchema.SchemaIndexDef
            {
                StartIndex = 0,
                IsGlobal = false,
                Name = errorTimestampsSlice
            };
            newTableSchema.DefineIndex(indexDef);
            newTableSchema.Create(step.WriteTx, "Errors", 16);
            
            var newErrorsTable = step.WriteTx.OpenTable(newTableSchema, "Errors");

            var newErrorsTableTableTree = step.WriteTx.ReadTree("Errors", RootObjectType.Table);

            byte* ptr;
            using (var indexTree = Tree.Create(step.WriteTx.LowLevelTransaction, step.WriteTx, indexDef.Name, isIndexTree:true))
            {
                using (newErrorsTableTableTree.DirectAdd(indexDef.Name, sizeof(TreeRootHeader),out ptr))
                {
                    indexTree.State.CopyTo((TreeRootHeader*)ptr);
                }
            }

            var newErrorTimestampsIndexTree = newErrorsTable.GetTree(newTableSchema.Indexes[errorTimestampsSlice]);
            return newErrorTimestampsIndexTree;
        }

        private static Table GetOldErrorsTable(UpdateStep step, Slice errorTimestampsSlice, out TableSchema oldTableSchema)
        {
            oldTableSchema = new TableSchema();

            oldTableSchema.DefineIndex(new TableSchema.SchemaIndexDef
            {
                StartIndex = 0,
                IsGlobal = true,
                Name = errorTimestampsSlice
            });

            var oldErrorsTable = step.ReadTx.OpenTable(oldTableSchema, "Errors");
            return oldErrorsTable;
        }
    }
}
