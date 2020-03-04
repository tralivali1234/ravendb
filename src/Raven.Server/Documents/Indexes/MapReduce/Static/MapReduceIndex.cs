﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Raven.Client;
using Raven.Client.Documents.Changes;
using Raven.Client.Documents.Indexes;
using Raven.Client.Exceptions.Documents.Indexes;
using Raven.Server.Documents.Indexes.Configuration;
using Raven.Server.Documents.Indexes.Persistence.Lucene;
using Raven.Server.Documents.Indexes.Persistence.Lucene.Documents;
using Raven.Server.Documents.Indexes.Static;
using Raven.Server.Documents.Indexes.Static.Roslyn.Rewriters;
using Raven.Server.Documents.Indexes.Workers;
using Raven.Server.Documents.Queries;
using Raven.Server.ServerWide.Context;
using Raven.Server.Utils;
using Sparrow.Json;
using Sparrow.Json.Parsing;
using Voron;

namespace Raven.Server.Documents.Indexes.MapReduce.Static
{
    public class MapReduceIndex : MapReduceIndexBase<MapReduceIndexDefinition, IndexField>
    {
        private readonly HashSet<string> _referencedCollections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        protected internal readonly StaticIndexBase _compiled;
        private bool? _isSideBySide;

        private HandleReferences _handleReferences;

        private readonly Dictionary<string, AnonymousObjectToBlittableMapResultsEnumerableWrapper> _enumerationWrappers = new Dictionary<string, AnonymousObjectToBlittableMapResultsEnumerableWrapper>();

        public PropertyAccessor OutputReduceToCollectionPropertyAccessor;

        private MapReduceIndex(MapReduceIndexDefinition definition, StaticIndexBase compiled)
            : base(IndexType.MapReduce, definition)
        {
            _compiled = compiled;

            if (_compiled.ReferencedCollections == null)
                return;

            foreach (var collection in _compiled.ReferencedCollections)
            {
                foreach (var referencedCollection in collection.Value)
                    _referencedCollections.Add(referencedCollection.Name);
            }
        }

        public override bool HasBoostedFields => _compiled.HasBoostedFields;

        public override bool IsMultiMap => _compiled.Maps.Count > 1 || _compiled.Maps.Any(x => x.Value.Count > 1);

        public override void ResetIsSideBySideAfterReplacement()
        {
            _isSideBySide = null;
        }

        protected override void HandleDocumentChange(DocumentChange change)
        {
            if (HandleAllDocs == false && Collections.Contains(change.CollectionName) == false &&
                _referencedCollections.Contains(change.CollectionName) == false)
                return;

            _mre.Set();
        }

        public static MapReduceIndex CreateNew(IndexDefinition definition, DocumentDatabase documentDatabase, bool isIndexReset = false)
        {
            var instance = CreateIndexInstance(definition);
            ValidateReduceResultsCollectionName(definition, instance._compiled, documentDatabase,
                checkIfCollectionEmpty: isIndexReset == false);

            instance.Initialize(documentDatabase,
                new SingleIndexConfiguration(definition.Configuration, documentDatabase.Configuration),
                documentDatabase.Configuration.PerformanceHints);

            return instance;
        }

        public static void ValidateReduceResultsCollectionName(IndexDefinition definition, StaticIndexBase index, DocumentDatabase database, bool checkIfCollectionEmpty)
        {
            var outputReduceToCollection = definition.OutputReduceToCollection;
            if (string.IsNullOrWhiteSpace(outputReduceToCollection))
                return;

            var collections = index.Maps.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (collections.Contains(Constants.Documents.Collections.AllDocumentsCollection))
                throw new IndexInvalidException($"It is forbidden to create the '{definition.Name}' index " +
                                                $"which would output reduce results to documents in the '{outputReduceToCollection}' collection, " +
                                                $"as this index is mapping all documents " +
                                                $"and this will result in an infinite loop.");

            foreach (var referencedCollection in index.ReferencedCollections)
            foreach (var collectionName in referencedCollection.Value)
            {
                collections.Add(collectionName.Name);
            }
            if (collections.Contains(outputReduceToCollection))
                throw new IndexInvalidException($"It is forbidden to create the '{definition.Name}' index " +
                                                $"which would output reduce results to documents in the '{outputReduceToCollection}' collection, " +
                                                $"as this index is mapping or referencing the '{outputReduceToCollection}' collection " +
                                                $"and this will result in an infinite loop.");

            var indexes = database.IndexStore.GetIndexes()
                .Where(x => x.Type == IndexType.MapReduce)
                .Cast<MapReduceIndex>()
                .Where(mapReduceIndex => string.IsNullOrWhiteSpace(mapReduceIndex.Definition.OutputReduceToCollection) == false &&
                                         mapReduceIndex.Name != definition.Name)
                .ToList();

            foreach (var otherIndex in indexes)
            {
                if (otherIndex.Definition.OutputReduceToCollection.Equals(outputReduceToCollection, StringComparison.OrdinalIgnoreCase))
                {
                    var sideBySideIndex = definition.Name.StartsWith(Constants.Documents.Indexing.SideBySideIndexNamePrefix, StringComparison.OrdinalIgnoreCase);
                    if (sideBySideIndex)
                    {
                        throw new IndexInvalidException($"In order to create the '{definition.Name}' side by side index " +
                                                        $"you firstly need to set {nameof(IndexDefinition.OutputReduceToCollection)} to be null " +
                                                        $"on the '{otherIndex.Name}' index " +
                                                        $"and than delete all of the documents in the '{otherIndex.Definition.OutputReduceToCollection}' collection.");
                    }

                    throw new IndexInvalidException($"It is forbidden to create the '{definition.Name}' index " +
                                                    $"which would output reduce results to documents in the '{outputReduceToCollection}' collection, " +
                                                    $"as there is another index named '{otherIndex.Name}' " +
                                                    $"which also output reduce results to documents in the same '{outputReduceToCollection}' collection. " +
                                                    $"{nameof(IndexDefinition.OutputReduceToCollection)} must by set to unique value for each index or be null.");
                }

                var otherIndexCollections = new HashSet<string>(otherIndex.Collections);

                foreach (var referencedCollection in otherIndex.GetReferencedCollections())
                foreach (var collectionName in referencedCollection.Value)
                {
                    otherIndexCollections.Add(collectionName.Name);
                }

                if (otherIndexCollections.Contains(outputReduceToCollection) &&
                    CheckIfThereIsAnIndexWhichWillOutputReduceDocumentsWhichWillBeUsedAsMapOnTheSpecifiedIndex(otherIndex, collections, indexes, out string description))
                {
                    description += Environment.NewLine + $"--> {definition.Name}: {string.Join(",", collections)} => *{outputReduceToCollection}*";
                    throw new IndexInvalidException($"It is forbidden to create the '{definition.Name}' index " +
                                                    $"which would output reduce results to documents in the '{outputReduceToCollection}' collection, " +
                                                    $"as '{outputReduceToCollection}' collection is consumed by other index in a way that would " +
                                                    $"lead to an infinite loop." +
                                                    Environment.NewLine + description);
                }
            }

            if (checkIfCollectionEmpty)
            {
                using (database.DocumentsStorage.ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
                using (context.OpenReadTransaction())
                {
                    var stats = database.DocumentsStorage.GetCollection(outputReduceToCollection, context);
                    if (stats.Count > 0)
                    {
                        throw new IndexInvalidException(
                            $"Index '{definition.Name}' is defined to output the Reduce results to documents in Collection '{outputReduceToCollection}'. " +
                            $"This collection currently has {stats.Count} document{(stats.Count == 1 ? ' ' : 's')}. " +
                            $"All documents in Collection '{stats.Name}' must be deleted first.");
                    }
                }
            }
        }

        private static bool CheckIfThereIsAnIndexWhichWillOutputReduceDocumentsWhichWillBeUsedAsMapOnTheSpecifiedIndex(
            MapReduceIndex indexToCheck, HashSet<string> indexCollections,
            List<MapReduceIndex> indexes, out string description)
        {
            description = $"{indexToCheck.Name}: {string.Join(",", indexToCheck.Collections)}";

            var referencedCollections = new HashSet<string>();

            foreach (var referencedCollection in indexToCheck.GetReferencedCollections())
            foreach (var collectionName in referencedCollection.Value)
            {
                referencedCollections.Add(collectionName.Name);
            }

            if (referencedCollections.Count > 0)
                description += $" (referenced: {string.Join(",", referencedCollections)})";

            description += $" => {indexToCheck.Definition.OutputReduceToCollection}";
            
            if (string.IsNullOrWhiteSpace(indexToCheck.Definition.OutputReduceToCollection))
                return false;

            if (indexCollections.Contains(indexToCheck.Definition.OutputReduceToCollection))
                return true;

            foreach (var index in indexes)
            {
                var otherIndexCollections = new HashSet<string>(index.Collections);
                foreach (var referencedCollection in index.GetReferencedCollections())
                foreach (var collectionName in referencedCollection.Value)
                {
                    otherIndexCollections.Add(collectionName.Name);
                }
                if (otherIndexCollections.Contains(indexToCheck.Definition.OutputReduceToCollection))
                {
                    var failed = CheckIfThereIsAnIndexWhichWillOutputReduceDocumentsWhichWillBeUsedAsMapOnTheSpecifiedIndex(index, indexCollections, indexes, out string innerDescription);
                    description += Environment.NewLine + innerDescription;
                    if (failed)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public static Index Open(StorageEnvironment environment, DocumentDatabase documentDatabase)
        {
            var definition = MapIndexDefinition.Load(environment);
            var instance = CreateIndexInstance(definition);

            instance.Initialize(environment, documentDatabase,
                new SingleIndexConfiguration(definition.Configuration, documentDatabase.Configuration),
                documentDatabase.Configuration.PerformanceHints);

            return instance;
        }

        public static void Update(Index index, IndexDefinition definition, DocumentDatabase documentDatabase)
        {
            var staticMapIndex = (MapReduceIndex)index;
            var staticIndex = staticMapIndex._compiled;

            var staticMapIndexDefinition = new MapReduceIndexDefinition(definition, staticIndex.Maps.Keys.ToHashSet(), staticIndex.OutputFields,
                staticIndex.GroupByFields, staticIndex.HasDynamicFields);
            staticMapIndex.Update(staticMapIndexDefinition, new SingleIndexConfiguration(definition.Configuration, documentDatabase.Configuration));
        }

        private static MapReduceIndex CreateIndexInstance(IndexDefinition definition)
        {
            var staticIndex = IndexCompilationCache.GetIndexInstance(definition);

            var staticMapIndexDefinition = new MapReduceIndexDefinition(definition, staticIndex.Maps.Keys.ToHashSet(), staticIndex.OutputFields,
                staticIndex.GroupByFields, staticIndex.HasDynamicFields);
            var instance = new MapReduceIndex(staticMapIndexDefinition, staticIndex);

            return instance;
        }

        protected override IIndexingWork[] CreateIndexWorkExecutors()
        {
            var workers = new List<IIndexingWork>();
            workers.Add(new CleanupDeletedDocuments(this, DocumentDatabase.DocumentsStorage, _indexStorage, Configuration, MapReduceWorkContext));

            if (_referencedCollections.Count > 0)
                workers.Add(_handleReferences = new HandleReferences(this, _compiled.ReferencedCollections, DocumentDatabase.DocumentsStorage, _indexStorage, Configuration));

            workers.Add(new MapDocuments(this, DocumentDatabase.DocumentsStorage, _indexStorage, MapReduceWorkContext, Configuration));
            workers.Add(new ReduceMapResultsOfStaticIndex(this, _compiled.Reduce, Definition, _indexStorage, DocumentDatabase.Metrics, MapReduceWorkContext));

            return workers.ToArray();
        }

        public override void HandleDelete(Tombstone tombstone, string collection, IndexWriteOperation writer, TransactionOperationContext indexContext, IndexingStatsScope stats)
        {
            if (_referencedCollections.Count > 0)
                _handleReferences.HandleDelete(tombstone, collection, writer, indexContext, stats);

            base.HandleDelete(tombstone, collection, writer, indexContext, stats);
        }

        public override IIndexedDocumentsEnumerator GetMapEnumerator(IEnumerable<Document> documents, string collection, TransactionOperationContext indexContext, IndexingStatsScope stats)
        {
            return new StaticIndexDocsEnumerator(documents, _compiled.Maps[collection], collection, stats);
        }

        public override int HandleMap(LazyStringValue lowerId, IEnumerable mapResults, IndexWriteOperation writer, TransactionOperationContext indexContext, IndexingStatsScope stats)
        {
            if (_enumerationWrappers.TryGetValue(CurrentIndexingScope.Current.SourceCollection, out AnonymousObjectToBlittableMapResultsEnumerableWrapper wrapper) == false)
            {
                _enumerationWrappers[CurrentIndexingScope.Current.SourceCollection] = wrapper = new AnonymousObjectToBlittableMapResultsEnumerableWrapper(this, indexContext);
            }

            wrapper.InitializeForEnumeration(mapResults, indexContext, stats);

            return PutMapResults(lowerId, wrapper, indexContext, stats);
        }

        protected override bool IsStale(DocumentsOperationContext databaseContext, TransactionOperationContext indexContext, long? cutoff = null, long? referenceCutoff = null, List<string> stalenessReasons = null)
        {
            var isStale = base.IsStale(databaseContext, indexContext, cutoff, referenceCutoff, stalenessReasons);
            if (isStale && stalenessReasons == null || _referencedCollections.Count == 0)
                return isStale;

            return StaticIndexHelper.IsStaleDueToReferences(this, databaseContext, indexContext, referenceCutoff, stalenessReasons) || isStale;
        }

        protected override unsafe long CalculateIndexEtag(DocumentsOperationContext documentsContext, TransactionOperationContext indexContext,
            QueryMetadata query, bool isStale)
        {
            if (_referencedCollections.Count == 0)
                return base.CalculateIndexEtag(documentsContext, indexContext, query, isStale);

            var minLength = MinimumSizeForCalculateIndexEtagLength();
            var length = minLength +
                         sizeof(long) * 2 * (Collections.Count * _referencedCollections.Count); // last referenced collection etags and last processed reference collection etags

            var indexEtagBytes = stackalloc byte[length];

            CalculateIndexEtagInternal(indexEtagBytes, isStale, State, documentsContext, indexContext);

            UseAllDocumentsEtag(documentsContext, query, length, indexEtagBytes);

            var writePos = indexEtagBytes + minLength;

            return StaticIndexHelper.CalculateIndexEtag(this, length, indexEtagBytes, writePos, documentsContext, indexContext);
        }

        protected override bool ShouldReplace()
        {
            if (_isSideBySide.HasValue == false)
                _isSideBySide = Name.StartsWith(Constants.Documents.Indexing.SideBySideIndexNamePrefix, StringComparison.OrdinalIgnoreCase);

            if (_isSideBySide == false)
                return false;

            using (DocumentDatabase.DocumentsStorage.ContextPool.AllocateOperationContext(out DocumentsOperationContext databaseContext))
            using (_contextPool.AllocateOperationContext(out TransactionOperationContext indexContext))
            {
                using (indexContext.OpenReadTransaction())
                using (databaseContext.OpenReadTransaction())
                {
                    var canReplace = IsStale(databaseContext, indexContext) == false;
                    if (canReplace)
                        _isSideBySide = null;

                    return canReplace;
                }
            }
        }

        public override Dictionary<string, HashSet<CollectionName>> GetReferencedCollections()
        {
            return _compiled.ReferencedCollections;
        }

        private class AnonymousObjectToBlittableMapResultsEnumerableWrapper : IEnumerable<MapResult>
        {
            private IEnumerable _items;
            private TransactionOperationContext _indexContext;
            private IndexingStatsScope _stats;
            private IndexingStatsScope _createBlittableResultStats;
            private readonly ReduceKeyProcessor _reduceKeyProcessor;
            private readonly HashSet<CompiledIndexField> _groupByFields;
            private readonly bool _isMultiMap;
            private PropertyAccessor _propertyAccessor;
            private readonly StaticIndexBase _compiledIndex;

            public AnonymousObjectToBlittableMapResultsEnumerableWrapper(MapReduceIndex index, TransactionOperationContext indexContext)
            {
                _indexContext = indexContext;
                _groupByFields = index.Definition.GroupByFields;
                _isMultiMap = index.IsMultiMap;
                _reduceKeyProcessor = new ReduceKeyProcessor(index.Definition.GroupByFields.Count, index._unmanagedBuffersPool);
                _compiledIndex = index._compiled;
            }

            public void InitializeForEnumeration(IEnumerable items, TransactionOperationContext indexContext, IndexingStatsScope stats)
            {
                _items = items;
                _indexContext = indexContext;

                if (_stats == stats)
                    return;

                _stats = stats;
                _createBlittableResultStats = _stats.For(IndexingOperation.Reduce.CreateBlittableJson, start: false);
            }

            IEnumerator<MapResult> IEnumerable<MapResult>.GetEnumerator()
            {
                return GetEnumerator();
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }

            private Enumerator GetEnumerator()
            {
                return new Enumerator(_items.GetEnumerator(), this, _createBlittableResultStats);
            }

            private class Enumerator : IEnumerator<MapResult>
            {
                private readonly IEnumerator _enumerator;
                private readonly AnonymousObjectToBlittableMapResultsEnumerableWrapper _parent;
                private readonly IndexingStatsScope _createBlittableResult;
                private readonly HashSet<CompiledIndexField> _groupByFields;
                private readonly ReduceKeyProcessor _reduceKeyProcessor;

                public Enumerator(IEnumerator enumerator, AnonymousObjectToBlittableMapResultsEnumerableWrapper parent, IndexingStatsScope createBlittableResult)
                {
                    _enumerator = enumerator;
                    _parent = parent;
                    _createBlittableResult = createBlittableResult;
                    _groupByFields = _parent._groupByFields;
                    _reduceKeyProcessor = _parent._reduceKeyProcessor;
                }

                public bool MoveNext()
                {
                    if (_enumerator.MoveNext() == false)
                        return false;

                    var output = _enumerator.Current;

                    using (_createBlittableResult.Start())
                    {
                        PropertyAccessor accessor;

                        if (_parent._isMultiMap == false)
                            accessor = _parent._propertyAccessor ??
                                       (_parent._propertyAccessor = PropertyAccessor.CreateMapReduceOutputAccessor(output.GetType(), output, _groupByFields));
                        else
                            accessor = TypeConverter.GetPropertyAccessorForMapReduceOutput(output, _groupByFields);

                        var mapResult = new DynamicJsonValue();

                        _reduceKeyProcessor.Reset();

                        var propertiesInOrder = accessor.PropertiesInOrder;
                        int properties = propertiesInOrder.Count;

                        for (int i = 0; i < properties; i++)
                        {
                            var field = propertiesInOrder[i];

                            var value = field.Value.GetValue(output);
                            var blittableValue = TypeConverter.ToBlittableSupportedType(value);
                            mapResult[field.Key] = blittableValue;

                            if (field.Value.IsGroupByField)
                            {
                                var valueForProcessor = field.Value.GroupByField.GetValue(value, blittableValue);
                                _reduceKeyProcessor.Process(_parent._indexContext.Allocator, valueForProcessor);
                            }
                                
                        }

                        if (_reduceKeyProcessor.ProcessedFields != _groupByFields.Count)
                            ThrowMissingGroupByFieldsInMapOutput(output, _groupByFields, _parent._compiledIndex);

                        var reduceHashKey = _reduceKeyProcessor.Hash;

                        Current.Data = _parent._indexContext.ReadObject(mapResult, "map-result");
                        Current.ReduceKeyHash = reduceHashKey;
                    }

                    return true;
                }

                public void Reset()
                {
                    throw new NotImplementedException();
                }

                public MapResult Current { get; } = new MapResult();

                object IEnumerator.Current => Current;

                public void Dispose()
                {
                    _reduceKeyProcessor.ReleaseBuffer();
                }

                private static void ThrowMissingGroupByFieldsInMapOutput(object output, HashSet<CompiledIndexField> groupByFields, StaticIndexBase compiledIndex)
                {
                    throw new InvalidOperationException(
                        $"The output of the mapping function does not contain all fields that the index is supposed to group by.{Environment.NewLine}" +
                        $"Output: {output}{Environment.NewLine}" +
                        $"Group by fields: {string.Join(",", groupByFields.Select(x => x.Name))}{Environment.NewLine}" +
                        $"Compiled index def:{Environment.NewLine}{compiledIndex.Source}");
                }
            }
        }
    }
}
