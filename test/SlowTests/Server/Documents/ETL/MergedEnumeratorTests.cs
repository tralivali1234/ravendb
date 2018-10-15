﻿using System.Collections.Generic;
using FastTests;
using Raven.Server.Documents.ETL;
using Raven.Server.Documents.ETL.Stats;
using Xunit;

namespace SlowTests.Server.Documents.ETL
{
    public class MergedEnumeratorTests : NoDisposalNeeded
    {
        [Fact]
        public void Returns_items_in_order()
        {
            using (var merged = new ExtractedItemsEnumerator<Item>(new EtlStatsScope(new EtlRunStats()), EtlItemType.Document))
            {
                var items1 = new List<Item>
                {
                    new Item(1), new Item(3), new Item(4)
                };

                var items2 = new List<Item>
                {
                    new Item(2), new Item(5), new Item(6)
                };

                merged.AddEnumerator(items1.GetEnumerator());
                merged.AddEnumerator(items2.GetEnumerator());

                for (var i = 1; i <= 6; i++)
                {
                    Assert.True(merged.MoveNext());
                    Assert.Equal(i, merged.Current.Etag);
                }
            }
        }

        private class Item : ExtractedItem
        {
            public Item(long etag)
            {
                Etag = etag;
            }
        }
    }
}
