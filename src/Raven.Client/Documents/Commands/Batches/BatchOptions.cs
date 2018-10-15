﻿using System;

namespace Raven.Client.Documents.Commands.Batches
{
    public class BatchOptions
    {
        public TimeSpan? RequestTimeout { get; set; }
        public ReplicationBatchOptions ReplicationOptions { get; set; }
        public IndexBatchOptions IndexOptions { get; set; }
    }

    public class IndexBatchOptions
    {
        public bool WaitForIndexes { get; set; }
        public TimeSpan WaitForIndexesTimeout { get; set; }
        public bool ThrowOnTimeoutInWaitForIndexes { get; set; }
        public string[] WaitForSpecificIndexes { get; set; }
    }

    public class ReplicationBatchOptions
    {
        public bool WaitForReplicas { get; set; }
        public int NumberOfReplicasToWaitFor { get; set; }
        public TimeSpan WaitForReplicasTimeout { get; set; }
        public bool Majority { get; set; }
        public bool ThrowOnTimeoutInWaitForReplicas { get; set; }
    }
}
