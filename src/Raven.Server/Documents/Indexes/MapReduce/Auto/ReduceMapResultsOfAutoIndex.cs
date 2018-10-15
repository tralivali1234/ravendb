﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Raven.Client.Documents.Indexes;
using Raven.Server.Json;
using Raven.Server.ServerWide.Context;
using Raven.Server.Utils;
using Sparrow;
using Sparrow.Json;
using Sparrow.Json.Parsing;

namespace Raven.Server.Documents.Indexes.MapReduce.Auto
{
    public unsafe class ReduceMapResultsOfAutoIndex : ReduceMapResultsBase<AutoMapReduceIndexDefinition>
    {
        public ReduceMapResultsOfAutoIndex(Index index, AutoMapReduceIndexDefinition indexDefinition, IndexStorage indexStorage, MetricCounters metrics, MapReduceIndexingContext mapReduceContext)
            : base(index, indexDefinition, indexStorage, metrics, mapReduceContext)
        {
        }

        protected override AggregationResult AggregateOn(List<BlittableJsonReaderObject> aggregationBatch, TransactionOperationContext indexContext, IndexingStatsScope stats, CancellationToken token)
        {
            var aggregatedResultsByReduceKey = new Dictionary<BlittableJsonReaderObject, Dictionary<string, PropertyResult>>(ReduceKeyComparer.Instance);

            foreach (var obj in aggregationBatch)
            {
                token.ThrowIfCancellationRequested();

                var aggregatedResult = new Dictionary<string, PropertyResult>();

                foreach (var propertyName in obj.GetPropertyNames())
                {
                    if (_indexDefinition.TryGetField(propertyName, out var indexField))
                    {
                        switch (indexField.Aggregation)
                        {
                            case AggregationOperation.None:
                                if (obj.TryGet(propertyName, out object groupByValue) == false)
                                    throw new InvalidOperationException($"Could not read group by value of '{propertyName}' property");

                                aggregatedResult[propertyName] = new PropertyResult
                                {
                                    ResultValue = groupByValue
                                };
                                break;
                            case AggregationOperation.Count:
                            case AggregationOperation.Sum:

                                if (obj.TryGetMember(propertyName, out var value) == false)
                                    throw new InvalidOperationException($"Could not read numeric value of '{propertyName}' property");

                                if (value == null)
                                {
                                    aggregatedResult[propertyName] = PropertyResult.NullNumber();
                                    break;
                                }

                                var numberType = BlittableNumber.Parse(value, out var doubleValue, out var longValue);

                                var aggregate = PropertyResult.Number(numberType);

                                switch (numberType)
                                {
                                    case NumberParseResult.Double:
                                        aggregate.ResultValue = aggregate.DoubleValue = doubleValue;
                                        break;
                                    case NumberParseResult.Long:
                                        aggregate.ResultValue = aggregate.LongValue = longValue;
                                        break;
                                    default:
                                        throw new ArgumentOutOfRangeException($"Unknown number type: {numberType}");
                                }
                                aggregatedResult[propertyName] = aggregate;
                                break;
                            //case FieldMapReduceOperation.None:
                            default:
                                throw new ArgumentOutOfRangeException($"Unhandled field type '{indexField.Aggregation}' to aggregate on");
                        }
                    }

                    if (_indexDefinition.GroupByFields.ContainsKey(propertyName) == false)
                    {
                        // we want to reuse existing entry to get a reduce key

                        if (obj.Modifications == null)
                            obj.Modifications = new DynamicJsonValue(obj);

                        obj.Modifications.Remove(propertyName);
                    }
                }

                var reduceKey = indexContext.ReadObject(obj, "reduce key");

                if (aggregatedResultsByReduceKey.TryGetValue(reduceKey, out Dictionary<string, PropertyResult> existingAggregate) == false)
                {
                    aggregatedResultsByReduceKey.Add(reduceKey, aggregatedResult);
                }
                else
                {
                    reduceKey.Dispose();

                    foreach (var propertyResult in existingAggregate)
                    {
                        propertyResult.Value.Aggregate(aggregatedResult[propertyResult.Key]);
                    }
                }
            }

            var resultObjects = new List<Document>(aggregatedResultsByReduceKey.Count);

            foreach (var aggregationResult in aggregatedResultsByReduceKey)
            {
                aggregationResult.Key.Dispose();

                var djv = new DynamicJsonValue();

                foreach (var aggregate in aggregationResult.Value)
                {
                    djv[aggregate.Key] = aggregate.Value.ResultValue;
                }

                resultObjects.Add(new Document { Data = indexContext.ReadObject(djv, "map/reduce") });
            }

            return new AggregatedDocuments(resultObjects);
        }

        private class PropertyResult
        {
            private NumberParseResult? _numberType;

            private bool _isNullNumber;

            public object ResultValue;

            public long LongValue;

            public double DoubleValue;

            public static PropertyResult NullNumber()
            {
                return new PropertyResult
                {
                    _isNullNumber = true,
                    ResultValue = 0
                };
            }

            public static PropertyResult Number(NumberParseResult type)
            {
                return new PropertyResult
                {
                    _numberType = type
                };
            }

            public void Aggregate(PropertyResult other)
            {
                if (_numberType != null)
                {
                    if (other._isNullNumber)
                        return;

                    Debug.Assert(other._numberType != null);

                    switch (_numberType.Value)
                    {
                        case NumberParseResult.Double:
                            ResultValue = DoubleValue += other.DoubleValue;
                            break;
                        case NumberParseResult.Long:
                            ResultValue = LongValue += other.LongValue;
                            break;
                        default:
                            throw new ArgumentOutOfRangeException($"Unknown number type: {_numberType.Value}");
                    }
                }
                else if (_isNullNumber)
                {
                    _numberType = other._numberType;
                    ResultValue = other.ResultValue;
                    DoubleValue = other.DoubleValue;
                    LongValue = other.LongValue;
                    _isNullNumber = false;
                }
            }
        }

        private class ReduceKeyComparer : IEqualityComparer<BlittableJsonReaderObject>
        {
            public static readonly ReduceKeyComparer Instance = new ReduceKeyComparer();

            public bool Equals(BlittableJsonReaderObject x, BlittableJsonReaderObject y)
            {
                if (x.Size != y.Size)
                    return false;

                return Memory.Compare(x.BasePointer, y.BasePointer, x.Size) == 0;
            }

            public int GetHashCode(BlittableJsonReaderObject obj)
            {
                return 1; // calculated hash of a reduce key is the same for all entries in a tree, we have to force Equals method to be called
            }
        }
    }
}
