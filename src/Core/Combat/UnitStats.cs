using System.Linq;
using System.Collections.Generic;

namespace HexTactics.Core.Combat;

/// <summary>
/// Minimal unit characteristics (spec part 3 §3). Values come from balance data (<c>data/balance.json</c> in the client),
/// never from input handlers.
/// </summary>
public sealed record UnitStats
{
    /// <summary>Spec part 3 §3: 100 HP.</summary>
    public int MaxHealth { get; init; } = 100;

    /// <summary>Spec part 3 §2.4: 1 hex per second.</summary>
    public double MoveSpeedHexPerSecond { get; init; } = 1.0;

    /// <summary>Spec part 3 §3: 3 normal attacks per second (interval 1/3 s).</summary>
    public double AttackRatePerSecond { get; init; } = 3.0;

    /// <summary>
    /// Attack Power (spec part 4 §5.1): damage of one normal attack and the base of «% от Attack Power» skill effects.
    /// PROTOTYPE_DEFAULT 10 (spec part 3 §11). A number, not the attack rate.
    /// </summary>
    public double AttackPower { get; init; } = 10;

    /// <summary>PROTOTYPE_DEFAULT (spec part 3 §3, §11): normal attack range in hex steps (1 = adjacent).</summary>
    public int NormalAttackRangeHex { get; init; } = 1;

    /// <summary>Damage type of the normal attack (spec part 5 §8.3); legacy units hit untyped.</summary>
    public string AttackDamageTypeId { get; init; } = DamageTypes.Untyped;

    /// <summary>
    /// Area of the normal attack (spec part 5 §2.1, §8.3): 0 — the target only; N — every enemy of the attacker within N
    /// hexes of the target's hex (the target included).
    /// </summary>
    public int AttackAreaRadius { get; init; }

    /// <summary>For an area attack: false — full strength to every target (default); true — the strength is split among them.</summary>
    public bool AttackAreaSplit { get; init; }

    /// <summary>Base numeric resistances (spec part 5 §3); temporary changes come from statuses.</summary>
    public ResistanceSet Resistances { get; init; } = ResistanceSet.Empty;

    /// <summary>Base defense A by layers (spec part 6 §3.2); zero for content made before part 6 (old numbers stay).</summary>
    public ArmorSet Armor { get; init; } = ArmorSet.Empty;

    /// <summary>Armor penetration of the normal attack: points (flat) and percent of the remaining A (spec part 6 §3.4 p.4).</summary>
    public double AttackArmorPenetration { get; init; }

    public double AttackArmorPenetrationPercent { get; init; }

    /// <summary>
    /// Constant percentage damage modifiers of the unit's passives (spec part 6 §4.2): outgoing ones change the damage it
    /// deals before D0, incoming ones the damage it takes after R, A and the flat reduction.
    /// </summary>
    public IReadOnlyList<DamageModifier> DamageModifiers { get; init; } = System.Array.Empty<DamageModifier>();

    /// <summary>Critical chance of the unit's hits in percentage points (spec part 6 §7.1); added to the ruleset's base.</summary>
    public double CritChance { get; init; }

    /// <summary>Hits against the unit are never critical / never surprises (spec part 6 §6.4, §7.2), from explicit passives.</summary>
    public bool CritImmune { get; init; }

    public bool SurpriseImmune { get; init; }

    /// <summary>Species lineage of the definition, from its own species up to the root (spec part 6 §5.2 <c>isA</c>); empty — none.</summary>
    public IReadOnlyList<string> Species { get; init; } = System.Array.Empty<string>();

    /// <summary>Pack / group of the unit (spec part 6 §5.5 <c>packId</c>); empty — no pack.</summary>
    public string PackId { get; init; } = string.Empty;

    /// <summary>Bonuses per matching ally (spec part 6 §5.5), recounted when the roster of the map changes.</summary>
    public IReadOnlyList<CountModifier> CountModifiers { get; init; } = System.Array.Empty<CountModifier>();

    /// <summary>True if the definition is of <paramref name="speciesId"/> or one of its descendants.</summary>
    public bool IsA(string speciesId) => Species.Contains(speciesId);

    /// <summary>Defense rules of the unit definition in the order of the definition (spec part 5 §4, §6.4).</summary>
    public IReadOnlyList<DefenseRule> DefenseRules { get; init; } = System.Array.Empty<DefenseRule>();

    /// <summary>Shields of the unit definition (spec part 5 §4.5); a live unit starts with them full.</summary>
    public IReadOnlyList<ShieldDefinition> Shields { get; init; } = System.Array.Empty<ShieldDefinition>();

    /// <summary>
    /// Multiplier of incoming healing (spec part 5 §4.6 «запрет лечения»): 1 = normal, 0 = no healing; changed by
    /// buffs/debuffs, never below 0.
    /// </summary>
    public double HealingReceived { get; init; } = 1.0;

    /// <summary>Minimal game time between two normal attacks.</summary>
    public double AttackInterval => 1.0 / AttackRatePerSecond;

    public static UnitStats Default { get; } = new();
}

/// <summary>Life state of a unit (spec part 3 §3).</summary>
public enum LifeState
{
    Alive = 0,

    /// <summary>
    /// CurrentHealth reached 0. The unit no longer attacks, moves or accepts orders; at the end of the tick it is removed
    /// from the map and stops occupying its hex (prototype assumption, spec part 3 §3).
    /// </summary>
    Dead = 1,
}

/// <summary>Progress of a unit's normal attack on its target (spec part 3 §4.2).</summary>
public enum AttackStatus
{
    None = 0,

    /// <summary>Still walking the waypoints that come before the attack.</summary>
    FollowingRoute = 1,

    /// <summary>Walking to the nearest free hex from which the target is in range.</summary>
    Approaching = 2,

    /// <summary>The target is in range: hits every <see cref="UnitStats.AttackInterval"/>.</summary>
    Attacking = 3,

    /// <summary>No free reachable hex within range of the target: the unit waits (no teleport) and keeps trying.</summary>
    TargetUnreachable = 4,
}

/// <summary>Execution state of a unit's normal attack (owned by the attack system).</summary>
public sealed class CombatState
{
    public AttackStatus Status { get; internal set; }

    /// <summary>Game time accumulated towards the next hit while the target is in range.</summary>
    public double AttackTimer { get; internal set; }

    /// <summary>The hex the unit is approaching to attack from, if any.</summary>
    public HexTactics.Core.Hex.HexCoord? ApproachCell { get; internal set; }

    /// <summary>Tick of the last approach planning (re-planning is rate-limited).</summary>
    public long ApproachPlannedTick { get; internal set; } = long.MinValue;

    /// <summary>Normal attacks delivered by this unit since it was created.</summary>
    public int HitsDelivered { get; internal set; }

    internal void Reset()
    {
        Status = AttackStatus.None;
        AttackTimer = 0;
        ApproachCell = null;
        ApproachPlannedTick = long.MinValue;
    }
}
