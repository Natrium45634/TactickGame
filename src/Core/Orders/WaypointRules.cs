using System;
using System.Collections.Generic;
using HexTactics.Core.Hex;
using HexTactics.Core.Pathfinding;
using HexTactics.Core.Units;

namespace HexTactics.Core.Orders;

/// <summary>How a new movement order affects the existing queue (spec §6.3).</summary>
public enum OrderMode
{
    /// <summary>Drag from the unit: the pending queue is replaced by the new waypoint.</summary>
    Replace = 0,

    /// <summary>Drag from the last waypoint: the waypoint is appended to the queue.</summary>
    Append = 1,
}

public enum WaypointError
{
    None = 0,
    UnknownUnit,
    OutOfBounds,
    NotWalkable,
    Occupied,
    SameAsSource,
    Unreachable,

    /// <summary>The attack target does not exist, is dead or is not an enemy (spec part 3 §4.1).</summary>
    InvalidTarget,

    /// <summary>A planned waypoint skill is unknown to the unit or refers to no waypoint (spec part 3 §6.5).</summary>
    InvalidSkill,
}

/// <summary>
/// Rules for placing a waypoint (ТД). The same function drives the drag preview colours and the
/// simulation command, so what the player sees is exactly what will be accepted.
/// </summary>
public static class WaypointRules
{
    /// <summary>
    /// The hex a new segment starts from. Replace: the unit's command anchor (its logical cell, or
    /// the hex it is entering while moving). Append: the last queued waypoint (or the anchor if the queue is empty).
    /// </summary>
    public static HexCoord SegmentSource(UnitState unit, OrderMode mode, HexCoord anchor)
    {
        if (mode == OrderMode.Append && unit.Orders.Last is { } last)
        {
            return last.Cell;
        }

        return anchor;
    }

    /// <summary>Static checks that do not need path finding.</summary>
    public static WaypointError ValidateTarget(GameState state, UnitId unit, HexCoord source, HexCoord target)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.FindUnit(unit) is null)
        {
            return WaypointError.UnknownUnit;
        }

        if (!state.Grid.Contains(target))
        {
            return WaypointError.OutOfBounds;
        }

        if (!state.Grid[target].IsWalkable)
        {
            return WaypointError.NotWalkable;
        }

        if (state.IsOccupiedByOther(target, unit))
        {
            return WaypointError.Occupied;
        }

        return target == source ? WaypointError.SameAsSource : WaypointError.None;
    }

    /// <summary>
    /// Validates a target against a precomputed set of reachable hexes (see <see cref="ReachableTargets"/>).
    /// A statically valid hex that is not in <paramref name="reachable"/> is <see cref="WaypointError.Unreachable"/>.
    /// </summary>
    public static WaypointError ValidateTarget(GameState state, UnitId unit, HexCoord source, HexCoord target, IReadOnlySet<HexCoord> reachable)
    {
        var error = ValidateTarget(state, unit, source, target);
        if (error != WaypointError.None)
        {
            return error;
        }

        return reachable.Contains(target) ? WaypointError.None : WaypointError.Unreachable;
    }

    /// <summary>
    /// All hexes the unit could path to from <paramref name="source"/> right now: terrain obstacles and
    /// the hexes of other units block; the unit's own hex does not.
    /// </summary>
    public static IReadOnlySet<HexCoord> ReachableTargets(GameState state, UnitId unit, HexCoord source) =>
        Pathfinder.Reachable(state.Grid, source, state.ObstaclesFor(unit));

    /// <summary>Static checks plus existence of a path (single A* query).</summary>
    public static WaypointError ValidateTargetWithPath(GameState state, UnitId unit, HexCoord source, HexCoord target)
    {
        var error = ValidateTarget(state, unit, source, target);
        if (error != WaypointError.None)
        {
            return error;
        }

        return Pathfinder.FindPath(state.Grid, source, target, state.ObstaclesFor(unit)) is null ? WaypointError.Unreachable : WaypointError.None;
    }
}
