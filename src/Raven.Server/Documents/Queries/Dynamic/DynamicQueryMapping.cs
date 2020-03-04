﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Raven.Client;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Indexes.Spatial;
using Raven.Client.Exceptions;
using Raven.Server.Documents.Indexes;
using Raven.Server.Documents.Indexes.Auto;
using Raven.Server.Documents.Indexes.MapReduce.Auto;
using Raven.Server.Documents.Queries.AST;

namespace Raven.Server.Documents.Queries.Dynamic
{
    public class DynamicQueryMapping
    {
        public string ForCollection { get; private set; }

        public Dictionary<string, DynamicQueryMappingItem> MapFields { get; private set; }

        public Dictionary<string, DynamicQueryMappingItem> GroupByFields { get; private set; }

#if FEATURE_HIGHLIGHTING
        public string[] HighlightedFields { get; private set; }
#endif

        public bool IsGroupBy { get; private set; }

        public List<Index> SupersededIndexes;

        public AutoIndexDefinitionBase CreateAutoIndexDefinition()
        {
            if (IsGroupBy == false)
            {
                return new AutoMapIndexDefinition(ForCollection, MapFields.Values.Select(field =>
                    {
                        var indexField = new AutoIndexField
                        {
                            Name = field.Name,
                            Storage = FieldStorage.No,
                            Indexing = AutoFieldIndexing.Default,
                            HasQuotedName = field.Name.IsQuoted
                        };

                        if (field.IsFullTextSearch)
                            indexField.Indexing |= AutoFieldIndexing.Search;

                        if (field.IsExactSearch)
                            indexField.Indexing |= AutoFieldIndexing.Exact;

                        if (field.Spatial != null)
                            indexField.Spatial = new AutoSpatialOptions(field.Spatial);

                        return indexField;
                    }
                ).ToArray());
            }

            if (GroupByFields.Count == 0)
                throw new InvalidOperationException("Invalid dynamic map-reduce query mapping. There is no group by field specified.");

            return new AutoMapReduceIndexDefinition(ForCollection, MapFields.Values.Select(field =>
                {
                    var indexField = new AutoIndexField
                    {
                        Name = field.Name,
                        Storage = FieldStorage.No,
                        Aggregation = field.AggregationOperation,
                        Indexing = AutoFieldIndexing.Default,
                        HasQuotedName = field.Name.IsQuoted
                    };

                    if (field.IsFullTextSearch)
                        indexField.Indexing |= AutoFieldIndexing.Search;

                    if (field.IsExactSearch)
                        indexField.Indexing |= AutoFieldIndexing.Exact;

                    if (field.Spatial != null)
                        indexField.Spatial = new AutoSpatialOptions(field.Spatial);

                    return indexField;
                }).ToArray(),
                GroupByFields.Values.Select(field =>
                {
                    var indexField = new AutoIndexField
                    {
                        Name = field.Name,
                        Storage = FieldStorage.No,
                        Indexing = AutoFieldIndexing.Default,
                        HasQuotedName = field.Name.IsQuoted,
                        GroupByArrayBehavior = field.GroupByArrayBehavior
                    };

                    if (field.IsFullTextSearch)
                        indexField.Indexing |= AutoFieldIndexing.Search;

                    if (field.IsExactSearch)
                        indexField.Indexing |= AutoFieldIndexing.Exact;

                    if (field.Spatial != null)
                        indexField.Spatial = new AutoSpatialOptions(field.Spatial);

                    return indexField;
                }).ToArray());
        }

        public void ExtendMappingBasedOn(AutoIndexDefinitionBase definitionOfExistingIndex)
        {
            Debug.Assert(definitionOfExistingIndex is AutoMapIndexDefinition || definitionOfExistingIndex is AutoMapReduceIndexDefinition, "Dynamic queries are handled only by auto indexes");

            switch (definitionOfExistingIndex)
            {
                case AutoMapIndexDefinition def:
                    Update(MapFields, def.MapFields, isGroupBy: false);
                    break;
                case AutoMapReduceIndexDefinition def:
                    Update(MapFields, def.MapFields, isGroupBy: false);
                    Update(GroupByFields, def.GroupByFields, isGroupBy: true);
                    break;
            }

            void Update<T>(Dictionary<string, DynamicQueryMappingItem> fields, Dictionary<string, T> indexFields, bool isGroupBy) where T : IndexFieldBase
            {
                foreach (var f in indexFields.Values)
                {
                    var indexField = f.As<AutoIndexField>();

                    if (fields.TryGetValue(indexField.Name, out var queryField))
                    {
                        var isFullTextSearch = queryField.IsFullTextSearch || indexField.Indexing.HasFlag(AutoFieldIndexing.Search);
                        var isExactSearch = queryField.IsExactSearch || indexField.Indexing.HasFlag(AutoFieldIndexing.Exact);

                        var field = isGroupBy == false
                            ? DynamicQueryMappingItem.Create(
                                queryField.Name,
                                queryField.AggregationOperation,
                                isFullTextSearch: isFullTextSearch,
                                isExactSearch: isExactSearch,
                                spatial: queryField.Spatial ?? indexField.Spatial)
                            : DynamicQueryMappingItem.CreateGroupBy(
                                queryField.Name,
                                queryField.GroupByArrayBehavior,
                                isSpecifiedInWhere: queryField.IsSpecifiedInWhere,
                                isFullTextSearch: isFullTextSearch,
                                isExactSearch: isExactSearch);

                        fields[queryField.Name] = field;
                    }
                    else
                    {
                        if (isGroupBy)
                            throw new InvalidOperationException("Cannot create new GroupBy field when extending mapping");

                        fields.Add(indexField.Name, DynamicQueryMappingItem.Create(
                            new QueryFieldName(indexField.Name, indexField.HasQuotedName),
                            indexField.Aggregation,
                            isFullTextSearch: indexField.Indexing.HasFlag(AutoFieldIndexing.Search),
                            isExactSearch: indexField.Indexing.HasFlag(AutoFieldIndexing.Exact),
                            spatial: indexField.Spatial));
                    }
                }
            }
        }

        public static DynamicQueryMapping Create(IndexQueryServerSide query)
        {
            var result = new DynamicQueryMapping
            {
                ForCollection = query.Metadata.CollectionName
            };

            var mapFields = new Dictionary<string, DynamicQueryMappingItem>(StringComparer.Ordinal);

            foreach (var field in query.Metadata.IndexFieldNames)
            {
                if (field == Constants.Documents.Indexing.Fields.DocumentIdFieldName)
                    continue;

                mapFields[field] = DynamicQueryMappingItem.Create(field, AggregationOperation.None, query.Metadata.WhereFields);
            }

            if (query.Metadata.OrderBy != null)
            {
                foreach (var field in query.Metadata.OrderBy)
                {
                    if (field.OrderingType == OrderByFieldType.Random)
                        continue;

                    if (field.OrderingType == OrderByFieldType.Score)
                        continue;

                    if (field.Name == Constants.Documents.Indexing.Fields.DocumentIdFieldName)
                        continue;

                    var fieldName = field.Name;

#if FEATURE_CUSTOM_SORTING
                    if (fieldName.Value.StartsWith(Constants.Documents.Indexing.Fields.CustomSortFieldName))
                        continue;
#endif

                    if (mapFields.ContainsKey(field.Name))
                        continue;

                    mapFields.Add(field.Name, DynamicQueryMappingItem.Create(fieldName, field.AggregationOperation));
                }
            }

            if (query.Metadata.IsGroupBy)
            {
                result.IsGroupBy = true;
                result.GroupByFields = CreateGroupByFields(query, mapFields);

                foreach (var field in mapFields)
                {
                    if (field.Value.AggregationOperation == AggregationOperation.None)
                    {
                        throw new InvalidQueryException($"Field '{field.Key}' isn't neither an aggregation operation nor part of the group by key", query.Metadata.QueryText,
                            query.QueryParameters);
                    }
                }
            }

            result.MapFields = mapFields;

            return result;
        }

        private static Dictionary<string, DynamicQueryMappingItem> CreateGroupByFields(IndexQueryServerSide query, Dictionary<string, DynamicQueryMappingItem> mapFields)
        {
            var groupByFields = query.Metadata.GroupBy;

            if (query.Metadata.SelectFields != null)
            {
                foreach (var field in query.Metadata.SelectFields)
                {
                    if (field.IsGroupByKey)
                        continue;

                    var fieldName = field.Name;

                    if (mapFields.TryGetValue(fieldName, out var existingField) == false)
                    {
                        switch (field.AggregationOperation)
                        {
                            case AggregationOperation.None:
                                break;
                            case AggregationOperation.Count:
                            case AggregationOperation.Sum:
                                mapFields.Add(fieldName, DynamicQueryMappingItem.Create(fieldName, field.AggregationOperation));
                                break;
                            default:
                                ThrowUnknownAggregationOperation(field.AggregationOperation);
                                break;
                        }
                    }
                    else if (field.AggregationOperation != AggregationOperation.None)
                    {
                        existingField.SetAggregation(field.AggregationOperation);
                    }
                }
            }

            var result = new Dictionary<string, DynamicQueryMappingItem>(groupByFields.Length, StringComparer.Ordinal);

            for (int i = 0; i < groupByFields.Length; i++)
            {
                var groupByField = groupByFields[i];

                result[groupByField.Name] = DynamicQueryMappingItem.CreateGroupBy(groupByField.Name, groupByField.GroupByArrayBehavior, query.Metadata.WhereFields);

                mapFields.Remove(groupByField.Name);  // ensure we don't have duplicated group by fields
            }

            return result;
        }

        private static void ThrowUnknownAggregationOperation(AggregationOperation operation)
        {
            throw new InvalidOperationException($"Unknown aggregation operation defined: {operation}");
        }
    }
}
