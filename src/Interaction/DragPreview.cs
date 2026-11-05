using HexTactics.Core.Hex;
using HexTactics.Core.Orders;
using HexTactics.Core.Units;

namespace HexTactics.Interaction;

/// <summary>What would happen if the order drag were released now.</summary>
public enum DragTargetStatus
{
    /// <summary>The pointer does not hit the map plane.</summary>
    NoTarget = 0,

    /// <summary>Over the source hex near its centre: releasing does nothing.</summary>
    SourceDeadZone,

    /// <summary>Over the source hex with a directional gesture: releasing turns the unit in place.</summary>
    RotateOnly,

    /// <summary>Over the source hex, but the unit already has waypoints: rotate-only is not offered.</summary>
    SourceHasOrders,

    /// <summary>A waypoint can be created on the hex under the pointer.</summary>
    Valid,

    OutOfBounds,
    NotWalkable,
    Occupied,
    Unreachable,
}

/// <summary>Snapshot of an active order drag for the presentation layer.</summary>
/// <param name="Unit">Unit receiving the order.</param>
/// <param name="Mode">Replace (drag from the unit) or Append (drag from the last waypoint).</param>
/// <param name="SourceCell">Start hex of the new segment.</param>
/// <param name="SourceCenter">Plane position of the source hex centre (arrow start).</param>
/// <param name="Pointer">Plane position of the cursor (arrow end), if any.</param>
/// <param name="TargetHex">Hex under the cursor (may be outside the map).</param>
/// <param name="Status">Result of releasing now.</param>
/// <param name="FacingPreview">Facing the unit would show (first step of the route, or the rotate direction).</param>
public sealed record DragPreview(
    UnitId Unit,
    OrderMode Mode,
    HexCoord SourceCell,
    PlaneVector SourceCenter,
    PlaneVector? Pointer,
    HexCoord? TargetHex,
    DragTargetStatus Status,
    HexDirection? FacingPreview)
{
    public bool CanCommit => Status is DragTargetStatus.Valid or DragTargetStatus.RotateOnly;
}
