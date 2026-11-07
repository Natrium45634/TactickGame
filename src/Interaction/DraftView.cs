using System.Collections.Generic;
using HexTactics.Core.Hex;
using HexTactics.Core.Orders;
using HexTactics.Core.Units;

namespace HexTactics.Interaction;

/// <summary>What a click (or the release of the first drag) would do right now while an order draft is open.</summary>
public enum DraftTargetStatus
{
    /// <summary>The pointer does not hit the map plane.</summary>
    NoTarget = 0,

    /// <summary>Over the centre of the source hex: clicking does nothing (prevents accidental confirmation).</summary>
    Neutral,

    /// <summary>Over one of the six edge sectors of the source hex: clicking confirms the final facing and the order.</summary>
    FacingSector,

    /// <summary>A waypoint (ТД) can be placed on the hex under the pointer.</summary>
    Valid,

    OutOfBounds,
    NotWalkable,
    Occupied,
    Unreachable,

    /// <summary>An enemy unit is under the pointer: reserved future <see cref="OrderKind.AttackTarget"/>; nothing happens now.</summary>
    AttackTarget,

    /// <summary>The tile type forbids waypoints (spec part 4 §7.1): the hex is red, units may still walk across it.</summary>
    WaypointsForbidden,
}

public enum DraftCancelReason
{
    /// <summary>RMB or Esc.</summary>
    ByUser = 0,

    /// <summary>An attempt to confirm a waypoint on a red hex (spec part 2 §8.1).</summary>
    InvalidTarget = 1,

    /// <summary>The unit disappeared.</summary>
    UnitLost = 2,
}

/// <summary>Snapshot of the open order draft for the presentation layer (all coordinates in map space).</summary>
/// <param name="Unit">Owner of the draft.</param>
/// <param name="Waypoints">Waypoints placed so far (preview only; not yet applied to the unit).</param>
/// <param name="Anchor">Hex the chain starts from (the unit's command anchor).</param>
/// <param name="Source">Start hex of the next segment (last draft waypoint or the anchor).</param>
/// <param name="SourceCenter">Plane position of <paramref name="Source"/>'s centre (live arrow start).</param>
/// <param name="Pointer">Plane position of the cursor (live arrow end), if any.</param>
/// <param name="TargetHex">Hex under the cursor (may be outside the map).</param>
/// <param name="Status">Result of clicking now.</param>
/// <param name="SectorFacing">Final facing previewed in the source hex's sector, if any.</param>
/// <param name="UnitFacingPreview">Facing the unit model previews (first path step, or the turn-in-place sector).</param>
/// <param name="Reachable">Hexes reachable from the source right now (green candidates).</param>
/// <param name="Path">Shortest path source → target when the target is valid.</param>
/// <param name="Held">True while the initial LMB drag is still held.</param>
/// <param name="IsEdit">True if the draft continues the unit's confirmed route.</param>
/// <param name="AttackTarget">Enemy unit under the pointer when <see cref="DraftTargetStatus.AttackTarget"/>.</param>
/// <param name="Skills">Skills planned at draft waypoints (index → skill and direction, spec part 3 §6).</param>
/// <param name="HoverMarker">Index of the draft waypoint whose central marker is under the pointer (a click opens its menu).</param>
public sealed record DraftView(
    UnitId Unit,
    IReadOnlyList<HexCoord> Waypoints,
    HexCoord Anchor,
    HexCoord Source,
    PlaneVector SourceCenter,
    PlaneVector? Pointer,
    HexCoord? TargetHex,
    DraftTargetStatus Status,
    HexDirection? SectorFacing,
    HexDirection? UnitFacingPreview,
    IReadOnlySet<HexCoord> Reachable,
    IReadOnlyList<HexCoord>? Path,
    bool Held,
    bool IsEdit,
    UnitId? AttackTarget = null,
    IReadOnlyDictionary<int, HexTactics.Core.Skills.WaypointSkillSpec>? Skills = null,
    int? HoverMarker = null);
