using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HexTactics.Content.Kinds;
using HexTactics.Content.Library;
using HexTactics.Content.Registry;
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
    /// <summary>The species of the unit and its ancestors, from the unit's own species up to the root (spec part 6 §5.2).</summary>
    public static IReadOnlyList<ContentItem> SpeciesChain(ContentLibrary? library, string speciesId)
    {
        var chain = new List<ContentItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var id = speciesId;
        while (library is not null && id.Length > 0 && chain.Count < SpeciesData.MaxDepth && seen.Add(id) && library.Find(ContentTypeRegistry.Species, id) is { Data: SpeciesData d } item)
        {
            chain.Add(item);
            id = d.Parent;
        }

        return chain;
    }

    /// <summary>True if the species <paramref name="speciesId"/> is <paramref name="ancestorId"/> or descends from it.</summary>
    public static bool IsA(ContentLibrary? library, string speciesId, string ancestorId) => SpeciesChain(library, speciesId).Any(i => i.Id == ancestorId);

    /// <summary>
    /// The traits that act and how many times (spec part 6 §5.3, §5.6): a trait listed again acts once unless it stacks (up to
    /// its limit); of an exclusive group one acts — the first for «error» (the unit is invalid anyway), the last for
    /// «replace», the highest priority for «highest_priority»; a trait not allowed for the unit's species does not act.
    /// </summary>
    public static IReadOnlyList<(ContentItem Trait, int Times)> ActiveTraits(UnitData unit, ContentLibrary? library)
    {
        if (library is null)
        {
            return Array.Empty<(ContentItem, int)>();
        }

        var counts = new List<(ContentItem Item, TraitData Data, int Times, int Order)>();
        for (var i = 0; i < unit.Traits.Count; i++)
        {
            if (library.Find(ContentTypeRegistry.Trait, unit.Traits[i]) is not { Data: TraitData t } item || counts.Any(c => c.Item.Id == item.Id))
            {
                continue;
            }

            if (t.AllowedSpecies.Count > 0 && !t.AllowedSpecies.Any(sp => IsA(library, unit.Species, sp)))
            {
                continue;
            }

            var times = unit.Traits.Count(x => x == item.Id);
            counts.Add((item, t, t.StackPolicy == TraitData.StackStack ? Math.Min(times, t.MaxStacks) : 1, unit.Traits.LastIndexOf(item.Id)));
        }

        var result = new List<(ContentItem, int)>();
        foreach (var c in counts)
        {
            var group = c.Data.ExclusiveGroup.Length == 0 ? new[] { c } : counts.Where(o => o.Data.ExclusiveGroup == c.Data.ExclusiveGroup).ToArray();
            var winner = group.Length == 1 ? c : c.Data.ConflictPolicy switch
            {
                TraitData.PolicyReplace => group.OrderBy(o => o.Order).Last(),
                TraitData.PolicyHighest => group.OrderBy(o => o.Data.Priority).ThenBy(o => o.Order).Last(),
                _ => group[0],
            };
            if (winner.Item.Id == c.Item.Id)
            {
                result.Add((c.Item, c.Times));
            }
        }

        return result;
    }

    /// <summary>
    /// Every passive acting on the unit with its source label (spec part 6 §5.2–5.3, §4): the species effects from the root to
    /// the unit's own species (an effect with the same key replaces, disables or stacks with the ancestor's — never applied
    /// twice by inheritance), the shared traits, then the personal passives.
    /// </summary>
    public static IReadOnlyList<(PassiveData Passive, string Source)> Active(UnitData unit, ContentLibrary? library)
    {
        var list = new List<(PassiveData, string)>();
        var inherited = new List<(string Key, PassiveData Passive, string Source)>();
        foreach (var item in SpeciesChain(library, unit.Species).Reverse())
        {
            foreach (var e in ((SpeciesData)item.Data).Effects)
            {
                if (e.Inherit == PassiveData.InheritDisable)
                {
                    inherited.RemoveAll(x => x.Key == e.Id);
                    continue;
                }

                if (e.Definition is null)
                {
                    continue;
                }

                if (e.Inherit != PassiveData.InheritStack)
                {
                    inherited.RemoveAll(x => x.Key == e.Id);
                }

                inherited.Add((e.Id, e, $"вид «{item.Name}»"));
            }
        }

        list.AddRange(inherited.Select(x => (x.Passive, x.Source)));
        foreach (var (trait, times) in ActiveTraits(unit, library))
        {
            for (var n = 0; n < times; n++)
            {
                list.AddRange(((TraitData)trait.Data).Effects.Where(e => e.Definition is not null).Select(e => (e, times > 1 ? $"тег «{trait.Name}» ×{n + 1}" : $"тег «{trait.Name}»")));
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in unit.Passives)
        {
            if (p.Definition is not null && seen.Add(p.Id))
            {
                list.Add((p, $"пассив «{p.Name}»")); // the same passive id twice never doubles (spec part 6 §4.3)
            }
        }

        return list;
    }

    /// <summary>Characteristics a passive or tag can change: the engine ones and the registry ones (spec part 6 §5.7).</summary>
    public static IReadOnlyList<(string Value, string Label)> StatChoices(ContentLibrary? library) =>
        PassiveOperations.Stats.Select(s => (s.Value, s.Label))
            .Concat(library?.Items(ContentTypeRegistry.UnitStat).Where(i => !i.Archived).Select(i => (i.Id, i.Name.ToLowerInvariant())) ?? Array.Empty<(string, string)>())
            .ToList();

    /// <summary>A characteristic with the passives: base × (1 + Σ% / 100) + Σflat, clamped to its safe range.</summary>
    public static ValueBreakdown Stat(UnitData unit, ContentLibrary? library, string stat)
    {
        if (!PassiveOperations.EngineStats.Contains(stat))
        {
            return RegistryStat(unit, library, stat);
        }

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

    /// <summary>A registry characteristic (Agility…): the unit's own base or the stat's, the passives, its range and rounding.</summary>
    private static ValueBreakdown RegistryStat(UnitData unit, ContentLibrary? library, string stat)
    {
        var def = library?.Find(ContentTypeRegistry.UnitStat, stat);
        var data = def?.Data as UnitStatData ?? new UnitStatData();
        var baseValue = unit.CustomStats.TryGetValue(stat, out var own) ? own : data.BaseValue;
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

        return new ValueBreakdown(stat, def?.Name ?? stat, baseValue, data.Clamp(ModifierMath.Stat(baseValue, percent, flat)), sources);
    }

    /// <summary>«Per matching ally» bonuses of the unit (spec part 6 §5.5) for <see cref="UnitStats.CountModifiers"/>.</summary>
    /// <summary>
    /// The unit's own critical chance in percentage points with its species, traits and passives (spec part 6 §7.1); the base of
    /// the combat rules is added in battle. Shared by the runtime and the editor preview.
    /// </summary>
    public static double CritChance(UnitData unit, ContentLibrary? library) =>
        unit.CritChance + Active(unit, library).Where(x => x.Passive.Operation == PassiveOperations.CritChanceDelta).Sum(x => x.Passive.Value);

    /// <summary>The attacker's surprise chance bonus in percentage points (spec part 6 §6.3 p.2), from its species, traits and passives.</summary>
    public static double SurpriseChanceBonus(UnitData unit, ContentLibrary? library) =>
        Active(unit, library).Where(x => x.Passive.Operation == PassiveOperations.SurpriseChanceDelta).Sum(x => x.Passive.Value);

    public static IReadOnlyList<CountModifier> CountModifiers(UnitData unit, ContentLibrary? library) => Active(unit, library)
        .Where(x => x.Passive.Operation == PassiveOperations.CountMatchingUnits)
        .Select(x => new CountModifier(x.Passive.Stat, x.Passive.Value, x.Passive.CountSpecies.Length == 0 ? null : x.Passive.CountSpecies, x.Passive.CountSamePack, x.Source)
        {
            IncludeSelf = x.Passive.CountSelf, Radius = x.Passive.CountRadius, MaxCount = x.Passive.CountMax,
        })
        .ToList();

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
        var stat = PassiveOperations.Stats.FirstOrDefault(s => s.Value == p.Stat)?.Label ?? library?.Find(ContentTypeRegistry.UnitStat, p.Stat)?.Name.ToLowerInvariant() ?? p.Stat;
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
            PassiveOperations.CritChanceDelta => $"шанс крита {Signed(p.Value)} п.п.",
            PassiveOperations.CritImmunity => "невосприимчив к критам",
            PassiveOperations.SurpriseImmunity => "невосприимчив к внезапным ударам",
            PassiveOperations.SurpriseChanceDelta => $"шанс внезапного удара {Signed(p.Value)} п.п.",
            PassiveOperations.CountMatchingUnits => $"{stat} {Signed(p.Value)} % за каждого живого союзника" +
                (p.CountSpecies.Length > 0 ? $" вида «{library?.Find(ContentTypeRegistry.Species, p.CountSpecies)?.Name ?? p.CountSpecies}» и потомков" : string.Empty) +
                (p.CountSamePack ? ", той же стаи" : string.Empty) + (p.CountSelf ? ", включая себя" : string.Empty) +
                (p.CountRadius > 0 ? $", в радиусе {p.CountRadius}" : string.Empty) + (p.CountMax > 0 ? $", не больше {p.CountMax}" : string.Empty),
            _ => $"✗ неизвестная операция «{p.Operation}»",
        };
        if (p.Inherit != PassiveData.InheritReplace)
        {
            text += p.Inherit == PassiveData.InheritDisable ? " · отключает унаследованный" : " · складывается с унаследованным";
        }

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
