using System;
using System.Collections.Generic;
using System.Linq;

namespace HexTactics.Core.Combat;

/// <summary>Where a percentage damage modifier acts (spec part 6 §4.2, §8 stages 2 and 8).</summary>
public enum DamageModifierKind
{
    /// <summary>Damage the unit deals: before D0 is frozen (before conversions, R and A).</summary>
    Outgoing = 0,

    /// <summary>Damage the unit takes: after R, A and the flat reduction, before the cap and shields.</summary>
    Incoming = 1,
}

/// <summary>
/// A constant percentage modifier of damage from a passive (spec part 6 §4.2 <c>OutgoingDamagePercent</c> /
/// <c>IncomingDamagePercent</c>). <see cref="DamageTypeId"/> null — every type (incoming: every resistance-affected type);
/// otherwise the type itself or, for a family id, the family with its subtypes. <see cref="Percent"/> is in percentage points
/// (+50 = ×1.5 alone). Modifiers of one <see cref="Group"/> add up; groups multiply.
/// </summary>
public sealed record DamageModifier(DamageModifierKind Kind, string? DamageTypeId, double Percent, string Group, string Source)
{
    /// <summary>Acts only while the owner's health is below this percent of its maximum (spec part 6 §4.2 <c>ConditionalEffect</c>).</summary>
    public double? HealthBelowPercent { get; init; }

    public bool Matches(DamageTypeDefinition type) => DamageTypeId is null
        ? Kind == DamageModifierKind.Outgoing || type.Strategy == DamageStrategy.ResistanceAffected
        : DamageTypeId == type.Id || DamageTypeId == type.FamilyId;
}

/// <summary>One applied source of a modifier, for «откуда взялось» (spec part 6 §4.3, §9.2).</summary>
public readonly record struct ModifierSource(string Source, double Percent, string Group);

/// <summary>The one rule of combining percentage modifiers (spec part 6 §4.3).</summary>
public static class ModifierMath
{
    /// <summary>
    /// The multiplier of every matching modifier of <paramref name="kind"/>: percentage points of one group add up
    /// (+50 % and +20 % → ×1.7; −50 % and −25 % → ×0.25), groups multiply; never below zero (no healing from damage).
    /// </summary>
    public static double DamageMultiplier(IEnumerable<DamageModifier> modifiers, DamageModifierKind kind, DamageTypeDefinition type, double healthPercent,
        List<ModifierSource>? applied = null)
    {
        var groups = new SortedDictionary<string, double>(StringComparer.Ordinal);
        foreach (var m in modifiers)
        {
            if (m.Kind != kind || !m.Matches(type) || !double.IsFinite(m.Percent) || (m.HealthBelowPercent is { } below && healthPercent >= below))
            {
                continue;
            }

            groups[m.Group] = groups.GetValueOrDefault(m.Group) + m.Percent;
            applied?.Add(new ModifierSource(m.Source, m.Percent, m.Group));
        }

        var multiplier = 1.0;
        foreach (var sum in groups.Values)
        {
            multiplier *= Math.Max(0, 1 + (sum / 100));
        }

        return multiplier;
    }

    /// <summary>
    /// A characteristic with its passive modifiers (spec part 6 §4.2, §4.3, D-154): base × (1 + Σ% / 100) + Σflat — the
    /// percentage acts on the accumulated base, flat bonuses are added after; the caller clamps to the safe range.
    /// </summary>
    public static double Stat(double baseValue, double percentSum, double flatSum) => (baseValue * (1 + (percentSum / 100))) + flatSum;
}
