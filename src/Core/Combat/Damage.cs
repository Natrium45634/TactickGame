using System;
using System.Collections.Generic;
using HexTactics.Core.Effects;
using HexTactics.Core.Units;

namespace HexTactics.Core.Combat;

/// <summary>
/// How a damage type is calculated against the target's defenses (spec part 5 §2.2). New strategies arrive only with new
/// handlers in code; content picks one of these.
/// </summary>
public enum DamageStrategy
{
    /// <summary>Numeric resistance (global + family + subtype + temporary, then penetration) changes the damage.</summary>
    ResistanceAffected = 0,

    /// <summary>Bypasses numeric resistance (and flat reduction); explicit immunities and shields still apply.</summary>
    TrueLike = 1,
}

/// <summary>
/// A damage type at runtime (spec part 5 §2): the nature of a hit — not its shape (single/AoE) or delivery. A family type
/// (physical, magical, true or a custom family) has <see cref="FamilyId"/> = its own id; a subtype (piercing, a custom
/// «plasma») belongs to a family and adds its own resistance layer.
/// </summary>
public sealed record DamageTypeDefinition(string Id, string Name, string FamilyId, DamageStrategy Strategy, IReadOnlyList<string> Tags)
{
    public bool IsFamily => Id == FamilyId;
}

/// <summary>Ids of the built-in damage types (spec part 5 §2.1, §9.5). Built-in ids never change.</summary>
public static class DamageTypes
{
    public const string Physical = "core.damage.physical";
    public const string Piercing = "core.damage.piercing";
    public const string Slashing = "core.damage.slashing";
    public const string Blunt = "core.damage.blunt";
    public const string Magical = "core.damage.magical";
    public const string True = "core.damage.true";

    /// <summary>
    /// Legacy neutral type of content made before part 5 (spec part 5 §9.5 «physical.untyped»): resistance-affected,
    /// physical family, hidden from new content; with zero resistances it gives the old numbers.
    /// </summary>
    public const string Untyped = "core.damage.untyped";

    /// <summary>
    /// Legacy «direct HP effect» of tile surfaces made before part 5 (spec part 5 §9.5): bypasses numeric resistance so a
    /// surface keeps exactly its old HP per second for every target.
    /// </summary>
    public const string Direct = "core.damage.direct";

    /// <summary>The built-in catalog (what the shipped content defines); used where no content library is loaded.</summary>
    public static IReadOnlyDictionary<string, DamageTypeDefinition> BuiltIn { get; } = new Dictionary<string, DamageTypeDefinition>(StringComparer.Ordinal)
    {
        [Physical] = new(Physical, "Физический", Physical, DamageStrategy.ResistanceAffected, Array.Empty<string>()),
        [Piercing] = new(Piercing, "Колющий", Physical, DamageStrategy.ResistanceAffected, Array.Empty<string>()),
        [Slashing] = new(Slashing, "Режущий", Physical, DamageStrategy.ResistanceAffected, Array.Empty<string>()),
        [Blunt] = new(Blunt, "Дробящий", Physical, DamageStrategy.ResistanceAffected, Array.Empty<string>()),
        [Magical] = new(Magical, "Магический", Magical, DamageStrategy.ResistanceAffected, Array.Empty<string>()),
        [True] = new(True, "Чистый", True, DamageStrategy.TrueLike, Array.Empty<string>()),
        [Untyped] = new(Untyped, "Без типа (старый контент)", Physical, DamageStrategy.ResistanceAffected, Array.Empty<string>()),
        [Direct] = new(Direct, "Прямой (старый эффект HP)", Direct, DamageStrategy.TrueLike, Array.Empty<string>()),
    };
}

/// <summary>
/// Base numeric resistances of a unit in percentage points (spec part 5 §3): the key <see cref="Global"/> applies to every
/// resistance-affected type, a family id to its family, a type id to that subtype. Missing keys are zero.
/// </summary>
public sealed class ResistanceSet
{
    public const string Global = "global";
    public const double Min = -500;
    public const double Max = 500;

    private readonly Dictionary<string, double> _values;

    public ResistanceSet(IReadOnlyDictionary<string, double>? values = null)
    {
        _values = new Dictionary<string, double>(StringComparer.Ordinal);
        if (values is not null)
        {
            foreach (var (key, value) in values)
            {
                if (value != 0 && double.IsFinite(value))
                {
                    _values[key] = value;
                }
            }
        }
    }

    public static ResistanceSet Empty { get; } = new();

    public double this[string key] => _values.TryGetValue(key, out var v) ? v : 0;

    public IReadOnlyDictionary<string, double> Values => _values;
}

/// <summary>The one resistance formula (spec part 5 §3.1).</summary>
public static class ResistanceMath
{
    /// <summary>
    /// R before penetration = clamp(R_global + R_family + R_subtype + R_temporary, −500, +500). Temporary layers come from
    /// statuses that change a resistance key (<see cref="Stats.Resistance"/>) by percentage points.
    /// </summary>
    public static double BeforePenetration(ResistanceSet resistances, IEnumerable<StatusEffect> statuses, DamageTypeDefinition type)
    {
        var r = resistances[ResistanceSet.Global] + resistances[type.FamilyId] + (type.IsFamily ? 0 : resistances[type.Id]);
        foreach (var s in statuses)
        {
            if (s.Kind == StatusKind.Modifier && s.Stat == Stats.Resistance &&
                (s.ResistanceKey == ResistanceSet.Global || s.ResistanceKey == type.FamilyId || (!type.IsFamily && s.ResistanceKey == type.Id)))
            {
                r += s.Delta;
            }
        }

        return Math.Clamp(r, ResistanceSet.Min, ResistanceSet.Max);
    }

    /// <summary>Penetration lowers only a positive R and never below zero; R ≤ 0 is left as it is.</summary>
    public static double AfterPenetration(double beforePenetration, double penetration) =>
        beforePenetration > 0 ? Math.Max(0, beforePenetration - Math.Max(0, penetration)) : beforePenetration;

    /// <summary>Damage multiplier max(0, 1 − R/100): −500 → ×6, 0 → ×1, ≥ +100 → ×0 (never healing).</summary>
    public static double Multiplier(double r) => Math.Max(0, 1 - (r / 100));
}

/// <summary>Where a damage event comes from (spec part 5 §5.5 «originKind»).</summary>
public enum DamageOrigin
{
    NormalAttack = 0,
    Skill = 1,
    Surface = 2,
    Status = 3,

    /// <summary>Created by a defensive reaction of the target (reflection etc.); never reflected again by default.</summary>
    Reaction = 4,
    Debug = 5,
}

/// <summary>
/// One typed damage component against one target (spec part 5 §2.3, §5.1): an area hit makes one event per target, a mixed
/// hit one event per component. <see cref="Amount"/> is D0 — the strength after the attacker's side (formula, hits, AoE
/// split) and before any defense of the target.
/// </summary>
public sealed record DamageEvent
{
    public long EventId { get; init; }

    /// <summary>The primary action the event belongs to (its own id for primary hits; inherited by reactions).</summary>
    public long RootActionId { get; init; }

    public UnitId? Source { get; init; }

    public required UnitId Target { get; init; }

    public required string DamageTypeId { get; init; }

    public required double Amount { get; init; }

    /// <summary>Percentage points that lower a positive resistance of the target (spec part 5 §3.1).</summary>
    public double Penetration { get; init; }

    /// <summary>Defense points subtracted from the target's A (spec part 6 §3.2 «ArmorPenetrationFlat»); never lowers R.</summary>
    public double ArmorPenetration { get; init; }

    /// <summary>Percent (0…100) of the remaining A ignored after the flat penetration (spec part 6 §3.2).</summary>
    public double ArmorPenetrationPercent { get; init; }

    public DamageOrigin Origin { get; init; }

    /// <summary>A slice of continuous damage (surface, periodic status).</summary>
    public bool Periodic { get; init; }

    /// <summary>Created by a reaction; reactions of the target are not triggered by it by default (spec part 5 §5.5).</summary>
    public bool Reactive { get; init; }

    public int ProcDepth { get; init; }
}

/// <summary>What happened to one damage event (spec part 5 §5.2 <c>DamageResult</c>); never shown to the player as numbers.</summary>
public sealed record DamageResult
{
    public required DamageEvent Event { get; init; }

    public bool Missed { get; init; }

    public double D0 { get; init; }

    /// <summary>Stage 3: an explicit immunity stopped the health loss.</summary>
    public bool Immune { get; init; }

    /// <summary>Stage 4: the part of D0 spent by conversions (negate, heal, shield, redirect).</summary>
    public double Converted { get; init; }

    /// <summary>Health restored by a conversion to healing (after the maximum and the healing multiplier).</summary>
    public double Healed { get; init; }

    /// <summary>Stage 6: damage taken by shields.</summary>
    public double ShieldLoss { get; init; }

    /// <summary>Effective R after penetration; null for true-like types (R not used).</summary>
    public double? Resistance { get; init; }

    public double AfterResistance { get; init; }

    /// <summary>A_raw (all layers and temporary changes, may be negative); null for true-like types (A not used).</summary>
    public double? ArmorRaw { get; init; }

    /// <summary>A_eff after max(0, ·) and the armor penetration; null for true-like types.</summary>
    public double? Armor { get; init; }

    /// <summary>The K used for this type (spec part 6 §3.2).</summary>
    public double DefenseK { get; init; } = CombatRules.DefaultDefenseK;

    /// <summary>Stage 7 (spec part 6 §8): the damage after K / (K + A_eff), before the flat reduction.</summary>
    public double AfterArmor { get; init; }

    public double HpLoss { get; init; }

    public bool Killed { get; init; }
}
