﻿using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Voron.Debugging;
using Voron.Impl;

namespace Voron.Util
{
    public class ActiveTransactions
    {
        public class Node
        {
            public LowLevelTransaction Transaction;
        }

        public class DynamicArray 
        {
            public Node[] Items = new Node[4];
            public int Length;

            public void Add(Node node)
            {
                if (Length < Items.Length)
                {
                    Items[Length++] = node;
                    return;
                }
                var newItems = new Node[Items.Length*2];
                Array.Copy(Items, newItems, Items.Length);
                Items = newItems;
                Items[Length++] = node;
            }
        }
        /// <summary>
        /// Note that this is using thread local variable, but a transaction can _move_ between threads!
        /// </summary>
        private readonly ThreadLocal<DynamicArray> _activeTransactions = new ThreadLocal<DynamicArray>(
            () => new DynamicArray(),
            trackAllValues: true);      

        public long OldestTransaction
        {
            get
            {
                var oldestTx = long.MaxValue;
                // ReSharper disable once LoopCanBeConvertedToQuery
                foreach (var threadActiveTransactions in _activeTransactions.Values)
                {
                    if(threadActiveTransactions == null)
                        continue;

                    var array = threadActiveTransactions.Items;
                    var len = Math.Min(array.Length, threadActiveTransactions.Length);
                    for (int i = 0; i < len; i++)
                    {
                        var node = array[i];
                        // ReSharper disable once UseNullPropagation
                        if (node == null)
                            continue;

                        var activeTransactionTransaction = node.Transaction;
                        // ReSharper disable once UseNullPropagation
                        if (activeTransactionTransaction == null)
                            continue;

                        if (oldestTx > activeTransactionTransaction.Id)
                            oldestTx = activeTransactionTransaction.Id;
                    }
                }
                
                if (oldestTx == long.MaxValue)
                    oldestTx = 0;
                
                return oldestTx;
            }
        }

        public void Add(LowLevelTransaction tx)
        {
            var threadActiveTxs = _activeTransactions.Value;
            for (int i = 0; i < threadActiveTxs.Length; i++)
            {
                var node = threadActiveTxs.Items[i];
              
                if (node.Transaction != null)
                    continue;

                tx.ActiveTransactionNode = node;
                node.Transaction = tx;
                return;
            }
            tx.ActiveTransactionNode = new Node
            {
                Transaction = tx
            };
            threadActiveTxs.Add(tx.ActiveTransactionNode);
        }

        internal List<ActiveTransaction> AllTransactions
        {
            get
            {
                var list = new List<ActiveTransaction>();

                foreach (var threadActiveTransactions in _activeTransactions.Values)
                {
                    var array = threadActiveTransactions.Items;
                    var len = Math.Min(array.Length, threadActiveTransactions.Length);
                    for (int i = 0; i < len; i++)
                    {
                        var node = array[i];
                        var transaction = node?.Transaction;
                        if (transaction == null)
                            continue;

                        list.Add(new ActiveTransaction
                        {
                            Id = transaction.Id,
                            Flags = transaction.Flags,
                            AsyncCommit = transaction.AsyncCommit != null
                        });
                    }
                }

                return list;
            }
        }

        internal List<LowLevelTransaction> AllTransactionsInstances
        {
            get
            {
                var list = new List<LowLevelTransaction>();

                foreach (var threadActiveTransactions in _activeTransactions.Values)
                {
                    var array = threadActiveTransactions.Items;
                    var len = Math.Min(array.Length, threadActiveTransactions.Length);
                    for (int i = 0; i < len; i++)
                    {
                        var node = array[i];
                        var transaction = node?.Transaction;
                        if (transaction == null)
                            continue;

                        list.Add(transaction);
                    };
                    }
                return list;
            }
        }

        public bool Contains(LowLevelTransaction tx)
        {
            return tx.ActiveTransactionNode.Transaction == tx;
        }

        public bool TryRemove(LowLevelTransaction tx)
        {
            if (tx.ActiveTransactionNode.Transaction != tx)
                return false;

            tx.ActiveTransactionNode.Transaction = null;
            tx.ActiveTransactionNode = null;

            return true;
        }
    }
}
