using HexTactics.Core.Hex;
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
