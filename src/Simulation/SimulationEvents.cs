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

/// <summary>A complete order (waypoints + final facing) replaced the unit's queue atomically.</summary>
public sealed record OrderApplied(long Tick, UnitId Unit, IReadOnlyList<HexCoord> Waypoints, HexDirection FinalFacing) : SimulationEvent(Tick);

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
