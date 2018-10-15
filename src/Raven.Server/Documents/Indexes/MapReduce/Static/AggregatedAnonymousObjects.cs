﻿using Sparrow.Json;
using System.Collections.Generic;
using Raven.Server.Documents.Indexes.Persistence.Lucene.Documents;
using Sparrow.Json.Parsing;
using Raven.Server.Utils;

namespace Raven.Server.Documents.Indexes.MapReduce.Static
{
    public class AggregatedAnonymousObjects : AggregationResult
    {
        private readonly List<object> _outputs;
        private readonly List<BlittableJsonReaderObject> _jsons;
        private readonly IPropertyAccessor _propertyAccessor;
        private readonly JsonOperationContext _indexContext;

        public AggregatedAnonymousObjects(List<object> results, IPropertyAccessor propertyAccessor, JsonOperationContext indexContext)
        {
            _outputs = results;
            _propertyAccessor = propertyAccessor;
            _jsons = new List<BlittableJsonReaderObject>(results.Count);
            _indexContext = indexContext;
        }

        public override int Count => _outputs.Count;

        public override IEnumerable<object> GetOutputs()
        {
            return _outputs;
        }

        public override IEnumerable<BlittableJsonReaderObject> GetOutputsToStore()
        {
            foreach (var output in _outputs)
            {
                var djv = new DynamicJsonValue();

                foreach (var property in _propertyAccessor.GetPropertiesInOrder(output))
                {
                    var value = property.Value;
                    djv[property.Key] = TypeConverter.ToBlittableSupportedType(value);
                }

                var item = _indexContext.ReadObject(djv, "map/reduce result to store");
                _jsons.Add(item);
                yield return item;
            }
        }

        public override void Dispose()
        {
            for (int i = _jsons.Count - 1; i >= 0; i--)
            {
                _jsons[i].Dispose();
            }
            _jsons.Clear();
        }
    }
}
