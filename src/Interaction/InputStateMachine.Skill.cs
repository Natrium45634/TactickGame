using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Hex;
using HexTactics.Core.Orders;
using HexTactics.Core.Skills;
using HexTactics.Core.Units;

namespace HexTactics.Interaction;

/// <summary>Aiming an immediate skill use (spec part 3 §5.2).</summary>
/// <param name="Origin">Hex the skill will be used from (the unit's command anchor).</param>
/// <param name="Direction">One of six directions chosen by the pointer, or null in the neutral centre.</param>
/// <param name="Area">Existing hexes hit for <paramref name="Direction"/> (empty without a direction).</param>
/// <param name="DraftIndex">Aiming for a waypoint of the open draft (index), or null.</param>
/// <param name="WaypointId">Aiming for a confirmed waypoint, or null.</param>
public sealed record SkillAimView(UnitId Unit, string SkillId, HexCoord Origin, PlaneVector OriginCenter, HexDirection? Direction, IReadOnlyList<HexCoord> Area, PlaneVector? Pointer, int? DraftIndex = null, int? WaypointId = null)
{
    public bool FromWaypoint => DraftIndex is not null || WaypointId is not null;
}

/// <summary>
/// The compact context menu next to a waypoint (spec part 3 §6.1 step 2): opened by a short click on the waypoint's
/// central marker; offers the skill icon (and removing a skill planned there).
/// </summary>
/// <param name="Number">The waypoint's number as shown (ТД1, ТД2 …).</param>
public sealed record WaypointMenuView(UnitId Unit, HexCoord Cell, int Number, int? DraftIndex, int? WaypointId, WaypointSkillSpec? Skill);

public sealed record WaypointMenuOpened(UnitId Unit, int Number) : InteractionFeedback;

/// <summary>A skill was planned at a waypoint (in the draft — applied with the order — or on a confirmed route).</summary>
public sealed record WaypointSkillPlanned(UnitId Unit, int Number, HexDirection Direction, bool InDraft) : InteractionFeedback;

public sealed record WaypointSkillCleared(UnitId Unit, int Number, bool InDraft) : InteractionFeedback;

public sealed record SkillAimStarted(UnitId Unit, string SkillId) : InteractionFeedback;

public sealed record SkillAimCancelled(UnitId Unit) : InteractionFeedback;

/// <summary>The skill use was planned; it fires once when the unit stands on a hex centre (after a pause: on resume).</summary>
public sealed record SkillUsePlanned(UnitId Unit, string SkillId, HexDirection Direction) : InteractionFeedback;

public sealed record SkillUseRefused(UnitId Unit, string Reason) : InteractionFeedback;

/// <summary>
/// Immediate skill use (spec part 3 §5.2, design proposal for the trigger): with a unit selected, the skill button / key
/// starts aiming — the pointer picks one of six directions around the unit's hex (neutral centre: none), the two hit
/// hexes are previewed in grey, LMB confirms, RMB/Esc cancel only the aiming.
/// </summary>
public sealed partial class InputStateMachine
{
    private string? _aimSkill;
    private HexCoord? _aimOrigin;
    private int? _aimDraftIndex;
    private int? _aimWaypointId;

    public SkillAimView? SkillAim { get; private set; }

    /// <summary>The open context menu of a waypoint, or null.</summary>
    public WaypointMenuView? WaypointMenu { get; private set; }

    public bool IsAimingSkill => Mode == InputMode.SkillAim;

    /// <summary>Starts aiming <paramref name="skillId"/> for the selected unit. Returns false if not possible.</summary>
    public bool BeginSkillAim(string skillId)
    {
        if (SelectedUnit is not { } id || _sim.State.FindUnit(id) is not { IsAlive: true, IsActive: true } unit
            || unit.Side != Settings.PlacementSide || !unit.Skills.Contains(skillId))
        {
            if (SelectedUnit is { } refused)
            {
                Feedback?.Invoke(new SkillUseRefused(refused, "not available"));
            }

            return false;
        }

        if (IsAimingSkill)
        {
            CancelSkillAim();
        }

        CloseWaypointMenu();
        Cancel(); // drafts, command mode, roster card, debug modes
        Select(id);
        _aimSkill = skillId;
        Mode = InputMode.SkillAim;
        Feedback?.Invoke(new SkillAimStarted(id, skillId));
        UpdateSkillAim(Pointer ?? new PointerSample(new ScreenPoint(0, 0), null, null));
        return true;
    }

    public void CancelSkillAim()
    {
        if (!IsAimingSkill)
        {
            return;
        }

        var unit = SkillAim?.Unit ?? SelectedUnit;
        EndSkillAim();
        if (unit is { } u)
        {
            Feedback?.Invoke(new SkillAimCancelled(u));
        }
    }

    private void EndSkillAim()
    {
        _aimSkill = null;
        _aimOrigin = null;
        _aimDraftIndex = null;
        _aimWaypointId = null;
        SkillAim = null;
        Mode = _draft is not null ? InputMode.SingleDraft : SelectedUnit is not null ? InputMode.UnitSelected : InputMode.Idle;
        if (_draft is not null && Pointer is { } p)
        {
            UpdateDraftView(p); // back to the draft exactly as it was (spec part 3 §6.1 step 7)
        }
    }

    // ------------------------------------------------------------------ waypoint menu and skills at waypoints (§6)

    /// <summary>Opens the context menu of draft waypoint <paramref name="index"/> or of a confirmed waypoint.</summary>
    private void OpenWaypointMenu(UnitId unit, HexCoord cell, int number, int? draftIndex, int? waypointId, WaypointSkillSpec? skill)
    {
        WaypointMenu = new WaypointMenuView(unit, cell, number, draftIndex, waypointId, skill);
        Feedback?.Invoke(new WaypointMenuOpened(unit, number));
    }

    public void CloseWaypointMenu() => WaypointMenu = null;

    /// <summary>The skill icon of the open waypoint menu was clicked: aim the skill from that waypoint (red arrow).</summary>
    public bool BeginWaypointSkillAim(string skillId)
    {
        if (WaypointMenu is not { } menu || _sim.State.FindUnit(menu.Unit) is not { IsAlive: true } unit || !unit.Skills.Contains(skillId))
        {
            return false;
        }

        WaypointMenu = null;
        _aimSkill = skillId;
        _aimOrigin = menu.Cell;
        _aimDraftIndex = menu.DraftIndex;
        _aimWaypointId = menu.WaypointId;
        if (_draft is null)
        {
            SelectInternal(menu.Unit, menu.WaypointId);
        }

        if (_draft is not null)
        {
            // Freeze the draft's live arrow while aiming (the red arrow takes over).
            UpdateDraftView(new PointerSample(new ScreenPoint(0, 0), null, null));
        }

        Mode = InputMode.SkillAim;
        UpdateSkillAim(Pointer ?? new PointerSample(new ScreenPoint(0, 0), null, null));
        Feedback?.Invoke(new SkillAimStarted(menu.Unit, skillId));
        return true;
    }

    /// <summary>"Remove skill" in the waypoint menu: drops the skill planned there (the waypoint stays).</summary>
    public void ClearWaypointSkillFromMenu()
    {
        if (WaypointMenu is not { } menu)
        {
            return;
        }

        WaypointMenu = null;
        if (menu.DraftIndex is { } index && _draft is { } draft)
        {
            draft.SetSkill(index, null);
            Feedback?.Invoke(new WaypointSkillCleared(menu.Unit, menu.Number, true));
            if (Pointer is { } p)
            {
                UpdateDraftView(p);
            }
        }
        else if (menu.WaypointId is { } id && _sim.RemoveWaypointSkill(menu.Unit, id).Success)
        {
            Feedback?.Invoke(new WaypointSkillCleared(menu.Unit, menu.Number, false));
        }
    }

    /// <summary>Index of the draft waypoint whose central marker is under the pointer (the latest one wins), or null.</summary>
    private int? DraftMarkerAt(OrderDraft draft, PointerSample sample)
    {
        if ((sample.Ground ?? sample.Plane) is not { } p)
        {
            return null;
        }

        for (var i = draft.Waypoints.Count - 1; i >= 0; i--)
        {
            if ((p - _layout.HexToPlane(draft.Waypoints[i])).Length <= Settings.NeutralZone * _layout.Size)
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>Short click in a draft: on a waypoint's central marker it opens the menu (spec part 3 §6.2).</summary>
    private bool TryOpenDraftWaypointMenu(OrderDraft draft, PointerSample sample)
    {
        if (DraftMarkerAt(draft, sample) is not { } index)
        {
            return false;
        }

        draft.Skills.TryGetValue(index, out var skill);
        OpenWaypointMenu(draft.Owner, draft.Waypoints[index], index + 1, index, null, skill);
        return true;
    }

    /// <summary>Short click on a confirmed waypoint marker opens its menu.</summary>
    private void OpenConfirmedWaypointMenu(UnitState unit, Waypoint wp)
    {
        // Command orders (КТД) carry no skills in this stage: spec part 3 §6 defines them for a unit's own route only.
        if (!unit.IsAlive || wp.IsDetour || unit.Orders.GroupId is not null || unit.Side != Settings.PlacementSide)
        {
            return;
        }

        var skill = wp.Skill is { Status: SkillActionStatus.Planned } planned ? new WaypointSkillSpec(planned.SkillId, planned.Direction) : null;
        OpenWaypointMenu(unit.Id, wp.Cell, wp.Sequence, null, wp.Id, skill);
    }

    private void UpdateSkillAim(PointerSample sample)
    {
        if (!IsAimingSkill || SelectedUnit is not { } id || _aimSkill is not { } skillId)
        {
            return;
        }

        if (_sim.State.FindUnit(id) is not { IsAlive: true })
        {
            EndSkillAim();
            return;
        }

        if (_aimWaypointId is { } wpId && _sim.State.GetUnit(id).Orders.IndexOf(wpId) < 0)
        {
            EndSkillAim(); // the waypoint was reached or removed meanwhile
            return;
        }

        var origin = _aimOrigin ?? _sim.CommandAnchor(id);
        var centre = _layout.HexToPlane(origin);
        HexDirection? direction = null;
        var point = sample.Ground ?? sample.Plane;
        if (point is { } p && (p - centre).Length >= Settings.NeutralZone * _layout.Size)
        {
            var v = p - centre;
            direction = HexDirections.FromPlaneVector(v.X, v.Z);
        }

        var area = direction is { } d && _sim.Settings.Skills.TryGetValue(skillId, out var skill)
            ? skill.Area(origin, d, _sim.State.Grid)
            : (IReadOnlyList<HexCoord>)System.Array.Empty<HexCoord>();
        SkillAim = new SkillAimView(id, skillId, origin, centre, direction, area, sample.Plane, _aimDraftIndex, _aimWaypointId);
    }

    private void SkillAimClick(PointerSample sample)
    {
        UpdateSkillAim(sample);
        if (SkillAim is not { Direction: { } direction } view)
        {
            return; // the neutral centre never confirms
        }

        if (view.DraftIndex is { } index && _draft is { } draft)
        {
            // Part of the same draft: applied atomically with the final facing (spec part 3 §6.2).
            draft.SetSkill(index, new WaypointSkillSpec(view.SkillId, direction));
            EndSkillAim();
            Feedback?.Invoke(new WaypointSkillPlanned(view.Unit, index + 1, direction, true));
            return;
        }

        if (view.WaypointId is { } waypointId)
        {
            var number = _sim.State.FindWaypoint(waypointId) is { } hit ? hit.Unit.Orders.Items[hit.Index].Sequence : 0;
            var assigned = _sim.AssignWaypointSkill(view.Unit, waypointId, view.SkillId, direction);
            EndSkillAim();
            Feedback?.Invoke(assigned.Success
                ? new WaypointSkillPlanned(view.Unit, number, direction, false)
                : new SkillUseRefused(view.Unit, assigned.Reason ?? "refused"));
            return;
        }

        var result = _sim.UseSkill(view.Unit, view.SkillId, direction);
        EndSkillAim();
        Feedback?.Invoke(result.Success
            ? new SkillUsePlanned(view.Unit, view.SkillId, direction)
            : new SkillUseRefused(view.Unit, result.Reason ?? "refused"));
    }
}
