using System.Collections.Generic;
using HexTactics.Core.Hex;
using HexTactics.Core.Map;
using HexTactics.Core.Orders;
using HexTactics.Core.Spawning;
using HexTactics.Core.Units;

namespace HexTactics.Simulation;

/// <summary>
/// Base type of everything the simulation reports. Future systems (abilities, attacks, waves)
/// add their own event records instead of hooking into input handlers.
/// </summary>
public abstract record SimulationEvent(long Tick);

public sealed record UnitSpawned(long Tick, UnitId Unit, HexCoord Cell, SpawnMethod Method) : SimulationEvent(Tick);

public sealed record SpawnRejected(long Tick, SpawnRequest Request, SpawnError Error, HexCoord? Cell, UnitId? Occupant) : SimulationEvent(Tick);

public sealed record FacingChanged(long Tick, UnitId Unit, HexDirection Facing) : SimulationEvent(Tick);

public sealed record OrderIssued(long Tick, UnitId Unit, Waypoint Waypoint, OrderMode Mode) : SimulationEvent(Tick);

public sealed record OrderRejected(long Tick, UnitId Unit, HexCoord Target, WaypointError Error) : SimulationEvent(Tick);

public sealed record StepStarted(long Tick, UnitId Unit, HexCoord From, HexCoord To) : SimulationEvent(Tick);

/// <summary>The unit's logical hex changed (at the midpoint of a step).</summary>
public sealed record UnitEnteredCell(long Tick, UnitId Unit, HexCoord From, HexCoord To) : SimulationEvent(Tick);

public sealed record WaypointReached(long Tick, UnitId Unit, Waypoint Waypoint) : SimulationEvent(Tick);

public sealed record UnitBlocked(long Tick, UnitId Unit, Waypoint Waypoint, BlockReason Reason) : SimulationEvent(Tick);

public sealed record UnitUnblocked(long Tick, UnitId Unit) : SimulationEvent(Tick);

public sealed record MovementFinished(long Tick, UnitId Unit, HexCoord Cell) : SimulationEvent(Tick);

public sealed record TerrainChanged(long Tick, HexCoord Cell, TileType Type) : SimulationEvent(Tick);

public sealed record PauseChanged(long Tick, bool Paused) : SimulationEvent(Tick);

/// <summary>A complete order (waypoints + final facing or attack target) replaced the unit's queue atomically.</summary>
public sealed record OrderApplied(long Tick, UnitId Unit, IReadOnlyList<HexCoord> Waypoints, HexDirection FinalFacing, UnitId? AttackTarget = null) : SimulationEvent(Tick);

/// <summary>An order was rejected as a whole; the previous queue is unchanged.</summary>
public sealed record OrderApplyRejected(long Tick, UnitId Unit, int FailedIndex, WaypointError Error) : SimulationEvent(Tick);

/// <summary>A waypoint and all following ones were removed from the unit's queue.</summary>
public sealed record OrdersTruncated(long Tick, UnitId Unit, int RemovedFromSequence, int Remaining) : SimulationEvent(Tick);

/// <summary>The mandatory final facing of a completed order was applied.</summary>
public sealed record FinalFacingApplied(long Tick, UnitId Unit, HexDirection Facing) : SimulationEvent(Tick);

/// <summary>A unit was placed from the roster; it waits for its mandatory facing (not active yet).</summary>
public sealed record UnitPlacedFromRoster(long Tick, int RosterEntry, UnitId Unit, HexCoord Cell) : SimulationEvent(Tick);

public sealed record PlacementRejected(long Tick, int RosterEntry, HexCoord? Cell, Core.Spawning.PlacementError Error) : SimulationEvent(Tick);

/// <summary>A placed unit received its first facing and became active.</summary>
public sealed record UnitActivated(long Tick, UnitId Unit, HexDirection Facing) : SimulationEvent(Tick);

/// <summary>A command order was accepted: every member's queue was replaced by its slots (spec part 2 §6).</summary>
public sealed record GroupOrderApplied(long Tick, int GroupId, UnitId Captain, IReadOnlyList<UnitId> Members, IReadOnlyList<HexCoord> Waypoints) : SimulationEvent(Tick);

/// <summary>A command order was rejected as a whole; no member's queue changed.</summary>
public sealed record GroupOrderRejected(long Tick, UnitId Captain, int FailedIndex, GroupPlanError Error) : SimulationEvent(Tick);

/// <summary>No member executes the command order any more (finished or replaced by personal orders).</summary>
public sealed record GroupOrderFinished(long Tick, int GroupId) : SimulationEvent(Tick);

/// <summary>A team member stepped aside to <paramref name="Aside"/> to let a teammate pass (spec part 2 §6.2).</summary>
public sealed record TeammateYielded(long Tick, UnitId Unit, UnitId For, HexCoord Aside) : SimulationEvent(Tick);

/// <summary>Health was lowered (spec part 3 §3, §8). <paramref name="HealthAfter"/> 0 means the target is dead.</summary>
public sealed record DamageApplied(long Tick, UnitId Target, UnitId? Source, double Amount, double HealthAfter, DamageKind Kind) : SimulationEvent(Tick);

/// <summary>A dead unit was removed from the map at the end of the tick; its hex is free (spec part 3 §3).</summary>
public sealed record UnitDied(long Tick, UnitId Unit, Side Side, HexCoord Cell) : SimulationEvent(Tick);

/// <summary>The target is in range: the unit starts hitting it (first hit after one interval).</summary>
public sealed record AttackStarted(long Tick, UnitId Unit, UnitId Target) : SimulationEvent(Tick);

/// <summary>The attack order ended because its target is gone (spec part 3 §4.2, §8).</summary>
public sealed record AttackTargetLost(long Tick, UnitId Unit, UnitId Target, AttackLostReason Reason) : SimulationEvent(Tick);

/// <summary>No free reachable hex within range of the target: the unit waits in place (spec part 3 §4.2).</summary>
public sealed record AttackTargetUnreachable(long Tick, UnitId Unit, UnitId Target) : SimulationEvent(Tick);

public enum DamageKind
{
    NormalAttack = 0,
    Skill = 1,
    Debug = 2,

    /// <summary>A periodic status (of a skill or a tile's enter/exit trigger).</summary>
    Periodic = 3,

    /// <summary>A tile type acting on the unit standing on it or entering/leaving it (spec part 4 §7).</summary>
    Surface = 4,
}

/// <summary>Health was restored (spec part 4 §6.3 p.2), never above the maximum.</summary>
public sealed record HealApplied(long Tick, UnitId Target, UnitId? Source, double Amount, double HealthAfter, DamageKind Kind) : SimulationEvent(Tick);

/// <summary>A timed status (buff, debuff, periodic effect) was applied to a unit.</summary>
public sealed record StatusApplied(long Tick, UnitId Target, UnitId? Source, string Key, Core.Effects.StatusKind Kind, string Stat, double Duration) : SimulationEvent(Tick);

/// <summary>A timed status ran out; the characteristic is back to its value without it.</summary>
public sealed record StatusExpired(long Tick, UnitId Target, string Key, Core.Effects.StatusKind Kind, string Stat) : SimulationEvent(Tick);

/// <summary>The last hit of a skill action was applied; the caster goes on (spec part 4 §6.5).</summary>
public sealed record SkillCompleted(long Tick, UnitId Unit, int ActionId, string SkillId) : SimulationEvent(Tick);

/// <summary>A running skill action stopped early (its waypoint was removed); hits already applied stay.</summary>
public sealed record SkillInterrupted(long Tick, UnitId Unit, int ActionId, string SkillId, int HitsDone, int HitsTotal) : SimulationEvent(Tick);

/// <summary>One hit event of a skill action (time → hit → cell → effect); <paramref name="Targets"/> = units affected now.</summary>
public sealed record SkillHit(long Tick, UnitId Unit, int ActionId, int HitIndex, HexCoord Cell, IReadOnlyList<UnitId> Targets) : SimulationEvent(Tick);

public enum AttackLostReason
{
    TargetDied = 0,
    TargetRemoved = 1,
}

/// <summary>A skill use was planned (immediate use or, later, at a waypoint).</summary>
public sealed record SkillPlanned(long Tick, UnitId Unit, int ActionId, string SkillId, HexDirection Direction, int? WaypointId = null) : SimulationEvent(Tick);

/// <summary>The skill planned at a waypoint was removed (by the player or with its waypoint).</summary>
public sealed record WaypointSkillRemoved(long Tick, UnitId Unit, int WaypointId) : SimulationEvent(Tick);

/// <summary>
/// A skill fired once (spec part 3 §5.1, §8): from <paramref name="Origin"/> towards <paramref name="Direction"/> on the
/// existing hexes <paramref name="Cells"/>; <paramref name="Hit"/> are the enemies standing there at this very tick.
/// </summary>
public sealed record SkillActivated(long Tick, UnitId Unit, int ActionId, string SkillId, HexCoord Origin, HexDirection Direction, IReadOnlyList<HexCoord> Cells, IReadOnlyList<UnitId> Hit) : SimulationEvent(Tick);
