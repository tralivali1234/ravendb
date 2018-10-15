﻿using System.Collections;
using System.Collections.Generic;

namespace Raven.Server.Documents.ETL.Providers.SQL.Enumerators
{
    public class TombstonesToSqlItems : IEnumerator<ToSqlItem>
    {
        private readonly IEnumerator<Tombstone> _tombstones;
        private readonly string _collection;

        public TombstonesToSqlItems(IEnumerator<Tombstone> tombstones, string collection)
        {
            _tombstones = tombstones;
            _collection = collection;
        }

        public bool MoveNext()
        {
            if (_tombstones.MoveNext() == false)
                return false;

            Current = new ToSqlItem(_tombstones.Current, _collection);

            return true;
        }

        public void Reset()
        {
            throw new System.NotImplementedException();
        }

        object IEnumerator.Current => Current;

        public void Dispose()
        {
        }

        public ToSqlItem Current { get; private set; }
    }
}
