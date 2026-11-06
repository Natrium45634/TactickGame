using System.Collections.Generic;
using HexTactics.Core.Hex;
using HexTactics.Core.Orders;
using HexTactics.Core.Units;

namespace HexTactics.Interaction;

/// <summary>What a click would do in the command mode at the current pointer position (spec part 2 §6, §8).</summary>
public enum GroupTargetStatus
{
    NoTarget = 0,

    /// <summary>No member yet: the first click must be on an allied unit (it becomes the captain).</summary>
    NeedCaptain,

    /// <summary>An allied unit that is not a member yet: a click adds it (the first one becomes the captain).</summary>
    AddMember,

    /// <summary>A unit that is already a member: a click changes nothing (no duplicates, the captain stays).</summary>
    AlreadyMember,

    /// <summary>The team already has the maximum number of members.</summary>
    TeamFull,

    /// <summary>Centre of the last КТД: a click never confirms.</summary>
    Neutral,

    /// <summary>A sector of the last КТД: a click sets the captain's final facing and applies the command order.</summary>
    FacingSector,

    /// <summary>The whole team fits around this hex: a click adds the next КТД.</summary>
    Valid,

    OutOfBounds,
    NotWalkable,
    Occupied,
    CaptainUnreachable,

    /// <summary>The captain could get there, but there are not enough free reachable hexes around for the companions.</summary>
    NotEnoughSlots,

    /// <summary>An enemy unit: future attack order (reserved, spec part 2 §7).</summary>
    AttackTarget,
}

/// <summary>
/// Read-only snapshot of the command mode for the presentation (spec part 2 §6): members (captain first), the КТД chain
/// with the formation of every confirmed stage, and the preview of the hex under the cursor.
/// </summary>
/// <param name="Anchors">Command anchor of every member (parallel to <paramref name="Members"/>).</param>
/// <param name="Sources">Where every member starts the next stage: its destination at the last КТД, or its anchor.</param>
/// <param name="Stages">Formation at every confirmed КТД (parallel to <paramref name="Waypoints"/>).</param>
/// <param name="Preview">Formation for the hex under the cursor (valid or with its error), or null.</param>
/// <param name="SectorFacing">Captain's final facing previewed in a sector of the last КТД.</param>
/// <param name="NextStage">Evaluator of the next КТД (lets the view colour every hex with the same rules).</param>
public sealed record GroupDraftView(
    IReadOnlyList<UnitId> Members,
    IReadOnlyList<HexCoord> Anchors,
    IReadOnlyList<HexCoord> Sources,
    IReadOnlyList<HexCoord> Waypoints,
    IReadOnlyList<GroupStagePlan> Stages,
    PlaneVector? Pointer,
    HexCoord? TargetHex,
    GroupTargetStatus Status,
    GroupStagePlan? Preview,
    HexDirection? SectorFacing,
    UnitId? HoverUnit,
    GroupStageEvaluator? NextStage)
{
    public UnitId? Captain => Members.Count > 0 ? Members[0] : null;

    /// <summary>True while members are collected (no КТД yet).</summary>
    public bool IsCollecting => Waypoints.Count == 0;

    /// <summary>The last КТД (sectors of this hex choose the captain's facing), or null while collecting.</summary>
    public HexCoord? Source => Waypoints.Count > 0 ? Waypoints[^1] : null;

    public bool IsValidTarget => Status == GroupTargetStatus.Valid;
}
