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
    private RouteLayer? _routes;
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
        _routes = new RouteLayer { Name = "Routes" };
        AddChild(_routes);
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
            _routes?.Show(System.Array.Empty<RouteDrawing>(), Layout, _ => 0f);
            return;
        }

        // Tower Defense: the red lines of the map's routes (their geometry is edited in the map editor, spec part 7 §9.2).
        var routes = new List<RouteDrawing>();
        if (_model.Scenario.IsTowerDefense)
        {
            for (var i = 0; i < _map.Routes.Count; i++)
            {
                var r = _map.Routes[i];
                routes.Add(new RouteDrawing(r.Id, (i + 1).ToString(CultureInfo.InvariantCulture), r.Cells, r.Enabled, false));
            }
        }

        _routes?.Show(routes, Layout, c => TilePalette.Current.HeightOf(_map.Grid, c));

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

        // Mission objects of a defense (spec part 7 §5.1): gold pillars.
        for (var i = 0; i < scenario.Objects.Count; i++)
        {
            var o = scenario.Objects[i];
            if (_model.SelectedObject == i)
            {
                _fills.SetFill(o.Cell, VisualConfig.SelectionRing);
            }

            if (_map.Grid.Contains(o.Cell))
            {
                var pillar = new Node3D { Position = Layout.HexToPlane(o.Cell).ToWorld(TilePalette.Current.HeightOf(_map.Grid, o.Cell)) };
                pillar.AddChild(new MeshInstance3D
                {
                    Mesh = new CylinderMesh { TopRadius = UnitView.BodyRadius * 1.15f, BottomRadius = UnitView.BodyRadius * 1.3f, Height = UnitView.BodyHeight * 1.5f, RadialSegments = 6 },
                    MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.86f, 0.70f, 0.25f), Roughness = 0.6f },
                    Position = new Vector3(0, UnitView.BodyHeight * 0.75f, 0),
                });
                _markers.AddChild(pillar);
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

        if (s.IsDefense)
        {
            BuildDefense(inspector);
        }

        if (s.IsTowerDefense || s.IsDefense)
        {
            BuildWaves(inspector);
        }

        inspector.Section(s.IsTowerDefense ? "Враги на карте с начала (необязательно)" : "Враги на карте");
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
            inspector.Choice(IdPrefix + "enemy.ai", "Поведение", AiData.Presets.Select(p => new ChoiceOption(p.Value, p.Label)).ToList(), selected.Ai.Preset, v => Refuse(_model.SetEnemyAi(v)),
                "Простой ИИ (ТЗ ч.7 §5.2): «охранник» бьёт только в радиусе вокруг своего гекса и возвращается.");
            if (selected.Ai.Preset == "guard")
            {
                inspector.LineText(IdPrefix + "enemy.guard", "Радиус охраны, гексов", selected.Ai.GuardRadius.ToString(CultureInfo.InvariantCulture), v => Refuse(_model.SetAiNumber(null, null, "guardRadius", v)));
            }
            else if (!selected.Ai.IsNone)
            {
                inspector.LineText(IdPrefix + "enemy.detect", "Замечает за, гексов (0 — всю карту)", selected.Ai.DetectRange.ToString(CultureInfo.InvariantCulture), v => Refuse(_model.SetAiNumber(null, null, "detectRange", v)));
            }

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
        if (s.IsTowerDefense)
        {
            inspector.Choice(IdPrefix + "defeat", "Поражение, когда", ScenarioData.DefeatPolicies.Select(p => new ChoiceOption(p.Value, p.Label)).ToList(), s.Defeat, v => _model.SetDefeat(v),
                "Как потеря жизней баз заканчивает бой. Утечка одного врага — минус 1 жизнь его базы.");
        }
    }

    /// <summary>
    /// Waves and bases of a Tower Defense scenario (spec part 7 §4.5–4.6): the list of waves, the card of the selected one with its
    /// groups (route of the map, unit, count, times), the lives of each player base. Sums are computed, never typed.
    /// </summary>
    private void BuildWaves(Inspector inspector)
    {
        var s = _model.Scenario;
        if (s.IsTowerDefense)
        {
            inspector.Section("Базы игрока");
            var bases = _model.PlayerBases();
            if (bases.Count == 0)
            {
                inspector.Text("✗ на карте нет базы игрока — врагам некуда идти", size: 12).AddThemeColorOverride("font_color", UiKit.Error);
            }

            foreach (var b in bases)
            {
                var number = b.Number;
                inspector.LineText(IdPrefix + $"base.{number}.lives", $"«{b.Name}»: жизни", b.Lives.ToString(CultureInfo.InvariantCulture), v => Refuse(_model.SetBaseLives(number, v)));
                inspector.Check(IdPrefix + $"base.{number}.critical", "критическая — её падение = поражение", b.Critical, on => _model.SetBaseCritical(number, on));
            }
        }

        inspector.Section($"Волны — врагов всего {s.WaveEnemies}");
        inspector.Hint(s.IsDefense
            ? "Кто и когда приходит: из базы врага или зоны появления карты с простым ИИ (или по маршруту)."
            : "Кто и когда идёт по маршрутам карты. Маршруты рисуются в редакторе карт («〰 Маршруты»).");
        if (s.IsTowerDefense && _model.MapRoutes().Count == 0)
        {
            inspector.Text("✗ на карте нет маршрутов врагов — откройте карту и нарисуйте их", size: 12).AddThemeColorOverride("font_color", UiKit.Error);
        }

        for (var i = 0; i < s.Waves.Count; i++)
        {
            var w = s.Waves[i];
            var id = w.Id;
            var pick = UiKit.Button($"{i + 1}. {w.Name} — {w.Total} вр." + (w.Enabled ? string.Empty : " (выкл.)"), () => { _model.SelectedWave = _model.SelectedWave == id ? null : id; Stamp++; },
                ButtonKind.Flat, IdPrefix + "wave.pick." + (i + 1), w.StartText(i == 0));
            pick.ToggleMode = true;
            pick.SetPressedNoSignal(_model.SelectedWave == id);
            pick.Alignment = HorizontalAlignment.Left;
            inspector.Add(pick);
        }

        inspector.Button(IdPrefix + "wave.add", "＋ Волна", () => { _model.AddWave(); Stamp++; });
        if (s.Waves.FirstOrDefault(w => w.Id == _model.SelectedWave) is { } wave)
        {
            BuildWaveCard(inspector, wave, s.Waves.IndexOf(wave));
        }
    }

    private void BuildWaveCard(Inspector inspector, WaveData wave, int index)
    {
        var id = wave.Id;
        inspector.Section($"Волна «{wave.Name}» — {wave.Total} вр.");
        inspector.LineText(IdPrefix + "wave.name", "Название", wave.Name, v => Refuse(_model.SetWaveName(id, v)));
        inspector.Choice(IdPrefix + "wave.start", "Старт", WaveData.Starts.Select(o => new ChoiceOption(o.Value, o.Label)).ToList(), wave.Start, v => Refuse(_model.SetWaveStart(id, v)));
        inspector.LineText(IdPrefix + "wave.delay", wave.Start == WaveData.AtTime ? "Секунда боя" : "Задержка, с", Inspector.Num(wave.Delay), v => Refuse(_model.SetWaveDelay(id, v)),
            wave.StartText(index == 0));
        inspector.Check(IdPrefix + "wave.enabled", "Включена", wave.Enabled, on => _model.SetWaveEnabled(id, on), "Выключенная волна остаётся в сценарии, но не идёт и не считается.");
        var row = inspector.Row();
        row.AddChild(UiKit.Button("Копия", () => { _model.DuplicateWave(id); Stamp++; }, ButtonKind.Normal, IdPrefix + "wave.copy"));
        row.AddChild(UiKit.Button("↑", () => _model.MoveWave(id, -1), ButtonKind.Normal, IdPrefix + "wave.up", "Раньше"));
        row.AddChild(UiKit.Button("↓", () => _model.MoveWave(id, 1), ButtonKind.Normal, IdPrefix + "wave.down", "Позже"));
        row.AddChild(UiKit.Button("Удалить", () => _model.RemoveWave(id), ButtonKind.Danger, IdPrefix + "wave.delete"));

        var routes = _model.MapRoutes().Select(r => new ChoiceOption(r.Id, r.Name + (r.Enabled ? string.Empty : " (выкл.)"))).ToList();
        var units = ReferenceOptions(ContentTypeRegistry.Unit, ReferenceFilter.Any, null, allowCreate: false);
        for (var k = 0; k < wave.Groups.Count; k++)
        {
            var g = wave.Groups[k];
            var gid = g.Id;
            var n = (k + 1).ToString(CultureInfo.InvariantCulture);
            inspector.Text($"Группа {n}: {g.Count} × «{_model.UnitName(g.Unit)}» " + (g.Route.Length > 0 ? $"по «{_model.RouteName(g.Route)}»" : $"из «{_model.SourceName(g.Source)}»"), size: 13);
            if (_model.Scenario.IsDefense)
            {
                // Spec part 7 §5.1: a source of the map without a route, or a route — one «Откуда».
                var from = _model.Sources().Select(o => new ChoiceOption(o.Value, o.Label))
                    .Concat(_model.MapRoutes().Select(r => new ChoiceOption("route:" + r.Id, $"по маршруту «{r.Name}»"))).ToList();
                inspector.Choice(IdPrefix + $"group.{n}.source", "Откуда", from, g.Route.Length > 0 ? "route:" + g.Route : g.Source,
                    v => Refuse(v.StartsWith("route:", System.StringComparison.Ordinal) ? _model.SetGroupRoute(id, gid, v["route:".Length..]) : _model.SetGroupSource(id, gid, v)));
                if (g.Route.Length == 0)
                {
                    inspector.Choice(IdPrefix + $"group.{n}.ai", "Поведение", AiData.Presets.Where(p => p.Value != "none").Select(p => new ChoiceOption(p.Value, p.Label)).ToList(), g.Ai.Preset,
                        v => Refuse(_model.SetGroupAi(id, gid, v)), "Простой ИИ: к объекту, атаковать юнитов, сначала одно — потом другое, охранник.");
                    inspector.LineText(IdPrefix + $"group.{n}.detect", "Замечает за, гексов (0 — всю карту)", g.Ai.DetectRange.ToString(CultureInfo.InvariantCulture), v => Refuse(_model.SetAiNumber(id, gid, "detectRange", v)));
                }
            }
            else
            {
                inspector.Choice(IdPrefix + $"group.{n}.route", "Маршрут", routes, g.Route, v => Refuse(_model.SetGroupRoute(id, gid, v)));
            }

            inspector.Choice(IdPrefix + $"group.{n}.unit", "Юнит врага", units, g.Unit, v => Refuse(_model.SetGroupUnit(id, gid, v)));
            inspector.LineText(IdPrefix + $"group.{n}.count", "Врагов", g.Count.ToString(CultureInfo.InvariantCulture), v => Refuse(_model.SetGroupCount(id, gid, v)));
            inspector.LineText(IdPrefix + $"group.{n}.delay", "Первый через, с", Inspector.Num(g.FirstDelay), v => Refuse(_model.SetGroupTime(id, gid, "firstDelay", v)));
            inspector.LineText(IdPrefix + $"group.{n}.interval", "Интервал, с", Inspector.Num(g.Interval), v => Refuse(_model.SetGroupTime(id, gid, "interval", v)),
                "0 — все сразу: занятый вход пропускает их по одному, без наложений.");
            inspector.LineText(IdPrefix + $"group.{n}.offset", "Сдвиг группы, с", Inspector.Num(g.Offset), v => Refuse(_model.SetGroupTime(id, gid, "offset", v)),
                "Сдвиг от начала волны, чтобы согласовать несколько маршрутов.");
            var grow = inspector.Row();
            var on = AutomationIds.Register(new CheckBox { Text = "включена", ButtonPressed = g.Enabled, FocusMode = Control.FocusModeEnum.None }, IdPrefix + $"group.{n}.enabled");
            on.Toggled += value => _model.SetGroupEnabled(id, gid, value);
            grow.AddChild(on);
            grow.AddChild(UiKit.Button("Убрать группу", () => _model.RemoveGroup(id, gid), ButtonKind.Flat, IdPrefix + $"group.{n}.remove"));
        }

        if (wave.Groups.Count < ScenarioData.MaxGroups)
        {
            inspector.Button(IdPrefix + "group.add", "＋ Группа", () => _model.AddGroup(id));
        }
    }

    /// <summary>The goal of a defense and its mission objects (spec part 7 §5.1, §5.3).</summary>
    private void BuildDefense(Inspector inspector)
    {
        var s = _model.Scenario;
        inspector.Section("Цель обороны");
        inspector.LineText(IdPrefix + "survive", "Выстоять, с (0 — отбить все волны)", Inspector.Num(s.SurviveSeconds), v => Refuse(_model.SetSurvive(v)),
            "Победа, когда прошло столько секунд боя (без пауз); 0 — когда все волны отбиты и врагов нет.");
        inspector.Choice(IdPrefix + "objects.policy", "Объекты", new[] { new ChoiceOption("all", "все критические должны уцелеть"), new ChoiceOption("any", "хотя бы один критический") }, s.ObjectsPolicy,
            v => _model.SetObjectsPolicy(v));
        inspector.Check(IdPrefix + "squadloss", "Гибель всего отряда — поражение", s.SquadLossDefeat, on => _model.SetSquadLoss(on));

        inspector.Section($"Объекты миссии — {s.Objects.Count}");
        inspector.Hint("Обороняемые точки: враги с ИИ «к объекту» идут к ним. Объект не двигается и не получает приказов.");
        var brush = inspector.Row();
        var enemies = UiKit.Button("Ставить: враги", () => { _model.PlaceObjects = false; Stamp++; }, _model.PlaceObjects ? ButtonKind.Normal : ButtonKind.Primary, IdPrefix + "brush.enemies");
        var objects = UiKit.Button("Ставить: объекты", () => { _model.PlaceObjects = true; Stamp++; }, _model.PlaceObjects ? ButtonKind.Primary : ButtonKind.Normal, IdPrefix + "brush.objects");
        brush.AddChild(enemies);
        brush.AddChild(objects);
        foreach (var o in s.Objects)
        {
            inspector.Text(o.Describe(), dim: true, size: 12);
        }

        if (_model.SelectedObjectData is { } obj)
        {
            inspector.LineText(IdPrefix + "object.name", "Название объекта", obj.Name, v => Refuse(_model.SetObjectName(v)));
            inspector.LineText(IdPrefix + "object.health", "Здоровье", Inspector.Num(obj.Health), v => Refuse(_model.SetObjectHealth(v)));
            inspector.Check(IdPrefix + "object.critical", "Критический — его потеря = поражение", obj.Critical, on => _model.SetObjectCritical(on));
            var row = inspector.Row();
            row.AddChild(UiKit.Button("Убрать объект", () => _model.RemoveObject(), ButtonKind.Danger, IdPrefix + "object.remove"));
            row.AddChild(UiKit.Button("Снять выбор", _model.DeselectObject, ButtonKind.Flat, IdPrefix + "object.deselect"));
        }
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
            case "scenario-objects":
                actual = s.Objects.Count.ToString(CultureInfo.InvariantCulture);
                ok = actual == a[1];
                return true;
            case "scenario-object":
                {
                    // expect:scenario-object:c,r:<name>:<health>:yes|no (critical)
                    var cr2 = a[1].Split(',');
                    var o = s.Objects.FirstOrDefault(x => x.Col.ToString(CultureInfo.InvariantCulture) == cr2[0] && x.Row.ToString(CultureInfo.InvariantCulture) == cr2[1]);
                    actual = o is null ? "none" : $"{o.Name}:{Inspector.Num(o.Health)}:{(o.Critical ? "yes" : "no")}";
                    ok = actual == string.Join(":", a.Skip(2));
                    return true;
                }

            case "scenario-enemy-ai":
                {
                    // expect:scenario-enemy-ai:c,r:<preset>
                    var cr3 = a[1].Split(',');
                    var e3 = s.Enemies.FirstOrDefault(x => x.Col.ToString(CultureInfo.InvariantCulture) == cr3[0] && x.Row.ToString(CultureInfo.InvariantCulture) == cr3[1]);
                    actual = e3?.Ai.Preset ?? "none-enemy";
                    ok = actual == a[2];
                    return true;
                }

            case "scenario-group":
                {
                    // expect:scenario-group:<wave>:<group>:<source or route:id>:<ai preset>
                    var wi = int.Parse(a[1], CultureInfo.InvariantCulture) - 1;
                    var gi = int.Parse(a[2], CultureInfo.InvariantCulture) - 1;
                    var g = wi < s.Waves.Count && gi < s.Waves[wi].Groups.Count ? s.Waves[wi].Groups[gi] : null;
                    actual = g is null ? "none" : $"{(g.Route.Length > 0 ? "route:" + g.Route : g.Source)}:{g.Ai.Preset}";
                    ok = actual == string.Join(":", a.Skip(3));
                    return true;
                }

            case "scenario-waves":
                actual = s.Waves.Count.ToString(CultureInfo.InvariantCulture);
                ok = actual == a[1];
                return true;
            case "scenario-wave-total":
                // expect:scenario-wave-total:<1-based wave>:<enemies>
                var w = int.Parse(a[1], CultureInfo.InvariantCulture) - 1;
                actual = w < s.Waves.Count ? s.Waves[w].Total.ToString(CultureInfo.InvariantCulture) : "none";
                ok = actual == a[2];
                return true;
            case "scenario-wave-enemies":
                actual = s.WaveEnemies.ToString(CultureInfo.InvariantCulture);
                ok = actual == a[1];
                return true;
            case "scenario-route-total":
                // expect:scenario-route-total:<route id>:<enemies over the enabled waves>
                actual = _model.RouteTotals().FirstOrDefault(t => t.Route == a[1]).Enemies.ToString(CultureInfo.InvariantCulture);
                ok = actual == a[2];
                return true;
            case "scenario-base-lives":
                actual = _model.PlayerBases().FirstOrDefault(b => b.Number.ToString(CultureInfo.InvariantCulture) == a[1]).Lives.ToString(CultureInfo.InvariantCulture);
                ok = actual == a[2];
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
