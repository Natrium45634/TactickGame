using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using HexTactics.Content.Model;
using HexTactics.Content.Registry;
using HexTactics.Content.Schema;

namespace HexTactics.Content.Kinds;

/// <summary>
/// Combat rules of a project or a map (spec part 6 §3.2, §9.5, §10.1 <c>CombatRuleset</c>): constants of the one damage
/// pipeline that the creator edits instead of code. In this edition: K of the defense formula K / (K + A) and optional K per
/// damage family. Direction and critical hits are added to the same kind by later versions (D-143).
/// </summary>
public sealed class CombatRulesetData : IContentData
{
    /// <summary>The built-in ruleset of new maps (spec part 6 §10.3 «в новых проектах предложить пресет»).</summary>
    public const string Standard = "core.ruleset.standard";

    /// <summary>
    /// The built-in compatible ruleset of maps made before part 6 (spec part 6 §10.3 <c>legacy_combat_ruleset</c>): a map
    /// without a ruleset uses it, so the old results never change silently.
    /// </summary>
    public const string Legacy = "core.ruleset.legacy";

    public const int MaxFamilies = 32;

    /// <summary>PROTOTYPE_DEFAULT K = 100 (spec part 6 §3.2): A = 100 halves the damage after R.</summary>
    public double DefenseK { get; set; } = HexTactics.Core.Combat.CombatRules.DefaultDefenseK;

    /// <summary>K for the types of one family (family damage type id → K).</summary>
    public SortedDictionary<string, double> DefenseKByFamily { get; } = new(StringComparer.Ordinal);

    /// <summary>Surprise by the side of the defender (spec part 6 §6): off for a new set until the author turns it on.</summary>
    public bool SurpriseEnabled { get; set; }

    /// <summary>Chance in percent by side, clockwise from the front: front, front right, rear right, rear, rear left, front left.</summary>
    public double[] SurpriseChances { get; set; } = { 0, 25, 75, 100, 75, 25 };

    public double SurpriseMultiplier { get; set; } = 1.5;

    /// <summary>Critical hits (spec part 6 §7): off for a new set until the author turns them on.</summary>
    public bool CritEnabled { get; set; }

    public double BaseCritChance { get; set; }

    public double CritMultiplier { get; set; } = 1.5;

    public double SuperCritMultiplier { get; set; } = 2.25;

    public static IReadOnlyList<FieldSchema> Fields { get; } = new FieldSchema[]
    {
        new()
        {
            Key = "defenseK", Label = "K защиты", Kind = FieldKind.Number, Default = 100.0,
            Min = HexTactics.Core.Combat.CombatRules.MinDefenseK, Max = HexTactics.Core.Combat.CombatRules.MaxDefenseK,
            Hint = "Урон после сопротивления × K / (K + A): при A = K урон уменьшается вдвое. Больше K — защита слабее.",
            Get = o => ((CombatRulesetData)o).DefenseK, Set = (o, v) => ((CombatRulesetData)o).DefenseK = (double)v!,
        },
    };

    public JsonObject ToJson() => new()
    {
        ["defenseK"] = DefenseK, ["defenseKByFamily"] = UnitData.ValuesJson(DefenseKByFamily),
        ["surpriseEnabled"] = SurpriseEnabled, ["surpriseChances"] = new JsonArray(SurpriseChances.Select(c => (JsonNode)c).ToArray()), ["surpriseMultiplier"] = SurpriseMultiplier,
        ["critEnabled"] = CritEnabled, ["baseCritChance"] = BaseCritChance, ["critMultiplier"] = CritMultiplier, ["superCritMultiplier"] = SuperCritMultiplier,
    };

    public static CombatRulesetData Read(JsonObject obj, IssueList issues)
    {
        var data = new CombatRulesetData
        {
            DefenseK = JsonFields.Number(obj, "defenseK", HexTactics.Core.Combat.CombatRules.DefaultDefenseK, issues, string.Empty),
            SurpriseEnabled = JsonFields.Flag(obj, "surpriseEnabled", false, issues, string.Empty),
            SurpriseMultiplier = JsonFields.Number(obj, "surpriseMultiplier", 1.5, issues, string.Empty),
            CritEnabled = JsonFields.Flag(obj, "critEnabled", false, issues, string.Empty),
            BaseCritChance = JsonFields.Number(obj, "baseCritChance", 0, issues, string.Empty),
            CritMultiplier = JsonFields.Number(obj, "critMultiplier", 1.5, issues, string.Empty),
            SuperCritMultiplier = JsonFields.Number(obj, "superCritMultiplier", 2.25, issues, string.Empty),
        };
        UnitData.ReadValues(obj, "defenseKByFamily", data.DefenseKByFamily, issues, "K семейства — число");
        if (obj["surpriseChances"] is JsonArray chances)
        {
            data.SurpriseChances = chances.Select(n => n is JsonValue v && v.TryGetValue<double>(out var d) ? d : double.NaN).ToArray();
        }

        return data;
    }

    public IEnumerable<ContentRef> References() =>
        DefenseKByFamily.Keys.Select(k => new ContentRef(ContentTypeRegistry.DamageType, k));

    public void RemapReferences(IReadOnlyDictionary<ContentRef, string> newIds) => UnitData.RemapKeys(DefenseKByFamily, newIds);

    public void Validate(ValidationContext context)
    {
        foreach (var field in Fields)
        {
            if (field.Check(field.Get!(this), field.Key) is { } issue)
            {
                context.Issues.AddRange(new[] { issue });
            }
        }

        if (SurpriseChances.Length != 6 || SurpriseChances.Any(c => !double.IsFinite(c) || c < 0 || c > 100))
        {
            context.Issues.Error("surpriseChances", "шесть шансов внезапности, каждый от 0 до 100 %");
        }

        foreach (var (key, value, min, max) in new[] { ("surpriseMultiplier", SurpriseMultiplier, 0.0, 100.0), ("baseCritChance", BaseCritChance, -1000.0, 1000.0), ("critMultiplier", CritMultiplier, 0.0, 100.0), ("superCritMultiplier", SuperCritMultiplier, 0.0, 100.0) })
        {
            if (!double.IsFinite(value) || value < min || value > max)
            {
                context.Issues.Error(key, $"«{key}»: от {min.ToString(CultureInfo.InvariantCulture)} до {max.ToString(CultureInfo.InvariantCulture)}, сейчас {value.ToString("0.##", CultureInfo.InvariantCulture)}");
            }
        }

        if (DefenseKByFamily.Count > MaxFamilies)
        {
            context.Issues.Error("defenseKByFamily", $"не больше {MaxFamilies} семейств со своим K");
        }

        foreach (var (key, value) in DefenseKByFamily)
        {
            if (!ContentIds.IsValid(key))
            {
                context.Issues.Error($"defenseKByFamily.{key}", $"неверный ключ семейства «{key}»");
            }
            else if (!double.IsFinite(value) || value < HexTactics.Core.Combat.CombatRules.MinDefenseK || value > HexTactics.Core.Combat.CombatRules.MaxDefenseK)
            {
                context.Issues.Error($"defenseKByFamily.{key}", $"K семейства «{key}»: больше 0 и не больше 1 000 000, сейчас {value.ToString("0.##", CultureInfo.InvariantCulture)}");
            }
        }
    }

    public string Summary() => $"K = {DefenseK.ToString("0.##", CultureInfo.InvariantCulture)}" +
        (DefenseKByFamily.Count > 0 ? $" · своих K семейств: {DefenseKByFamily.Count}" : string.Empty) +
        (SurpriseEnabled ? " · внезапность" : string.Empty) + (CritEnabled ? " · криты" : string.Empty);

    public IContentData Clone()
    {
        var copy = new CombatRulesetData
        {
            DefenseK = DefenseK, SurpriseEnabled = SurpriseEnabled, SurpriseChances = (double[])SurpriseChances.Clone(), SurpriseMultiplier = SurpriseMultiplier,
            CritEnabled = CritEnabled, BaseCritChance = BaseCritChance, CritMultiplier = CritMultiplier, SuperCritMultiplier = SuperCritMultiplier,
        };
        foreach (var (key, value) in DefenseKByFamily)
        {
            copy.DefenseKByFamily[key] = value;
        }

        return copy;
    }
}
