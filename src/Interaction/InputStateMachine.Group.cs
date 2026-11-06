using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Hex;
using HexTactics.Core.Orders;
using HexTactics.Core.Units;

namespace HexTactics.Interaction;

/// <summary>
/// Command mode (spec part 2 §6): the "Команда" button starts collecting a team — the first clicked allied unit is the
/// captain, every further allied unit joins (no duplicates); a cyan arrow follows the cursor from every member. A click on
/// a hex where the whole team fits becomes КТД1, further clicks add КТД2…; a click in a sector of the last КТД sets the
/// captain's mandatory facing and applies the command order to all members at once. RMB/Esc cancel, an attempt to
/// confirm a red hex cancels too, RMB on a draft КТД removes it and the following ones.
/// </summary>
public sealed partial class InputStateMachine
{
    private readonly List<UnitId> _groupMembers = new();
    private readonly List<HexCoord> _groupWaypoints = new();
    private List<GroupStagePlan> _groupStages = new();
    private string _groupStagesKey = string.Empty;
    private GroupStageEvaluator? _groupEvaluator;
    private string _groupEvaluatorKey = string.Empty;

    /// <summary>The command mode snapshot (collection or КТД draft), or null.</summary>
    public GroupDraftView? Group { get; private set; }

    public bool IsGrouping => Mode is InputMode.GroupCollect or InputMode.GroupDraft;

    /// <summary>"Команда" button: switches the command mode on, or cancels it (nothing is applied).</summary>
    public void ToggleGroupMode()
    {
        if (IsGrouping)
        {
            CancelGroup(DraftCancelReason.ByUser, GroupTargetStatus.NoTarget, null);
        }
        else
        {
            BeginGroup();
        }
    }

    public void BeginGroup()
    {
        if (IsGrouping)
        {
            return;
        }

        Cancel(); // roster card, draft, debug modes
        _press = null;
        SelectInternal(null, null);
        ClearGroup();
        Mode = InputMode.GroupCollect;
        Feedback?.Invoke(new GroupModeChanged(true));
        UpdateGroupView(Pointer ?? new PointerSample(new ScreenPoint(0, 0), null, null));
    }

    /// <summary>Handles a click (LMB release) in the command mode.</summary>
    private void GroupClick(PointerSample sample)
    {
        UpdateGroupView(sample);
        var view = Group!;
        switch (view.Status)
        {
            case GroupTargetStatus.AddMember:
                {
                    var unit = view.HoverUnit!.Value;
                    _groupMembers.Add(unit);
                    _groupEvaluatorKey = string.Empty;
                    Feedback?.Invoke(new GroupMemberAdded(unit, _groupMembers.Count == 1, _groupMembers.Count));
                    break;
                }

            case GroupTargetStatus.AlreadyMember or GroupTargetStatus.TeamFull:
                Feedback?.Invoke(new GroupMemberRefused(view.HoverUnit!.Value, view.Status));
                break;

            case GroupTargetStatus.NeedCaptain:
                Feedback?.Invoke(new GroupNeedsCaptain());
                break;

            case GroupTargetStatus.AttackTarget:
                if (view.Captain is { } captain && view.HoverUnit is { } target)
                {
                    Feedback?.Invoke(new AttackTargetReserved(captain, target));
                }

                break;

            case GroupTargetStatus.Valid:
                _groupWaypoints.Add(view.TargetHex!.Value);
                _groupStagesKey = string.Empty;
                Mode = InputMode.GroupDraft;
                Feedback?.Invoke(new GroupWaypointAdded(_groupWaypoints.Count, view.TargetHex.Value, view.Preview!));
                break;

            case GroupTargetStatus.FacingSector:
                CommitGroup(view.SectorFacing!.Value);
                return;

            case GroupTargetStatus.Neutral or GroupTargetStatus.NoTarget:
                break;

            default:
                // An attempt to confirm a red hex cancels the command draft (hovering alone never does).
                CancelGroup(DraftCancelReason.InvalidTarget, view.Status, view.TargetHex);
                return;
        }

        UpdateGroupView(sample);
    }

    private void GroupSecondaryPress(PointerSample sample)
    {
        if (sample.Hex is { } hex)
        {
            var index = _groupWaypoints.LastIndexOf(hex);
            if (index >= 0)
            {
                _groupWaypoints.RemoveRange(index, _groupWaypoints.Count - index);
                _groupStagesKey = string.Empty;
                if (_groupWaypoints.Count == 0)
                {
                    Mode = InputMode.GroupCollect;
                }

                Feedback?.Invoke(new GroupDraftTruncated(index + 1, _groupWaypoints.Count));
                UpdateGroupView(sample);
                return;
            }
        }

        CancelGroup(DraftCancelReason.ByUser, GroupTargetStatus.NoTarget, null);
    }

    private void CommitGroup(HexDirection captainFacing)
    {
        var order = new GroupOrder(_groupMembers[0], _groupMembers.ToArray(), _groupWaypoints.ToArray(), captainFacing);
        var result = _sim.ApplyGroupOrder(order);
        if (!result.Success)
        {
            // The game changed meanwhile: keep the draft open, nothing was applied (as for single orders, §9.5).
            Feedback?.Invoke(new GroupCommitFailed(result));
            return;
        }

        var captain = order.Captain;
        EndGroup();
        SelectInternal(captain, null);
        Feedback?.Invoke(new GroupCommitted(order, result.GroupId!.Value));
    }

    private void CancelGroup(DraftCancelReason reason, GroupTargetStatus status, HexCoord? cell)
    {
        if (!IsGrouping)
        {
            return;
        }

        EndGroup();
        Feedback?.Invoke(new GroupCancelled(reason, status, cell));
        Feedback?.Invoke(new GroupModeChanged(false));
    }

    private void EndGroup()
    {
        ClearGroup();
        Mode = SelectedUnit is not null ? InputMode.UnitSelected : InputMode.Idle;
    }

    private void ClearGroup()
    {
        _groupMembers.Clear();
        _groupWaypoints.Clear();
        _groupStages = new List<GroupStagePlan>();
        _groupStagesKey = string.Empty;
        _groupEvaluator = null;
        _groupEvaluatorKey = string.Empty;
        Group = null;
    }

    /// <summary>Rebuilds <see cref="Group"/> for the pointer; members that disappeared cancel the command draft.</summary>
    private void UpdateGroupView(PointerSample sample)
    {
        if (!IsGrouping)
        {
            return;
        }

        var state = _sim.State;
        if (_groupMembers.Any(m => state.FindUnit(m) is null))
        {
            CancelGroup(DraftCancelReason.UnitLost, GroupTargetStatus.NoTarget, null);
            return;
        }

        var anchors = _groupMembers.Select(_sim.CommandAnchor).ToArray();
        RefreshGroupStages(anchors);
        var sources = _groupStages.Count == _groupWaypoints.Count && _groupStages.Count > 0 && _groupStages[^1].IsValid
            ? _groupMembers.Select(m => _groupStages[^1].DestinationOf(m)!.Value).ToArray()
            : anchors;
        var evaluator = _groupMembers.Count > 0 ? EvaluatorFor(sources) : null;

        var status = GroupTargetStatus.NoTarget;
        GroupStagePlan? preview = null;
        HexDirection? sector = null;
        UnitId? hoverUnit = null;
        if (sample.Plane is { } plane && sample.Hex is { } hex)
        {
            var source = _groupWaypoints.Count > 0 ? _groupWaypoints[^1] : (HexCoord?)null;
            var unitAt = sample.Unit is { } u && state.FindUnit(u) is not null ? state.FindUnit(u) : state.UnitAt(hex);
            var captainSide = _groupMembers.Count > 0 ? state.GetUnit(_groupMembers[0]).Side : Settings.PlacementSide;
            if (source == hex)
            {
                sector = SectorAt(hex, plane);
                status = sector is null ? GroupTargetStatus.Neutral : GroupTargetStatus.FacingSector;
            }
            else if (unitAt is not null && unitAt.Side != captainSide)
            {
                hoverUnit = unitAt.Id;
                status = _groupMembers.Count > 0 ? GroupTargetStatus.AttackTarget : GroupTargetStatus.NeedCaptain;
            }
            else if (unitAt is not null && _groupMembers.Contains(unitAt.Id))
            {
                hoverUnit = unitAt.Id;
                status = GroupTargetStatus.AlreadyMember;
            }
            else if (unitAt is not null && _groupWaypoints.Count == 0)
            {
                hoverUnit = unitAt.Id;
                status = _groupMembers.Count >= GroupRoutePlanner.MaxMembers ? GroupTargetStatus.TeamFull : GroupTargetStatus.AddMember;
            }
            else if (_groupMembers.Count == 0)
            {
                status = GroupTargetStatus.NeedCaptain;
            }
            else if (evaluator is not null)
            {
                preview = evaluator.Evaluate(hex);
                status = Map(preview.Error);
            }
        }

        Group = new GroupDraftView(
            _groupMembers.ToArray(),
            anchors,
            sources,
            _groupWaypoints.ToArray(),
            _groupStages.ToArray(),
            sample.Plane,
            sample.Hex,
            status,
            preview,
            sector,
            hoverUnit,
            evaluator);
    }

    /// <summary>Re-plans the confirmed КТД stages when members move or the map changes (the preview stays truthful).</summary>
    private void RefreshGroupStages(IReadOnlyList<HexCoord> anchors)
    {
        var key = $"{_sim.State.TopologyVersion}|{string.Join(';', anchors)}|{string.Join(';', _groupWaypoints)}|{_groupMembers.Count}";
        if (key == _groupStagesKey)
        {
            return;
        }

        _groupStagesKey = key;
        _groupStages = _groupWaypoints.Count == 0
            ? new List<GroupStagePlan>()
            : GroupRoutePlanner.PlanChain(_sim.State, _groupMembers, anchors, _groupWaypoints).Stages.ToList();
    }

    private GroupStageEvaluator EvaluatorFor(IReadOnlyList<HexCoord> sources)
    {
        var key = $"{_sim.State.TopologyVersion}|{string.Join(';', _groupMembers)}|{string.Join(';', sources)}|{_groupWaypoints.Count}";
        if (_groupEvaluator is null || key != _groupEvaluatorKey)
        {
            _groupEvaluator = new GroupStageEvaluator(_sim.State, _groupMembers.ToArray(), sources, _groupWaypoints.Count);
            _groupEvaluatorKey = key;
        }

        return _groupEvaluator;
    }

    private static GroupTargetStatus Map(GroupPlanError error) => error switch
    {
        GroupPlanError.None => GroupTargetStatus.Valid,
        GroupPlanError.OutOfBounds => GroupTargetStatus.OutOfBounds,
        GroupPlanError.NotWalkable => GroupTargetStatus.NotWalkable,
        GroupPlanError.Occupied => GroupTargetStatus.Occupied,
        GroupPlanError.SameAsSource => GroupTargetStatus.Neutral,
        GroupPlanError.CaptainUnreachable => GroupTargetStatus.CaptainUnreachable,
        GroupPlanError.NotEnoughSlots => GroupTargetStatus.NotEnoughSlots,
        _ => GroupTargetStatus.NoTarget,
    };
}
