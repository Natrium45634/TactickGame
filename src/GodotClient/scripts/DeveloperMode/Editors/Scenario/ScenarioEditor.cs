using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using HexTactics.Client.DeveloperMode.SharedInspector;
using HexTactics.Client.InputLayer;
using HexTactics.Client.Presentation;
using HexTactics.Client.UI;
using HexTactics.Content.Kinds;
using HexTactics.Content.Library;
using HexTactics.Content.Registry;
using HexTactics.Content.Runtime;
using HexTactics.Content.Schema;
using HexTactics.Core.Hex;
using HexTactics.Core.Map;
using HexTactics.Editor.Scenarios;

namespace HexTactics.Client.DeveloperMode.Editors.Scenario;

/// <summary>
/// «Сценарий» (spec part 7 §3.1, §3.4, §9.1, §9.3): the scenario's map in the middle with the enemies already on it — LMB on a
/// free hex places an enemy of the chosen unit, LMB on an enemy selects it, RMB removes it, Q / E turn it; the left panel holds
/// the mode cards (only the ones with an executor can be chosen), the map, the enemies, the player's squad and the rules of
/// the battle; the right one — the scenario and its goals in plain words and the checks. «▶ Тест сценария» plays a copy.
/// </summary>
public partial class ScenarioEditor : DefinitionEditorShell
{
    private static readonly string[] FacingNames = { "север", "северо-восток", "юго-восток", "юг", "юго-запад", "северо-запад" };

    private ScenarioEditorModel _model = null!;
    private Node3D? _boardRoot;
    private Node3D _markers = null!;
    private HexFillLayer? _fills;
    private BoardPicker? _picker;
    private MapDefinition? _map;
    private string _builtFor = string.Empty;
    private HexCoord? _hover;
    private Vector2? _pressAt;
    private string _squadPick = string.Empty;

    public override string Kind => ContentTypeRegistry.Scenario;

    protected override string IdPrefix => "scenario.";

    protected override string KindTitle => "Сценарий";

    protected override string? TestButtonText => "▶ Тест сценария";

    public ScenarioEditorModel ScenarioModel => _model;

    public void Configure(ContentLibrary library, ContentItem item, ContentItem? draft = null)
    {
        _model = new ScenarioEditorModel(library, item);
        if (draft is not null)
        {
            _model.LoadDraft(draft);
        }

        Attach(library, _model);
        _squadPick = item.Data is ScenarioData s && s.Squad.Count > 0 ? s.Squad[^1] : _model.Brush;
    }

    // ------------------------------------------------------------------ canvas

    protected override void BuildCanvas()
    {
        _markers = new Node3D { Name = "Enemies" };
        AddChild(_markers);
        RebuildBoard();
    }

    /// <summary>The board of the scenario's map; rebuilt when the map (or a saved revision of it) changes.</summary>
    private void RebuildBoard()
    {
        var mapItem = Library.Find(ContentTypeRegistry.Map, _model.Scenario.Map);
        var key = mapItem is null ? "none" : $"{mapItem.Id}#{mapItem.Revision}";
        if (key == _builtFor)
        {
            return;
        }

        _builtFor = key;
        _boardRoot?.QueueFree();
        _boardRoot = new Node3D { Name = "MapBoard" };
        AddChild(_boardRoot);
        _map = _model.Map();
        _fills = null;
        _picker = null;
        if (_map is null)
        {
            return;
        }

        var visuals = new Dictionary<string, TileVisual>();
        foreach (var t in Library.Items(ContentTypeRegistry.Tile))
        {
            visuals[t.Id] = RuntimeFactory.Visual(t);
        }

        TilePalette.Current = new TilePalette(visuals);
        var board = new BoardView { Name = "Board" };
        _boardRoot.AddChild(board);
        board.Build(_map.Grid, Layout);
        _fills = new HexFillLayer { Name = "Marks", RenderPriority = 2, ExtraLift = 0.002f };
        _boardRoot.AddChild(_fills);
        _fills.Build(_map.Grid, Layout);
        _picker = new BoardPicker(_map.Grid, Layout);
        CallDeferred(MethodName.FitCamera);
    }

    protected override void UpdateCanvas()
    {
        if (_markers is null)
        {
            return;
        }

        RebuildBoard();
        foreach (var child in _markers.GetChildren())
        {
            child.QueueFree();
        }

        _fills?.ClearAll();
        if (_map is null || _fills is null)
        {
            return;
        }

        if (_hover is { } h && _map.Grid.Exists(h))
        {
            _fills.SetFill(h, new Color(1, 1, 1, 0.16f));
        }

        var scenario = _model.Scenario;
        for (var i = 0; i < scenario.Enemies.Count; i++)
        {
            var e = scenario.Enemies[i];
            var ok = _map.Grid.Exists(e.Cell) && _map.Grid.IsWalkable(e.Cell) && Library.Find(ContentTypeRegistry.Unit, e.Unit) is not null;
            if (_model.Selected == i)
            {
                _fills.SetFill(e.Cell, VisualConfig.SelectionRing);
            }
            else if (!ok)
            {
                _fills.SetFill(e.Cell, new Color(0.95f, 0.25f, 0.2f, 0.55f));
            }

            if (_map.Grid.Contains(e.Cell))
            {
                _markers.AddChild(Marker(e.Cell, e.Facing, ok ? VisualConfig.UnitEnemy : new Color(0.5f, 0.5f, 0.5f)));
            }
        }
    }

    private Node3D Marker(HexCoord cell, HexDirection facing, Color color)
    {
        var unit = new Node3D { Position = Layout.HexToPlane(cell).ToWorld(TilePalette.Current.HeightOf(_map!.Grid, cell)) };
        unit.AddChild(new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = UnitView.BodyRadius, BottomRadius = UnitView.BodyRadius, Height = UnitView.BodyHeight, RadialSegments = 24 },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = color, Roughness = 0.75f },
            Position = new Vector3(0, UnitView.BodyHeight * 0.5f, 0),
        });
        var north = Layout.EdgeMidpoint(cell, HexDirection.North) - Layout.HexToPlane(cell);
        var side = new PlaneVector(-north.Z, north.X) * 0.3;
        var arrow = new ImmediateMesh();
        arrow.SurfaceBegin(Mesh.PrimitiveType.Triangles, new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = VisualConfig.UnitFacingPointer, CullMode = BaseMaterial3D.CullModeEnum.Disabled, NoDepthTest = true, RenderPriority = 20 });
        arrow.SurfaceAddVertex((north * 0.98).ToWorld(UnitView.BodyHeight + 0.02f));
        arrow.SurfaceAddVertex(((north * 0.45) + side).ToWorld(UnitView.BodyHeight + 0.02f));
        arrow.SurfaceAddVertex(((north * 0.45) - side).ToWorld(UnitView.BodyHeight + 0.02f));
        arrow.SurfaceEnd();
        var pivot = new Node3D { Rotation = new Vector3(0, -Mathf.DegToRad(60f * (int)facing), 0) };
        pivot.AddChild(new MeshInstance3D { Mesh = arrow });
        unit.AddChild(pivot);
        return unit;
    }

    protected override Aabb CanvasBounds()
    {
        if (_map is null)
        {
            return new Aabb(new Vector3(-4, -0.2f, -4), new Vector3(8, 1, 8));
        }

        var min = new Vector3(float.MaxValue, -VisualConfig.TileThickness, float.MaxValue);
        var max = new Vector3(float.MinValue, 0.6f, float.MinValue);
        foreach (var c in _map.Grid.Coords.Where(_map.Grid.Exists))
        {
            var p = Layout.HexToPlane(c);
            min = new Vector3(Mathf.Min(min.X, (float)(p.X - Layout.Size)), min.Y, Mathf.Min(min.Z, (float)(p.Z - Layout.Size)));
            max = new Vector3(Mathf.Max(max.X, (float)(p.X + Layout.Size)), max.Y, Mathf.Max(max.Z, (float)(p.Z + Layout.Size)));
        }

        return min.X > max.X ? new Aabb(new Vector3(-4, -0.2f, -4), new Vector3(8, 1, 8)) : new Aabb(min, max - min);
    }

    protected override void HandleCanvasInput(InputEvent @event)
    {
        if (ModalDialog.Open is not null || _picker is null)
        {
            return;
        }

        switch (@event)
        {
            case InputEventMouseMotion motion:
                var hover = _picker.Pick(CameraRig.Camera, motion.Position).Hex;
                if (hover != _hover)
                {
                    _hover = hover;
                    Stamp++;
                }

                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } left:
                if (left.Pressed)
                {
                    _pressAt = left.Position;
                }
                else if (_pressAt is { } at && at.DistanceTo(left.Position) <= 6f && _picker.Pick(CameraRig.Camera, left.Position).Hex is { } hex)
                {
                    _pressAt = null;
                    Refuse(_model.Click(hex));
                    GetViewport().SetInputAsHandled();
                }

                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: false } right:
                if (_picker.Pick(CameraRig.Camera, right.Position).Hex is { } cell && _model.RemoveEnemy(cell))
                {
                    Message("Враг убран (Ctrl+Z — вернуть).", null);
                    GetViewport().SetInputAsHandled();
                }

                break;
        }
    }

    protected override bool HandleKey(InputEventKey key)
    {
        switch (key.Keycode)
        {
            case Key.Q:
                _model.Turn(-1);
                return true;
            case Key.E:
                _model.Turn(1);
                return true;
            case Key.Delete:
                return _model.RemoveEnemy();
            case Key.Escape when _model.Selected is not null:
                _model.Deselect();
                return true;
            default:
                return false;
        }
    }

    // ------------------------------------------------------------------ panels

    protected override void BuildProperties(Inspector inspector)
    {
        var s = _model.Scenario;
        inspector.Section("Тип сценария");
        foreach (var pair in ScenarioData.Modes.Chunk(2))
        {
            var row = inspector.Row();
            foreach (var mode in pair)
            {
                var button = UiKit.Button((mode.Id == s.Mode ? "✓ " : string.Empty) + mode.Title + (mode.Available ? string.Empty : $" · {mode.AvailableFrom}"),
                    () => Refuse(_model.SetMode(mode.Id)), mode.Id == s.Mode ? ButtonKind.Primary : ButtonKind.Normal, IdPrefix + "mode." + mode.Id,
                    mode.Available ? mode.Summary : $"{mode.Summary} Появится в {mode.AvailableFrom}.");
                button.Disabled = !mode.Available;
                button.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                row.AddChild(button);
            }
        }

        inspector.Hint(ScenarioData.Modes.FirstOrDefault(m => m.Id == s.Mode)?.Summary ?? string.Empty);

        inspector.Section("Карта");
        inspector.Choice(IdPrefix + "map", "Карта", ReferenceOptions(ContentTypeRegistry.Map, ReferenceFilter.Any, s.Map, allowCreate: false), s.Map, v => Refuse(_model.SetMap(v)));
        inspector.Button(IdPrefix + "map.open", "✎ Открыть карту в редакторе", OpenMap, ButtonKind.Flat,
            "Гексы выставления, препятствия и уровни правятся в редакторе карт; карта общая для всех сценариев с ней.");

        inspector.Section("Враги на карте");
        inspector.Hint("Щёлкните свободный гекс — поставить врага; щёлкните врага — выбрать; ПКМ — убрать; Q / E — повернуть.");
        var selected = _model.SelectedEnemy;
        var units = ReferenceOptions(ContentTypeRegistry.Unit, ReferenceFilter.Any, selected?.Unit ?? _model.Brush, allowCreate: false);
        inspector.Choice(IdPrefix + "brush", selected is null ? "Ставить" : "Юнит врага", units, selected?.Unit ?? _model.Brush, v => Refuse(_model.SetSelectedUnit(v)));
        var turn = inspector.Row();
        turn.AddChild(UiKit.Button("↶ Повернуть", () => _model.Turn(-1), ButtonKind.Normal, IdPrefix + "turn.left", "Q"));
        turn.AddChild(UiKit.Button("Повернуть ↷", () => _model.Turn(1), ButtonKind.Normal, IdPrefix + "turn.right", "E"));
        if (selected is not null)
        {
            inspector.Text($"Выбран враг на {selected.Col},{selected.Row}: «{_model.UnitName(selected.Unit)}», взгляд — {FacingNames[(int)selected.Facing]}.", size: 12);
            var actions = inspector.Row();
            actions.AddChild(UiKit.Button("Убрать врага", () => _model.RemoveEnemy(), ButtonKind.Danger, IdPrefix + "enemy.remove", "Delete"));
            actions.AddChild(UiKit.Button("Снять выбор", _model.Deselect, ButtonKind.Flat, IdPrefix + "enemy.deselect", "Esc"));
        }
        else
        {
            inspector.Text($"Новые враги смотрят на {FacingNames[(int)_model.BrushFacing]}.", dim: true, size: 12);
        }

        inspector.Text($"Врагов: {s.Enemies.Count} — {_model.EnemiesText()}", dim: true, size: 12);
        var misplaced = _map is null ? 0 : s.Enemies.Count(e => !_map.Grid.Exists(e.Cell) || !_map.Grid.IsWalkable(e.Cell));
        if (misplaced > 0)
        {
            inspector.Button(IdPrefix + "enemies.fix", $"Убрать врагов вне проходимых гексов ({misplaced})", () => Message($"Убрано врагов: {_model.RemoveMisplaced()}.", null), ButtonKind.Danger);
        }

        inspector.Section("Отряд игрока");
        inspector.Hint("Карточки нижней панели в бою, по порядку; игрок выставляет их на гексы выставления карты.");
        for (var i = 0; i < s.Squad.Count; i++)
        {
            var index = i;
            var row = inspector.Row();
            var label = UiKit.Label($"{i + 1}. {_model.UnitName(s.Squad[i])}", 13);
            label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            row.AddChild(label);
            var remove = UiKit.Button("✕", () => Refuse(_model.RemoveFromSquad(index)), ButtonKind.Flat, IdPrefix + "squad.remove." + i, "Убрать из отряда");
            remove.Disabled = s.Squad.Count == 1;
            row.AddChild(remove);
        }

        if (s.Squad.Count < ScenarioData.MaxSquad)
        {
            inspector.Choice(IdPrefix + "squad.pick", "Добавить юнит", ReferenceOptions(ContentTypeRegistry.Unit, ReferenceFilter.Any, _squadPick, allowCreate: false), _squadPick, v => { _squadPick = v; Stamp++; });
            inspector.Button(IdPrefix + "squad.add", "＋ В отряд", () => Refuse(_model.AddToSquad(_squadPick)));
        }

        inspector.Section("Правила боя");
        var rulesets = ReferenceOptions(ContentTypeRegistry.CombatRuleset, ReferenceFilter.Any, s.Ruleset.Length == 0 ? null : s.Ruleset, allowCreate: false).ToList();
        rulesets.Insert(0, new ChoiceOption(string.Empty, "как на карте"));
        inspector.Choice(IdPrefix + "ruleset", "Правила урона и критов", rulesets, s.Ruleset, v => Refuse(_model.SetRuleset(v)));
        inspector.Check(IdPrefix + "startpaused", "Подготовка на паузе", s.StartPaused, on => _model.SetFlag("startPaused", on),
            "Бой ждёт на паузе, пока игрок выставляет отряд и задаёт маршруты; «Начать бой» — пробел.");
        inspector.Check(IdPrefix + "autoattacks", "Автоатака юнитов", s.AutoAttacks, on => _model.SetFlag("autoAttacks", on),
            "Юниты сами бьют врагов в области своей базовой атаки.");
        inspector.Check(IdPrefix + "blocking", "Блок", s.Blocking, on => _model.SetFlag("blocking", on),
            "Юниты с «Блоком» удерживают соседних врагов; снято — свободный манёвр.");
        inspector.LineText(IdPrefix + "timelimit", "Ограничение времени, с", Inspector.Num(s.TimeLimit), v => Refuse(_model.SetTimeLimit(v)),
            "0 — без ограничения. Когда время боя (без пауз) вышло, а враги ещё живы, — поражение.");
    }

    private void OpenMap()
    {
        if (Library.Find(ContentTypeRegistry.Map, _model.Scenario.Map) is not { } map)
        {
            Message("✗ Карта сценария не найдена — выберите другую.", HexTactics.Content.Model.IssueSeverity.Error);
            return;
        }

        RequestNested(map.Clone(), saved =>
        {
            if (saved is not null && saved.Id != _model.Scenario.Map)
            {
                _model.SetMap(saved.Id);
                Message($"Сценарий использует сохранённую копию карты «{saved.Name}».", null);
            }
            else if (saved is not null)
            {
                Message($"Карта «{saved.Name}» сохранена.", null);
            }

            _builtFor = string.Empty;
            Stamp++;
        });
    }

    private void Refuse(string? refusal)
    {
        if (refusal is not null)
        {
            Message("✗ " + refusal, HexTactics.Content.Model.IssueSeverity.Error);
            Stamp++;
        }
    }

    protected override void BuildPreview(Inspector preview)
    {
        preview.Section("Сценарий словами");
        foreach (var line in _model.Preview())
        {
            preview.Text(line, size: 12);
        }
    }

    // ------------------------------------------------------------------ automation

    public override int RunAutomationStep(string step, HexTactics.Client.Debug.AutomationRunner runner)
    {
        var parts = step.Split(':', 2);
        if (parts[0] is "cell" or "rcell" && parts.Length > 1)
        {
            // cell:col,row — LMB on a hex of the map; rcell:col,row — RMB (remove the enemy there).
            var cr = parts[1].Split(',');
            var hex = HexCoord.FromOffset(int.Parse(cr[0], CultureInfo.InvariantCulture), int.Parse(cr[1], CultureInfo.InvariantCulture));
            var y = _map is null ? 0f : TilePalette.Current.HeightOf(_map.Grid, hex);
            HexTactics.Client.Debug.AutomationInput.Click(CameraRig.Camera.UnprojectPosition(Layout.HexToPlane(hex).ToWorld(y)), parts[0] == "cell" ? MouseButton.Left : MouseButton.Right);
            return 3;
        }

        return base.RunAutomationStep(step, runner);
    }

    protected override bool Expect(string[] a, out string actual, out bool ok)
    {
        var s = _model.Scenario;
        switch (a[0])
        {
            case "scenario-enemies":
                actual = s.Enemies.Count.ToString(CultureInfo.InvariantCulture);
                ok = actual == a[1];
                return true;
            case "scenario-enemy":
                // expect:scenario-enemy:col,row:<unit id>:<Facing>
                var cr = a[1].Split(',');
                var at = s.Enemies.FirstOrDefault(e => e.Col.ToString(CultureInfo.InvariantCulture) == cr[0] && e.Row.ToString(CultureInfo.InvariantCulture) == cr[1]);
                actual = at is null ? "none" : $"{at.Unit}:{at.Facing}";
                ok = actual == string.Join(":", a.Skip(2));
                return true;
            case "scenario-squad":
                actual = string.Join(",", s.Squad);
                ok = actual == a[1] || s.Squad.Count.ToString(CultureInfo.InvariantCulture) == a[1];
                return true;
            case "scenario-errors":
                actual = _model.Validate().Count(i => i.IsError).ToString(CultureInfo.InvariantCulture);
                ok = actual == a[1];
                return true;
            case "scenario-preview":
                actual = string.Join(" | ", _model.Preview());
                ok = actual.Contains(string.Join(":", a.Skip(1)), System.StringComparison.Ordinal);
                return true;
            case "scenario-selected":
                actual = _model.SelectedEnemy is { } e ? $"{e.Col},{e.Row}" : "none";
                ok = actual == a[1];
                return true;
            default:
                actual = "unknown";
                ok = false;
                return false;
        }
    }
}
