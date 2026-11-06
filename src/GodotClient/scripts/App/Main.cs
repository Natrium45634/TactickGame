using System;
using System.Globalization;
using Godot;
using HexTactics.Client.Debug;
using HexTactics.Client.InputLayer;
using HexTactics.Client.Presentation;
using HexTactics.Client.UI;
using HexTactics.Core.Hex;
using HexTactics.Core.Map;
using HexTactics.Core.Orders;
using HexTactics.Core.Roster;
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
    private BoardView _board = null!;
    private HexFillLayer _fillLayer = null!;
    private HexFillLayer _hoverLayer = null!;
    private UnitsView _units = null!;
    private OrdersView _orders = null!;
    private MotionDebugView _motionDebug = null!;
    private HealthBarsView _healthBars = null!;
    private CombatFxView _combatFx = null!;
    private Button _enemyButton = null!;
    private PointerInput _input = null!;
    private DebugHud _hud = null!;
    private RosterBar _rosterBar = null!;
    private AutomationRunner _automation = null!;

    private Label _cellLabel = null!;
    private Label _statusLabel = null!;
    private Label _selectionLabel = null!;
    private Label _messageLabel = null!;
    private Button _placementButton = null!;
    private Button _obstacleButton = null!;
    private Button _pauseButton = null!;
    private Label _timeLabel = null!;
    private long _markedTick;
    private readonly System.Collections.Generic.Dictionary<HexTactics.Core.Units.UnitId, long> _finishedTick = new();
    private readonly System.Collections.Generic.Dictionary<string, int> _pollCounts = new();

    private HexCoord? _hovered;
    private string _fillStamp = string.Empty;

    public override void _Ready()
    {
        GD.Print(BuildInfo.Full);
        if (!BuildInfo.VersionsMatch)
        {
            GD.PushWarning($"project.godot version {BuildInfo.ProjectVersion} differs from assembly version {BuildInfo.Version}");
        }

        InputBindings.Register();
        _map = MapParser.Parse(FileAccess.GetFileAsString(MapPath));
        _layout = new HexLayout(VisualConfig.HexSize);
        _sim = new GameSimulation(_map, new SimulationSettings { DefaultStats = LoadBalance() }, RosterState.Prototype());
        _fsm = new InputStateMachine(_sim, _layout);
        _fsm.Feedback += OnFeedback;
        _picker = new BoardPicker(_map.Grid, _layout);

        BuildEnvironment();

        _board = new BoardView { Name = "Board" };
        AddChild(_board);
        _board.Build(_map.Grid, _layout);

        _fillLayer = new HexFillLayer { Name = "PassabilityLayer", RenderPriority = 1 };
        AddChild(_fillLayer);
        _fillLayer.Build(_map.Grid, _layout);

        _hoverLayer = new HexFillLayer { Name = "HoverLayer", RenderPriority = 3, ExtraLift = 0.002f };
        AddChild(_hoverLayer);
        _hoverLayer.Build(_map.Grid, _layout);

        _orders = new OrdersView { Name = "Orders" };
        AddChild(_orders);
        _orders.Initialize(_layout);

        _units = new UnitsView { Name = "Units" };
        _units.Initialize(_layout);
        AddChild(_units);

        _motionDebug = new MotionDebugView { Name = "MotionDebug" };
        AddChild(_motionDebug);
        _motionDebug.Initialize(_layout);

        _combatFx = new CombatFxView { Name = "CombatFx" };
        AddChild(_combatFx);
        var barsLayer = new CanvasLayer { Name = "HealthBars", Layer = 5 };
        AddChild(barsLayer);
        _healthBars = new HealthBarsView { Name = "Bars" };
        barsLayer.AddChild(_healthBars);

        _cameraRig = new CameraRig { Name = "CameraRig" };
        AddChild(_cameraRig);
        var (min, max) = _map.Grid.PlaneBounds(_layout);
        _cameraRig.SetFocus(new Aabb(
            new Vector3((float)min.X, -VisualConfig.TileThickness, (float)min.Z),
            new Vector3((float)(max.X - min.X), VisualConfig.TileThickness + 1.0f, (float)(max.Z - min.Z))));

        BuildHud();
        _rosterBar = new RosterBar { Name = "Roster" };
        _rosterBar.Initialize(_sim.State.Roster);
        _rosterBar.CardPressed += OnRosterCardPressed;
        _rosterBar.CommandPressed += () => _fsm.ToggleGroupMode();
        AddChild(_rosterBar);

        _input = new PointerInput { Name = "PointerInput" };
        AddChild(_input);
        _input.Initialize(_fsm, () => _cameraRig.Camera, _picker, _units, _orders, IsOverUi);
        _input.DebugKey += OnDebugKey;
        _input.PauseToggled += () => _sim.TogglePause();

        _automation = new AutomationRunner { Name = "Automation" };
        _automation.StepRequested = RunAutomationStep;
        AddChild(_automation);

        GetViewport().SizeChanged += FitCamera;
        CallDeferred(MethodName.FitCamera);
    }

    public override void _Process(double delta)
    {
        // Fixed-step simulation: rules advance in whole ticks of 1/30 s regardless of the frame rate.
        _sim.Advance(delta);
        HandleSimulationEvents(_sim.DrainEvents());
        _board.SyncTerrain(_map.Grid, _layout);
        _fillLayer.SyncTerrain();
        _hoverLayer.SyncTerrain();

        _rosterBar.UiScale = _hud.UiScale;
        _rosterBar.LeftEdge = _hud.ScreenRect.End.X;

        _fsm.Validate();
        _fsm.Refresh();
        var draft = _fsm.Draft;
        var interpolation = _sim.InterpolationAlpha * _sim.Settings.TickDuration;
        var group = _fsm.Group;
        _units.Sync(_sim.State, _fsm.SelectedUnit, (float)delta, interpolation, draft?.Unit, draft?.UnitFacingPreview, group?.Members);
        _orders.Sync(_sim.State, _fsm.SelectedUnit, _fsm.SelectedWaypointId, draft, group, _fsm.HoverTurnFacing, _sim.CommandAnchor, _units.VisualPlanePosition);
        _motionDebug.Sync(_sim.State, u => _units.PositionOf(u, interpolation));
        _healthBars.UiScale = _hud.UiScale;
        _healthBars.Sync(_sim.State, _cameraRig.Camera, u => _units.PositionOf(u, interpolation), _fsm.SelectedUnit);
        _rosterBar.SetCommandState(group is not null, group is null ? "клик — собрать"
            : group.IsCollecting ? $"сбор: {group.Members.Count} (ПКМ — выход)"
            : $"КТД: {group.Waypoints.Count} · взгляд у ребра");
        var placement = _fsm.Placement;
        var ghostCell = placement?.TargetHex is { } target && _map.Grid.Contains(target) ? target : (HexCoord?)null;
        _units.SyncGhost(ghostCell, placement?.CanPlace ?? false);
        UpdateHover();
        UpdateFills(draft, group);
        UpdateHud();
    }

    // ------------------------------------------------------------------ view updates

    private void UpdateHover()
    {
        HexCoord? hex = null;
        if (_input.MouseInside && !IsOverUi(_input.MousePosition))
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

    /// <summary>
    /// Passability layer. Placement mode: every cell shows whether a unit can be placed there.
    /// Order draft: all 120 cells are green/red, the source hex (facing sectors) neutral.
    /// </summary>
    private void UpdateFills(DraftView? drag, GroupDraftView? group)
    {
        string stamp;
        if (group?.NextStage is not null)
        {
            stamp = $"group:{string.Join(',', group.Members)}:{string.Join(',', group.Sources)}:{string.Join(',', group.Waypoints)}:{_sim.State.TopologyVersion}";
        }
        else if (drag is not null)
        {
            stamp = $"draft:{drag.Unit}:{drag.Source}:{_sim.State.TopologyVersion}";
        }
        else if (_fsm.Mode == InputMode.RosterDrag)
        {
            // Allowed cells can be taken or freed by moving units while the card is held.
            var free = new System.Text.StringBuilder();
            foreach (var cell in PlacementRules.AllowedCells(_map))
            {
                free.Append(PlacementRules.ValidateCell(_sim.State, cell) == PlacementError.None ? '1' : '0');
            }

            stamp = $"roster:{_sim.State.TopologyVersion}:{free}";
        }
        else if (_fsm.Mode == InputMode.Placement)
        {
            stamp = $"place:{_sim.State.TopologyVersion}";
        }
        else
        {
            stamp = "none";
        }

        if (stamp == _fillStamp)
        {
            return;
        }

        _fillStamp = stamp;
        _fillLayer.ClearAll();
        if (group?.NextStage is { } next)
        {
            // Command mode: green = the whole team fits around this hex as the next КТД (spec part 2 §6.2), red = not;
            // the last КТД (captain's facing sectors) is neutral.
            foreach (var cell in _map.Grid.Coords)
            {
                if (cell == group.Source)
                {
                    _fillLayer.SetFill(cell, VisualConfig.FillNeutral);
                    continue;
                }

                _fillLayer.SetFill(cell, next.Evaluate(cell).IsValid ? VisualConfig.FillValid : VisualConfig.FillInvalid);
            }
        }
        else if (drag is not null)
        {
            // All 120 cells: green = a waypoint can be placed (reachable and free), red = blocked, occupied
            // or isolated, neutral = the source hex (rotate-in-place area).
            foreach (var cell in _map.Grid.Coords)
            {
                if (cell == drag.Source)
                {
                    _fillLayer.SetFill(cell, VisualConfig.FillNeutral);
                    continue;
                }

                var ok = WaypointRules.ValidateTarget(_sim.State, drag.Unit, drag.Source, cell, drag.Reachable) == WaypointError.None;
                _fillLayer.SetFill(cell, ok ? VisualConfig.FillValid : VisualConfig.FillInvalid);
            }
        }
        else if (_fsm.Mode == InputMode.RosterDrag)
        {
            // Spec part 2 §5.2: allowed hexes (player base + deploy cells of this map) green, all others softly red.
            foreach (var cell in _map.Grid.Coords)
            {
                var ok = PlacementRules.ValidateCell(_sim.State, cell) == PlacementError.None;
                _fillLayer.SetFill(cell, ok ? VisualConfig.FillPlacementAllowed : VisualConfig.FillPlacementDenied);
            }
        }
        else if (_fsm.Mode == InputMode.Placement)
        {
            foreach (var cell in _map.Grid.Coords)
            {
                var ok = SpawnRules.ValidateCell(_sim.State, cell) == SpawnError.None;
                _fillLayer.SetFill(cell, ok ? VisualConfig.FillValid : VisualConfig.FillInvalid);
            }
        }
    }

    private void UpdateHud()
    {
        var size = GetViewport().GetVisibleRect().Size;
        _statusLabel.Text = $"Окно {size.X:0}×{size.Y:0} · {Engine.GetFramesPerSecond():0} FPS · юнитов: {_sim.State.UnitCount}";
        var selected = _fsm.SelectedUnit is { } id ? _sim.State.FindUnit(id) : null;
        _selectionLabel.Text = selected is null ? "Юнит не выбран (щелчок ЛКМ по юниту)" : UiText.UnitInfo(selected, _fsm.SelectedWaypointId, _sim.IsPaused, _sim.State.Roster.FindByUnit(selected.Id)?.Name);
        _placementButton.SetPressedNoSignal(_fsm.Mode == InputMode.Placement && _fsm.PlacementSide == HexTactics.Core.Units.Side.Player);
        _enemyButton.SetPressedNoSignal(_fsm.Mode == InputMode.Placement && _fsm.PlacementSide == HexTactics.Core.Units.Side.Enemy);
        _pauseButton.SetPressedNoSignal(_sim.IsPaused);
        _pauseButton.Text = _sim.IsPaused ? "▶ Продолжить (пробел)" : "⏸ Пауза (пробел)";
        _timeLabel.Text = $"Игровое время {_sim.GameTime:0.0} с · тик {_sim.Tick} · {(_sim.IsPaused ? "пауза" : "идёт")}";
        _obstacleButton.SetPressedNoSignal(_fsm.Mode == InputMode.ObstacleEdit);
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
            TileType.DeployZone => "зона выставления",
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

    /// <summary>Card pressed in the roster bar: available operatives are dragged onto the map, placed ones are selected.</summary>
    private void OnRosterCardPressed(int entryId)
    {
        var entry = _sim.State.Roster.Find(entryId);
        if (entry is null)
        {
            return;
        }

        if (entry.State == RosterEntryState.AvailableInRoster)
        {
            _fsm.BeginRosterDrag(entryId);
        }
        else
        {
            _fsm.SelectRosterEntry(entryId);
        }
    }

    private bool IsOverUi(Vector2 position) => _hud.IsOverUi(position) || _rosterBar.IsOverUi(position);

    /// <summary>
    /// Test stand of spec part 3 §9: static enemy targets (no AI) next to the wall corner and in the open, so attacks,
    /// approach and smoothing can be checked by hand together with the roster and the command mode.
    /// </summary>
    private void SpawnTestStand()
    {
        foreach (var (c, r) in new[] { (7, 4), (9, 5), (3, 8), (10, 1) })
        {
            _sim.Spawn(SpawnRequest.AtCell(HexCoord.FromOffset(c, r), HexTactics.Core.Units.Side.Enemy, HexDirection.South));
        }

        ShowMessage("Стенд: четыре неподвижные вражеские мишени (без ИИ). Протяните стрелку юнита на врага — атака.", false);
    }

    /// <summary>Unit characteristics from <c>data/balance.json</c> (spec part 3 §8: balance in data, not in code).</summary>
    private static HexTactics.Core.Combat.UnitStats LoadBalance()
    {
        const string path = "res://data/balance.json";
        var stats = HexTactics.Core.Combat.UnitStats.Default;
        if (!FileAccess.FileExists(path))
        {
            GD.PushWarning($"{path} not found — default characteristics are used.");
            return stats;
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(FileAccess.GetFileAsString(path));
            var root = doc.RootElement;
            int I(string name, int fallback) => root.TryGetProperty(name, out var v) ? v.GetInt32() : fallback;
            double D(string name, double fallback) => root.TryGetProperty(name, out var v) ? v.GetDouble() : fallback;
            return stats with
            {
                MaxHealth = I("maxHealth", stats.MaxHealth),
                MoveSpeedHexPerSecond = D("moveSpeedHexPerSecond", stats.MoveSpeedHexPerSecond),
                AttackRatePerSecond = D("attackRatePerSecond", stats.AttackRatePerSecond),
                NormalAttackDamage = I("normalAttackDamage", stats.NormalAttackDamage),
                NormalAttackRangeHex = I("normalAttackRangeHex", stats.NormalAttackRangeHex),
            };
        }
        catch (System.Text.Json.JsonException ex)
        {
            GD.PushWarning($"{path}: {ex.Message} — default characteristics are used.");
            return stats;
        }
    }

    private void RotateSelected(int steps)
    {
        if (_fsm.SelectedUnit is { } id && _sim.State.FindUnit(id) is { } unit)
        {
            _sim.SetFacing(id, (unit.Facing ?? HexDirection.North).RotateClockwise(steps));
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
                ShowMessage(_fsm.PlacementSide == HexTactics.Core.Units.Side.Enemy
                    ? "Мишень врага: щёлкните по зелёной клетке — неподвижный враг (без ИИ). Esc / ПКМ — выход."
                    : "Режим выставления: щёлкните по зелёной клетке. Esc / ПКМ — выход.", false);
                break;
            case DraftStarted d:
                ShowMessage(d.IsEdit
                    ? "Продолжение маршрута: клик — следующая ТД, сектор последней ТД — финальный взгляд. ПКМ/Esc — отмена."
                    : "Прокладка: клик по зелёной клетке — ТД, клик у ребра исходной клетки — финальный взгляд. ПКМ/Esc — отмена.", false);
                break;
            case DraftWaypointAdded a:
                ShowMessage($"ТД{a.Number} → {UiText.Cell(a.Cell)}. Дальше: клик — ТД{a.Number + 1}, сектор у ребра ТД{a.Number} — финальный взгляд.", false);
                break;
            case DraftTruncated t:
                ShowMessage($"Удалены ТД{t.RemovedFromNumber} и следующие (осталось {t.Remaining}).", false);
                break;
            case DraftCommitted c:
                ShowMessage(UiText.Committed(c), false);
                break;
            case DraftCancelled x:
                ShowMessage(UiText.Cancelled(x), x.Reason != DraftCancelReason.ByUser);
                break;
            case DraftCommitFailed f:
                ShowMessage(UiText.CommitFailed(f), true);
                break;
            case AttackTargetReserved r:
                ShowMessage($"Атака юнита {r.Target} — будущий приказ AttackTarget (зарезервирован, в этой версии не выполняется).", true);
                break;
            case ConfirmedWaypointsRemoved removed:
                ShowMessage(removed.Result.Success
                    ? $"Юнит {removed.Unit}: удалены ТД{removed.From.Sequence} и следующие."
                    : $"ТД не удалена: {removed.Result.Reason}", !removed.Result.Success);
                break;
            case ChainContinueRefused refused:
                ShowMessage($"Продолжить цепочку можно только из последней ТД (ТД{refused.Last.Sequence}).", true);
                break;
            case ObstacleEditModeChanged { Active: true }:
                ShowMessage("Отладка: щелчок по клетке ставит/убирает препятствие. Esc / ПКМ — выход.", false);
                break;
            case ObstacleToggled o:
                ShowMessage(o.Result.Success
                    ? $"Клетка {UiText.Cell(o.Cell)}: препятствие {(o.Blocked ? "поставлено" : "убрано")}. Маршруты перестраиваются."
                    : $"Клетка {UiText.Cell(o.Cell)}: нельзя изменить ({(o.Result.Reason == "occupied" ? "на клетке юнит" : "база / зона появления")}).", !o.Result.Success);
                break;
            case RosterDragStarted r:
                ShowMessage($"«{RosterName(r.RosterEntry)}»: отпустите на зелёной клетке. Красная, ПКМ, Esc — карточка вернётся.", false);
                break;
            case RosterCardReturned r:
                ShowMessage(UiText.RosterReturned(RosterName(r.RosterEntry), r.Error, r.Cell), r.Error != PlacementError.None);
                break;
            case RosterUnitPlaced r:
                ShowMessage($"«{RosterName(r.RosterEntry)}» на {UiText.Cell(r.Cell)}. Клик по зелёной — ТД1, у ребра клетки — взгляд (обязателен).", false);
                break;
            case GroupModeChanged { Active: true }:
                ShowMessage("Команда: кликните своего юнита — он капитан; затем остальных участников. Клик по клетке — КТД1. ПКМ/Esc — выход.", false);
                break;
            case GroupMemberAdded m:
                ShowMessage(m.IsCaptain
                    ? $"Капитан — юнит {m.Unit}. Добавьте участников кликом или кликните клетку — КТД1."
                    : $"Юнит {m.Unit} в команде (участников: {m.Count}). Клик по клетке — КТД1.", false);
                break;
            case GroupMemberRefused r:
                ShowMessage(r.Reason == GroupTargetStatus.TeamFull
                    ? $"В команде уже {HexTactics.Core.Orders.GroupRoutePlanner.MaxMembers} участников — больше нельзя."
                    : $"Юнит {r.Unit} уже в команде (капитан не меняется).", true);
                break;
            case GroupNeedsCaptain:
                ShowMessage("Сначала кликните своего юнита — он станет капитаном.", true);
                break;
            case GroupWaypointAdded k:
                ShowMessage($"КТД{k.Number} → {UiText.Cell(k.Cell)}: капитан в центр, остальные рядом. Клик — КТД{k.Number + 1}, у ребра КТД{k.Number} — взгляд капитана.", false);
                break;
            case GroupDraftTruncated t:
                ShowMessage(t.Remaining == 0 ? "КТД удалены — снова сбор команды." : $"Удалены КТД{t.RemovedFromNumber} и следующие (осталось {t.Remaining}).", false);
                break;
            case GroupCommitted g:
                ShowMessage($"Команда принята: {g.Order.Members.Count} юнит(а/ов), КТД: {g.Order.GroupWaypoints.Count}, взгляд капитана — {UiText.Direction(g.Order.FinalCaptainFacing)}.", false);
                break;
            case GroupCancelled x:
                ShowMessage(UiText.GroupCancelled(x), x.Reason != DraftCancelReason.ByUser);
                break;
            case GroupCommitFailed f:
                ShowMessage(UiText.GroupCommitFailed(f), true);
                break;
            case WaypointSelected w:
                ShowMessage($"Выбрана ТД{w.Waypoint.Sequence} юнита {w.Unit}: клетка {UiText.Cell(w.Waypoint.Cell)}.", false);
                break;
        }
    }

    private string RosterName(int entryId) => _sim.State.Roster.Find(entryId)?.Name ?? $"#{entryId}";

    private void OnDebugKey(Key key)
    {
        switch (key)
        {
            case Key.Q:
                RotateSelected(-1); // counter-clockwise
                break;
            case Key.E:
                RotateSelected(+1); // clockwise
                break;
            case Key.T:
                _fsm.TogglePlacementMode(HexTactics.Core.Units.Side.Enemy);
                break;
            case Key.P:
                _fsm.TogglePlacementMode();
                break;
            case Key.O:
                _fsm.ToggleObstacleEditMode();
                break;
            case Key.F3:
                _motionDebug.Enabled = !_motionDebug.Enabled;
                ShowMessage(_motionDebug.Enabled
                    ? "Отладка движения: жёлтые — технический путь (GraphPath), белый — логический гекс, розовая — кривая шага, оранжевая — пройденный след."
                    : "Отладка движения выключена.", false);
                break;
        }
    }

    /// <summary>Movement events of the simulation become HUD messages (spawn/order messages come from the FSM).</summary>
    private void HandleSimulationEvents(System.Collections.Generic.IReadOnlyList<SimulationEvent> events)
    {
        foreach (var e in events)
        {
            switch (e)
            {
                case DamageApplied or UnitDied:
                    _combatFx.Handle(e, id => _units.Views.TryGetValue(id, out var v) ? v.Position : null, _layout);
                    if (e is UnitDied died)
                    {
                        ShowMessage($"Юнит {died.Unit} ({UiText.SideName(died.Side)}) погиб — клетка {UiText.Cell(died.Cell)} свободна.", died.Side == HexTactics.Core.Units.Side.Player);
                    }

                    break;
                case AttackStarted s:
                    ShowMessage($"Юнит {s.Unit} атакует юнит {s.Target}: {_sim.Settings.DefaultStats.AttackRatePerSecond:0.#} удара/с.", false);
                    break;
                case AttackTargetLost l:
                    ShowMessage($"Юнит {l.Unit}: цель {l.Target} уничтожена — атака завершена.", false);
                    break;
                case AttackTargetUnreachable u:
                    ShowMessage($"Юнит {u.Unit}: цель {u.Target} недостижима — рядом нет свободной клетки для удара. Юнит ждёт.", true);
                    break;
                case WaypointReached { Waypoint.IsDetour: true }:
                    break; // stepping aside for a teammate is not an order of the player
                case TeammateYielded y:
                    ShowMessage($"Юнит {y.Unit} уступил проход юниту {y.For} (шаг в {UiText.Cell(y.Aside)}).", false);
                    break;
                case WaypointReached r:
                    ShowMessage($"Юнит {r.Unit} достиг ТД{r.Waypoint.Sequence} {UiText.Cell(r.Waypoint.Cell)}.", false);
                    break;
                case UnitBlocked b:
                    ShowMessage(UiText.Blocked(b, _sim.State), true);
                    break;
                case UnitUnblocked u:
                    ShowMessage($"Юнит {u.Unit}: путь найден, движение продолжается.", false);
                    break;
                case GroupOrderFinished g:
                    ShowMessage($"Командный приказ #{g.GroupId} выполнен: команда на местах.", false);
                    break;
                case MovementFinished f when RecordFinish(f):
                    ShowMessage($"Юнит {f.Unit} выполнил все ТД и стоит на {UiText.Cell(f.Cell)}.", false);
                    break;
                case PauseChanged p:
                    _hud.SetPaused(p.Paused);
                    ShowMessage(p.Paused
                        ? "Пауза: время остановлено. Можно выбирать юнитов и строить цепочки ТД."
                        : "Игра продолжается.", false);
                    break;
            }
        }
    }

    private bool RecordFinish(MovementFinished f)
    {
        _finishedTick[f.Unit] = f.Tick;
        return true;
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

        _hud.AddHeader("Время");
        _timeLabel = _hud.AddLabel(string.Empty, fontSize: 13);
        _pauseButton = _hud.AddButton("⏸ Пауза (пробел)", () => _sim.TogglePause());
        _pauseButton.ToggleMode = true;
        _hud.AddSeparator();

        _hud.AddHeader("Выбранный юнит");
        _selectionLabel = _hud.AddLabel(string.Empty, fontSize: 13);
        _hud.AddSeparator();

        _hud.AddHeader("Приказы");
        _hud.AddLabel(
            "тянуть от юнита → ТД1, клики → ТД2…\n" +
            "клик у ребра последней ТД → взгляд, приказ принят\n" +
            "ПКМ / Esc → отмена · ПКМ по ТД → удалить её и дальше\n" +
            "«Команда»: клик по своим, затем по клетке → КТД",
            dim: true,
            fontSize: 12);
        _hud.AddSeparator();

        _hud.AddHeader("Отладка: появление юнита");
        var row = _hud.AddRow();
        AddRowButton(row, "База", () => SpawnFromPanel(SpawnRequest.AtBase()));
        foreach (var zone in _map.SpawnZones)
        {
            var zoneId = zone.Id;
            AddRowButton(row, ShortZoneName(zone.Name), () => SpawnFromPanel(SpawnRequest.AtZone(zoneId)));
        }

        _placementButton = _hud.AddButton("Выставить на клетку (P)", () => _fsm.TogglePlacementMode());
        _placementButton.ToggleMode = true;
        var combatRow = _hud.AddRow();
        _enemyButton = new Button { Text = "Мишень врага (T)", FocusMode = Control.FocusModeEnum.None, ToggleMode = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _enemyButton.Pressed += () => _fsm.TogglePlacementMode(HexTactics.Core.Units.Side.Enemy);
        combatRow.AddChild(_enemyButton);
        AddRowButton(combatRow, "Стенд", SpawnTestStand);
        _obstacleButton = _hud.AddButton("Препятствие: поставить/убрать (O)", () => _fsm.ToggleObstacleEditMode());
        _obstacleButton.ToggleMode = true;
        _messageLabel = _hud.AddLabel("—", fontSize: 13);
        _hud.AddSeparator();

        _hud.AddHeader("Карта");
        _cellLabel = _hud.AddLabel("Клетка: —", fontSize: 13);
        _hud.AddLegendItem(VisualConfig.TileWalkable, "Проходимая");
        _hud.AddLegendItem(VisualConfig.TileBlocked, "Препятствие");
        _hud.AddLegendItem(VisualConfig.TilePlayerBase, _map.PlayerBase?.Name ?? "База игрока");
        _hud.AddLegendItem(VisualConfig.TileDeployZone, "Зона выставления");
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
        // The roster bar sits at the bottom right of the HUD: the board is fitted above it.
        var barTop = viewport.Y - ((RosterBar.BarHeight + DebugHud.Margin) * _hud.UiScale);
        var available = new Rect2(left, margin, viewport.X - left - margin, barTop - (2 * margin));
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
            case "press":
                {
                    var p = PointScreen(arg);
                    InjectMotion(p);
                    InjectButton(p, MouseButton.Left, true);
                    return 2;
                }

            case "move":
                InjectMotion(PointScreen(arg));
                return 2;
            case "release":
                {
                    var p = PointScreen(arg);
                    InjectMotion(p);
                    InjectButton(p, MouseButton.Left, false);
                    return 2;
                }

            case "drag":
                {
                    // drag:A>B>C — press on A, move through the points, release on the last one.
                    var points = arg.Split('>');
                    var first = PointScreen(points[0]);
                    InjectMotion(first);
                    InjectButton(first, MouseButton.Left, true);
                    var last = first;
                    for (var i = 1; i < points.Length; i++)
                    {
                        var next = PointScreen(points[i]);
                        for (var k = 1; k <= 4; k++)
                        {
                            InjectMotion(last.Lerp(next, k / 4f));
                        }

                        last = next;
                    }

                    InjectButton(last, MouseButton.Left, false);
                    return 2;
                }

            case "click":
                {
                    var p = PointScreen(arg);
                    InjectMotion(p);
                    InjectButton(p, MouseButton.Left, true);
                    InjectButton(p, MouseButton.Left, false);
                    return 2;
                }

            case "rclick":
                {
                    var p = PointScreen(arg);
                    InjectMotion(p);
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
                        "enemy" => SpawnRequest.AtCell(ParseHex(a[1]), HexTactics.Core.Units.Side.Enemy, HexDirection.South),
                        _ => SpawnRequest.AtCell(ParseHex(a[1])),
                    };
                    SpawnFromPanel(request);
                    return 2;
                }

            case "place":
                _fsm.TogglePlacementMode();
                return 2;
            case "obstacle":
                {
                    var cell = ParseHex(arg);
                    _sim.SetObstacle(cell, _map.Grid[cell].IsWalkable);
                    return 2;
                }
            case "key":
                {
                    var key = Enum.Parse<Key>(arg);
                    Godot.Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = true });
                    Godot.Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = false });
                    return 2;
                }
            case "draft":
                if (_fsm.SelectedUnit is { } draftUnit)
                {
                    _fsm.StartDraft(draftUnit);
                }

                return 2;
            case "rpress":
                {
                    var p = PointScreen(arg);
                    InjectMotion(p);
                    InjectButton(p, MouseButton.Right, true);
                    InjectButton(p, MouseButton.Right, false);
                    return 2;
                }

            case "roster":
                {
                    // roster:N>A>B — presses the real card N in the bottom bar, drags through the points, releases
                    // on the last one ("bar" = back over the card). "roster:N" alone presses and releases on the card
                    // (select a placed operative). A final ">hold" keeps the button pressed (finish with release:P).
                    var points = arg.Split('>');
                    var hold = points[^1] == "hold";
                    if (hold)
                    {
                        points = points[..^1];
                    }

                    var entry = int.Parse(points[0], CultureInfo.InvariantCulture);
                    var card = _rosterBar.CardCenter(entry);
                    InjectMotion(card);
                    InjectButton(card, MouseButton.Left, true);
                    var last = card;
                    for (var i = 1; i < points.Length; i++)
                    {
                        var next = points[i] == "bar" ? card + new Vector2(0, -4) : PointScreen(points[i]);
                        for (var k = 1; k <= 4; k++)
                        {
                            InjectMotion(last.Lerp(next, k / 4f));
                        }

                        last = next;
                    }

                    if (!hold)
                    {
                        InjectButton(last, MouseButton.Left, false);
                    }

                    return 2;
                }

            case "command":
                {
                    // Presses the real "Команда" card in the bottom bar.
                    var p = _rosterBar.CommandCenter;
                    InjectMotion(p);
                    InjectButton(p, MouseButton.Left, true);
                    InjectButton(p, MouseButton.Left, false);
                    return 2;
                }

            case "face":
                if (_fsm.SelectedUnit is { } id)
                {
                    _sim.SetFacing(id, Enum.Parse<HexDirection>(arg));
                }

                return 2;
            case "pause":
                _sim.SetPaused(arg switch { "on" => true, "off" => false, _ => !_sim.IsPaused });
                return 2;
            case "debug":
                _motionDebug.Enabled = arg != "off";
                return 1;
            case "mark":
                _markedTick = _sim.Tick;
                return 1;
            case "ticks":
                {
                    // Feeds game time in tick-sized chunks (respects the pause like real frames do).
                    var n = int.Parse(arg, CultureInfo.InvariantCulture);
                    for (var i = 0; i < n; i++)
                    {
                        _sim.Advance(_sim.Settings.TickDuration);
                    }

                    return 1;
                }

            case "waitidle":
                {
                    // waitidle:<unit>[:<maxFrames>] — polls until the unit has finished its orders.
                    var a = arg.Split(':');
                    var unit = _sim.State.FindUnit(new HexTactics.Core.Units.UnitId(int.Parse(a[0], CultureInfo.InvariantCulture)));
                    var max = a.Length > 1 ? int.Parse(a[1], CultureInfo.InvariantCulture) : 1200;
                    var count = _pollCounts.TryGetValue(step, out var c) ? c + 1 : 1;
                    _pollCounts[step] = count;
                    var idle = unit is not null && unit.Orders.IsEmpty && !unit.Motion.IsStepping;
                    if (!idle && count < max)
                    {
                        return AutomationRunner.Repeat;
                    }

                    _pollCounts.Remove(step);
                    _automation.ReportCheck(idle, $"{step} (after {count} frames)");
                    return 1;
                }

            case "waitdead":
                {
                    // waitdead:<unit>[:<maxFrames>] — polls until the unit has been removed after its death.
                    var a = arg.Split(':');
                    var gone = _sim.State.FindUnit(new HexTactics.Core.Units.UnitId(int.Parse(a[0], CultureInfo.InvariantCulture))) is null;
                    var max = a.Length > 1 ? int.Parse(a[1], CultureInfo.InvariantCulture) : 1200;
                    var count = _pollCounts.TryGetValue(step, out var c) ? c + 1 : 1;
                    _pollCounts[step] = count;
                    if (!gone && count < max)
                    {
                        return AutomationRunner.Repeat;
                    }

                    _pollCounts.Remove(step);
                    _automation.ReportCheck(gone, $"{step} (after {count} frames)");
                    return 1;
                }

            case "expect":
                RunExpectation(arg);
                return 1;
            case "select":
                _fsm.Select(new HexTactics.Core.Units.UnitId(int.Parse(arg, CultureInfo.InvariantCulture)));
                return 1;
            case "wait":
                return int.Parse(arg, CultureInfo.InvariantCulture);
            default:
                GD.PushWarning($"[automation] unknown step '{step}'");
                return 1;
        }
    }

    /// <summary>
    /// Screen position of an automation point: <c>c,r</c> (hex centre) or <c>c,r@Direction:distance</c>
    /// (offset from the hex centre on the map plane, in hex sizes).
    /// </summary>
    private Vector2 PointScreen(string text)
    {
        var at = text.Split('@');
        var hex = ParseHex(at[0]);
        if (at.Length == 1)
        {
            return HexScreen(hex);
        }

        var dd = at[1].Split(':');
        var dir = Enum.Parse<HexDirection>(dd[0]);
        var dist = dd.Length > 1 ? double.Parse(dd[1], CultureInfo.InvariantCulture) : 0.5;
        var p = _layout.HexToPlane(hex) + (HexLayout.DirectionVector(dir) * dist);
        return _cameraRig.Camera.UnprojectPosition(p.ToWorld(0f));
    }

    /// <summary>
    /// expect:units:N · expect:cell:ID:c,r · expect:waypoints:ID:N · expect:facing:ID:Dir ·
    /// expect:paused:true|false · expect:same-tick (no tick since <c>mark</c>) · expect:status:ID:Idle|Moving|Blocked ·
    /// expect:draft:none|N (open draft with N waypoints) · expect:roster:ENTRY:State · expect:lifecycle:ID:Active|PlacedPendingFacing ·
    /// expect:mode:InputMode · expect:group:none|M:K · expect:captain:ID · expect:ingroup:ID:yes|no · expect:facing-near:ID:Dir ·
    /// expect:arrived:ID:SECONDS (game time from mark to the unit's arrival) · expect:in-cell:ID · expect:hp:ID:N ·
    /// expect:alive:ID:yes|no · expect:attack:ID:None|FollowingRoute|Approaching|Attacking|TargetUnreachable
    /// </summary>
    private void RunExpectation(string arg)
    {
        var a = arg.Split(':');
        HexTactics.Core.Units.UnitState? Unit(int i) => _sim.State.FindUnit(new HexTactics.Core.Units.UnitId(int.Parse(a[i], CultureInfo.InvariantCulture)));
        bool ok;
        string actual;
        switch (a[0])
        {
            case "units":
                actual = _sim.State.UnitCount.ToString(CultureInfo.InvariantCulture);
                ok = actual == a[1];
                break;
            case "cell":
                {
                    var u = Unit(1);
                    actual = u is null ? "none" : $"{u.Cell.ToOffset().Col},{u.Cell.ToOffset().Row}";
                    ok = actual == a[2];
                    break;
                }

            case "waypoints":
                actual = Unit(1)?.Orders.Count.ToString(CultureInfo.InvariantCulture) ?? "none";
                ok = actual == a[2];
                break;
            case "facing":
                actual = Unit(1)?.Facing.ToString() ?? "none";
                ok = actual == a[2];
                break;
            case "status":
                actual = Unit(1)?.Motion.Status.ToString() ?? "none";
                ok = actual == a[2];
                break;
            case "facing-near":
                {
                    // expect:facing-near:ID:Dir — the unit looks Dir or 60° aside (companion facings of a command order)
                    var expected = Enum.Parse<HexDirection>(a[2]);
                    var f = Unit(1)?.Facing;
                    actual = f?.ToString() ?? "none";
                    ok = f == expected || f == expected.RotateClockwise(1) || f == expected.RotateClockwise(-1);
                    break;
                }
            case "paused":
                actual = _sim.IsPaused ? "true" : "false";
                ok = actual == a[1];
                break;
            case "draft":
                actual = _fsm.Draft is null ? "none" : _fsm.Draft.Waypoints.Count.ToString(CultureInfo.InvariantCulture);
                ok = actual == a[1];
                break;
            case "roster":
                actual = _sim.State.Roster.Find(int.Parse(a[1], CultureInfo.InvariantCulture))?.State.ToString() ?? "none";
                ok = actual == a[2];
                break;
            case "lifecycle":
                actual = Unit(1)?.Lifecycle.ToString() ?? "none";
                ok = actual == a[2];
                break;
            case "mode":
                actual = _fsm.Mode.ToString();
                ok = actual == a[1];
                break;
            case "group":
                {
                    // expect:group:none · expect:group:M:K (M members, K КТД in the open command draft)
                    var g = _fsm.Group;
                    actual = g is null ? "none" : $"{g.Members.Count}:{g.Waypoints.Count}";
                    ok = actual == (a.Length > 2 ? $"{a[1]}:{a[2]}" : a[1]);
                    break;
                }

            case "captain":
                actual = _fsm.Group?.Captain?.Value.ToString(CultureInfo.InvariantCulture) ?? "none";
                ok = actual == a[1];
                break;
            case "ingroup":
                // expect:ingroup:ID:yes|no — the unit executes a command order
                actual = Unit(1)?.Orders.GroupId is null ? "no" : "yes";
                ok = actual == a[2];
                break;
            case "arrived":
                {
                    // expect:arrived:ID:SECONDS — game time from `mark` to the unit's MovementFinished (±0.05 s)
                    var finished = _finishedTick.TryGetValue(new HexTactics.Core.Units.UnitId(int.Parse(a[1], CultureInfo.InvariantCulture)), out var t) ? t : -1;
                    var seconds = (finished - _markedTick) * _sim.Settings.TickDuration;
                    actual = finished < 0 ? "not arrived" : seconds.ToString("0.000", CultureInfo.InvariantCulture);
                    ok = finished >= 0 && Math.Abs(seconds - double.Parse(a[2], CultureInfo.InvariantCulture)) <= 0.05;
                    break;
                }

            case "hp":
                actual = Unit(1)?.CurrentHealth.ToString(CultureInfo.InvariantCulture) ?? "none";
                ok = actual == a[2];
                break;
            case "alive":
                actual = Unit(1) is { IsAlive: true } ? "yes" : "no";
                ok = actual == a[2];
                break;
            case "attack":
                actual = Unit(1)?.Combat.Status.ToString() ?? "none";
                ok = actual == a[2];
                break;
            case "in-cell":
                {
                    // expect:in-cell:ID — the drawn body lies in the unit's logical cell (spec part 3 §2.3)
                    var u = Unit(1);
                    var hex = u is null ? (HexCoord?)null : _sim.Layout.PlaneToHex(_sim.PositionOf(u));
                    actual = hex is { } h ? $"{h.ToOffset().Col},{h.ToOffset().Row}" : "none";
                    ok = u is not null && hex == u.Cell;
                    break;
                }

            case "same-tick":
                actual = $"{_markedTick} -> {_sim.Tick}";
                ok = _markedTick == _sim.Tick;
                break;
            default:
                actual = "unknown expectation";
                ok = false;
                break;
        }

        _automation.ReportCheck(ok, $"expect:{arg} (actual {actual})");
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
