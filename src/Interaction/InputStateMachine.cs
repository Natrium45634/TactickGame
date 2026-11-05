using System;
using HexTactics.Core.Hex;
using HexTactics.Core.Orders;
using HexTactics.Core.Spawning;
using HexTactics.Core.Units;
using HexTactics.Simulation;

namespace HexTactics.Interaction;

/// <summary>Current mode of the input state machine (spec §5.3).</summary>
public enum InputMode
{
    Idle = 0,
    UnitSelected = 1,

    /// <summary>Debug placement mode: the next click on a hex places a unit (spawn method 3).</summary>
    Placement = 2,

    /// <summary>LMB held on a unit and moved: drawing an order arrow from the unit.</summary>
    DraggingFromUnit = 3,

    /// <summary>LMB held on the last waypoint and moved: drawing the next waypoint of the chain.</summary>
    DraggingFromWaypoint = 4,
}

public sealed record InteractionSettings
{
    /// <summary>Pointer travel (pixels) below which a press-release is a click, not a drag.</summary>
    public double DragThresholdPixels { get; init; } = 8.0;

    /// <summary>
    /// Radius around the source hex centre (in hex sizes) inside which a release does not rotate:
    /// a rotate-only order needs a clearly directional gesture.
    /// </summary>
    public double RotateDeadZone { get; init; } = 0.25;

    public Side PlacementSide { get; init; } = Side.Player;

    public static InteractionSettings Default { get; } = new();
}

/// <summary>
/// Finite state machine for mouse/keyboard interaction, working purely in map coordinates:
/// <c>Idle → UnitSelected → DraggingFromUnit / DraggingFromWaypoint → commit / rotate / cancel → UnitSelected</c>.
/// The client feeds pointer samples; the machine issues commands to <see cref="GameSimulation"/>
/// and reports <see cref="InteractionFeedback"/>. No Godot dependency, fully unit-testable.
/// </summary>
public sealed class InputStateMachine
{
    private readonly GameSimulation _sim;
    private readonly HexLayout _layout;
    private PressInfo? _press;
    private DragState? _drag;

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

    /// <summary>Waypoint selected by a short click (belongs to <see cref="SelectedUnit"/>).</summary>
    public int? SelectedWaypointId { get; private set; }

    /// <summary>Last known pointer sample (for previews).</summary>
    public PointerSample? Pointer { get; private set; }

    /// <summary>Active order drag, or null.</summary>
    public DragPreview? Drag { get; private set; }

    public bool IsDragging => _drag is not null;

    public bool IsPlacementActive => Mode == InputMode.Placement;

    // ------------------------------------------------------------------ pointer events

    public void PointerDown(PointerButton button, PointerSample sample)
    {
        Pointer = sample;
        if (button == PointerButton.Secondary && IsDragging)
        {
            // RMB cancels only the unfinished drag; existing waypoints are untouched.
            CancelDrag(byUser: true);
            _press = null;
            return;
        }

        if (IsDragging)
        {
            return;
        }

        _press = new PressInfo(button, sample, Classify(sample));
    }

    public void PointerMove(PointerSample sample)
    {
        Pointer = sample;
        if (IsDragging)
        {
            UpdateDrag(sample);
            return;
        }

        if (_press is not { Button: PointerButton.Primary } press || Mode == InputMode.Placement)
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
                BeginDrag(u.Unit, OrderMode.Replace, _sim.CommandAnchor(u.Unit));
                UpdateDrag(sample);
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
                        BeginDrag(w.Unit, OrderMode.Append, wp.Cell);
                        UpdateDrag(sample);
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
        if (IsDragging)
        {
            if (button == PointerButton.Primary)
            {
                FinishDrag(sample);
            }

            _press = null;
            return;
        }

        var press = _press;
        _press = null;
        if (press is null || press.Value.Button != button)
        {
            return;
        }

        var isClick = press.Value.Sample.Screen.DistanceTo(sample.Screen) <= Settings.DragThresholdPixels;
        if (!isClick)
        {
            return;
        }

        if (button == PointerButton.Secondary)
        {
            if (Mode == InputMode.Placement)
            {
                ExitPlacementMode();
            }

            return;
        }

        OnPrimaryClick(press.Value, sample);
    }

    /// <summary>
    /// Esc: cancels the unfinished arrow (the existing queue is not changed) or leaves placement mode.
    /// </summary>
    public void Cancel()
    {
        if (IsDragging)
        {
            CancelDrag(byUser: true);
        }
        else if (Mode == InputMode.Placement)
        {
            ExitPlacementMode();
        }

        _press = null;
    }

    // ------------------------------------------------------------------ placement / selection

    public void EnterPlacementMode()
    {
        if (Mode == InputMode.Placement)
        {
            return;
        }

        if (IsDragging)
        {
            CancelDrag(byUser: true);
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

    /// <summary>Selects a unit programmatically (e.g. after spawning from the debug panel).</summary>
    public void Select(UnitId? unit)
    {
        if (unit is { } id && _sim.State.FindUnit(id) is null)
        {
            return;
        }

        if (IsDragging)
        {
            CancelDrag(byUser: false);
        }

        if (Mode == InputMode.Placement)
        {
            ExitPlacementMode();
        }

        SelectInternal(unit, null);
    }

    /// <summary>Drops selections that no longer exist (call once per frame).</summary>
    public void Validate()
    {
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

        switch (press.Target)
        {
            case PressTarget.WaypointTarget w when _sim.State.FindUnit(w.Unit) is { } unit && unit.Orders.IndexOf(w.WaypointId) is var i && i >= 0:
                var wp = unit.Orders.Items[i];
                SelectInternal(w.Unit, wp.Id);
                Feedback?.Invoke(new WaypointSelected(w.Unit, wp));
                break;
            case PressTarget.UnitTarget u:
                SelectInternal(u.Unit, null);
                break;
            default:
                SelectInternal(null, null);
                break;
        }
    }

    private void PlaceAt(HexCoord? hex)
    {
        // A pointer that misses the plane yields a request without a cell (rejected as MissingCell).
        var request = hex is { } cell
            ? SpawnRequest.AtCell(cell, Settings.PlacementSide)
            : new SpawnRequest(SpawnMethod.DirectPlacement, Settings.PlacementSide);
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
    /// What a press refers to. Priority: waypoint of the selected unit, then a unit (body or its hex),
    /// then a waypoint of another unit, then the ground.
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
        Mode = unit is null ? InputMode.Idle : (IsDragging ? Mode : InputMode.UnitSelected);
        SelectedWaypointId = unit is null ? null : waypointId;
        if (SelectedUnit != unit)
        {
            SelectedUnit = unit;
            Feedback?.Invoke(new SelectionChanged(unit));
        }
    }

    // ------------------------------------------------------------------ order drag

    private void BeginDrag(UnitId unit, OrderMode mode, HexCoord source)
    {
        _drag = new DragState(unit, mode, source);
        Mode = mode == OrderMode.Replace ? InputMode.DraggingFromUnit : InputMode.DraggingFromWaypoint;
        Feedback?.Invoke(new DragStarted(unit, mode, source));
    }

    private void UpdateDrag(PointerSample sample)
    {
        if (_drag is not { } drag)
        {
            return;
        }

        var sourceCenter = _layout.HexToPlane(drag.Source);
        DragTargetStatus status;
        HexDirection? facing = null;

        if (sample.Plane is not { } plane || sample.Hex is not { } hex)
        {
            status = DragTargetStatus.NoTarget;
        }
        else if (hex == drag.Source)
        {
            status = SourceStatus(drag, plane - sourceCenter, out facing);
        }
        else
        {
            status = Map(WaypointRules.ValidateTarget(_sim.State, drag.Unit, drag.Source, hex));
            if (status == DragTargetStatus.Valid && drag.Mode == OrderMode.Replace)
            {
                facing = PreviewFirstStep(drag.Source, hex);
            }
        }

        Drag = new DragPreview(drag.Unit, drag.Mode, drag.Source, sourceCenter, sample.Plane, sample.Hex, status, facing);
    }

    private DragTargetStatus SourceStatus(DragState drag, PlaneVector offset, out HexDirection? facing)
    {
        facing = null;
        if (drag.Mode != OrderMode.Replace)
        {
            return DragTargetStatus.SourceDeadZone;
        }

        var unit = _sim.State.GetUnit(drag.Unit);
        if (!unit.Orders.IsEmpty)
        {
            return DragTargetStatus.SourceHasOrders;
        }

        if (offset.Length < Settings.RotateDeadZone * _layout.Size)
        {
            return DragTargetStatus.SourceDeadZone;
        }

        // Direction on the map plane (not the screen angle, which differs under the tilted camera).
        facing = HexDirections.FromPlaneVector(offset.X, offset.Z);
        return facing is null ? DragTargetStatus.SourceDeadZone : DragTargetStatus.RotateOnly;
    }

    /// <summary>Facing preview: direction of the first step towards the target (straight hex line until v0.4.0).</summary>
    private static HexDirection? PreviewFirstStep(HexCoord source, HexCoord target) => HexLine.FirstStepDirection(source, target);

    private void FinishDrag(PointerSample sample)
    {
        UpdateDrag(sample);
        var drag = _drag!.Value;
        var preview = Drag!;
        switch (preview.Status)
        {
            case DragTargetStatus.RotateOnly:
                {
                    var result = _sim.RotateInPlace(drag.Unit, preview.FacingPreview!.Value);
                    if (result.Success)
                    {
                        Feedback?.Invoke(new RotatedInPlace(drag.Unit, preview.FacingPreview.Value));
                    }
                    else
                    {
                        Feedback?.Invoke(new DragCancelled(drag.Unit, ByUser: false));
                    }

                    break;
                }

            case DragTargetStatus.Valid:
                {
                    var result = _sim.IssueMoveOrder(drag.Unit, preview.TargetHex!.Value, drag.Mode);
                    Feedback?.Invoke(new OrderCommitted(drag.Unit, drag.Mode, result));
                    if (result.Success)
                    {
                        SelectedWaypointId = null;
                    }

                    break;
                }

            case DragTargetStatus.SourceDeadZone:
            case DragTargetStatus.SourceHasOrders:
                Feedback?.Invoke(new DragCancelled(drag.Unit, ByUser: false));
                break;

            default:
                Feedback?.Invoke(new OrderNotCreated(drag.Unit, preview.Status, preview.TargetHex));
                break;
        }

        EndDrag();
    }

    private void CancelDrag(bool byUser)
    {
        if (_drag is not { } drag)
        {
            return;
        }

        EndDrag();
        Feedback?.Invoke(new DragCancelled(drag.Unit, byUser));
    }

    private void EndDrag()
    {
        _drag = null;
        Drag = null;
        Mode = SelectedUnit is not null ? InputMode.UnitSelected : InputMode.Idle;
    }

    private static DragTargetStatus Map(WaypointError error) => error switch
    {
        WaypointError.None => DragTargetStatus.Valid,
        WaypointError.OutOfBounds => DragTargetStatus.OutOfBounds,
        WaypointError.NotWalkable => DragTargetStatus.NotWalkable,
        WaypointError.Occupied => DragTargetStatus.Occupied,
        WaypointError.Unreachable => DragTargetStatus.Unreachable,
        WaypointError.SameAsSource => DragTargetStatus.SourceDeadZone,
        _ => DragTargetStatus.NoTarget,
    };

    private readonly record struct PressInfo(PointerButton Button, PointerSample Sample, PressTarget Target);

    private readonly record struct DragState(UnitId Unit, OrderMode Mode, HexCoord Source);

    private abstract record PressTarget
    {
        public sealed record UnitTarget(UnitId Unit) : PressTarget;

        public sealed record WaypointTarget(UnitId Unit, int WaypointId) : PressTarget;

        public sealed record Ground : PressTarget;
    }
}
