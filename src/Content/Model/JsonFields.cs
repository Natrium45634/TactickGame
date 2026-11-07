using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HexTactics.Content.Model;

/// <summary>
/// Lenient typed reading of JSON objects: a missing field takes its default, a field of the wrong type is reported as an
/// issue and also takes the default (the object is never silently emptied — the issue blocks saving).
/// </summary>
internal static class JsonFields
{
    public static double Number(JsonObject obj, string key, double fallback, IssueList issues, string path)
    {
        if (!obj.TryGetPropertyValue(key, out var node) || node is null)
        {
            return fallback;
        }

        if (node is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue<double>(out var d))
        {
            return d;
        }

        issues.Error(Join(path, key), "ожидается число");
        return fallback;
    }

    public static int Integer(JsonObject obj, string key, int fallback, IssueList issues, string path)
    {
        if (!obj.TryGetPropertyValue(key, out var node) || node is null)
        {
            return fallback;
        }

        if (node is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue<double>(out var d) && Math.Abs(d - Math.Round(d)) < 1e-9 && Math.Abs(d) < int.MaxValue)
        {
            return (int)Math.Round(d);
        }

        issues.Error(Join(path, key), "ожидается целое число");
        return fallback;
    }

    public static string Text(JsonObject obj, string key, string fallback, IssueList issues, string path)
    {
        if (!obj.TryGetPropertyValue(key, out var node) || node is null)
        {
            return fallback;
        }

        if (node is JsonValue v && v.GetValueKind() == JsonValueKind.String)
        {
            return v.GetValue<string>();
        }

        issues.Error(Join(path, key), "ожидается строка");
        return fallback;
    }

    public static bool Flag(JsonObject obj, string key, bool fallback, IssueList issues, string path)
    {
        if (!obj.TryGetPropertyValue(key, out var node) || node is null)
        {
            return fallback;
        }

        if (node is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
        {
            return v.GetValue<bool>();
        }

        issues.Error(Join(path, key), "ожидается true/false");
        return fallback;
    }

    public static JsonArray? Array(JsonObject obj, string key, IssueList issues, string path)
    {
        if (!obj.TryGetPropertyValue(key, out var node) || node is null)
        {
            return null;
        }

        if (node is JsonArray a)
        {
            return a;
        }

        issues.Error(Join(path, key), "ожидается список");
        return null;
    }

    public static List<string> TextList(JsonObject obj, string key, IssueList issues, string path)
    {
        var list = new List<string>();
        if (Array(obj, key, issues, path) is not { } array)
        {
            return list;
        }

        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is JsonValue v && v.GetValueKind() == JsonValueKind.String)
            {
                list.Add(v.GetValue<string>());
            }
            else
            {
                issues.Error($"{Join(path, key)}[{i}]", "ожидается строка");
            }
        }

        return list;
    }

    /// <summary>A scalar JSON value as <see cref="double"/>, <see cref="string"/> or <see cref="bool"/>; null otherwise.</summary>
    public static object? Scalar(JsonNode? node) => node is JsonValue v ? v.GetValueKind() switch
    {
        JsonValueKind.Number when v.TryGetValue<double>(out var d) => d,
        JsonValueKind.String => v.GetValue<string>(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    } : null;

    public static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        double d => JsonValue.Create(d),
        int i => JsonValue.Create(i),
        long l => JsonValue.Create(l),
        float f => JsonValue.Create((double)f),
        bool b => JsonValue.Create(b),
        string s => JsonValue.Create(s),
        _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture)),
    };

    public static string Join(string path, string key) => path.Length == 0 ? key : $"{path}.{key}";
}
