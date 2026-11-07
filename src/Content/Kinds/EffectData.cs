using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using HexTactics.Content.Model;
using HexTactics.Content.Registry;
using HexTactics.Content.Schema;
using HexTactics.Core.Effects;

namespace HexTactics.Content.Kinds;

/// <summary>
/// One effect in content data: a registered type and a flat set of parameter values, stored as
/// <c>{"type": "damage", "amount": 20, "targets": "enemies"}</c>. Fields and their ranges come from the
/// <see cref="EffectTypeRegistry"/>; a value that is not set takes the field default.
/// </summary>
public sealed class EffectData
{
    public EffectData(string type, IDictionary<string, object>? parameters = null)
    {
        Type = type;
        Params = parameters is null ? new SortedDictionary<string, object>(StringComparer.Ordinal) : new SortedDictionary<string, object>(parameters, StringComparer.Ordinal);
    }

    public string Type { get; set; }

    public SortedDictionary<string, object> Params { get; }

    public EffectData Clone() => new(Type, Params);

    public object? Get(string key) => Params.TryGetValue(key, out var v) ? v : null;

    public void Set(string key, object? value)
    {
        if (value is null)
        {
            Params.Remove(key);
        }
        else
        {
            Params[key] = value is int i ? (double)i : value;
        }
    }

    public JsonObject ToJson()
    {
        var obj = new JsonObject { ["type"] = Type };
        foreach (var (key, value) in Params)
        {
            obj[key] = JsonFields.ToNode(value);
        }

        return obj;
    }

    public static EffectData Read(JsonNode? node, IssueList issues, string path)
    {
        if (node is not JsonObject obj)
        {
            issues.Error(path, "эффект должен быть объектом");
            return new EffectData("invalid");
        }

        var type = JsonFields.Text(obj, "type", string.Empty, issues, path);
        if (type.Length == 0)
        {
            issues.Error(path, "у эффекта нет типа");
        }

        var effect = new EffectData(type);
        foreach (var (key, value) in obj)
        {
            if (key == "type")
            {
                continue;
            }

            if (JsonFields.Scalar(value) is { } scalar)
            {
                effect.Params[key] = scalar;
            }
            else
            {
                issues.Error(JsonFields.Join(path, key), "значение параметра должно быть числом, строкой или true/false");
            }
        }

        return effect;
    }

    /// <summary>Checks the type and every field against the registry (unknown types are errors, unknown fields warnings).</summary>
    public void Validate(ValidationContext context, string path, EffectContext usage)
    {
        var descriptor = context.Effects.Find(Type);
        if (descriptor is null)
        {
            context.Issues.Error(path, $"неизвестный тип эффекта «{Type}»");
            return;
        }

        if ((descriptor.Contexts & usage) == 0)
        {
            context.Issues.Error(path, $"эффект «{descriptor.Label}» здесь не поддерживается");
        }

        foreach (var field in descriptor.Fields)
        {
            if (field.VisibleWhen is { } visible && !visible(this))
            {
                continue;
            }

            var value = Get(field.Key) ?? field.Default;
            if (field.Check(value, JsonFields.Join(path, field.Key)) is { } issue)
            {
                context.Issues.AddRange(new[] { issue });
            }
        }

        foreach (var key in Params.Keys)
        {
            if (descriptor.FindField(key) is null)
            {
                context.Issues.Warning(JsonFields.Join(path, key), $"поле «{key}» не используется эффектом «{descriptor.Label}»");
            }
        }
    }

    /// <summary>The runtime effect: every field of the type with its value or default (unknown fields are dropped).</summary>
    public EffectSpec ToSpec(EffectTypeRegistry registry)
    {
        var values = new Dictionary<string, object>(StringComparer.Ordinal);
        if (registry.Find(Type) is { } descriptor)
        {
            foreach (var field in descriptor.Fields)
            {
                if ((Get(field.Key) ?? field.Default) is { } value)
                {
                    values[field.Key] = value is int i ? (double)i : value;
                }
            }
        }

        return new EffectSpec(Type, values);
    }
}
