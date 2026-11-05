using System.Collections.Generic;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Units;

public enum MovementStatus
{
    /// <summary>No pending waypoints.</summary>
    Idle = 0,

    /// <summary>Executing a step or about to start the next one.</summary>
    Moving = 1,

    /// <summary>The next hex is reserved by another unit that is stepping into/out of it.</summary>
    Waiting = 2,

    /// <summary>No path to the current waypoint: the unit stands still and keeps its orders.</summary>
    Blocked = 3,
}

public enum BlockReason
{
    None = 0,

    /// <summary>The current waypoint hex is occupied by another unit.</summary>
    TargetOccupied = 1,

    /// <summary>The waypoint hex is free but no path leads to it (obstacles / units around).</summary>
    NoPath = 2,
}

/// <summary>
/// Movement state of a unit between hex centres. A step goes from <see cref="StepFrom"/> to
/// <see cref="StepTo"/> with <see cref="Progress"/> 0 → 1. The logical cell (<see cref="UnitState.Cell"/>)
/// switches exactly once per step, when progress reaches 0.5 (<see cref="CellSwitched"/>).
/// Mutated only by the simulation.
/// </summary>
public sealed class UnitMotion
{
    internal readonly List<HexCoord> PathCells = new();

    public HexCoord? StepFrom { get; internal set; }

    public HexCoord? StepTo { get; internal set; }

    /// <summary>Fraction of the current step, 0 ≤ progress &lt; 1.</summary>
    public double Progress { get; internal set; }

    /// <summary>True once the logical cell moved to <see cref="StepTo"/> (progress ≥ 0.5).</summary>
    public bool CellSwitched { get; internal set; }

    public bool IsStepping => StepTo is not null;

    public MovementStatus Status { get; internal set; }

    public BlockReason BlockReason { get; internal set; }

    /// <summary>Remaining planned hexes of the current segment (next step first), excluding the unit's position.</summary>
    public IReadOnlyList<HexCoord> Path => PathCells;

    /// <summary>Waypoint the current <see cref="Path"/> leads to.</summary>
    public int? PathWaypointId { get; internal set; }

    /// <summary><see cref="GameState.TopologyVersion"/> the path was planned against.</summary>
    public long PathTopologyVersion { get; internal set; } = -1;

    /// <summary>Topology version of the last failed planning attempt (avoids re-planning every tick).</summary>
    public long BlockedAtTopologyVersion { get; internal set; } = -1;
}
