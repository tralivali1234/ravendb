﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Raven.Client.Documents.Conventions;
using Sparrow.Json;
using Sparrow.Json.Parsing;

namespace Raven.Client.Json
{
    internal static class TypeConverter
    {
        public static object ToBlittableSupportedType(object value, DocumentConventions conventions, JsonOperationContext context)
        {
            if (value == null)
                return null;

            var type = value.GetType();
            var underlyingType = Nullable.GetUnderlyingType(type);
            if (underlyingType != null)
                type = underlyingType;

            if (type == typeof(string))
                return value;

            if (type == typeof(LazyStringValue) || type == typeof(LazyCompressedStringValue))
                return value;

            if (type == typeof(bool))
                return value;

            if (type == typeof(int) || type == typeof(long) || type == typeof(double) || 
                type == typeof(decimal) || type == typeof(float) || type == typeof(uint) || 
                type == typeof(ulong) || type == typeof(short) || type == typeof(byte))
                return value;

            if (type == typeof(LazyNumberValue))
                return value;

            if (type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan))
                return value;

            if (type == typeof(Guid))
                return ((Guid)value).ToString("D");

            if (type.GetTypeInfo().IsSubclassOf(typeof(Enum)))
                return value.ToString();

            if (value is byte[] bytes)
            {
                return Convert.ToBase64String(bytes);
            }

            if (value is IDictionary dictionary)
            {
                var @object = new DynamicJsonValue();
                foreach (var key in dictionary.Keys)
                    @object[key.ToString()] = ToBlittableSupportedType(dictionary[key], conventions, context);

                return @object;
            }

            if (value is IEnumerable enumerable)
            {
                var items = value is IEnumerable<object> objectEnumerable
                    ? objectEnumerable.Select(x => ToBlittableSupportedType(x, conventions, context))
                    : enumerable.Cast<object>().Select(x => ToBlittableSupportedType(x, conventions, context));

                return new DynamicJsonArray(items);
            }

            using (var writer = new BlittableJsonWriter(context))
            {
                var serializer = conventions.CreateSerializer();

                writer.WriteStartObject();
                writer.WritePropertyName("Value");

                serializer.Serialize(writer, value);

                writer.WriteEndObject();

                writer.FinalizeDocument();

                var reader = writer.CreateReader();
                return reader["Value"];
            }
        }
    }
}
