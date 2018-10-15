﻿using System.Collections.Generic;
using Sparrow.Json.Parsing;

namespace Raven.Client.Documents.Operations
{
    public class BulkOperationResult : IOperationResult
    {
        public BulkOperationResult()
        {
            Details = new List<IBulkOperationDetails>();
        }

        public long Total { get; set; }
        
        public string Query { get; set; }

        public string Message => $"Processed {Total:#,#0} items.";

        public List<IBulkOperationDetails> Details { get; }

        public DynamicJsonValue ToJson()
        {
            var details = new DynamicJsonArray();
            if (Details != null && Details.Count > 0)
            {
                foreach (var detail in Details)
                    details.Add(detail.ToJson());
            }

            return new DynamicJsonValue(GetType())
            {
                [nameof(Total)] = Total,
                [nameof(Message)] = Message,
                [nameof(Details)] = details,
                [nameof(Query)] = Query
            };
        }

        public bool ShouldPersist => false;

        public class PatchDetails : IBulkOperationDetails
        {
            public string Id { get; set; }
            public string ChangeVector { get; set; }
            public PatchStatus Status { get; set; }

            public DynamicJsonValue ToJson()
            {
                return new DynamicJsonValue(GetType())
                {
                    [nameof(Id)] = Id,
                    [nameof(ChangeVector)] = ChangeVector,
                    [nameof(Status)] = Status
                };
            }
        }

        public class DeleteDetails : IBulkOperationDetails
        {
            public string Id { get; set; }
            public long? Etag { get; set; }

            public DynamicJsonValue ToJson()
            {
                return new DynamicJsonValue(GetType())
                {
                    [nameof(Id)] = Id,
                    [nameof(Etag)] = Etag
                };
            }
        }

        public class OperationDetails : IOperationDetailedDescription
        {
            public string Query { get; set; }

            DynamicJsonValue IOperationDetailedDescription.ToJson()
            {
                return new DynamicJsonValue(GetType())
                {
                    [nameof(Query)] = Query
                };
            }
        }
    }

    public interface IBulkOperationDetails
    {
        DynamicJsonValue ToJson();
    }
}
