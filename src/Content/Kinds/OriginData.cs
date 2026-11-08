using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using HexTactics.Content.Model;
using HexTactics.Content.Registry;

namespace HexTactics.Content.Kinds;

/// <summary>Shared helpers of the kinds that carry a list of effects (species, traits).</summary>
internal static class EffectLists
{
    public static JsonArray Write(IEnumerable<PassiveData> effects) => new(effects.Select(e => (JsonNode)e.ToJson()).ToArray());

    public static void Read(JsonObject obj, List<PassiveData> into, IssueList issues)
    {
        if (JsonFields.Array(obj, "effects", issues, string.Empty) is { } list)
        {
            for (var i = 0; i < list.Count; i++)
            {
                if (list[i] is JsonObject o)
                {
                    into.Add(PassiveData.Read(o, issues, $"effects[{i}]"));
                }
                else
                {
                    issues.Error($"effects[{i}]", "эффект должен быть объектом");
                }
            }
        }
    }

    public static void Validate(IReadOnlyList<PassiveData> effects, IssueList issues)
    {
        if (effects.Count > PassiveData.MaxPerUnit)
        {
            issues.Error("effects", $"не больше {PassiveData.MaxPerUnit} эффектов");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < effects.Count; i++)
        {
            effects[i].Validate(issues, $"effects[{i}]");
            if (!ids.Add(effects[i].Id))
            {
                issues.Error($"effects[{i}].id", $"два эффекта с ключом «{effects[i].Id}»");
            }
        }
    }

    public static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode)v).ToArray());
}

/// <summary>
/// A numeric characteristic of the registry (spec part 6 §5.7 <c>unit_stat</c>, D-158): a number every unit has (its base
/// value or the unit's own), changed by passives and tags. It changes the battle only through a mechanic that reads it;
/// none does in this edition, so e.g. Agility is shown and modified but has no invented effect (P6-32).
/// </summary>
public sealed class UnitStatData : IContentData
{
    public const string Agility = "core.stat.agility";

    /// <summary>Unit label: «очки», «%», «гекс/с»…</summary>
    public string Unit { get; set; } = "очки";

    public double BaseValue { get; set; }

    public double Min { get; set; }

    public double Max { get; set; } = 1_000_000;

    /// <summary>Digits after the decimal point of the shown and stored final value (0…4).</summary>
    public int Decimals { get; set; }

    public JsonObject ToJson() => new() { ["unit"] = Unit, ["baseValue"] = BaseValue, ["min"] = Min, ["max"] = Max, ["decimals"] = Decimals };

    public static UnitStatData Read(JsonObject obj, IssueList issues) => new()
    {
        Unit = JsonFields.Text(obj, "unit", "очки", issues, string.Empty),
        BaseValue = JsonFields.Number(obj, "baseValue", 0, issues, string.Empty),
        Min = JsonFields.Number(obj, "min", 0, issues, string.Empty),
        Max = JsonFields.Number(obj, "max", 1_000_000, issues, string.Empty),
        Decimals = JsonFields.Integer(obj, "decimals", 0, issues, string.Empty),
    };

    public IEnumerable<ContentRef> References() => Array.Empty<ContentRef>();

    public void Validate(ValidationContext context)
    {
        if (!double.IsFinite(Min) || !double.IsFinite(Max) || Min > Max || Math.Abs(Min) > 1e9 || Math.Abs(Max) > 1e9)
        {
            context.Issues.Error("max", "диапазон: конечные числа, минимум не больше максимума");
        }
        else if (!double.IsFinite(BaseValue) || BaseValue < Min || BaseValue > Max)
        {
            context.Issues.Error("baseValue", $"базовое значение — в диапазоне {Min.ToString("0.##", CultureInfo.InvariantCulture)}…{Max.ToString("0.##", CultureInfo.InvariantCulture)}");
        }

        if (Decimals is < 0 or > 4)
        {
            context.Issues.Error("decimals", "знаков после запятой — от 0 до 4");
        }

        if (Unit.Length > 16)
        {
            context.Issues.Error("unit", "единица — до 16 символов");
        }
    }

    public double Clamp(double value) => Math.Round(Math.Clamp(value, Min, Max), Decimals, MidpointRounding.AwayFromZero);

    public string Summary() => $"база {BaseValue.ToString("0.##", CultureInfo.InvariantCulture)} {Unit}";

    public IContentData Clone() => new UnitStatData { Unit = Unit, BaseValue = BaseValue, Min = Min, Max = Max, Decimals = Decimals };
}

/// <summary>
/// A node of the species tree (spec part 6 §5.2): exactly one parent or a root, effects inherited by every descendant once.
/// An effect of a descendant with the same id as an ancestor's replaces it (or disables it, or stacks with it — its
/// <see cref="PassiveData.Inherit"/>). Cycles are refused.
/// </summary>
public sealed class SpeciesData : IContentData
{
    public const int MaxDepth = 16;

    public string Parent { get; set; } = string.Empty;

    public string Icon { get; set; } = string.Empty;

    public List<PassiveData> Effects { get; } = new();

    public JsonObject ToJson() => new() { ["parent"] = Parent, ["icon"] = Icon, ["effects"] = EffectLists.Write(Effects) };

    public static SpeciesData Read(JsonObject obj, IssueList issues)
    {
        var data = new SpeciesData
        {
            Parent = JsonFields.Text(obj, "parent", string.Empty, issues, string.Empty),
            Icon = JsonFields.Text(obj, "icon", string.Empty, issues, string.Empty),
        };
        EffectLists.Read(obj, data.Effects, issues);
        return data;
    }

    public IEnumerable<ContentRef> References()
    {
        if (Parent.Length > 0)
        {
            yield return new ContentRef(ContentTypeRegistry.Species, Parent);
        }

        foreach (var r in Effects.SelectMany(e => e.References()).Distinct())
        {
            yield return r;
        }
    }

    public void RemapReferences(IReadOnlyDictionary<ContentRef, string> newIds)
    {
        if (newIds.TryGetValue(new ContentRef(ContentTypeRegistry.Species, Parent), out var parent))
        {
            Parent = parent;
        }

        foreach (var e in Effects)
        {
            e.RemapReferences(newIds);
        }
    }

    public void Validate(ValidationContext context)
    {
        if (Parent.Length > 0 && !ContentIds.IsValid(Parent))
        {
            context.Issues.Error("parent", $"неверная ссылка на вид «{Parent}»");
        }

        if (Icon.Length > 4)
        {
            context.Issues.Error("icon", "значок — до 4 символов");
        }

        EffectLists.Validate(Effects, context.Issues);
    }

    public string Summary() => (Parent.Length == 0 ? "корень дерева" : "потомок") + (Effects.Count > 0 ? $" · эффектов: {Effects.Count}" : string.Empty);

    public IContentData Clone()
    {
        var copy = new SpeciesData { Parent = Parent, Icon = Icon };
        copy.Effects.AddRange(Effects.Select(e => e.Clone()));
        return copy;
    }
}

/// <summary>
/// A shared reusable trait (spec part 6 §5.3, §5.6): a category, effects (maybe none — a pure classification), the species
/// it allows, the traits it needs or excludes, an exclusive group with its conflict policy and the stacking policy.
/// </summary>
public sealed class TraitData : IContentData
{
    public const string PolicyError = "error";
    public const string PolicyReplace = "replace";
    public const string PolicyHighest = "highest_priority";
    public const string StackUnique = "unique";
    public const string StackStack = "stack";

    public static IReadOnlyList<(string Value, string Label)> Categories { get; } = new[]
    {
        ("feature", "особенность"), ("state", "состояние"), ("class", "класс / роль"), ("faction", "фракция"), ("material", "материал"),
    };

    public string Category { get; set; } = "feature";

    public string Icon { get; set; } = string.Empty;

    public string Color { get; set; } = "#C9A86A";

    public List<PassiveData> Effects { get; } = new();

    public List<string> AllowedSpecies { get; } = new();

    public List<string> RequiredTraits { get; } = new();

    public List<string> ExcludedTraits { get; } = new();

    /// <summary>Traits of one group are mutually exclusive (e.g. «Здоров», «Ранен»); empty — none.</summary>
    public string ExclusiveGroup { get; set; } = string.Empty;

    public string ConflictPolicy { get; set; } = PolicyError;

    public int Priority { get; set; }

    public string StackPolicy { get; set; } = StackUnique;

    public int MaxStacks { get; set; } = 1;

    public JsonObject ToJson() => new()
    {
        ["category"] = Category, ["icon"] = Icon, ["color"] = Color, ["effects"] = EffectLists.Write(Effects),
        ["allowedSpecies"] = EffectLists.Strings(AllowedSpecies), ["requiredTraits"] = EffectLists.Strings(RequiredTraits),
        ["excludedTraits"] = EffectLists.Strings(ExcludedTraits), ["exclusiveGroup"] = ExclusiveGroup, ["conflictPolicy"] = ConflictPolicy,
        ["priority"] = Priority, ["stackPolicy"] = StackPolicy, ["maxStacks"] = MaxStacks,
    };

    public static TraitData Read(JsonObject obj, IssueList issues)
    {
        var data = new TraitData
        {
            Category = JsonFields.Text(obj, "category", "feature", issues, string.Empty),
            Icon = JsonFields.Text(obj, "icon", string.Empty, issues, string.Empty),
            Color = JsonFields.Text(obj, "color", "#C9A86A", issues, string.Empty),
            ExclusiveGroup = JsonFields.Text(obj, "exclusiveGroup", string.Empty, issues, string.Empty),
            ConflictPolicy = JsonFields.Text(obj, "conflictPolicy", PolicyError, issues, string.Empty),
            Priority = JsonFields.Integer(obj, "priority", 0, issues, string.Empty),
            StackPolicy = JsonFields.Text(obj, "stackPolicy", StackUnique, issues, string.Empty),
            MaxStacks = JsonFields.Integer(obj, "maxStacks", 1, issues, string.Empty),
        };
        EffectLists.Read(obj, data.Effects, issues);
        data.AllowedSpecies.AddRange(JsonFields.TextList(obj, "allowedSpecies", issues, string.Empty));
        data.RequiredTraits.AddRange(JsonFields.TextList(obj, "requiredTraits", issues, string.Empty));
        data.ExcludedTraits.AddRange(JsonFields.TextList(obj, "excludedTraits", issues, string.Empty));
        return data;
    }

    public IEnumerable<ContentRef> References() =>
        AllowedSpecies.Select(s => new ContentRef(ContentTypeRegistry.Species, s))
            .Concat(RequiredTraits.Concat(ExcludedTraits).Select(t => new ContentRef(ContentTypeRegistry.Trait, t)))
            .Concat(Effects.SelectMany(e => e.References()))
            .Distinct();

    public void RemapReferences(IReadOnlyDictionary<ContentRef, string> newIds)
    {
        void Remap(List<string> list, string kind)
        {
            for (var i = 0; i < list.Count; i++)
            {
                if (newIds.TryGetValue(new ContentRef(kind, list[i]), out var id))
                {
                    list[i] = id;
                }
            }
        }

        Remap(AllowedSpecies, ContentTypeRegistry.Species);
        Remap(RequiredTraits, ContentTypeRegistry.Trait);
        Remap(ExcludedTraits, ContentTypeRegistry.Trait);
        foreach (var e in Effects)
        {
            e.RemapReferences(newIds);
        }
    }

    public void Validate(ValidationContext context)
    {
        var issues = context.Issues;
        if (Categories.All(c => c.Value != Category))
        {
            issues.Error("category", $"неизвестная категория «{Category}»");
        }

        if (ConflictPolicy is not (PolicyError or PolicyReplace or PolicyHighest))
        {
            issues.Error("conflictPolicy", "политика конфликта: error, replace или highest_priority");
        }

        if (StackPolicy is not (StackUnique or StackStack) || MaxStacks is < 1 or > 16)
        {
            issues.Error("stackPolicy", "повтор: unique или stack, предел 1…16");
        }

        if (ExclusiveGroup.Length > 40 || Icon.Length > 4 || Priority is < -1000 or > 1000)
        {
            issues.Error("exclusiveGroup", "группа — до 40 символов, значок — до 4, приоритет −1000…1000");
        }

        if (!HexTactics.Content.Schema.FieldSchema.IsColor(Color))
        {
            issues.Error("color", "цвет в формате #RRGGBB");
        }

        foreach (var id in AllowedSpecies.Concat(RequiredTraits).Concat(ExcludedTraits))
        {
            if (!ContentIds.IsValid(id))
            {
                issues.Error("references", $"неверная ссылка «{id}»");
            }
        }

        if (RequiredTraits.Intersect(ExcludedTraits, StringComparer.Ordinal).Any())
        {
            issues.Error("excludedTraits", "тег не может быть одновременно обязательным и исключённым");
        }

        EffectLists.Validate(Effects, issues);
    }

    public string Summary() => (Categories.FirstOrDefault(c => c.Value == Category).Label ?? Category) +
        (Effects.Count == 0 ? " · без эффектов" : $" · эффектов: {Effects.Count}") + (ExclusiveGroup.Length > 0 ? $" · группа «{ExclusiveGroup}»" : string.Empty);

    public IContentData Clone()
    {
        var copy = new TraitData
        {
            Category = Category, Icon = Icon, Color = Color, ExclusiveGroup = ExclusiveGroup, ConflictPolicy = ConflictPolicy, Priority = Priority,
            StackPolicy = StackPolicy, MaxStacks = MaxStacks,
        };
        copy.Effects.AddRange(Effects.Select(e => e.Clone()));
        copy.AllowedSpecies.AddRange(AllowedSpecies);
        copy.RequiredTraits.AddRange(RequiredTraits);
        copy.ExcludedTraits.AddRange(ExcludedTraits);
        return copy;
    }
}
