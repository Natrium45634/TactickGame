using System;
using System.Globalization;
using Godot;
using HexTactics.Client.Debug;
using HexTactics.Client.InputLayer;
using HexTactics.Client.Presentation;
using HexTactics.Client.UI;
using HexTactics.Core.Hex;
using HexTactics.Core.Map;

namespace HexTactics.Client.App;

/// <summary>
/// Composition root of the client: loads map data into the pure model, creates the visual
/// layers (board → fills → … → UI), the camera and the input glue. Contains no game rules.
/// </summary>
public partial class Main : Node3D
{
    private const string MapPath = "res://maps/prototype.hexmap";

    private MapDefinition _map = null!;
    private HexLayout _layout = null!;
    private CameraRig _cameraRig = null!;
    private BoardPicker _picker = null!;
    private HexFillLayer _hoverLayer = null!;
    private DebugHud _hud = null!;
    private AutomationRunner _automation = null!;

    private Label _cellLabel = null!;
    private Label _statusLabel = null!;

    private Vector2 _mousePosition = new(-1, -1);
    private bool _mouseInside;
    private HexCoord? _hovered;

    public override void _Ready()
    {
        GD.Print(BuildInfo.Full);
        if (!BuildInfo.VersionsMatch)
        {
            GD.PushWarning($"project.godot version {BuildInfo.ProjectVersion} differs from assembly version {BuildInfo.Version}");
        }

        _map = MapParser.Parse(FileAccess.GetFileAsString(MapPath));
        _layout = new HexLayout(VisualConfig.HexSize);
        _picker = new BoardPicker(_map.Grid, _layout);

        BuildEnvironment();

        var board = new BoardView { Name = "Board" };
        AddChild(board);
        board.Build(_map.Grid, _layout);

        _hoverLayer = new HexFillLayer { Name = "HoverLayer", RenderPriority = 3, ExtraLift = 0.002f };
        AddChild(_hoverLayer);
        _hoverLayer.Build(_map.Grid, _layout);

        _cameraRig = new CameraRig { Name = "CameraRig" };
        AddChild(_cameraRig);
        var (min, max) = _map.Grid.PlaneBounds(_layout);
        _cameraRig.SetFocus(new Aabb(
            new Vector3((float)min.X, -VisualConfig.TileThickness, (float)min.Z),
            new Vector3((float)(max.X - min.X), VisualConfig.TileThickness + 1.0f, (float)(max.Z - min.Z))));

        BuildHud();

        _automation = new AutomationRunner { Name = "Automation" };
        _automation.StepRequested = RunAutomationStep;
        AddChild(_automation);

        GetViewport().SizeChanged += FitCamera;
        CallDeferred(MethodName.FitCamera);
    }

    public override void _Notification(int what)
    {
        if (what == NotificationWMMouseExit)
        {
            _mouseInside = false;
        }
        else if (what == NotificationWMMouseEnter)
        {
            _mouseInside = true;
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventMouse mouse)
        {
            _mousePosition = mouse.Position;
            _mouseInside = true;
        }
    }

    public override void _Process(double delta)
    {
        UpdateHover();
        var size = GetViewport().GetVisibleRect().Size;
        _statusLabel.Text = $"Окно {size.X:0}×{size.Y:0} · {Engine.GetFramesPerSecond():0} FPS";
    }

    private void UpdateHover()
    {
        HexCoord? hex = null;
        PlaneVector plane = default;
        if (_mouseInside && !_hud.IsOverUi(_mousePosition))
        {
            var pick = _picker.Pick(_cameraRig.Camera, _mousePosition);
            hex = pick.Hex;
            plane = pick.PlanePoint;
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
        _ = plane;
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
        return $"Клетка: col {o.Col}, row {o.Row}\naxial q {h.Q}, r {h.R} · {type}";
    }

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

        var sun = new DirectionalLight3D
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
        };
        AddChild(sun);
    }

    private void BuildHud()
    {
        _hud = new DebugHud { Name = "Hud" };
        AddChild(_hud);
        _hud.AddTitle(BuildInfo.Title);
        _hud.AddLabel($"{BuildInfo.Commit} · Godot {BuildInfo.EngineVersion}\n{BuildInfo.DotNetVersion}", dim: true, fontSize: 12);
        _hud.AddSeparator();
        _hud.AddHeader("Карта");
        _hud.AddLabel($"{_map.Name}: {_map.Grid.Columns} × {_map.Grid.Rows} = {_map.Grid.CellCount} гексов", fontSize: 13);
        _cellLabel = _hud.AddLabel("Клетка: —", fontSize: 13);
        _hud.AddSeparator();
        _hud.AddHeader("Легенда");
        _hud.AddLegendItem(VisualConfig.TileWalkable, "Проходимая клетка");
        _hud.AddLegendItem(VisualConfig.TileBlocked, "Препятствие");
        _hud.AddLegendItem(VisualConfig.TilePlayerBase, _map.PlayerBase?.Name ?? "База игрока");
        _hud.AddLegendItem(VisualConfig.TileSpawnZone, "Зона появления (край)");
        _hud.AddSeparator();
        _hud.AddHeader("Камера");
        _hud.AddLabel($"Ортографическая, возвышение {VisualConfig.CameraElevationDegrees:0}° (90° — строго сверху)", dim: true, fontSize: 12);
        _statusLabel = _hud.AddLabel(string.Empty, dim: true, fontSize: 12);
        _hud.LayoutChanged += FitCamera;
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

    /// <summary>Executes one command-line automation step; returns frames to wait.</summary>
    private int RunAutomationStep(string step)
    {
        var parts = step.Split(':', 2);
        var arg = parts.Length > 1 ? parts[1] : string.Empty;
        switch (parts[0])
        {
            case "hover":
                {
                    var hex = ParseHex(arg);
                    var screen = _cameraRig.Camera.UnprojectPosition(_layout.HexToPlane(hex).ToWorld(VisualConfig.TileTopHeight(_map.Grid[hex].Type)));
                    InjectMouseMotion(screen);
                    return 2;
                }

            case "mouse":
                {
                    var xy = arg.Split(',');
                    InjectMouseMotion(new Vector2(float.Parse(xy[0], CultureInfo.InvariantCulture), float.Parse(xy[1], CultureInfo.InvariantCulture)));
                    return 2;
                }

            case "wait":
                return int.Parse(arg, CultureInfo.InvariantCulture);

            default:
                GD.PushWarning($"[automation] unknown step '{step}'");
                return 1;
        }
    }

    private static HexCoord ParseHex(string text)
    {
        var cr = text.Split(',');
        return HexCoord.FromOffset(int.Parse(cr[0], CultureInfo.InvariantCulture), int.Parse(cr[1], CultureInfo.InvariantCulture));
    }

    private void InjectMouseMotion(Vector2 position)
    {
        Godot.Input.ParseInputEvent(new InputEventMouseMotion { Position = position, GlobalPosition = position });
    }
}
