using System;
using HexTactics.Core.Hex;
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
}
