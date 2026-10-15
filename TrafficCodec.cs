using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;

namespace Apocapatrol
{
    // This game's Unity JSON serializer omits arrays of nested mod-defined records (also seen in car templates).
    // Use the existing template JSON parser/writer and only public DTO fields; never serialize Unity objects/properties.
    internal static class TrafficCodec
    {
        internal static string Write(TrafficSave save) { return StoreJson.Write(Encode(save, 0)); }
        internal static TrafficSave Read(string json) { return (TrafficSave)Decode(Json.Parse(json), typeof(TrafficSave), 0); }
        internal static string WriteRecord(object record) { return StoreJson.Write(Encode(record, 0)); }
        internal static T ReadRecord<T>(string json) { return (T)Decode(Json.Parse(json), typeof(T), 0); }

        private static object Encode(object value, int depth)
        {
            if (depth > 16) throw new InvalidDataException("Traffic data nesting too deep");
            if (value == null || value is string || value is bool || value is int) return value;
            if (value is float number)
            { if (float.IsNaN(number) || float.IsInfinity(number)) throw new InvalidDataException("Invalid traffic number"); return (double)number; }
            if (value is Array array)
            {
                var list = new List<object>(); foreach (var item in array) list.Add(Encode(item, depth + 1)); return list;
            }
            var result = new Dictionary<string, object>();
            foreach (var field in value.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
                result[field.Name] = Encode(field.GetValue(value), depth + 1);
            return result;
        }

        private static object Decode(object value, Type type, int depth)
        {
            if (depth > 16) throw new InvalidDataException("Traffic data nesting too deep");
            if (value == null) return type.IsValueType ? Activator.CreateInstance(type) : null;
            if (type == typeof(string)) return value as string ?? throw new InvalidDataException("Expected traffic string");
            if (type == typeof(bool)) return value is bool ? value : throw new InvalidDataException("Expected traffic boolean");
            if (type == typeof(float) || type == typeof(int))
            {
                if (!(value is double || value is float || value is int || value is long)) throw new InvalidDataException("Expected traffic number");
                if (type == typeof(int)) return Convert.ToInt32(value, CultureInfo.InvariantCulture);
                float number = Convert.ToSingle(value, CultureInfo.InvariantCulture);
                if (float.IsNaN(number) || float.IsInfinity(number)) throw new InvalidDataException("Invalid traffic number");
                return number;
            }
            if (type.IsArray)
            {
                var items = value as IList; if (items == null || items.Count > 20000) throw new InvalidDataException("Invalid traffic array");
                var element = type.GetElementType(); var array = Array.CreateInstance(element, items.Count);
                for (int i = 0; i < items.Count; i++) array.SetValue(Decode(items[i], element, depth + 1), i);
                return array;
            }
            var fields = value as Dictionary<string, object>; if (fields == null) throw new InvalidDataException("Expected traffic record");
            var result = Activator.CreateInstance(type);
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                if (fields.TryGetValue(field.Name, out var item)) field.SetValue(result, Decode(item, field.FieldType, depth + 1));
            return result;
        }
    }
}
