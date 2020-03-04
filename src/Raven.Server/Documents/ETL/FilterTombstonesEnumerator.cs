﻿using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using Raven.Server.Documents.ETL.Stats;
using Raven.Server.ServerWide.Context;

namespace Raven.Server.Documents.ETL
{
    public class FilterTombstonesEnumerator : IEnumerator<Tombstone>
    {
        private readonly IEnumerator<Tombstone> _tombstones;
        private readonly EtlStatsScope _stats;
        private readonly Tombstone.TombstoneType _tombstoneType;
        private readonly DocumentsOperationContext _context;
        private readonly List<string> _fromCollections;

        public FilterTombstonesEnumerator(IEnumerator<Tombstone> tombstones, EtlStatsScope stats, Tombstone.TombstoneType tombstoneType, DocumentsOperationContext context,
            List<string> fromCollections = null)
        {
            _tombstones = tombstones;
            _stats = stats;
            _tombstoneType = tombstoneType;
            _context = context;
            _fromCollections = fromCollections;
        }

        public bool MoveNext()
        {
            Current = null;

            while (_tombstones.MoveNext())
            {
                var current = _tombstones.Current;
                if (current.Type == _tombstoneType)
                {
                    if (_fromCollections == null)
                    {
                        Current = current;
                        return true;
                    }

                    var tombstoneCollection = (string)current.Collection;

                    if (string.IsNullOrEmpty(tombstoneCollection))
                    {
                        if (_tombstoneType == Tombstone.TombstoneType.Attachment)
                        {
                            var documentId = AttachmentsStorage.ExtractDocIdAndAttachmentNameFromTombstone(_context, current.LowerId).DocId;
                            var document = _context.DocumentDatabase.DocumentsStorage.Get(_context, documentId);

                            if (document != null) // document could be deleted, no need to send DELETE of tombstone, we can filter it out
                            {
                                tombstoneCollection = _context.DocumentDatabase.DocumentsStorage.ExtractCollectionName(_context, document.Data).Name;
                            }
                        }
                        else
                        {
                            ThrowUnexpectedNullCollectionTombstone(_tombstoneType);
                        }
                    }

                    if (_fromCollections.Contains(tombstoneCollection, StringComparer.OrdinalIgnoreCase))
                    {
                        Current = current;
                        return true;
                    }
                }

                _stats.RecordChangeVector(current.ChangeVector);
                _stats.RecordLastFilteredOutEtag(current.Etag);
            }

            return false;
        }

        public void Reset()
        {
            throw new NotSupportedException();
        }

        public Tombstone Current { get; private set; }

        object IEnumerator.Current => Current;

        public void Dispose()
        {
        }

        private static void ThrowUnexpectedNullCollectionTombstone(Tombstone.TombstoneType tombstoneType)
        {
            throw new NotSupportedException($"Unexpected 'null' collection of {tombstoneType} tombstone while filtering by collection is specified");
        }
    }
}
