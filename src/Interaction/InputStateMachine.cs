using System;
using System.Collections.Generic;
using HexTactics.Core.Hex;
using HexTactics.Core.Orders;
using HexTactics.Core.Pathfinding;
using HexTactics.Core.Roster;
using HexTactics.Core.Spawning;
using HexTactics.Core.Units;
using HexTactics.Simulation;

namespace HexTactics.Interaction;

/// <summary>Current mode of the input state machine (spec part 2 §10: <c>CommandInputStateMachine</c>).</summary>
public enum InputMode
{
    Idle = 0,

    /// <summary>A unit is selected: its hex's six sectors turn it in place; dragging from it opens a draft.</summary>
    UnitSelected = 1,

    /// <summary>Debug placement mode: the next click on a hex places a unit (spawn method 3).</summary>
    Placement = 2,

    /// <summary>
    /// An order draft of one unit is open: a live arrow follows the cursor from the last waypoint, clicks add
    /// waypoints, a click in a sector of the source hex confirms the final facing and applies the order.
    /// </summary>
    SingleDraft = 3,

    /// <summary>Debug mode: clicking a hex toggles an obstacle (to test re-planning).</summary>
    ObstacleEdit = 5,

    /// <summary>A roster card is dragged over the map (spec part 2 §5).</summary>
    RosterDrag = 6,

    /// <summary>"Команда" pressed: allied units are clicked to form the team, the first one is the captain (§6.1).</summary>
    GroupCollect = 7,

    /// <summary>The team has at least one КТД: clicks add КТД, a sector of the last one confirms (§6.2, §6.3).</summary>
    GroupDraft = 8,
}

public sealed record InteractionSettings
{
    /// <summary>Pointer travel (pixels) below which a press-release is a click, not a drag.</summary>
    public double DragThresholdPixels { get; init; } = 8.0;

    /// <summary>
    /// Radius (in hex sizes) of the neutral area around the source hex centre: only a clear offset towards one of
    /// the six edges selects a facing sector, so a click on the centre never confirms by accident.
    /// </summary>
    public double NeutralZone { get; init; } = 0.3;

    public Side PlacementSide { get; init; } = Side.Player;

    public static InteractionSettings Default { get; } = new();
}

/// <summary>
/// Finite state machine for mouse/keyboard interaction in map coordinates (spec part 2 §4, §8, §9):
/// <list type="bullet">
/// <item>LMB drag from a unit opens a <see cref="OrderDraft"/>; releasing on a green hex places ТД1 and the draft
/// stays open — a live arrow follows the cursor from ТД1 without holding a button; each LMB click on a green hex adds
/// the next ТД.</item>
/// <item>The six sectors of the current source hex (unit or last ТД) select the mandatory final facing; a click there
/// applies the whole chain at once (transaction). Without ТД it is a turn in place.</item>
/// <item>RMB/Esc cancel the whole draft; an attempt to confirm a red hex cancels it too; merely hovering a red hex
/// does not. RMB on a draft ТД removes it and all following ones.</item>
/// </list>
/// No Godot dependency; fully unit-testable.
/// </summary>
public sealed partial class InputStateMachine
{
    private readonly GameSimulation _sim;
    private readonly HexLayout _layout;
    private PressInfo? _press;
    private OrderDraft? _draft;
    private bool _held;
    private int? _rosterEntry;
    private IReadOnlySet<HexCoord> _reachable = new HashSet<HexCoord>();
    private HexCoord _reachableSource;
    private UnitId _reachableUnit;
    private long _reachableVersion = -1;

    public InputStateMachine(GameSimulation simulation, HexLayout layout, InteractionSettings? settings = null)
    {
        _sim = simulation ?? throw new ArgumentNullException(nameof(simulation));
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        Settings = settings ?? InteractionSettings.Default;
    }

    public event Action<InteractionFeedback>? Feedback;

    public InteractionSettings Settings { get; }

    public InputMode Mode { get; private set; } = InputMode.Idle;

    public UnitId? SelectedUnit { get; private set; }

    /// <summary>Confirmed waypoint selected by a short click (belongs to <see cref="SelectedUnit"/>).</summary>
    public int? SelectedWaypointId { get; private set; }

    /// <summary>Last known pointer sample.</summary>
    public PointerSample? Pointer { get; private set; }

    /// <summary>The open order draft, or null.</summary>
    public DraftView? Draft { get; private set; }

    /// <summary>The roster card being dragged over the map, or null.</summary>
    public PlacementView? Placement { get; private set; }

    public bool IsDrafting => _draft is not null;

    /// <summary>
    /// With a unit selected and no draft: the facing a click would turn it to (pointer in a sector of its hex), or null.
    /// </summary>
    public HexDirection? HoverTurnFacing { get; private set; }

    public bool IsPlacementActive => Mode == InputMode.Placement;

    // ------------------------------------------------------------------ pointer events

    public void PointerDown(PointerButton button, PointerSample sample)
    {
        Pointer = sample;
        if (button == PointerButton.Secondary)
        {
            OnSecondaryPress(sample);
            _press = null;
            return;
        }

        _press = new PressInfo(button, sample, Classify(sample));
    }

    public void PointerMove(PointerSample sample)
    {
        Pointer = sample;
        if (Mode == InputMode.RosterDrag)
        {
            UpdatePlacementView(sample);
            return;
        }

        if (IsGrouping)
        {
            UpdateGroupView(sample);
            return;
        }

        if (IsDrafting)
        {
            UpdateDraftView(sample);
            return;
        }

        UpdateHoverTurn(sample);
        if (_press is not { Button: PointerButton.Primary } press || Mode is InputMode.Placement or InputMode.ObstacleEdit)
        {
            return;
        }

        if (press.Sample.Screen.DistanceTo(sample.Screen) <= Settings.DragThresholdPixels)
        {
            return; // hand jitter: still a potential click
        }

        switch (press.Target)
        {
            case PressTarget.UnitTarget u:
                SelectInternal(u.Unit, null);
                BeginDraft(new OrderDraft(u.Unit), held: true);
                UpdateDraftView(sample);
                break;

            case PressTarget.WaypointTarget w:
                {
                    var unit = _sim.State.FindUnit(w.Unit);
                    var index = unit?.Orders.IndexOf(w.WaypointId) ?? -1;
                    if (unit is null || index < 0)
                    {
                        _press = null; // the waypoint was consumed meanwhile
                        break;
                    }

                    var wp = unit.Orders.Items[index];
                    SelectInternal(w.Unit, wp.Id);
                    if (unit.Orders.Last is { } last && last.Id == wp.Id)
                    {
                        // Continue the confirmed route: the draft starts with the remaining confirmed waypoints.
                        var cells = new List<HexCoord>();
                        foreach (var item in unit.Orders.Items)
                        {
                            cells.Add(item.Cell);
                        }

                        BeginDraft(new OrderDraft(w.Unit, DraftKind.Single, cells, isEdit: true), held: true);
                        UpdateDraftView(sample);
                    }
                    else if (unit.Orders.Last is { } lastWp)
                    {
                        Feedback?.Invoke(new ChainContinueRefused(w.Unit, wp, lastWp));
                        _press = null;
                    }

                    break;
                }
        }
    }

    public void PointerUp(PointerButton button, PointerSample sample)
    {
        Pointer = sample;
        var press = _press;
        _press = null;
        if (button != PointerButton.Primary)
        {
            return;
        }

        if (Mode == InputMode.RosterDrag)
        {
            DropRosterCard(sample);
            return;
        }

        if (IsGrouping)
        {
            if (press is not null)
            {
                GroupClick(sample);
            }

            return;
        }

        if (IsDrafting)
        {
            if (_held)
            {
                // Release of the initial drag: same meaning as a click at the release point.
                _held = false;
                Confirm(sample);
            }
            else if (press is not null)
            {
                Confirm(sample);
            }

            return;
        }

        if (press is null || press.Value.Button != button)
        {
            return;
        }

        if (press.Value.Sample.Screen.DistanceTo(sample.Screen) <= Settings.DragThresholdPixels)
        {
            OnPrimaryClick(press.Value, sample);
        }
    }

    /// <summary>Esc: same as RMB — cancels the current draft (or leaves a debug mode). Earlier orders are untouched.</summary>
    public void Cancel()
    {
        _press = null;
        if (Mode == InputMode.RosterDrag)
        {
            CancelRosterDrag(PlacementError.None, null);
        }
        else if (IsGrouping)
        {
            CancelGroup(DraftCancelReason.ByUser, GroupTargetStatus.NoTarget, null);
        }
        else if (IsDrafting)
        {
            CancelDraft(DraftCancelReason.ByUser, DraftTargetStatus.NoTarget, null);
        }
        else if (Mode == InputMode.Placement)
        {
            ExitPlacementMode();
        }
        else if (Mode == InputMode.ObstacleEdit)
        {
            ToggleObstacleEditMode();
        }
    }

    /// <summary>
    /// Re-evaluates the open draft against the current game state (call once per frame): units move, so
    /// reachability and the anchor of a moving unit change.
    /// </summary>
    public void Refresh()
    {
        if (IsDrafting && Pointer is { } pointer)
        {
            UpdateDraftView(pointer);
        }
        else if (IsGrouping && Pointer is { } groupPointer)
        {
            UpdateGroupView(groupPointer);
        }
    }

    /// <summary>
    /// Opens a draft for <paramref name="unit"/> without a held button (e.g. right after placing it from the roster):
    /// the live arrow follows the cursor immediately.
    /// </summary>
    public void StartDraft(UnitId unit)
    {
        if (_sim.State.FindUnit(unit) is null)
        {
            return;
        }

        if (IsGrouping)
        {
            CancelGroup(DraftCancelReason.ByUser, GroupTargetStatus.NoTarget, null);
        }

        if (IsDrafting)
        {
            CancelDraft(DraftCancelReason.ByUser, DraftTargetStatus.NoTarget, null);
        }

        if (Mode is InputMode.Placement or InputMode.ObstacleEdit)
        {
            Cancel();
        }

        SelectInternal(unit, null);
        BeginDraft(new OrderDraft(unit), held: false);
        if (Pointer is { } p)
        {
            UpdateDraftView(p);
        }
    }

    // ------------------------------------------------------------------ roster (spec part 2 §5)

    /// <summary>
    /// LMB pressed on a roster card: the card is dragged over the map; allowed hexes are green, others red.
    /// Releasing on a green hex places the unit and immediately opens its order draft.
    /// </summary>
    public void BeginRosterDrag(int entryId)
    {
        if (Mode == InputMode.RosterDrag)
        {
            return;
        }

        if (IsGrouping)
        {
            CancelGroup(DraftCancelReason.ByUser, GroupTargetStatus.NoTarget, null);
        }

        if (IsDrafting)
        {
            CancelDraft(DraftCancelReason.ByUser, DraftTargetStatus.NoTarget, null);
        }

        if (Mode is InputMode.Placement or InputMode.ObstacleEdit)
        {
            Cancel();
        }

        if (!_sim.BeginRosterPlacement(entryId).Success)
        {
            return;
        }

        _rosterEntry = entryId;
        _press = null;
        Mode = InputMode.RosterDrag;
        Feedback?.Invoke(new RosterDragStarted(entryId));
        UpdatePlacementView(Pointer ?? new PointerSample(new ScreenPoint(0, 0), null, null));
    }

    /// <summary>A click on the card of an already placed operative selects it (and asks for its facing if pending).</summary>
    public void SelectRosterEntry(int entryId)
    {
        if (_sim.State.Roster.Find(entryId)?.Unit is { } unit && _sim.State.FindUnit(unit) is not null)
        {
            Select(unit);
            OpenDraftIfPendingFacing(unit);
        }
    }

    private void UpdatePlacementView(PointerSample sample)
    {
        if (_rosterEntry is not { } entry)
        {
            return;
        }

        var error = sample.Hex is { } hex ? PlacementRules.ValidateCell(_sim.State, hex) : PlacementError.OutOfBounds;
        Placement = new PlacementView(entry, sample.Hex, error, PlacementRules.AllowedCells(_sim.State.Map));
    }

    private void DropRosterCard(PointerSample sample)
    {
        UpdatePlacementView(sample);
        var entry = _rosterEntry!.Value;
        var view = Placement!;
        if (!view.CanPlace)
        {
            CancelRosterDrag(view.Error, view.TargetHex);
            return;
        }

        var result = _sim.PlaceFromRoster(entry, view.TargetHex!.Value);
        EndRosterDrag();
        if (!result.Success)
        {
            Feedback?.Invoke(new RosterCardReturned(entry, result.Error, view.TargetHex));
            return;
        }

        var placed = result.Unit!.Value;
        Feedback?.Invoke(new RosterUnitPlaced(entry, placed, result.Cell!.Value));
        StartDraft(placed); // the arrow is active at once, no extra click on the unit
    }

    private void CancelRosterDrag(PlacementError error, HexCoord? cell)
    {
        if (_rosterEntry is not { } entry)
        {
            return;
        }

        _sim.CancelRosterPlacement(entry);
        EndRosterDrag();
        Feedback?.Invoke(new RosterCardReturned(entry, error, cell));
    }

    private void EndRosterDrag()
    {
        _rosterEntry = null;
        Placement = null;
        Mode = SelectedUnit is not null ? InputMode.UnitSelected : InputMode.Idle;
    }

    /// <summary>A placed unit without facing asks for it whenever it is selected (spec part 2 §8.2).</summary>
    private void OpenDraftIfPendingFacing(UnitId unit)
    {
        if (!IsDrafting && _sim.State.FindUnit(unit) is { IsActive: false })
        {
            StartDraft(unit);
        }
    }

    // ------------------------------------------------------------------ placement / selection / debug modes

    /// <summary>Side of units created by the debug placement mode (enemy targets for the test stand, spec part 3 §9).</summary>
    public Side PlacementSide { get; private set; } = Side.Player;

    /// <summary>Debug placement of units of the given side (the enemy side places static targets — no AI).</summary>
    public void TogglePlacementMode(Side side)
    {
        if (Mode == InputMode.Placement && PlacementSide == side)
        {
            ExitPlacementMode();
            return;
        }

        if (Mode == InputMode.Placement)
        {
            ExitPlacementMode();
        }

        EnterPlacementMode(side);
    }

    public void EnterPlacementMode() => EnterPlacementMode(Settings.PlacementSide);

    public void EnterPlacementMode(Side side)
    {
        PlacementSide = side;
        if (Mode == InputMode.Placement)
        {
            return;
        }

        if (IsGrouping)
        {
            CancelGroup(DraftCancelReason.ByUser, GroupTargetStatus.NoTarget, null);
        }

        if (IsDrafting)
        {
            CancelDraft(DraftCancelReason.ByUser, DraftTargetStatus.NoTarget, null);
        }

        if (Mode == InputMode.ObstacleEdit)
        {
            ToggleObstacleEditMode();
        }

        Mode = InputMode.Placement;
        Feedback?.Invoke(new PlacementModeChanged(true));
    }

    public void ExitPlacementMode()
    {
        if (Mode != InputMode.Placement)
        {
            return;
        }

        Mode = SelectedUnit is not null ? InputMode.UnitSelected : InputMode.Idle;
        Feedback?.Invoke(new PlacementModeChanged(false));
    }

    public void TogglePlacementMode()
    {
        if (Mode == InputMode.Placement)
        {
            ExitPlacementMode();
        }
        else
        {
            EnterPlacementMode();
        }
    }

    public void ToggleObstacleEditMode()
    {
        if (IsGrouping)
        {
            CancelGroup(DraftCancelReason.ByUser, GroupTargetStatus.NoTarget, null);
        }

        if (IsDrafting)
        {
            CancelDraft(DraftCancelReason.ByUser, DraftTargetStatus.NoTarget, null);
        }

        if (Mode == InputMode.ObstacleEdit)
        {
            Mode = SelectedUnit is not null ? InputMode.UnitSelected : InputMode.Idle;
            Feedback?.Invoke(new ObstacleEditModeChanged(false));
        }
        else
        {
            if (Mode == InputMode.Placement)
            {
                ExitPlacementMode();
            }

            Mode = InputMode.ObstacleEdit;
            Feedback?.Invoke(new ObstacleEditModeChanged(true));
        }
    }

    /// <summary>Selects a unit programmatically (e.g. after spawning from the debug panel). Cancels an open draft.</summary>
    public void Select(UnitId? unit)
    {
        if (unit is { } id && _sim.State.FindUnit(id) is null)
        {
            return;
        }

        if (Mode == InputMode.RosterDrag)
        {
            CancelRosterDrag(PlacementError.None, null);
        }

        if (IsGrouping)
        {
            CancelGroup(DraftCancelReason.ByUser, GroupTargetStatus.NoTarget, null);
        }

        if (IsDrafting)
        {
            CancelDraft(DraftCancelReason.ByUser, DraftTargetStatus.NoTarget, null);
        }

        if (Mode == InputMode.Placement)
        {
            ExitPlacementMode();
        }

        if (Mode == InputMode.ObstacleEdit)
        {
            ToggleObstacleEditMode();
        }

        SelectInternal(unit, null);
    }

    /// <summary>Drops selections that no longer exist (call once per frame).</summary>
    public void Validate()
    {
        if (_draft is { } draft && _sim.State.FindUnit(draft.Owner) is null)
        {
            CancelDraft(DraftCancelReason.UnitLost, DraftTargetStatus.NoTarget, null);
        }

        if (SelectedUnit is { } id && _sim.State.FindUnit(id) is null)
        {
            Select(null);
            return;
        }

        if (SelectedWaypointId is { } wp && (SelectedUnit is null || _sim.State.GetUnit(SelectedUnit.Value).Orders.IndexOf(wp) < 0))
        {
            SelectedWaypointId = null;
        }
    }

    private void OnPrimaryClick(PressInfo press, PointerSample sample)
    {
        if (Mode == InputMode.Placement)
        {
            PlaceAt(sample.Hex);
            return;
        }

        if (Mode == InputMode.ObstacleEdit)
        {
            if (sample.Hex is { } cell && _sim.State.Grid.Contains(cell))
            {
                var blocked = _sim.State.Grid[cell].IsWalkable;
                Feedback?.Invoke(new ObstacleToggled(cell, blocked, _sim.SetObstacle(cell, blocked)));
            }

            return;
        }

        // A click in a sector of the selected unit's current hex turns it in place (spec part 2 §4.4).
        if (SelectedUnit is { } selected && TurnSectorFor(selected, sample) is { } facing)
        {
            Commit(new OrderDraft(selected), facing);
            return;
        }

        switch (press.Target)
        {
            case PressTarget.WaypointTarget w when _sim.State.FindUnit(w.Unit) is { } unit && unit.Orders.IndexOf(w.WaypointId) is var i && i >= 0:
                var wp = unit.Orders.Items[i];
                SelectInternal(w.Unit, wp.Id);
                Feedback?.Invoke(new WaypointSelected(w.Unit, wp));
                break;
            case PressTarget.UnitTarget u:
                SelectInternal(u.Unit, null);
                OpenDraftIfPendingFacing(u.Unit);
                break;
            default:
                SelectInternal(null, null);
                break;
        }
    }

    private void OnSecondaryPress(PointerSample sample)
    {
        if (Mode == InputMode.RosterDrag)
        {
            CancelRosterDrag(PlacementError.None, null);
            return;
        }

        if (IsGrouping)
        {
            GroupSecondaryPress(sample);
            return;
        }

        if (IsDrafting)
        {
            var draft = _draft!;
            var index = DraftWaypointIndexAt(draft, sample);
            if (index >= 0)
            {
                draft.TruncateFrom(index);
                Feedback?.Invoke(new DraftTruncated(draft.Owner, index + 1, draft.Waypoints.Count));
                UpdateDraftView(sample);
            }
            else
            {
                CancelDraft(DraftCancelReason.ByUser, DraftTargetStatus.NoTarget, null);
            }

            return;
        }

        if (Mode == InputMode.Placement)
        {
            ExitPlacementMode();
            return;
        }

        if (Mode == InputMode.ObstacleEdit)
        {
            ToggleObstacleEditMode();
            return;
        }

        // RMB on a confirmed waypoint of the selected unit: remove it and all following ones.
        if (SelectedUnit is { } selected && sample.WaypointId is { } wpId && _sim.State.FindWaypoint(wpId) is { } hit && hit.Unit.Id == selected)
        {
            var wp = hit.Unit.Orders.Items[hit.Index];
            var result = _sim.TruncateOrders(selected, wpId);
            SelectedWaypointId = null;
            Feedback?.Invoke(new ConfirmedWaypointsRemoved(selected, wp, result));
        }
    }

    private void PlaceAt(HexCoord? hex)
    {
        // A pointer that misses the plane yields a request without a cell (rejected as MissingCell).
        var request = hex is { } cell
            ? SpawnRequest.AtCell(cell, PlacementSide)
            : new SpawnRequest(SpawnMethod.DirectPlacement, PlacementSide);
        var result = _sim.Spawn(request);
        Feedback?.Invoke(new SpawnAttempted(request, result));
        if (result.Success)
        {
            Mode = InputMode.UnitSelected;
            Feedback?.Invoke(new PlacementModeChanged(false));
            SelectInternal(result.Unit, null);
        }
    }

    /// <summary>
    /// What a press refers to. Priority: waypoint of the selected unit, then a unit (body or the hex it logically
    /// occupies — a moving unit is selectable on its current hex), then a waypoint of another unit, then the ground.
    /// </summary>
    private PressTarget Classify(PointerSample sample)
    {
        var state = _sim.State;
        var wpHit = sample.WaypointId is { } wpId ? state.FindWaypoint(wpId) : null;
        if (wpHit is { } own && own.Unit.Id == SelectedUnit)
        {
            return new PressTarget.WaypointTarget(own.Unit.Id, own.Unit.Orders.Items[own.Index].Id);
        }

        if (sample.Unit is { } u && state.FindUnit(u) is not null)
        {
            return new PressTarget.UnitTarget(u);
        }

        if (sample.Hex is { } hex && state.UnitAt(hex) is { } occupant)
        {
            return new PressTarget.UnitTarget(occupant.Id);
        }

        if (wpHit is { } other)
        {
            return new PressTarget.WaypointTarget(other.Unit.Id, other.Unit.Orders.Items[other.Index].Id);
        }

        return new PressTarget.Ground();
    }

    private void SelectInternal(UnitId? unit, int? waypointId)
    {
        if (!IsDrafting && !IsGrouping)
        {
            Mode = unit is null ? InputMode.Idle : InputMode.UnitSelected;
        }

        SelectedWaypointId = unit is null ? null : waypointId;
        if (SelectedUnit != unit)
        {
            SelectedUnit = unit;
            HoverTurnFacing = null;
            Feedback?.Invoke(new SelectionChanged(unit));
        }
    }

    // ------------------------------------------------------------------ draft

    private void BeginDraft(OrderDraft draft, bool held)
    {
        _draft = draft;
        _held = held;
        _reachableVersion = -1;
        HoverTurnFacing = null;
        Mode = InputMode.SingleDraft;
        Feedback?.Invoke(new DraftStarted(draft.Owner, draft.IsEdit));
    }

    /// <summary>Handles a click (or the release of the initial drag) while a draft is open.</summary>
    private void Confirm(PointerSample sample)
    {
        UpdateDraftView(sample);
        var view = Draft!;
        var draft = _draft!;
        switch (view.Status)
        {
            case DraftTargetStatus.Valid:
                draft.Add(view.TargetHex!.Value);
                Feedback?.Invoke(new DraftWaypointAdded(draft.Owner, draft.Waypoints.Count, view.TargetHex.Value));
                UpdateDraftView(sample); // the live arrow now starts at the new waypoint
                break;

            case DraftTargetStatus.FacingSector:
                Commit(draft, view.SectorFacing!.Value);
                break;

            case DraftTargetStatus.Neutral:
                break; // the centre never confirms; the draft stays open

            case DraftTargetStatus.AttackTarget:
                // Spec part 3 §4.1: a living enemy at the end of the arrow becomes the attack target instead of a waypoint;
                // the waypoints so far stay, no facing is asked — the target sets it.
                CommitAttack(draft, view.AttackTarget!.Value);
                break;

            default:
                // An attempt to confirm a red hex cancels the whole draft (hovering alone never does).
                CancelDraft(DraftCancelReason.InvalidTarget, view.Status, view.TargetHex);
                break;
        }
    }

    private void Commit(OrderDraft draft, HexDirection facing)
    {
        var order = draft.ToOrder(facing);
        var result = _sim.ApplyOrder(order);
        if (!result.Success)
        {
            // The game changed meanwhile: keep the draft open and the previous route untouched (spec part 2 §9.5).
            Feedback?.Invoke(new DraftCommitFailed(draft.Owner, result));
            return;
        }

        if (draft.State == DraftState.Drafting)
        {
            draft.MarkCommitted();
        }

        var owner = draft.Owner;
        if (ReferenceEquals(draft, _draft))
        {
            EndDraft();
        }

        Feedback?.Invoke(new DraftCommitted(owner, order));
    }

    private void CommitAttack(OrderDraft draft, UnitId target)
    {
        var anchor = _sim.CommandAnchor(draft.Owner);
        var targetCell = _sim.State.FindUnit(target)?.Cell ?? anchor;
        var facing = HexDirections.Toward(draft.SourceFor(anchor), targetCell) ?? HexDirection.North;
        var order = IndividualOrder.Attack(draft.Owner, draft.Waypoints, target, facing);
        var result = _sim.ApplyOrder(order);
        if (!result.Success)
        {
            Feedback?.Invoke(new DraftCommitFailed(draft.Owner, result));
            return;
        }

        draft.MarkCommitted();
        var owner = draft.Owner;
        EndDraft();
        Feedback?.Invoke(new DraftCommitted(owner, order));
    }

    private void CancelDraft(DraftCancelReason reason, DraftTargetStatus status, HexCoord? cell)
    {
        if (_draft is not { } draft)
        {
            return;
        }

        draft.MarkCancelled();
        EndDraft();
        Feedback?.Invoke(new DraftCancelled(draft.Owner, reason, status, cell));
    }

    private void EndDraft()
    {
        _draft = null;
        _held = false;
        Draft = null;
        Mode = SelectedUnit is not null ? InputMode.UnitSelected : InputMode.Idle;
        if (Pointer is { } p)
        {
            UpdateHoverTurn(p);
        }
    }

    private void UpdateDraftView(PointerSample sample)
    {
        if (_draft is not { } draft)
        {
            return;
        }

        var state = _sim.State;
        var owner = state.FindUnit(draft.Owner);
        if (owner is null)
        {
            CancelDraft(DraftCancelReason.UnitLost, DraftTargetStatus.NoTarget, null);
            return;
        }

        var anchor = _sim.CommandAnchor(draft.Owner);
        var source = draft.SourceFor(anchor);
        var reachable = ReachableFrom(draft.Owner, source);
        var sourceCenter = _layout.HexToPlane(source);
        DraftTargetStatus status;
        HexDirection? sector = null;
        HexDirection? unitFacing = null;
        IReadOnlyList<HexCoord>? path = null;
        UnitId? attack = null;

        if (sample.Plane is not { } plane || sample.Hex is not { } hex)
        {
            status = DraftTargetStatus.NoTarget;
        }
        else if (hex == source || OnGroundOf(source, sample) is not null)
        {
            sector = SectorAt(source, OnGroundOf(source, sample) ?? plane);
            status = sector is null ? DraftTargetStatus.Neutral : DraftTargetStatus.FacingSector;
            if (sector is not null && draft.Waypoints.Count == 0)
            {
                unitFacing = sector; // turn in place preview
            }
        }
        else if (EnemyUnder(sample, hex, owner) is { } other)
        {
            // Checked before the "red hex cancels" rule: a living enemy is a target, not a forbidden waypoint (spec part 3 §4.1).
            status = DraftTargetStatus.AttackTarget;
            attack = other.Id;
        }
        else
        {
            status = Map(WaypointRules.ValidateTarget(state, draft.Owner, source, hex, reachable));
            if (status == DraftTargetStatus.Valid)
            {
                path = Pathfinder.FindPath(state.Grid, source, hex, state.ObstaclesFor(draft.Owner));
                if (draft.Waypoints.Count == 0 && path is { Count: > 1 })
                {
                    // Preview facing = first step of the shortest path, not the straight line (spec part 1 §5.2).
                    unitFacing = source.DirectionTo(path[1]);
                }
            }
        }

        draft.PreviewFacing = sector;
        Draft = new DraftView(draft.Owner, draft.Waypoints, anchor, source, sourceCenter, sample.Plane, sample.Hex, status, sector, unitFacing, reachable, path, _held, draft.IsEdit, attack);
    }

    /// <summary>A living enemy of <paramref name="owner"/> under the pointer: its body, or the unit on the hex.</summary>
    private UnitState? EnemyUnder(PointerSample sample, HexCoord hex, UnitState owner)
    {
        var state = _sim.State;
        if (sample.Unit is { } picked && state.FindUnit(picked) is { IsAlive: true } body && body.Side != owner.Side)
        {
            return body;
        }

        return state.UnitAt(hex) is { IsAlive: true } other && other.Side != owner.Side ? other : null;
    }

    private void UpdateHoverTurn(PointerSample sample)
    {
        HoverTurnFacing = Mode == InputMode.UnitSelected && SelectedUnit is { } selected ? TurnSectorFor(selected, sample) : null;
    }

    /// <summary>Facing sector of the selected unit's current hex under the pointer, or null (outside / centre).</summary>
    private HexDirection? TurnSectorFor(UnitId unit, PointerSample sample)
    {
        if (sample.Plane is not { } plane || sample.Hex is not { } hex || _sim.State.FindUnit(unit) is null)
        {
            return null;
        }

        var anchor = _sim.CommandAnchor(unit);
        if (OnGroundOf(anchor, sample) is { } ground)
        {
            return SectorAt(anchor, ground);
        }

        return hex == anchor ? SectorAt(anchor, plane) : null;
    }

    /// <summary>
    /// The ground point of the sample if it lies in <paramref name="hex"/>: a raised obstacle next to the source hex can
    /// cover its edge on screen, yet the facing sector there must stay selectable (spec part 2 §4.2).
    /// </summary>
    private PlaneVector? OnGroundOf(HexCoord hex, PointerSample sample) =>
        sample.Ground is { } ground && _layout.PlaneToHex(ground) == hex ? ground : null;

    /// <summary>One of six edge sectors of <paramref name="hex"/> in map-plane coordinates, or null in the neutral centre.</summary>
    private HexDirection? SectorAt(HexCoord hex, PlaneVector plane)
    {
        var offset = plane - _layout.HexToPlane(hex);
        if (offset.Length < Settings.NeutralZone * _layout.Size)
        {
            return null;
        }

        return HexDirections.FromPlaneVector(offset.X, offset.Z);
    }

    private static int DraftWaypointIndexAt(OrderDraft draft, PointerSample sample)
    {
        if (sample.Hex is not { } hex)
        {
            return -1;
        }

        for (var i = draft.Waypoints.Count - 1; i >= 0; i--)
        {
            if (draft.Waypoints[i] == hex)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Hexes reachable from the draft source, computed when the source changes and recomputed whenever the game
    /// topology (terrain or unit positions) changes.
    /// </summary>
    private IReadOnlySet<HexCoord> ReachableFrom(UnitId unit, HexCoord source)
    {
        var version = _sim.State.TopologyVersion;
        if (version != _reachableVersion || source != _reachableSource || unit != _reachableUnit)
        {
            _reachable = WaypointRules.ReachableTargets(_sim.State, unit, source);
            _reachableVersion = version;
            _reachableSource = source;
            _reachableUnit = unit;
        }

        return _reachable;
    }

    private static DraftTargetStatus Map(WaypointError error) => error switch
    {
        WaypointError.None => DraftTargetStatus.Valid,
        WaypointError.OutOfBounds => DraftTargetStatus.OutOfBounds,
        WaypointError.NotWalkable => DraftTargetStatus.NotWalkable,
        WaypointError.Occupied => DraftTargetStatus.Occupied,
        WaypointError.Unreachable => DraftTargetStatus.Unreachable,
        WaypointError.SameAsSource => DraftTargetStatus.Neutral,
        _ => DraftTargetStatus.NoTarget,
    };

    private readonly record struct PressInfo(PointerButton Button, PointerSample Sample, PressTarget Target);

    private abstract record PressTarget
    {
        public sealed record UnitTarget(UnitId Unit) : PressTarget;

        public sealed record WaypointTarget(UnitId Unit, int WaypointId) : PressTarget;

        public sealed record Ground : PressTarget;
    }
}
