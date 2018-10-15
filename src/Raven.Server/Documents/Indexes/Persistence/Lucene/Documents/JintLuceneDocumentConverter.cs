﻿using System;
using System.Collections.Generic;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Lucene.Net.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Server.Documents.Indexes.Static;
using Raven.Server.Documents.Indexes.Static.Spatial;
using Raven.Server.Documents.Patch;
using Raven.Server.Utils;
using Sparrow.Json;

namespace Raven.Server.Documents.Indexes.Persistence.Lucene.Documents
{
    public class JintLuceneDocumentConverter : LuceneDocumentConverterBase
    {
        public JintLuceneDocumentConverter(ICollection<IndexField> fields, bool reduceOutput = false) : base(fields, reduceOutput)
        {
        }

        private const string CreatedFieldValuePropertyName = "$value";
        private const string CreatedFieldOptionsPropertyName = "$options";
        private const string CreatedFieldNamePropertyName = "$name";

        protected override int GetFields<T>(T instance, LazyStringValue key, object document, JsonOperationContext indexContext)
        {
            if (!(document is ObjectInstance documentToProcess))
                return 0;

            int newFields = 0;
            if (key != null)
            {
                instance.Add(GetOrCreateKeyField(key));
                newFields++;
            }

            if (_reduceOutput)
            {
                var reduceResult = JsBlittableBridge.Translate(indexContext,
                    documentToProcess.Engine,
                    documentToProcess);

                instance.Add(GetReduceResultValueField(reduceResult));
                newFields++;
            }

            foreach (var (property, propertyDescriptor) in documentToProcess.GetOwnProperties())
            {
                if (_fields.TryGetValue(property, out var field) == false)
                {
                    field = new IndexField
                    {
                        Name = property,
                        Indexing = _allFields.Indexing,
                        Storage = _allFields.Storage,
                        Analyzer = _allFields.Analyzer,
                        Spatial = _allFields.Spatial,
                        HasSuggestions = _allFields.HasSuggestions,
                        TermVector = _allFields.TermVector
                    };
                }

                var obj = propertyDescriptor.Value;
                foreach (var v in EnumerateValues(obj))
                {
                    var actualValue = v;
                    object value;
                    if (actualValue.IsObject() && actualValue.IsArray() == false)
                    {
                        //In case TryDetectDynamicFieldCreation finds a dynamic field it will populate 'field.Name' with the actual property name 
                        //so we must use field.Name and not property from this point on.
                        var val = TryDetectDynamicFieldCreation(property, actualValue.AsObject(), field);
                        if (val != null)
                        {
                            if (val.IsObject() && val.AsObject().TryGetValue("$spatial", out _))
                            {
                                actualValue = val; //Here we populate the dynamic spatial field that will be handled below.
                            }
                            else
                            {
                                value = TypeConverter.ToBlittableSupportedType(val, flattenArrays: false, engine: documentToProcess.Engine, context: indexContext);
                                newFields += GetRegularFields(instance, field, value, indexContext);
                                continue;
                            }
                        }

                        var objectValue = actualValue.AsObject();
                        if (objectValue.HasOwnProperty("$spatial") && objectValue.TryGetValue("$spatial", out var inner))
                        {

                            SpatialField spatialField;
                            IEnumerable<AbstractField> spatial;
                            if (inner.IsString())
                            {

                                spatialField = StaticIndexBase.GetOrCreateSpatialField(field.Name);
                                spatial = StaticIndexBase.CreateSpatialField(spatialField, inner.AsString());
                            }
                            else if (inner.IsObject())
                            {
                                var innerObject = inner.AsObject();
                                if (innerObject.HasOwnProperty("Lat") && innerObject.HasOwnProperty("Lng") && innerObject.TryGetValue("Lat", out var lat)
                                    && lat.IsNumber() && innerObject.TryGetValue("Lng", out var lng) && lng.IsNumber())
                                {
                                    spatialField = StaticIndexBase.GetOrCreateSpatialField(field.Name);
                                    spatial = StaticIndexBase.CreateSpatialField(spatialField, lat.AsNumber(), lng.AsNumber());
                                }
                                else
                                {
                                    continue; //Ignoring bad spatial field 
                                }
                            }
                            else
                            {
                                continue; //Ignoring bad spatial field 
                            }
                            newFields += GetRegularFields(instance, field, spatial, indexContext, nestedArray: false);

                            continue;
                        }
                    }

                    value = TypeConverter.ToBlittableSupportedType(propertyDescriptor.Value, flattenArrays: false, engine: documentToProcess.Engine, context: indexContext);
                    newFields += GetRegularFields(instance, field, value, indexContext, nestedArray: true);
                }
            }

            return newFields;
        }

        private static JsValue TryDetectDynamicFieldCreation(string property, ObjectInstance valueAsObject, IndexField field)
        {
            //We have a field creation here _ = {"$value":val, "$name","$options":{...}}
            if (!valueAsObject.HasOwnProperty(CreatedFieldValuePropertyName) ||
                !valueAsObject.HasOwnProperty(CreatedFieldNamePropertyName))
                return null;

            var value = valueAsObject.GetOwnProperty(CreatedFieldValuePropertyName).Value;
            var fieldNameObj = valueAsObject.GetOwnProperty(CreatedFieldNamePropertyName).Value;
            if (fieldNameObj.IsString() == false)
                throw new ArgumentException($"Dynamic field {property} is expected to have a string {CreatedFieldNamePropertyName} property but got {fieldNameObj}");


            field.Name = fieldNameObj.AsString();

            if (valueAsObject.HasOwnProperty(CreatedFieldOptionsPropertyName))
            {
                var options = valueAsObject.GetOwnProperty(CreatedFieldOptionsPropertyName).Value;
                if (options.IsObject() == false)
                {
                    throw new ArgumentException($"Dynamic field {property} is expected to contain an object with three properties " +
                                                $"{CreatedFieldOptionsPropertyName}, {CreatedFieldNamePropertyName} and {CreatedFieldOptionsPropertyName} the later should be a valid IndexFieldOptions object.");
                }

                var optionObj = options.AsObject();
                foreach (var kvp in optionObj.GetOwnProperties())
                {
                    var optionValue = kvp.Value.Value;
                    if (optionValue.IsUndefined() || optionValue.IsNull())
                        continue;

                    var propertyName = kvp.Key;
                    if (string.Equals(propertyName, nameof(CreateFieldOptions.Indexing), StringComparison.OrdinalIgnoreCase))
                    {
                        field.Indexing = GetEnum<FieldIndexing>(optionValue, propertyName);

                        continue;
                    }

                    if (string.Equals(propertyName, nameof(CreateFieldOptions.Storage), StringComparison.OrdinalIgnoreCase))
                    {
                        if (optionValue.IsBoolean())
                            field.Storage = optionValue.AsBoolean()
                                ? FieldStorage.Yes
                                : FieldStorage.No;
                        else
                            field.Storage = GetEnum<FieldStorage>(optionValue, propertyName);

                        continue;
                    }

                    if (string.Equals(propertyName, nameof(CreateFieldOptions.TermVector), StringComparison.OrdinalIgnoreCase))
                    {
                        field.TermVector = GetEnum<FieldTermVector>(optionValue, propertyName);

                        continue;
                    }
                }
            }

            return value;

            TEnum GetEnum<TEnum>(JsValue optionValue, string propertyName)
            {
                if (optionValue.IsString() == false)
                    throw new ArgumentException($"Could not parse dynamic field option property '{propertyName}' value ('{optionValue}') because it is not a string.");

                var optionValueAsString = optionValue.AsString();
                if (Enum.TryParse(typeof(TEnum), optionValueAsString, true, out var enumValue) == false)
                    throw new ArgumentException($"Could not parse dynamic field option property '{propertyName}' value ('{optionValueAsString}') into '{typeof(TEnum).Name}' enum.");

                return (TEnum)enumValue;
            }
        }

        private static IEnumerable<JsValue> EnumerateValues(JsValue jv)
        {
            if (jv.IsArray())
            {
                var arr = jv.AsArray();
                foreach (var (key, val) in arr.GetOwnProperties())
                {
                    if (key == "length")
                        continue;

                    yield return val.Value;
                }
            }
            else
            {
                yield return jv;
            }
        }
    }
}
