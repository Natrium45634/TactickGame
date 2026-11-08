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

    public JsonObject ToJson() => new() { ["defenseK"] = DefenseK, ["defenseKByFamily"] = UnitData.ValuesJson(DefenseKByFamily) };

    public static CombatRulesetData Read(JsonObject obj, IssueList issues)
    {
        var data = new CombatRulesetData { DefenseK = JsonFields.Number(obj, "defenseK", HexTactics.Core.Combat.CombatRules.DefaultDefenseK, issues, string.Empty) };
        UnitData.ReadValues(obj, "defenseKByFamily", data.DefenseKByFamily, issues, "K семейства — число");
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
        (DefenseKByFamily.Count > 0 ? $" · своих K семейств: {DefenseKByFamily.Count}" : string.Empty);

    public IContentData Clone()
    {
        var copy = new CombatRulesetData { DefenseK = DefenseK };
        foreach (var (key, value) in DefenseKByFamily)
        {
            copy.DefenseKByFamily[key] = value;
        }

        return copy;
    }
}
