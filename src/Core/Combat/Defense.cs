using System;
using System.Collections.Generic;

namespace HexTactics.Core.Combat;

/// <summary>When a defense rule is considered (spec part 5 §6.3 «КОГДА»).</summary>
public enum DefenseTrigger
{
    /// <summary>On every hit by a matching component (contact): immunities, conversions, flat reduction, cap.</summary>
    OnHit = 0,

    /// <summary>After the hit took health (actual loss &gt; 0): retaliation.</summary>
    OnHealthLoss = 1,

    /// <summary>On the attacker's side, after its hit took health from a target: vampirism.</summary>
    OnDamageDealt = 2,

    /// <summary>When a status is applied to the defender: status immunity.</summary>
    OnStatusApplied = 3,
}

/// <summary>What a defense rule does (spec part 5 §4.1); every kind has its own stage in the damage resolver.</summary>
public enum DefenseActionKind
{
    /// <summary>Stage 3: matching components take no health.</summary>
    Immunity = 0,

    /// <summary>Stage 4: a share of D0 is removed without any other effect.</summary>
    Negate = 1,

    /// <summary>Stage 4: a share of D0 heals the defender (before the rest of the hit).</summary>
    ConvertToHealing = 2,

    /// <summary>Stage 4: a share of D0 refills a named shield of the defender.</summary>
    ConvertToShield = 3,

    /// <summary>Stage 4: a share of D0 becomes a new reactive damage packet towards targets.</summary>
    RedirectAsDamage = 4,

    /// <summary>Stage 5: subtracted from the damage after resistance (not for true-like types).</summary>
    FlatReduction = 5,

    /// <summary>Stage 5: upper bound of one hit of a component.</summary>
    DamageCap = 6,

    /// <summary>Stage 7: an independent answer based on the actual health (or shield) loss; does not lower the hit.</summary>
    Retaliate = 7,

    /// <summary>Stage 7 on the attacker: heals it for a share of the health it actually took.</summary>
    Vampirism = 8,

    /// <summary>Blocks statuses of a category when they are applied (spec part 5 §4.1).</summary>
    StatusImmunity = 9,
}

/// <summary>Who receives a redirected or retaliating packet (spec part 5 §4.4).</summary>
public enum ReactionTargeting
{
    /// <summary>The source of the hit (if it still matches the filter).</summary>
    Attacker = 0,

    /// <summary>Every unit within the radius of the defender's hex (the defender itself only with «include center»).</summary>
    Area = 1,
}

/// <summary>Which units a reaction may hit, relative to the defender.</summary>
public enum ReactionFilter
{
    EnemiesOfDefender = 0,
    AlliesOfDefender = 1,
    All = 2,
}

/// <summary>What a retaliation is based on (spec part 5 §4.4).</summary>
public enum RetaliationBase
{
    HealthLoss = 0,
    ShieldLoss = 1,
}

/// <summary>One action of a defense rule. Unused fields of a kind are ignored.</summary>
public sealed record DefenseAction
{
    public required DefenseActionKind Kind { get; init; }

    /// <summary>Share of D0 (conversions) or of the base (retaliation, vampirism), 0…1.</summary>
    public double Fraction { get; init; }

    /// <summary>HP for flat reduction and damage cap.</summary>
    public double Amount { get; init; }

    public ReactionTargeting Targeting { get; init; } = ReactionTargeting.Attacker;

    public int Radius { get; init; } = 1;

    public bool IncludeCenter { get; init; }

    public ReactionFilter Filter { get; init; } = ReactionFilter.EnemiesOfDefender;

    /// <summary>For several targets: split the packet among them instead of the full packet to each.</summary>
    public bool Split { get; init; }

    /// <summary>Damage type of the produced packet; null = the same type as the incoming hit.</summary>
    public string? ResponseDamageType { get; init; }

    public RetaliationBase Base { get; init; } = RetaliationBase.HealthLoss;

    /// <summary>Shield refilled by <see cref="DefenseActionKind.ConvertToShield"/>.</summary>
    public string? Shield { get; init; }

    /// <summary>Status category blocked by <see cref="DefenseActionKind.StatusImmunity"/>: buff, debuff, periodic or all.</summary>
    public string StatusCategory { get; init; } = "debuff";
}

/// <summary>
/// A declared defense rule (spec part 5 §6.3 «условие → реакция»): when, against which damage, under which AND-conditions,
/// what to do. Rules are data; the damage resolver executes them in its fixed stages.
/// </summary>
public sealed record DefenseRule
{
    public required string Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public DefenseTrigger Trigger { get; init; }

    /// <summary>Damage type or family the rule reacts to; null = any type (true damage included).</summary>
    public string? DamageType { get; init; }

    /// <summary>Additional filter: the hit's damage type carries this tag (spec part 5 §4.2 «группа тегов»); null = any.</summary>
    public string? DamageTag { get; init; }

    /// <summary>Condition: the effective resistance (after penetration) against the hit is at least this value.</summary>
    public double? MinEffectiveResistance { get; init; }

    /// <summary>Condition: the defender's health is below this percentage of its maximum before the hit.</summary>
    public double? HealthBelowPercent { get; init; }

    /// <summary>Lets the rule act on a hit the defender is immune to (spec part 5 §4.2); off by default.</summary>
    public bool TriggerOnImmuneContact { get; init; }

    /// <summary>Lower first; equal priorities by id (spec part 5 §4.3).</summary>
    public int Priority { get; init; } = 100;

    /// <summary>Game seconds between two activations; 0 = none.</summary>
    public double Cooldown { get; init; }

    /// <summary>Activations per unit and match; 0 = unlimited.</summary>
    public int MaxTriggers { get; init; }

    public IReadOnlyList<DefenseAction> Actions { get; init; } = Array.Empty<DefenseAction>();
}

/// <summary>A shield of a unit definition (spec part 5 §4.5): a pool of protection that takes calculated damage before health.</summary>
public sealed record ShieldDefinition(string Name, double Capacity, string? DamageType, int Order);

/// <summary>The live points of one shield of a unit.</summary>
public sealed class ShieldState
{
    public ShieldState(ShieldDefinition definition)
    {
        Definition = definition;
        Current = Math.Max(0, definition.Capacity);
    }

    public ShieldDefinition Definition { get; }

    public double Current { get; set; }
}
