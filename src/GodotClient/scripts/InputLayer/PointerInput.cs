using System;
using Godot;
using HexTactics.Client.Presentation;
using HexTactics.Core.Hex;
using HexTactics.Interaction;

namespace HexTactics.Client.InputLayer;

/// <summary>
/// Glue between Godot input events and the pure <see cref="InputStateMachine"/>: converts the
/// mouse position into a <see cref="PointerSample"/> (plane point, hex, unit under the cursor) and
/// forwards button/key events. Contains no game rules.
/// </summary>
public partial class PointerInput : Node
{
    private InputStateMachine _fsm = null!;
    private Func<Camera3D> _camera = null!;
    private BoardPicker _picker = null!;
    private UnitsView _units = null!;
    private OrdersView _orders = null!;
    private Func<Vector2, bool> _isOverUi = null!;
    private bool _primaryDown;
    private bool _secondaryDown;

    /// <summary>Raised for keys that are not part of the state machine (debug shortcuts).</summary>
    public event Action<Key>? DebugKey;

    /// <summary>Space: tactical pause (handled by the simulation, not by the order state machine).</summary>
    public event Action? PauseToggled;

    public Vector2 MousePosition { get; private set; } = new(-1, -1);

    public bool MouseInside { get; private set; }

    public void Initialize(InputStateMachine fsm, Func<Camera3D> camera, BoardPicker picker, UnitsView units, OrdersView orders, Func<Vector2, bool> isOverUi)
    {
        _orders = orders;
        _fsm = fsm;
        _camera = camera;
        _picker = picker;
        _units = units;
        _isOverUi = isOverUi;
        ProcessMode = ProcessModeEnum.Always;
    }

    public override void _Notification(int what)
    {
        if (what == NotificationWMMouseExit)
        {
            MouseInside = false;
        }
    }

    /// <summary>Builds a sample for an arbitrary screen position (also used by automation).</summary>
    public PointerSample Sample(Vector2 screen)
    {
        if (_fsm.Mode == InputMode.RosterDrag && _isOverUi(screen))
        {
            // A roster card held over the panels is not over the map: dropping it there returns the card.
            return new PointerSample(new ScreenPoint(screen.X, screen.Y), null, null);
        }

        var camera = _camera();
        var pick = _picker.Pick(camera, screen);
        HexCoord? hex = null;
        PlaneVector? plane = null;
        if (pick.HasPlanePoint)
        {
            plane = pick.PlanePoint;
            hex = pick.Hex ?? _picker.HexAt(pick.PlanePoint); // may be outside of the map
        }

        var unit = _units.PickUnit(camera.ProjectRayOrigin(screen), camera.ProjectRayNormal(screen));
        int? waypoint = null;
        PlaneVector? groundPoint = null;
        if (BoardPicker.GroundPoint(camera, screen, out var ground))
        {
            waypoint = _orders.PickWaypoint(ground, _fsm.SelectedUnit);
            groundPoint = ground;
        }

        return new PointerSample(new ScreenPoint(screen.X, screen.Y), plane, hex, unit, waypoint, groundPoint);
    }

    public override void _Input(InputEvent @event)
    {
        if (HexTactics.Client.UI.ModalDialog.Open is not null)
        {
            return; // a modal dialog owns the mouse and the keyboard (spec part 4 §3.2: no clicks through it)
        }

        switch (@event)
        {
            case InputEventMouseMotion motion:
                MousePosition = motion.Position;
                MouseInside = true;
                _fsm.PointerMove(Sample(motion.Position));
                break;

            case InputEventMouseButton button:
                MousePosition = button.Position;
                MouseInside = true;
                HandleButton(button);
                break;

            case InputEventKey { Pressed: true, Echo: false } key:
                if (key.IsActionPressed(InputBindings.CancelOrder))
                {
                    // Esc cancels only the unfinished arrow (or leaves a debug mode).
                    _fsm.Cancel();
                    GetViewport().SetInputAsHandled();
                }
                else if (key.IsActionPressed(InputBindings.TogglePause))
                {
                    PauseToggled?.Invoke();
                    GetViewport().SetInputAsHandled();
                }
                else
                {
                    DebugKey?.Invoke(key.Keycode);
                }

                break;
        }
    }

    private void HandleButton(InputEventMouseButton button)
    {
        var which = button.ButtonIndex switch
        {
            MouseButton.Left => PointerButton.Primary,
            MouseButton.Right => PointerButton.Secondary,
            _ => (PointerButton?)null,
        };
        if (which is not { } b)
        {
            return;
        }

        if (button.Pressed)
        {
            // Presses over the HUD / roster belong to the UI; releases are always forwarded so that
            // a drag that ends over a panel is not left dangling. RMB cancels a held roster card anywhere.
            if (_isOverUi(button.Position) && !(b == PointerButton.Secondary && _fsm.Mode == InputMode.RosterDrag))
            {
                return;
            }

            SetDown(b, true);
            _fsm.PointerDown(b, Sample(button.Position));
            GetViewport().SetInputAsHandled();
        }
        else if (b == PointerButton.Primary && _fsm.Mode == InputMode.RosterDrag)
        {
            // The press started on a roster card (UI); the release over the map drops the card. The event is not
            // consumed so that the GUI also sees the release and drops its mouse focus on the card.
            SetDown(b, false);
            _fsm.PointerUp(b, Sample(button.Position));
        }
        else if (IsDown(b))
        {
            SetDown(b, false);
            _fsm.PointerUp(b, Sample(button.Position));
            GetViewport().SetInputAsHandled();
        }
    }

    private bool IsDown(PointerButton b) => b == PointerButton.Primary ? _primaryDown : _secondaryDown;

    private void SetDown(PointerButton b, bool value)
    {
        if (b == PointerButton.Primary)
        {
            _primaryDown = value;
        }
        else
        {
            _secondaryDown = value;
        }
    }
}
