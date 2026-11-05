using System;
using System.Globalization;
using Godot;
using HexTactics.Client.Debug;
using HexTactics.Client.InputLayer;
using HexTactics.Client.Presentation;
using HexTactics.Client.UI;
using HexTactics.Core.Hex;
using HexTactics.Core.Map;
using HexTactics.Core.Spawning;
using HexTactics.Interaction;
using HexTactics.Simulation;

namespace HexTactics.Client.App;

/// <summary>
/// Composition root of the client: loads map data into the pure model, creates the simulation,
/// the input state machine, the visual layers (board → fills → units → UI), the camera and the
/// input glue, and keeps the views in sync. Contains no game rules.
/// </summary>
public partial class Main : Node3D
{
    private const string MapPath = "res://maps/prototype.hexmap";

    private MapDefinition _map = null!;
    private HexLayout _layout = null!;
    private GameSimulation _sim = null!;
    private InputStateMachine _fsm = null!;

    private CameraRig _cameraRig = null!;
    private BoardPicker _picker = null!;
    private HexFillLayer _fillLayer = null!;
    private HexFillLayer _hoverLayer = null!;
    private UnitsView _units = null!;
    private PointerInput _input = null!;
    private DebugHud _hud = null!;
    private AutomationRunner _automation = null!;

    private Label _cellLabel = null!;
    private Label _statusLabel = null!;
    private Label _selectionLabel = null!;
    private Label _messageLabel = null!;
    private Button _placementButton = null!;

    private HexCoord? _hovered;
    private long _fillStamp = -1;

    public override void _Ready()
    {
        GD.Print(BuildInfo.Full);
        if (!BuildInfo.VersionsMatch)
        {
            GD.PushWarning($"project.godot version {BuildInfo.ProjectVersion} differs from assembly version {BuildInfo.Version}");
        }

        _map = MapParser.Parse(FileAccess.GetFileAsString(MapPath));
        _layout = new HexLayout(VisualConfig.HexSize);
        _sim = new GameSimulation(_map);
        _fsm = new InputStateMachine(_sim);
        _fsm.Feedback += OnFeedback;
        _picker = new BoardPicker(_map.Grid, _layout);

        BuildEnvironment();

        var board = new BoardView { Name = "Board" };
        AddChild(board);
        board.Build(_map.Grid, _layout);

        _fillLayer = new HexFillLayer { Name = "PassabilityLayer", RenderPriority = 1 };
        AddChild(_fillLayer);
        _fillLayer.Build(_map.Grid, _layout);

        _hoverLayer = new HexFillLayer { Name = "HoverLayer", RenderPriority = 3, ExtraLift = 0.002f };
        AddChild(_hoverLayer);
        _hoverLayer.Build(_map.Grid, _layout);

        _units = new UnitsView { Name = "Units" };
        _units.Initialize(_layout);
        AddChild(_units);

        _cameraRig = new CameraRig { Name = "CameraRig" };
        AddChild(_cameraRig);
        var (min, max) = _map.Grid.PlaneBounds(_layout);
        _cameraRig.SetFocus(new Aabb(
            new Vector3((float)min.X, -VisualConfig.TileThickness, (float)min.Z),
            new Vector3((float)(max.X - min.X), VisualConfig.TileThickness + 1.0f, (float)(max.Z - min.Z))));

        BuildHud();

        _input = new PointerInput { Name = "PointerInput" };
        AddChild(_input);
        _input.Initialize(_fsm, () => _cameraRig.Camera, _picker, _units, _hud.IsOverUi);
        _input.DebugKey += OnDebugKey;

        _automation = new AutomationRunner { Name = "Automation" };
        _automation.StepRequested = RunAutomationStep;
        AddChild(_automation);

        GetViewport().SizeChanged += FitCamera;
        CallDeferred(MethodName.FitCamera);
    }

    public override void _Process(double delta)
    {
        _units.Sync(_sim.State, _fsm.SelectedUnit, (float)delta);
        UpdateHover();
        UpdateFills();
        UpdateHud();
    }

    // ------------------------------------------------------------------ view updates

    private void UpdateHover()
    {
        HexCoord? hex = null;
        if (_input.MouseInside && !_hud.IsOverUi(_input.MousePosition))
        {
            hex = _picker.Pick(_cameraRig.Camera, _input.MousePosition).Hex;
        }

        if (hex != _hovered)
        {
            if (_hovered is { } old)
            {
                _hoverLayer.SetFill(old, Colors.Transparent);
            }

            if (hex is { } now)
            {
                _hoverLayer.SetFill(now, VisualConfig.HoverFill);
            }

            _hovered = hex;
        }

        _cellLabel.Text = hex is { } h ? DescribeCell(h) : "Клетка: — (курсор вне поля)";
    }

    /// <summary>Passability layer: in placement mode every cell shows whether a unit can be placed there.</summary>
    private void UpdateFills()
    {
        var stamp = _fsm.Mode == InputMode.Placement ? _sim.State.TopologyVersion : -1;
        if (stamp == _fillStamp)
        {
            return;
        }

        _fillStamp = stamp;
        if (_fsm.Mode != InputMode.Placement)
        {
            _fillLayer.ClearAll();
            return;
        }

        foreach (var cell in _map.Grid.Coords)
        {
            var ok = SpawnRules.ValidateCell(_sim.State, cell) == SpawnError.None;
            _fillLayer.SetFill(cell, ok ? VisualConfig.FillValid : VisualConfig.FillInvalid);
        }
    }

    private void UpdateHud()
    {
        var size = GetViewport().GetVisibleRect().Size;
        _statusLabel.Text = $"Окно {size.X:0}×{size.Y:0} · {Engine.GetFramesPerSecond():0} FPS · юнитов: {_sim.State.UnitCount}";
        var selected = _fsm.SelectedUnit is { } id ? _sim.State.FindUnit(id) : null;
        _selectionLabel.Text = selected is null ? "Юнит не выбран (щелчок ЛКМ по юниту)" : UiText.UnitInfo(selected);
        _placementButton.SetPressedNoSignal(_fsm.Mode == InputMode.Placement);
    }

    private string DescribeCell(HexCoord h)
    {
        var tile = _map.Grid[h];
        var o = h.ToOffset();
        var type = tile.Type switch
        {
            TileType.Blocked => "непроходимая",
            TileType.PlayerBase => _map.PlayerBase?.Name ?? "база",
            TileType.SpawnZone => _map.FindZone(tile.ZoneId)?.Name ?? $"зона {tile.ZoneId}",
            _ => "проходимая",
        };
        var occupant = _sim.State.UnitAt(h) is { } u ? $" · юнит {u.Id}" : string.Empty;
        return $"Клетка: col {o.Col}, row {o.Row} (axial {h.Q}, {h.R})\n{type}{occupant}";
    }

    // ------------------------------------------------------------------ actions

    private void SpawnFromPanel(SpawnRequest request)
    {
        var result = _sim.Spawn(request);
        ShowMessage(UiText.Spawn(request, result, _map), !result.Success);
        if (result.Success)
        {
            _fsm.Select(result.Unit);
        }
    }

    private void RotateSelected(int steps)
    {
        if (_fsm.SelectedUnit is { } id && _sim.State.FindUnit(id) is { } unit)
        {
            _sim.SetFacing(id, unit.Facing.RotateCounterClockwise(steps));
        }
    }

    private void OnFeedback(InteractionFeedback feedback)
    {
        switch (feedback)
        {
            case SpawnAttempted s:
                ShowMessage(UiText.Spawn(s.Request, s.Result, _map), !s.Result.Success);
                break;
            case PlacementModeChanged { Active: true }:
                ShowMessage("Режим выставления: щёлкните по зелёной клетке. Esc / ПКМ — выход.", false);
                break;
        }
    }

    private void OnDebugKey(Key key)
    {
        switch (key)
        {
            case Key.Q:
                RotateSelected(+1);
                break;
            case Key.E:
                RotateSelected(-1);
                break;
            case Key.P:
                _fsm.TogglePlacementMode();
                break;
        }
    }

    private void ShowMessage(string text, bool isError)
    {
        _messageLabel.Text = text;
        _messageLabel.AddThemeColorOverride("font_color", isError ? new Color(1f, 0.62f, 0.55f) : new Color(0.70f, 0.92f, 0.72f));
        GD.Print($"[message] {text}");
    }

    // ------------------------------------------------------------------ setup

    private void BuildEnvironment()
    {
        var sunEnergy = 0.35f;
        var ambientEnergy = 0.25f;
        foreach (var arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith("--hx-sun=", StringComparison.Ordinal))
            {
                sunEnergy = float.Parse(arg[9..], CultureInfo.InvariantCulture);
            }
            else if (arg.StartsWith("--hx-ambient=", StringComparison.Ordinal))
            {
                ambientEnergy = float.Parse(arg[13..], CultureInfo.InvariantCulture);
            }
        }

        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = VisualConfig.Background,
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.88f, 0.89f, 0.91f),
            AmbientLightEnergy = ambientEnergy,
            TonemapMode = Godot.Environment.ToneMapper.Linear,
        };
        AddChild(new WorldEnvironment { Name = "Environment", Environment = env });

        AddChild(new DirectionalLight3D
        {
            Name = "Sun",
            RotationDegrees = new Vector3(-52f, -32f, 0f),
            LightEnergy = sunEnergy,
            ShadowEnabled = true,
            ShadowBlur = 1.0f,
            ShadowBias = 0.06f,
            ShadowNormalBias = 1.5f,
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Orthogonal,
            DirectionalShadowMaxDistance = 90f,
        });
    }

    private void BuildHud()
    {
        _hud = new DebugHud { Name = "Hud" };
        AddChild(_hud);
        _hud.AddTitle(BuildInfo.Title);
        _hud.AddLabel($"{BuildInfo.Commit} · Godot {BuildInfo.EngineVersion}\n{BuildInfo.DotNetVersion}", dim: true, fontSize: 12);
        _hud.AddSeparator();

        _hud.AddHeader("Выбранный юнит");
        _selectionLabel = _hud.AddLabel(string.Empty, fontSize: 13);
        _hud.AddLabel("Q / E — повернуть выбранного юнита (отладка)", dim: true, fontSize: 12);
        _hud.AddSeparator();

        _hud.AddHeader("Появление юнита");
        var row = _hud.AddRow();
        AddRowButton(row, "База", () => SpawnFromPanel(SpawnRequest.AtBase()));
        foreach (var zone in _map.SpawnZones)
        {
            var zoneId = zone.Id;
            AddRowButton(row, ShortZoneName(zone.Name), () => SpawnFromPanel(SpawnRequest.AtZone(zoneId)));
        }

        _placementButton = _hud.AddButton("Выставить на клетку (P)", () => _fsm.TogglePlacementMode());
        _placementButton.ToggleMode = true;
        _messageLabel = _hud.AddLabel("—", fontSize: 13);
        _hud.AddSeparator();

        _hud.AddHeader("Карта");
        _cellLabel = _hud.AddLabel("Клетка: —", fontSize: 13);
        _hud.AddLegendItem(VisualConfig.TileWalkable, "Проходимая");
        _hud.AddLegendItem(VisualConfig.TileBlocked, "Препятствие");
        _hud.AddLegendItem(VisualConfig.TilePlayerBase, _map.PlayerBase?.Name ?? "База игрока");
        _hud.AddLegendItem(VisualConfig.TileSpawnZone, "Зона появления (край)");
        _hud.AddSeparator();
        _statusLabel = _hud.AddLabel(string.Empty, dim: true, fontSize: 12);
        _hud.LayoutChanged += FitCamera;
    }

    private static void AddRowButton(HBoxContainer row, string text, Action action)
    {
        var button = new Button { Text = text, FocusMode = Control.FocusModeEnum.None, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        button.Pressed += action;
        row.AddChild(button);
    }

    private static string ShortZoneName(string name)
    {
        var parts = name.Split(' ');
        return parts.Length > 0 ? parts[0] : name;
    }

    private void FitCamera()
    {
        if (_cameraRig?.Camera is null || _hud is null)
        {
            return;
        }

        var viewport = GetViewport().GetVisibleRect().Size;
        var panel = _hud.ScreenRect;
        var left = panel.End.X + DebugHud.Margin;
        var margin = DebugHud.Margin;
        var available = new Rect2(left, margin, viewport.X - left - margin, viewport.Y - (2 * margin));
        _cameraRig.Fit(available, viewport);
    }

    // ------------------------------------------------------------------ automation

    /// <summary>Executes one command-line automation step; returns frames to wait.</summary>
    private int RunAutomationStep(string step)
    {
        var parts = step.Split(':', 2);
        var arg = parts.Length > 1 ? parts[1] : string.Empty;
        switch (parts[0])
        {
            case "hover":
                InjectMotion(HexScreen(ParseHex(arg)));
                return 2;
            case "mouse":
                InjectMotion(ParseVector(arg));
                return 2;
            case "click":
                {
                    var p = HexScreen(ParseHex(arg));
                    InjectMotion(p);
                    InjectButton(p, MouseButton.Left, true);
                    InjectButton(p, MouseButton.Left, false);
                    return 2;
                }

            case "rclick":
                {
                    var p = HexScreen(ParseHex(arg));
                    InjectButton(p, MouseButton.Right, true);
                    InjectButton(p, MouseButton.Right, false);
                    return 2;
                }

            case "spawn":
                {
                    var a = arg.Split(':', 2);
                    var request = a[0] switch
                    {
                        "base" => SpawnRequest.AtBase(),
                        "zone" => SpawnRequest.AtZone(int.Parse(a[1], CultureInfo.InvariantCulture)),
                        _ => SpawnRequest.AtCell(ParseHex(a[1])),
                    };
                    SpawnFromPanel(request);
                    return 2;
                }

            case "place":
                _fsm.TogglePlacementMode();
                return 2;
            case "key":
                Godot.Input.ParseInputEvent(new InputEventKey { Keycode = Enum.Parse<Key>(arg), Pressed = true });
                Godot.Input.ParseInputEvent(new InputEventKey { Keycode = Enum.Parse<Key>(arg), Pressed = false });
                return 2;
            case "face":
                if (_fsm.SelectedUnit is { } id)
                {
                    _sim.SetFacing(id, Enum.Parse<HexDirection>(arg));
                }

                return 2;
            case "wait":
                return int.Parse(arg, CultureInfo.InvariantCulture);
            default:
                GD.PushWarning($"[automation] unknown step '{step}'");
                return 1;
        }
    }

    private Vector2 HexScreen(HexCoord hex)
    {
        var height = _map.Grid.Contains(hex) ? VisualConfig.TileTopHeight(_map.Grid[hex].Type) : 0f;
        return _cameraRig.Camera.UnprojectPosition(_layout.HexToPlane(hex).ToWorld(height));
    }

    private static HexCoord ParseHex(string text)
    {
        var cr = text.Split(',');
        return HexCoord.FromOffset(int.Parse(cr[0], CultureInfo.InvariantCulture), int.Parse(cr[1], CultureInfo.InvariantCulture));
    }

    private static Vector2 ParseVector(string text)
    {
        var xy = text.Split(',');
        return new Vector2(float.Parse(xy[0], CultureInfo.InvariantCulture), float.Parse(xy[1], CultureInfo.InvariantCulture));
    }

    private static void InjectMotion(Vector2 position) =>
        Godot.Input.ParseInputEvent(new InputEventMouseMotion { Position = position, GlobalPosition = position });

    private static void InjectButton(Vector2 position, MouseButton button, bool pressed) =>
        Godot.Input.ParseInputEvent(new InputEventMouseButton { Position = position, GlobalPosition = position, ButtonIndex = button, Pressed = pressed });
}
