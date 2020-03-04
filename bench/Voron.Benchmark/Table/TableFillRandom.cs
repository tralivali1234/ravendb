﻿using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Attributes;
using Sparrow;
using Voron.Data.Tables;

namespace Voron.Benchmark.Table
{
    public class TableFillRandom : StorageBenchmark
    {
        private static readonly Slice TableNameSlice;
        private static readonly Slice SchemaPKNameSlice;
        private static readonly TableSchema Schema;

        /// <summary>
        /// We have one list per Transaction to carry out. Each one of these 
        /// lists has exactly the number of items we want to insert, with
        /// distinct keys for each one of them.
        /// 
        /// It is important for them to be lists, this way we can ensure the
        /// order of insertions remains the same throughout runs.
        /// </summary>
        private List<TableValueBuilder>[] _valueBuilders;

        /// <summary>
        /// Length of the keys to be inserted when filling randomly (bytes)
        /// </summary>
        [Params(100)]
        public int KeyLength { get; set; } = 100;

        /// <summary>
        /// Random seed. If -1, uses time for seeding.
        /// </summary>
        [Params(-1)]
        public int RandomSeed { get; set; } = -1;

        static TableFillRandom()
        {
            Slice.From(Configuration.Allocator, "TestTable1", ByteStringType.Immutable, out TableNameSlice);
            Slice.From(Configuration.Allocator, "TestSchema1", ByteStringType.Immutable, out SchemaPKNameSlice);

            Schema = new TableSchema()
                .DefineKey(new TableSchema.SchemaIndexDef
                {
                    StartIndex = 0,
                    Count = 0,
                    IsGlobal = false,
                    Name = SchemaPKNameSlice,
                    Type = TableIndexType.BTree
                });
        }

        [GlobalSetup]
        public override void Setup()
        {
            base.Setup();

            using (var tx = Env.WriteTransaction())
            {
                Schema.Create(tx, TableNameSlice, 16);
                tx.Commit();
            }

            var totalPairs = Utils.GenerateUniqueRandomSlicePairs(
                NumberOfTransactions * NumberOfRecordsPerTransaction,
                KeyLength,
                RandomSeed == -1 ? null as int? : RandomSeed);

            _valueBuilders = new List<TableValueBuilder>[NumberOfTransactions];

            for (var i = 0; i < NumberOfTransactions; ++i)
            {
                _valueBuilders[i] = new List<TableValueBuilder>();

                foreach (var pair in totalPairs.Take(NumberOfRecordsPerTransaction))
                {
                    _valueBuilders[i].Add(new TableValueBuilder
                    {
                        pair.Item1,
                        pair.Item2
                    });
                }

                totalPairs.RemoveRange(0, NumberOfRecordsPerTransaction);
            }
        }

        [Benchmark(OperationsPerInvoke = Configuration.RecordsPerTransaction * Configuration.Transactions)]
        public void FillRandomOneTransaction()
        {
            using (var tx = Env.WriteTransaction())
            {
                var table = tx.OpenTable(Schema, TableNameSlice);

                for (var i = 0; i < NumberOfTransactions; i++)
                {
                    foreach (var value in _valueBuilders[i])
                    {
                        table.Insert(value);
                    }
                }

                tx.Commit();
            }
        }

        [Benchmark(OperationsPerInvoke = Configuration.RecordsPerTransaction * Configuration.Transactions)]
        public void FillRandomMultipleTransactions()
        {
            for (var i = 0; i < NumberOfTransactions; i++)
            {
                using (var tx = Env.WriteTransaction())
                {
                    var table = tx.OpenTable(Schema, TableNameSlice);

                    foreach (var value in _valueBuilders[i])
                    {
                        table.Insert(value);
                    }

                    tx.Commit();
                }
            }
        }
    }
}
