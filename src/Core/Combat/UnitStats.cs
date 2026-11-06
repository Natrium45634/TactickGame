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

    /// <summary>PROTOTYPE_DEFAULT (spec part 3 §3, §11): the author has not set normal attack damage yet.</summary>
    public int NormalAttackDamage { get; init; } = 10;

    /// <summary>PROTOTYPE_DEFAULT (spec part 3 §3, §11): normal attack range in hex steps (1 = adjacent).</summary>
    public int NormalAttackRangeHex { get; init; } = 1;

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
