﻿using System;
using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Attributes;
using Sparrow;

namespace Voron.Benchmark.BTree
{
    public class BTreeFillRandom : StorageBenchmark
    {
        private static readonly Slice TreeNameSlice;

        /// <summary>
        /// We have one list per Transaction to carry out. Each one of these 
        /// lists has exactly the number of items we want to insert, with
        /// distinct keys for each one of them.
        /// 
        /// It is important for them to be lists, this way we can ensure the
        /// order of insertions remains the same throughout runs.
        /// </summary>
        private List<Tuple<Slice, Slice>>[] _pairs;

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

        static BTreeFillRandom()
        {
            Slice.From(Configuration.Allocator, "TestTreeRandomFill", ByteStringType.Immutable, out TreeNameSlice);
        }

        [GlobalSetup]
        public override void Setup()
        {
            base.Setup();

            using (var tx = Env.WriteTransaction())
            {
                tx.CreateTree(TreeNameSlice);
                tx.Commit();
            }

            var totalPairs = Utils.GenerateUniqueRandomSlicePairs(
                NumberOfTransactions * NumberOfRecordsPerTransaction,
                KeyLength,
                RandomSeed == -1 ? null as int? : RandomSeed);

            _pairs = new List<Tuple<Slice, Slice>>[NumberOfTransactions];

            for (var i = 0; i < NumberOfTransactions; ++i)
            {
                _pairs[i] = totalPairs.Take(NumberOfRecordsPerTransaction).ToList();
                totalPairs.RemoveRange(0, NumberOfRecordsPerTransaction);
            }
        }

        [Benchmark(OperationsPerInvoke = Configuration.RecordsPerTransaction * Configuration.Transactions)]
        public void FillRandomOneTransaction()
        {
            using (var tx = Env.WriteTransaction())
            {
                var tree = tx.CreateTree(TreeNameSlice);

                for (var i = 0; i < NumberOfTransactions; i++)
                {
                    foreach (var pair in _pairs[i])
                    {
                        tree.Add(pair.Item1, pair.Item2);
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
                    var tree = tx.CreateTree(TreeNameSlice);

                    foreach (var pair in _pairs[i])
                    {
                        tree.Add(pair.Item1, pair.Item2);
                    }

                    tx.Commit();
                }
            }
        }
    }
}
