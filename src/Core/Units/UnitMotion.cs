using System;
using System.Collections.Generic;
using HexTactics.Core.Hex;
using HexTactics.Core.Motion;

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

    /// <summary>Only members of the same command order close the way and none of them can step aside.</summary>
    BlockedByTeammates = 3,
}

/// <summary>Why a unit with orders is not stepping right now (status <see cref="MovementStatus.Waiting"/>).</summary>
public enum WaitReason
{
    None = 0,

    /// <summary>The next hex is reserved by another unit stepping into/out of it.</summary>
    Reservation = 1,

    /// <summary>Command order: this member reached the current КТД stage and waits for the rest of the team.</summary>
    TeamStage = 2,

    /// <summary>Command order: a teammate stands in the way (it will move on or step aside).</summary>
    Teammate = 3,

    /// <summary>Command order: stepped aside and waits until the teammate it made way for has passed.</summary>
    Yielding = 4,
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

    /// <summary>True once the logical cell moved to <see cref="StepTo"/> (progress ≥ <see cref="SwitchAt"/>).</summary>
    public bool CellSwitched { get; internal set; }

    /// <summary>
    /// Step progress at which the body crosses the shared edge into <see cref="StepTo"/>: the single deterministic moment
    /// the logical cell switches (spec part 3 §2.3). 0.5 for a straight step; depends on the curve otherwise.
    /// </summary>
    public double SwitchAt { get; internal set; } = 0.5;

    /// <summary>
    /// Continuous position on the map plane (hex size 1). Null means the centre of the logical cell (never moved).
    /// Authoritative for the view: the body follows the step curves of the simulation, not a free-moving node.
    /// </summary>
    public PlaneVector? Position { get; internal set; }

    /// <summary>Unit direction of travel while the unit moves on without stopping; null at rest.</summary>
    public PlaneVector? Heading { get; internal set; }

    internal double? HeadingControl { get; set; }

    /// <summary>Geometry of the running step (null at rest).</summary>
    public StepPlan? Plan { get; internal set; }

    /// <summary>Step progress at which <see cref="Plan"/> starts (0, or the moment the step was re-planned).</summary>
    internal double PlanProgressStart { get; set; }

    /// <summary>The hex after <see cref="StepTo"/> the running step's curve turns towards, or null (stops there).</summary>
    public HexCoord? StepLookahead { get; internal set; }

    internal int StepOrdersVersion { get; set; }

    /// <summary>Position on the running step's curve at step progress <paramref name="progress"/> (or the rest position).</summary>
    public PlaneVector? PositionAtProgress(double progress)
    {
        if (Plan is not { } plan)
        {
            return Position;
        }

        var p0 = PlanProgressStart;
        var span = 1.0 - p0;
        var f = span < 1e-9 ? 1.0 : Math.Clamp((progress - p0) / span, 0, 1);
        return plan.Curve.PointAtArc(f * plan.Curve.Length);
    }

    /// <summary>Unit tangent of the running step's curve at step progress <paramref name="progress"/>.</summary>
    public PlaneVector TangentAtProgress(double progress)
    {
        if (Plan is not { } plan)
        {
            return Heading ?? default;
        }

        var span = 1.0 - PlanProgressStart;
        var f = span < 1e-9 ? 1.0 : Math.Clamp((progress - PlanProgressStart) / span, 0, 1);
        return plan.Curve.TangentAtArc(f * plan.Curve.Length);
    }

    public bool IsStepping => StepTo is not null;

    public MovementStatus Status { get; internal set; }

    public BlockReason BlockReason { get; internal set; }

    /// <summary>Reason of the current <see cref="MovementStatus.Waiting"/> status.</summary>
    public WaitReason WaitReason { get; internal set; }

    /// <summary>Tick the current wait for teammates started (yield timer), or null.</summary>
    public long? WaitingSinceTick { get; internal set; }

    /// <summary>The teammate this unit stepped aside for (it holds aside until that unit has passed), or null.</summary>
    public UnitId? YieldFor { get; internal set; }

    /// <summary>The hex this unit vacated for <see cref="YieldFor"/>.</summary>
    public HexCoord? YieldVacated { get; internal set; }

    /// <summary>Where <see cref="YieldFor"/> stood when it asked (it must have moved on before this unit returns).</summary>
    public HexCoord? YieldRequesterCell { get; internal set; }

    /// <summary>Tick of the yield request (safety timeout).</summary>
    public long YieldSinceTick { get; internal set; }

    /// <summary>Remaining planned hexes of the current segment (next step first), excluding the unit's position.</summary>
    public IReadOnlyList<HexCoord> Path => PathCells;

    /// <summary>Waypoint the current <see cref="Path"/> leads to.</summary>
    public int? PathWaypointId { get; internal set; }

    /// <summary><see cref="GameState.TopologyVersion"/> the path was planned against.</summary>
    public long PathTopologyVersion { get; internal set; } = -1;

    /// <summary>Topology version of the last failed planning attempt (avoids re-planning every tick).</summary>
    public long BlockedAtTopologyVersion { get; internal set; } = -1;
}
