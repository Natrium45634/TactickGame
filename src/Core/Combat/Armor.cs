using System;
using System.Collections.Generic;
using HexTactics.Core.Effects;

namespace HexTactics.Core.Combat;

/// <summary>
/// Base defense A of a unit in defense points (spec part 6 §3.2), separate from the resistance R: the key
/// <see cref="Global"/> applies to every resistance-affected type, a family id to its family, a type id to that subtype.
/// Missing keys are zero; stored values are never negative (a debuff may push the raw total below zero, see
/// <see cref="ArmorMath"/>).
/// </summary>
public sealed class ArmorSet
{
    public const string Global = ResistanceSet.Global;

    public const double Min = 0;

    /// <summary>Editor limit of one stored value (spec part 6 §3.2: «тестовый диапазон 0…1 000 000»).</summary>
    public const double Max = 1_000_000;

    private readonly Dictionary<string, double> _values;

    public ArmorSet(IReadOnlyDictionary<string, double>? values = null)
    {
        _values = new Dictionary<string, double>(StringComparer.Ordinal);
        if (values is not null)
        {
            foreach (var (key, value) in values)
            {
                if (value > 0 && double.IsFinite(value))
                {
                    _values[key] = Math.Min(Max, value);
                }
            }
        }
    }

    public static ArmorSet Empty { get; } = new();

    public double this[string key] => _values.TryGetValue(key, out var v) ? v : 0;

    public IReadOnlyDictionary<string, double> Values => _values;
}

/// <summary>
/// Combat constants of a ruleset that the damage pipeline reads (spec part 6 §3.2, §10.1 <c>CombatRuleset</c>): the defense
/// constant K (PROTOTYPE_DEFAULT 100) and optional K per damage family. Content edits them in <c>combat_ruleset</c>.
/// </summary>
public sealed record CombatRules
{
    public const double DefaultDefenseK = 100;

    public const double MinDefenseK = 0.01;

    public const double MaxDefenseK = 1_000_000;

    /// <summary>K of the defense formula K / (K + A); always positive.</summary>
    public double DefenseK { get; init; } = DefaultDefenseK;

    /// <summary>K for the types of one family (family id → K); families not listed use <see cref="DefenseK"/>.</summary>
    public IReadOnlyDictionary<string, double> DefenseKByFamily { get; init; } = new Dictionary<string, double>(StringComparer.Ordinal);

    /// <summary>The K for a damage type.</summary>
    public double KFor(DamageTypeDefinition type) =>
        Math.Clamp(DefenseKByFamily.TryGetValue(type.FamilyId, out var k) ? k : DefenseK, MinDefenseK, MaxDefenseK);

    public static CombatRules Default { get; } = new();
}

/// <summary>The one defense formula (spec part 6 §3.2): applied after R and before the flat reduction.</summary>
public static class ArmorMath
{
    /// <summary>
    /// A_raw = A_global + A_family + A_subtype + A_temporary. Temporary layers come from statuses that change
    /// <see cref="Stats.Armor"/> by points (the layer is <see cref="StatusEffect.ResistanceKey"/>); the result may be negative.
    /// Each layer is counted once: a family type has no subtype layer.
    /// </summary>
    public static double Raw(ArmorSet armor, IEnumerable<StatusEffect> statuses, DamageTypeDefinition type)
    {
        var a = armor[ArmorSet.Global] + armor[type.FamilyId] + (type.IsFamily ? 0 : armor[type.Id]);
        foreach (var s in statuses)
        {
            if (s.Kind == StatusKind.Modifier && s.Stat == Stats.Armor &&
                (s.ResistanceKey == ArmorSet.Global || s.ResistanceKey == type.FamilyId || (!type.IsFamily && s.ResistanceKey == type.Id)))
            {
                a += s.Delta;
            }
        }

        return double.IsFinite(a) ? a : 0;
    }

    /// <summary>
    /// A_eff = max(0, max(0, A_raw) − flat) × (1 − clamp(percent, 0, 100) / 100): a negative total is zero (never extra
    /// damage), flat penetration first, then the percentage.
    /// </summary>
    public static double Effective(double raw, double penetrationFlat, double penetrationPercent)
    {
        var prePen = Math.Max(0, raw);
        var flat = double.IsFinite(penetrationFlat) ? Math.Max(0, penetrationFlat) : 0;
        var percent = double.IsFinite(penetrationPercent) ? Math.Clamp(penetrationPercent, 0, 100) : 0;
        return Math.Max(0, prePen - flat) * (1 - (percent / 100));
    }

    /// <summary>Damage multiplier K / (K + A_eff): A = 0 → exactly ×1 (old content keeps its numbers), A = K → ×0.5.</summary>
    public static double Multiplier(double effective, double k) => effective <= 0 ? 1 : k / (k + effective);
}
