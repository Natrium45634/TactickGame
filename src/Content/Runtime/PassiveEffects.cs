using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HexTactics.Content.Kinds;
using HexTactics.Content.Library;
using HexTactics.Core.Combat;

namespace HexTactics.Content.Runtime;

/// <summary>One source of a final value: who changed it and how («+10 % · пассив «Ярость»»), spec part 6 §4.3, §9.2.</summary>
public sealed record ValueSource(string Source, string Change);

/// <summary>A value with its base, its final number and every source between them («откуда взялось»).</summary>
public sealed record ValueBreakdown(string Key, string Label, double Base, double Final, IReadOnlyList<ValueSource> Sources);

/// <summary>
/// The single modifier engine of unit definitions (spec part 6 §4, D-154): it turns the passives of a unit into its runtime
/// characteristics, R, A, granted rules and constant damage modifiers. The battle (<see cref="RuntimeFactory"/>) and the
/// editor's preview use this one code, so the numbers in the editor are the numbers in battle.
/// </summary>
public static class PassiveEffects
{
    /// <summary>Every passive acting on the unit with its source label (personal passives; tags and species join in v0.29.0).</summary>
    public static IReadOnlyList<(PassiveData Passive, string Source)> Active(UnitData unit, ContentLibrary? library)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<(PassiveData, string)>();
        foreach (var p in unit.Passives)
        {
            if (p.Definition is not null && seen.Add(p.Id))
            {
                list.Add((p, $"пассив «{p.Name}»")); // the same passive id twice never doubles (spec part 6 §4.3)
            }
        }

        return list;
    }

    /// <summary>A characteristic with the passives: base × (1 + Σ% / 100) + Σflat, clamped to its safe range.</summary>
    public static ValueBreakdown Stat(UnitData unit, ContentLibrary? library, string stat)
    {
        var (label, baseValue, min) = stat switch
        {
            "maxHealth" => ("Максимум здоровья", unit.MaxHealth, 1.0),
            "moveSpeed" => ("Скорость движения", unit.MoveSpeed, HexTactics.Core.Effects.Stats.MinMoveSpeed),
            "attackRate" => ("Скорость атак", unit.AttackRate, HexTactics.Core.Effects.Stats.MinAttackRate),
            _ => ("Сила атаки", unit.AttackPower, HexTactics.Core.Effects.Stats.MinAttackPower),
        };
        var sources = new List<ValueSource>();
        double percent = 0, flat = 0;
        foreach (var (p, source) in Active(unit, library).Where(x => x.Passive.Stat == stat))
        {
            if (p.Operation == PassiveOperations.StatAddPercent)
            {
                percent += p.Value;
                sources.Add(new ValueSource(source, Signed(p.Value) + " %"));
            }
            else if (p.Operation == PassiveOperations.StatAddFlat)
            {
                flat += p.Value;
                sources.Add(new ValueSource(source, Signed(p.Value)));
            }
        }

        var final = Math.Max(min, ModifierMath.Stat(baseValue, percent, flat));
        if (stat == "maxHealth")
        {
            final = Math.Max(1, Math.Round(final));
        }

        return new ValueBreakdown(stat, label, baseValue, final, sources);
    }

    /// <summary>The unit's base R (sets and own values) with the passive changes of each layer in percentage points.</summary>
    public static Dictionary<string, double> Resistances(UnitData unit, ContentLibrary? library, List<ValueBreakdown>? breakdown = null)
    {
        var values = ResistanceLayers.Values(unit, library);
        foreach (var group in Active(unit, library).Where(x => x.Passive.Operation == PassiveOperations.ResistanceDeltaPP).GroupBy(x => x.Passive.Key))
        {
            var before = values.GetValueOrDefault(group.Key);
            var after = before + group.Sum(x => x.Passive.Value);
            values[group.Key] = after;
            breakdown?.Add(new ValueBreakdown(group.Key, "R " + KeyName(library, group.Key), before, after,
                group.Select(x => new ValueSource(x.Source, Signed(x.Passive.Value) + " п.п.")).ToList()));
        }

        return values;
    }

    /// <summary>The unit's base A with the passive changes of each layer: A × (1 + Σ% / 100) + Σflat, never below 0.</summary>
    public static Dictionary<string, double> Armor(UnitData unit, ContentLibrary? library, List<ValueBreakdown>? breakdown = null)
    {
        var values = ResistanceLayers.Values(unit, library, armor: true);
        foreach (var group in Active(unit, library)
                     .Where(x => x.Passive.Operation is PassiveOperations.DefenseDeltaFlat or PassiveOperations.DefenseDeltaPercent)
                     .GroupBy(x => x.Passive.Key))
        {
            var before = values.GetValueOrDefault(group.Key);
            var percent = group.Where(x => x.Passive.Operation == PassiveOperations.DefenseDeltaPercent).Sum(x => x.Passive.Value);
            var flat = group.Where(x => x.Passive.Operation == PassiveOperations.DefenseDeltaFlat).Sum(x => x.Passive.Value);
            var after = Math.Max(0, ModifierMath.Stat(before, percent, flat));
            values[group.Key] = after;
            breakdown?.Add(new ValueBreakdown(group.Key, "A " + KeyName(library, group.Key), before, after,
                group.Select(x => new ValueSource(x.Source, Signed(x.Passive.Value) + (x.Passive.Operation == PassiveOperations.DefenseDeltaPercent ? " %" : " очк."))).ToList()));
        }

        return values;
    }

    /// <summary>The constant damage modifiers of the passives (outgoing / incoming), for <see cref="UnitStats.DamageModifiers"/>.</summary>
    public static IReadOnlyList<DamageModifier> DamageModifiers(UnitData unit, ContentLibrary? library) => Active(unit, library)
        .Where(x => x.Passive.Operation is PassiveOperations.OutgoingDamagePercent or PassiveOperations.IncomingDamagePercent)
        .Select(x =>
        {
            var kind = x.Passive.Operation == PassiveOperations.OutgoingDamagePercent ? DamageModifierKind.Outgoing : DamageModifierKind.Incoming;
            var group = x.Passive.StackGroup.Length > 0 ? x.Passive.StackGroup : kind == DamageModifierKind.Outgoing ? "outgoing" : "incoming";
            return new DamageModifier(kind, x.Passive.DamageType.Length == 0 ? null : x.Passive.DamageType, x.Passive.Value, group, x.Source)
            {
                HealthBelowPercent = x.Passive.HealthBelowPercent > 0 ? x.Passive.HealthBelowPercent : null,
            };
        })
        .ToList();

    /// <summary>Defense rules granted by passives, after the unit's own ones, without repeats.</summary>
    public static IReadOnlyList<string> GrantedRules(UnitData unit, ContentLibrary? library) => Active(unit, library)
        .Where(x => x.Passive.Operation == PassiveOperations.GrantDefenseRule && x.Passive.Rule.Length > 0 && !unit.DefenseRules.Contains(x.Passive.Rule))
        .Select(x => x.Passive.Rule)
        .Distinct(StringComparer.Ordinal)
        .ToList();

    /// <summary>A passive in words: «+50 % исходящего урона: огонь · пока здоровье ниже 50 %».</summary>
    public static string Describe(PassiveData p, ContentLibrary? library)
    {
        string Type(string id) => id.Length == 0 ? "всех типов" : library?.Find("damage_type", id)?.Name.ToLowerInvariant() ?? id;
        var stat = PassiveOperations.Stats.FirstOrDefault(s => s.Value == p.Stat)?.Label ?? p.Stat;
        var text = p.Operation switch
        {
            PassiveOperations.OutgoingDamagePercent => $"{Signed(p.Value)} % исходящего урона: {Type(p.DamageType)}",
            PassiveOperations.IncomingDamagePercent => $"{Signed(p.Value)} % получаемого урона: {Type(p.DamageType)} (после R, A и плоского снижения)",
            PassiveOperations.StatAddPercent => $"{stat} {Signed(p.Value)} %",
            PassiveOperations.StatAddFlat => $"{stat} {Signed(p.Value)}",
            PassiveOperations.ResistanceDeltaPP => $"сопротивление {KeyName(library, p.Key)} {Signed(p.Value)} п.п.",
            PassiveOperations.DefenseDeltaFlat => $"защита A {KeyName(library, p.Key)} {Signed(p.Value)} очк.",
            PassiveOperations.DefenseDeltaPercent => $"защита A {KeyName(library, p.Key)} {Signed(p.Value)} %",
            PassiveOperations.GrantDefenseRule => $"правило защиты «{library?.Find("defense_rule", p.Rule)?.Name ?? p.Rule}»",
            _ => $"✗ неизвестная операция «{p.Operation}»",
        };
        if (p.Definition?.UsesCondition == true && p.HealthBelowPercent > 0)
        {
            text += $" · пока здоровье ниже {N(p.HealthBelowPercent)} %";
        }

        return p.StackGroup.Length > 0 ? text + $" · группа «{p.StackGroup}»" : text;
    }

    private static string KeyName(ContentLibrary? library, string key) =>
        key == UnitData.GlobalResistance ? "все типы" : library?.Find("damage_type", key)?.Name.ToLowerInvariant() ?? key;

    private static string Signed(double v) => v.ToString("+0.##;−0.##;0", CultureInfo.InvariantCulture);

    private static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
