using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using HexTactics.Content.Library;
using HexTactics.Content.Model;
using HexTactics.Content.Registry;

namespace HexTactics.Content.Kinds;

/// <summary>
/// A resistance set of the library (spec part 5 §6.1 «Наборы сопротивлений», §6.4, §9.1 <c>resistance_profile</c>): a saved
/// table «global / type id → R» that units apply by reference; editable data, not a combat mechanic of its own. A set may
/// extend another (<see cref="Parent"/>): own values replace the parent's per key. Cycles are refused.
/// </summary>
public sealed class ResistanceProfileData : IContentData
{
    /// <summary>Set this one extends; empty — none.</summary>
    public string Parent { get; set; } = string.Empty;

    /// <summary>Own values, percentage points −500…+500.</summary>
    public SortedDictionary<string, double> Values { get; } = new(StringComparer.Ordinal);

    public JsonObject ToJson()
    {
        var values = new JsonObject();
        foreach (var (key, value) in Values)
        {
            values[key] = value;
        }

        return new JsonObject { ["parent"] = Parent, ["values"] = values };
    }

    public static ResistanceProfileData Read(JsonObject obj, IssueList issues)
    {
        var data = new ResistanceProfileData { Parent = JsonFields.Text(obj, "parent", string.Empty, issues, string.Empty) };
        if (obj["values"] is JsonObject values)
        {
            foreach (var (key, node) in values)
            {
                if (node is JsonValue v && v.TryGetValue<double>(out var r))
                {
                    data.Values[key] = r;
                }
                else
                {
                    issues.Error($"values.{key}", "сопротивление — число в процентных пунктах");
                }
            }
        }
        else if (obj["values"] is not null)
        {
            issues.Error("values", "ожидается объект «тип урона → процент»");
        }

        return data;
    }

    public IEnumerable<ContentRef> References()
    {
        if (Parent.Length > 0)
        {
            yield return new ContentRef(ContentTypeRegistry.ResistanceProfile, Parent);
        }

        foreach (var key in Values.Keys)
        {
            if (key != UnitData.GlobalResistance)
            {
                yield return new ContentRef(ContentTypeRegistry.DamageType, key);
            }
        }
    }

    public void RemapReferences(IReadOnlyDictionary<ContentRef, string> newIds)
    {
        if (newIds.TryGetValue(new ContentRef(ContentTypeRegistry.ResistanceProfile, Parent), out var parent))
        {
            Parent = parent;
        }

        foreach (var key in Values.Keys.ToList())
        {
            if (newIds.TryGetValue(new ContentRef(ContentTypeRegistry.DamageType, key), out var renamed))
            {
                var value = Values[key];
                Values.Remove(key);
                Values[renamed] = value;
            }
        }
    }

    public void Validate(ValidationContext context)
    {
        if (Parent.Length > 0 && !ContentIds.IsValid(Parent))
        {
            context.Issues.Error("parent", $"неверная ссылка на набор «{Parent}»");
        }

        if (Values.Count > 64)
        {
            context.Issues.Error("values", "не больше 64 значений в наборе");
        }

        foreach (var (key, value) in Values)
        {
            if (!double.IsFinite(value) || value < UnitData.MinResistance || value > UnitData.MaxResistance)
            {
                context.Issues.Error($"values.{key}", $"сопротивление «{key}»: от −500 до +500 %, сейчас {value.ToString("0.##", CultureInfo.InvariantCulture)}");
            }
            else if (key != UnitData.GlobalResistance && !ContentIds.IsValid(key))
            {
                context.Issues.Error($"values.{key}", $"неверный ключ сопротивления «{key}»");
            }
        }
    }

    public string Summary() => Values.Count == 0 ? "пустой набор" : $"значений: {Values.Count}" + (Parent.Length > 0 ? " · на основе другого набора" : string.Empty);

    public IContentData Clone()
    {
        var copy = new ResistanceProfileData { Parent = Parent };
        foreach (var (key, value) in Values)
        {
            copy.Values[key] = value;
        }

        return copy;
    }
}

/// <summary>Where a resistance value of a unit comes from (spec part 5 §6.4: inherited or overridden).</summary>
public sealed record ResistanceValue(double Value, string? ProfileId, bool Own);

/// <summary>
/// The layering of resistance sets and unit values (spec part 5 §6.4, D-133): sets in the unit's order (a later set replaces
/// an earlier one per key; a set's own values replace its parent's), then the unit's own values replace everything. The
/// result are the unit's base resistances; the combat formula then sums global + family + subtype as before.
/// </summary>
public static class ResistanceLayers
{
    public const int MaxDepth = 8;

    /// <summary>The effective values of a set with its parents (missing or cyclic parents end the chain).</summary>
    public static IReadOnlyDictionary<string, double> Of(ContentLibrary library, string profileId)
    {
        var chain = new List<ResistanceProfileData>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var id = profileId;
        while (id.Length > 0 && chain.Count < MaxDepth && seen.Add(id) && library.Find(ContentTypeRegistry.ResistanceProfile, id)?.Data is ResistanceProfileData data)
        {
            chain.Add(data);
            id = data.Parent;
        }

        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            foreach (var (key, value) in chain[i].Values)
            {
                result[key] = value;
            }
        }

        return result;
    }

    /// <summary>Every key that has a value for this unit, with its source.</summary>
    public static SortedDictionary<string, ResistanceValue> Effective(UnitData unit, ContentLibrary? library)
    {
        var result = new SortedDictionary<string, ResistanceValue>(StringComparer.Ordinal);
        if (library is not null)
        {
            foreach (var profile in unit.ResistanceProfiles)
            {
                foreach (var (key, value) in Of(library, profile))
                {
                    result[key] = new ResistanceValue(value, profile, false);
                }
            }
        }

        foreach (var (key, value) in unit.Resistances)
        {
            result[key] = new ResistanceValue(value, null, true);
        }

        return result;
    }

    /// <summary>The base values for the combat formula (zeros dropped).</summary>
    public static Dictionary<string, double> Values(UnitData unit, ContentLibrary? library) =>
        Effective(unit, library).Where(x => x.Value.Value != 0).ToDictionary(x => x.Key, x => x.Value.Value, StringComparer.Ordinal);
}
