﻿using Voron;
using Xunit;

namespace FastTests.Voron.Bugs
{
    public class RavenDB_8036 : StorageTest
    {
        protected override void Configure(StorageEnvironmentOptions options)
        {
            options.ManualFlushing = true;
        }

        [Fact]
        public void Flushing_should_not_throw_on_freeing_scratch_page_async_commit()
        {
            var tx1 = Env.WriteTransaction();

            try
            {
                var allocatePage = tx1.LowLevelTransaction.AllocatePage(1);

                using (var tx2 = tx1.BeginAsyncCommitAndStartNewTransaction())
                {
                    using (tx1)
                    {
                        tx2.LowLevelTransaction.FreePage(allocatePage.PageNumber); // free the same page that was allocated by the parent tx

                        tx1.EndAsyncCommit();
                    }

                    tx1 = null;

                    tx2.Commit();
                }
            }
            finally
            {
                tx1?.Dispose();
            }

            using (var tx = Env.WriteTransaction())
            {
                // just to increment transaction id

                tx.LowLevelTransaction.ModifyPage(0);
                tx.Commit();
            }

            Env.FlushLogToDataFile();
        }

        [Fact]
        public void Flushing_should_not_throw_on_freeing_scratch_page()
        {
            long pageNumber;

            using (var tx1 = Env.WriteTransaction())
            {
                pageNumber = tx1.LowLevelTransaction.AllocatePage(1).PageNumber;
                tx1.Commit();
            }

            using (var tx2 = Env.WriteTransaction())
            {
                tx2.LowLevelTransaction.FreePage(pageNumber); // free the same page that was allocated by the prev tx

                tx2.Commit();
            }

            using (var tx = Env.WriteTransaction())
            {
                // just to increment transaction id

                tx.LowLevelTransaction.ModifyPage(0);
                tx.Commit();
            }

            Env.FlushLogToDataFile();
        }
    }
}
