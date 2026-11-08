using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using HexTactics.Client.Debug;
using HexTactics.Client.DeveloperMode.SharedInspector;
using HexTactics.Client.InputLayer;
using HexTactics.Client.Presentation;
using HexTactics.Client.UI;
using HexTactics.Content.Kinds;
using HexTactics.Content.Library;
using HexTactics.Content.Model;
using HexTactics.Content.Registry;
using HexTactics.Content.Runtime;
using HexTactics.Core.Hex;
using HexTactics.Core.Map;
using HexTactics.Editor.Maps;
using TileData = HexTactics.Content.Kinds.TileData;

namespace HexTactics.Client.DeveloperMode.Editors.Map;

/// <summary>
/// The map editor (spec part 4 §4): a virtual flat-top grid of the workspace (faint outlines with coordinates — not tiles),
/// LMB on an empty place creates one real tile and shows its settings on the left, LMB on a tile selects it; the red
/// two-stage «Удалить гексы» tool; map settings, validation, saving, drafts and a test run in the game mode. Wheel = zoom,
/// middle button or Space+drag = pan, «Вид» switches 60° / strictly from above. The model is <see cref="MapEditorModel"/>.
/// </summary>
public partial class MapEditor : Node3D, IContentEditor
{
    private const float DraftSeconds = 2f;

    private readonly HexLayout _layout = new(VisualConfig.HexSize);
    private readonly List<(string Text, IssueSeverity? Severity)> _messages = new();
    private MapEditorModel _model = null!;
    private ContentLibrary _library = null!;

    private CameraRig _camera = null!;
    private BoardView _board = null!;
    private MeshInstance3D _outlines = null!;
    private Node3D _labels = null!;
    private HexFillLayer _marks = null!;
    private HexGrid _grid = null!;
    private BoardPicker _picker = null!;

    private Control _root = null!;
    private Label _title = null!;
    private Label _status = null!;
    private Button _undo = null!;
    private Button _redo = null!;
    private Button _view = null!;
    private Button _delete = null!;
    private Button _deleteCancel = null!;
    private Label _deleteHint = null!;
    private PanelContainer _left = null!;
    private PanelContainer _top = null!;
    private PanelContainer _bottom = null!;
    private VBoxContainer _messageList = null!;
    private Inspector _inspector = null!;
    private bool _showMapTab;

    private HexCoord? _hover;
    private Vector2? _pressAt;
    private bool _panning;
    private bool _spaceHeld;
    private double _draftTimer;
    private string _lastDraft = string.Empty;
    private int _shownStamp = -1;
    private int _stamp;
    private bool _topView;

    public string Kind => ContentTypeRegistry.Map;

    public string ObjectName => _model.Item.Name;

    public bool IsDirty => _model.IsDirty;

    public MapEditorModel Model => _model;

    public event Action? CloseRequested;

    public event Action<ContentItem>? TestRequested;

#pragma warning disable CS0067 // the map editor opens no nested editors
    public event Action<ContentItem, Action<ContentItem?>>? NestedRequested;
#pragma warning restore CS0067

    /// <summary>The map editor has no collapsed sections.</summary>
    public bool Reveal(string automationId) => false;

    /// <summary>Opens <paramref name="item"/> (a library object or a new unsaved one); call before adding the node.</summary>
    public void Configure(ContentLibrary library, ContentItem item, ContentItem? draft = null)
    {
        _library = library;
        _model = new MapEditorModel(library, item);
        if (draft is not null)
        {
            _model.LoadDraft(draft);
        }

        _model.Changed += () => _stamp++;
    }

    public override void _Ready()
    {
        TilePalette.Current = new TilePalette(VisualsOf(_library));
        AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = VisualConfig.Background,
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.88f, 0.89f, 0.91f),
                AmbientLightEnergy = 0.25f,
                TonemapMode = Godot.Environment.ToneMapper.Linear,
            },
        });
        AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-52f, -32f, 0f), LightEnergy = 0.35f, ShadowEnabled = true, DirectionalShadowMode = DirectionalLight3D.ShadowMode.Orthogonal, DirectionalShadowMaxDistance = 90f });

        _board = new BoardView { Name = "Board" };
        AddChild(_board);
        _outlines = new MeshInstance3D { Name = "Workspace", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(_outlines);
        _labels = new Node3D { Name = "Coordinates" };
        AddChild(_labels);
        _marks = new HexFillLayer { Name = "Marks", RenderPriority = 2, ExtraLift = 0.003f };
        AddChild(_marks);
        _camera = new CameraRig { Name = "Camera" };
        AddChild(_camera);

        BuildUi();
        Rebuild();
        CallDeferred(MethodName.Fit);
        Message(_model.Item.IsBuiltIn
            ? "Встроенная карта: изменения сохранятся как ваша копия с новым ID."
            : "ЛКМ по пустому месту — новый гекс, по гексу — его настройки. Колесо — масштаб, средняя кнопка или пробел+перетаскивание — сдвиг.", null);
    }

    public override void _EnterTree()
    {
        GetViewport().SizeChanged += OnViewportResized;
        if (_camera is not null)
        {
            // Back from a test run: the game mode replaced the palette.
            TilePalette.Current = new TilePalette(VisualsOf(_library));
            _stamp++;
            CallDeferred(MethodName.OnViewportResized);
        }
    }

    public override void _ExitTree() => GetViewport().SizeChanged -= OnViewportResized;

    private void OnViewportResized()
    {
        LayoutUi();
        Fit();
    }

    public override void _Process(double delta)
    {
        if (_stamp != _shownStamp)
        {
            _shownStamp = _stamp;
            Rebuild();
        }

        _draftTimer += delta;
        if (_draftTimer >= DraftSeconds)
        {
            _draftTimer = 0;
            SaveDraft();
        }
    }

    // ------------------------------------------------------------------ IContentEditor

    public ContentOperationResult Save()
    {
        var wasBuiltIn = _model.IsBuiltIn;
        var result = _model.Save();
        if (result.Ok)
        {
            _lastDraft = string.Empty;
            Message(wasBuiltIn ? $"Сохранено как копия «{_model.Item.Name}» ({_model.Item.Id})." : $"Сохранено: «{_model.Item.Name}», редакция {_model.Item.Revision}.", null);
            foreach (var w in result.Issues.Where(i => !i.IsError))
            {
                Message("⚠ " + w, IssueSeverity.Warning);
            }
        }
        else
        {
            Message("Не сохранено: исправьте ошибки.", IssueSeverity.Error);
            foreach (var e in result.Issues.Where(i => i.IsError))
            {
                Message("✗ " + e, IssueSeverity.Error);
            }
        }

        _stamp++;
        return result;
    }

    public void Discard()
    {
        _library.User?.DeleteDraft("maps", _model.Item.Id);
        _lastDraft = string.Empty;
    }

    // ------------------------------------------------------------------ input

    public override void _UnhandledInput(InputEvent @event)
    {
        if (ModalDialog.Open is not null)
        {
            return;
        }

        switch (@event)
        {
            case InputEventKey { Keycode: Key.Space } space:
                _spaceHeld = space.Pressed;
                break;
            case InputEventKey { Pressed: true, Echo: false } key:
                HandleKey(key);
                break;
            case InputEventMouseMotion motion:
                if (_panning)
                {
                    _camera.Pan(motion.Relative);
                    _pressAt = null;
                }

                SetHover(Pick(motion.Position));
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp, Pressed: true } up:
                _camera.Zoom(0.9f, up.Position, GetViewport().GetVisibleRect().Size);
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelDown, Pressed: true } down:
                _camera.Zoom(1.1f, down.Position, GetViewport().GetVisibleRect().Size);
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Middle } middle:
                _panning = middle.Pressed;
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } left:
                if (left.Pressed)
                {
                    _panning = _spaceHeld;
                    _pressAt = _spaceHeld ? null : left.Position;
                }
                else
                {
                    if (_pressAt is { } at && at.DistanceTo(left.Position) <= 6f && Pick(left.Position) is { } hex)
                    {
                        OnCanvasClick(hex);
                    }

                    _pressAt = null;
                    _panning = false;
                }

                break;
            default:
                return;
        }

        GetViewport().SetInputAsHandled();
    }

    private void HandleKey(InputEventKey key)
    {
        if (key.CtrlPressed && key.Keycode == Key.S)
        {
            Save();
        }
        else if (key.CtrlPressed && key.Keycode == Key.Z && !key.ShiftPressed)
        {
            Undo();
        }
        else if (key.CtrlPressed && (key.Keycode == Key.Y || (key.Keycode == Key.Z && key.ShiftPressed)))
        {
            Redo();
        }
        else if (key.Keycode == Key.Escape)
        {
            if (_model.DeleteMode)
            {
                _model.CancelDelete();
                Message("Режим удаления закрыт, ничего не удалено.", null);
            }
            else
            {
                _model.Select(null);
            }
        }
    }

    private HexCoord? Pick(Vector2 screen)
    {
        var pick = _picker.Pick(_camera.Camera, screen);
        return pick.Hex is { } h && _model.InWorkspace(h) ? h : null;
    }

    private void SetHover(HexCoord? hex)
    {
        if (_hover != hex)
        {
            _hover = hex;
            UpdateMarks();
        }
    }

    private void OnCanvasClick(HexCoord hex)
    {
        var o = hex.ToOffset();
        switch (_model.Click(hex))
        {
            case MapClick.Created:
                _showMapTab = false;
                Message($"Создан гекс ({o.Col},{o.Row}).", null);
                break;
            case MapClick.Selected:
                _showMapTab = false;
                break;
        }
    }

    // ------------------------------------------------------------------ actions

    private void Undo()
    {
        var what = _model.History.UndoDescription;
        if (_model.Undo())
        {
            Message($"Отменено: {what}", null);
        }
    }

    private void Redo()
    {
        var what = _model.History.RedoDescription;
        if (_model.Redo())
        {
            Message($"Возвращено: {what}", null);
        }
    }

    private void PressDelete()
    {
        if (!_model.PressDeleteButton())
        {
            Message(_model.DeleteMode ? "Режим удаления: отметьте гексы кликами, повторное нажатие красной кнопки удалит их. Esc — отмена." : "Ничего не отмечено — режим удаления закрыт.", null);
            return;
        }

        var impact = _model.DeleteImpactNow();
        if (!impact.NeedsConfirmation)
        {
            DoDelete(impact.Tiles);
            return;
        }

        ModalDialog.Show(this, $"Удалить гексы: {impact.Tiles}?", "Будут затронуты связанные объекты (это часть той же операции, Ctrl+Z вернёт всё):\n• " + string.Join("\n• ", impact.Links), new[]
        {
            new DialogButton("Отмена", "dialog.cancel", ButtonKind.Flat, null, IsCancel: true),
            new DialogButton("Удалить", "dialog.ok", ButtonKind.Danger, () => DoDelete(impact.Tiles)),
        });
    }

    private void DoDelete(int count)
    {
        _model.ExecuteDelete();
        Message($"Удалено гексов: {count}. Ctrl+Z — вернуть.", null);
    }

    private void ValidateNow()
    {
        var issues = _model.Validate();
        Message($"Проверка карты: ошибок {issues.Count(i => i.IsError)}, замечаний {issues.Count(i => !i.IsError)}.", issues.Any(i => i.IsError) ? IssueSeverity.Error : null);
        foreach (var issue in issues)
        {
            Message((issue.IsError ? "✗ " : "⚠ ") + issue, issue.IsError ? IssueSeverity.Error : IssueSeverity.Warning);
        }
    }

    private void Test()
    {
        var errors = _model.Validate().Where(i => i.IsError).ToList();
        if (errors.Count > 0)
        {
            Message("Тест невозможен: карта с ошибками — «Проверить».", IssueSeverity.Error);
            return;
        }

        if (_model.Map.Tiles.Count == 0)
        {
            Message("Тест невозможен: на карте нет гексов.", IssueSeverity.Error);
            return;
        }

        Message("Тест карты: временная копия в игровом режиме — изменения боя в карту не попадут.", null);
        TestRequested?.Invoke(_model.Item.Clone());
    }

    private void ToggleView()
    {
        _topView = !_topView;
        _camera.ElevationDegrees = _topView ? 90f : VisualConfig.CameraElevationDegrees;
        _view.Text = _topView ? "Вид: сверху" : "Вид: 60°";
        Fit();
    }

    private void RequestClose() => CloseRequested?.Invoke();

    private void SaveDraft()
    {
        if (!_model.IsDirty || _library.User is null)
        {
            return;
        }

        var text = _model.Snapshot();
        if (text != _lastDraft)
        {
            _lastDraft = text;
            _library.User.SaveDraft("maps", _model.Item.Id, text);
        }
    }

    // ------------------------------------------------------------------ view

    private void Rebuild()
    {
        var issues = new IssueList();
        var map = RuntimeFactory.BuildMap(_model.Item, _library, issues);
        _grid = map.Grid;
        _picker = new BoardPicker(_grid, _layout);
        _board.Build(_grid, _layout);
        _marks.Build(_grid, _layout);
        BuildWorkspace();
        UpdateMarks();
        UpdateUi();
    }

    /// <summary>Faint outlines of every place of the workspace and the coordinates of the empty ones.</summary>
    private void BuildWorkspace()
    {
        var empty = _grid.Coords.Where(c => !_grid.Exists(c)).ToList();
        if (empty.Count == 0)
        {
            _outlines.Mesh = null;
        }
        else
        {
            var mesh = new ImmediateMesh();
            var material = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                AlbedoColor = new Color(1f, 1f, 1f, 0.2f),
            };
            mesh.SurfaceBegin(Mesh.PrimitiveType.Lines, material);
            foreach (var c in empty)
            {
                for (var i = 0; i < 6; i++)
                {
                    mesh.SurfaceAddVertex(_layout.Corner(c, i).ToWorld(0.004f));
                    mesh.SurfaceAddVertex(_layout.Corner(c, (i + 1) % 6).ToWorld(0.004f));
                }
            }

            mesh.SurfaceEnd();
            _outlines.Mesh = mesh;
        }

        foreach (var child in _labels.GetChildren())
        {
            child.QueueFree();
        }

        foreach (var p in _model.Map.TestPoints)
        {
            _labels.AddChild(new Label3D
            {
                Text = p.Label,
                FontSize = 64,
                PixelSize = 0.006f,
                Modulate = new Color(0.98f, 0.98f, 0.99f),
                OutlineModulate = new Color(0.08f, 0.09f, 0.11f),
                OutlineSize = 14,
                Position = _layout.HexToPlane(p.Coord).ToWorld(TilePalette.Current.HeightOf(_grid, p.Coord) + 0.02f),
                RotationDegrees = new Vector3(-90, 0, 0),
                NoDepthTest = true,
                Shaded = false,
                RenderPriority = 30,
            });
        }

        foreach (var c in _grid.Coords)
        {
            if (_grid.Exists(c))
            {
                continue;
            }

            var o = c.ToOffset();
            _labels.AddChild(new Label3D
            {
                Text = $"{o.Col},{o.Row}",
                FontSize = 40,
                PixelSize = 0.0045f,
                Modulate = new Color(1, 1, 1, 0.42f),
                OutlineSize = 0,
                Position = _layout.HexToPlane(c).ToWorld(0.005f),
                RotationDegrees = new Vector3(-90, 0, 0),
                NoDepthTest = false,
                Shaded = false,
            });
        }
    }

    private void UpdateMarks()
    {
        if (_marks is null || _grid is null)
        {
            return;
        }

        _marks.ClearAll();
        if (_hover is { } h && !_model.DeleteMode)
        {
            _marks.SetFill(h, _grid.Exists(h) ? new Color(1, 1, 1, 0.16f) : new Color(0.45f, 0.85f, 0.55f, 0.30f));
        }
        else if (_hover is { } dh && _model.DeleteMode && _grid.Exists(dh))
        {
            _marks.SetFill(dh, new Color(0.95f, 0.35f, 0.30f, 0.25f));
        }

        if (_model.Selected is { } s)
        {
            _marks.SetFill(s, new Color(1f, 0.85f, 0.35f, 0.34f));
        }

        foreach (var d in _model.DeleteSelection)
        {
            _marks.SetFill(d, new Color(0.92f, 0.16f, 0.14f, 0.55f));
        }
    }

    private void Fit()
    {
        if (_camera?.Camera is null || _left is null || !IsInsideTree())
        {
            return;
        }

        var viewport = GetViewport().GetVisibleRect().Size;
        var scale = UiKit.ScaleFor(viewport);
        var leftEdge = (_left.GetGlobalRect().End.X + 12) * scale;
        var top = (_deletePanel.GetGlobalRect().End.Y + 8) * scale;
        var bottom = (_bottom.GetGlobalRect().Position.Y - 12) * scale;
        var (min, max) = WorkspaceBounds();
        _camera.SetFocus(new Aabb(new Vector3((float)min.X, -VisualConfig.TileThickness, (float)min.Z), new Vector3((float)(max.X - min.X), VisualConfig.TileThickness + 0.6f, (float)(max.Z - min.Z))));
        _camera.Fit(new Rect2(leftEdge, top, viewport.X - leftEdge - 12, bottom - top), viewport);
    }

    private (PlaneVector Min, PlaneVector Max) WorkspaceBounds() => _grid.PlaneBounds(_layout);

    // ------------------------------------------------------------------ UI

    private void BuildUi()
    {
        var layer = new CanvasLayer { Name = "EditorUi", Layer = 10 };
        AddChild(layer);
        _root = new Control { Theme = UiKit.Theme, MouseFilter = Control.MouseFilterEnum.Ignore };
        layer.AddChild(_root);

        _top = UiKit.Panel(UiKit.PanelColor, 10);
        _root.AddChild(_top);
        var bar = UiKit.Row(8);
        _top.AddChild(bar);
        _title = UiKit.Label(string.Empty, 17);
        bar.AddChild(_title);
        _status = UiKit.Label(string.Empty, 13);
        _status.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _status.VerticalAlignment = VerticalAlignment.Center;
        bar.AddChild(_status);
        bar.AddChild(UiKit.Button("Сохранить", () => Save(), ButtonKind.Primary, "map.save", "Проверить и сохранить (Ctrl+S)"));
        _undo = UiKit.Button("↶", Undo, ButtonKind.Normal, "map.undo");
        _redo = UiKit.Button("↷", Redo, ButtonKind.Normal, "map.redo");
        bar.AddChild(_undo);
        bar.AddChild(_redo);
        bar.AddChild(UiKit.Button("Проверить", ValidateNow, ButtonKind.Normal, "map.validate", "Проверить карту: гексы, типы, зоны, маршрут между тестовыми точками"));
        bar.AddChild(UiKit.Button("▶ Тест карты", Test, ButtonKind.Normal, "map.test", "Запустить временную копию в игровом режиме: юниты, ТД/КТД, пауза"));
        _view = UiKit.Button("Вид: 60°", ToggleView, ButtonKind.Normal, "map.view", "Переключить вид: как в игре (60°) / строго сверху");
        bar.AddChild(_view);
        bar.AddChild(UiKit.Button("← Каталог", RequestClose, ButtonKind.Flat, "map.back", "Вернуться в каталог (спросит про несохранённые изменения)"));

        _left = UiKit.Panel(UiKit.PanelColor, 14);
        _left.CustomMinimumSize = new Vector2(330, 0);
        _root.AddChild(_left);
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _left.AddChild(scroll);
        var column = UiKit.Column(6);
        column.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        scroll.AddChild(column);
        _inspector = new Inspector(column);

        var deletePanel = UiKit.Panel(new Color(0.09f, 0.095f, 0.11f, 0.85f), 8);
        deletePanel.Name = "DeleteTool";
        _root.AddChild(deletePanel);
        var deleteColumn = UiKit.Column(4);
        deletePanel.AddChild(deleteColumn);
        var deleteRow = UiKit.Row(6);
        deleteColumn.AddChild(deleteRow);
        _delete = UiKit.Button("✕ Удалить гексы", PressDelete, ButtonKind.Danger, "map.delete", "1-е нажатие — выбрать гексы кликами, 2-е — удалить выбранные", 15);
        deleteRow.AddChild(_delete);
        _deleteCancel = UiKit.Button("Отмена (Esc)", () => _model.CancelDelete(), ButtonKind.Normal, "map.delete.cancel");
        deleteRow.AddChild(_deleteCancel);
        _deleteHint = UiKit.Label(string.Empty, 12, dim: true);
        deleteRow.AddChild(_deleteHint);

        _bottom = UiKit.Panel(UiKit.PanelColor, 8);
        _bottom.CustomMinimumSize = new Vector2(0, 92);
        _root.AddChild(_bottom);
        var messages = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _bottom.AddChild(messages);
        _messageList = UiKit.Column(2);
        _messageList.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        messages.AddChild(_messageList);

        _deletePanel = deletePanel;
        LayoutUi();
    }

    private PanelContainer _deletePanel = null!;

    private void LayoutUi()
    {
        if (_root is null || !IsInsideTree())
        {
            return;
        }

        var viewport = GetViewport().GetVisibleRect().Size;
        var scale = UiKit.ScaleFor(viewport);
        _root.Scale = new Vector2(scale, scale);
        var size = viewport / scale;
        _root.Size = size;
        _top.Position = new Vector2(12, 12);
        _top.Size = new Vector2(size.X - 24, 0);
        _bottom.Position = new Vector2(12, size.Y - 104);
        _bottom.Size = new Vector2(size.X - 24, 92);
        var topEnd = 12 + _top.GetCombinedMinimumSize().Y + 10;
        _left.Position = new Vector2(12, topEnd);
        _left.Size = new Vector2(330, size.Y - 104 - 10 - topEnd);
        _deletePanel.Position = new Vector2(12 + 330 + 12, topEnd);
    }

    private void UpdateUi()
    {
        if (_title is null)
        {
            return;
        }

        var item = _model.Item;
        _title.Text = $"Карта: {item.Name}" + (item.IsBuiltIn ? " 🔒" : string.Empty);
        var errors = _model.Validate().Count(i => i.IsError);
        (_status.Text, var color) = errors > 0
            ? ($"Ошибка ({errors}) · {(_model.IsDirty ? "есть изменения" : "сохранено")}", UiKit.Error)
            : _model.IsDirty ? ("Есть изменения", UiKit.Warning) : ("Сохранено", UiKit.Ok);
        _status.Text += $" · гексов: {_model.Map.Tiles.Count} · область {_model.Map.Columns}×{_model.Map.Rows}";
        _status.AddThemeColorOverride("font_color", color);
        _undo.Disabled = !_model.History.CanUndo;
        _redo.Disabled = !_model.History.CanRedo;
        _undo.TooltipText = _model.History.UndoDescription is { } u ? $"Отменить: {u} (Ctrl+Z)" : "Нечего отменять";
        _redo.TooltipText = _model.History.RedoDescription is { } r ? $"Вернуть: {r} (Ctrl+Y)" : "Нечего возвращать";
        _delete.Text = _model.DeleteMode ? $"✕ Удалить выбранные: {_model.DeleteSelection.Count}" : "✕ Удалить гексы";
        _deleteCancel.Visible = _model.DeleteMode;
        _deleteHint.Text = _model.DeleteMode ? "клик — отметить/снять" : string.Empty;
        _deleteHint.Visible = _model.DeleteMode;
        _deletePanel.ResetSize();
        BuildInspector();
    }

    private void BuildInspector()
    {
        _inspector.Clear();
        var tabs = _inspector.Row();
        var hexTab = UiKit.Button("Гекс", () => { _showMapTab = false; _stamp++; }, ButtonKind.Flat, "map.tab.hex");
        var mapTab = UiKit.Button("Карта", () => { _showMapTab = true; _stamp++; }, ButtonKind.Flat, "map.tab.map");
        hexTab.ToggleMode = mapTab.ToggleMode = true;
        var showMap = _showMapTab || _model.Selected is null;
        hexTab.SetPressedNoSignal(!showMap);
        mapTab.SetPressedNoSignal(showMap);
        tabs.AddChild(hexTab);
        tabs.AddChild(mapTab);

        _inspector.Section("Новые гексы");
        _inspector.Choice("map.brush", "Тип для клика по пустому месту", TileOptions(), _model.Brush, v => { _model.Brush = v; _stamp++; });

        if (!showMap && _model.Selected is { } hex && _model.TileAt(hex) is { } tile)
        {
            BuildHexPanel(hex, tile);
        }
        else
        {
            BuildMapPanel();
        }
    }

    private void BuildHexPanel(HexCoord hex, MapTileData tile)
    {
        var o = hex.ToOffset();
        var type = _library.Find(ContentTypeRegistry.Tile, tile.Tile);
        var data = type?.Data as TileData;
        _inspector.Section($"Гекс ({o.Col},{o.Row})");
        _inspector.ReadOnly("Координата", $"col {o.Col}, row {o.Row} · axial ({hex.Q}, {hex.R})");
        _inspector.Choice("map.tile.type", "Тип / шаблон", TileOptions(), tile.Tile, v => _model.SetTileType(hex, v), "Обычный, высокий, базы, препятствие, зона появления или ваш тип из библиотеки.");
        if (data is null)
        {
            _inspector.Text($"✗ Тип «{tile.Tile}» не найден в библиотеке.").AddThemeColorOverride("font_color", UiKit.Error);
            return;
        }

        _inspector.ReadOnly("Проходимость", data.Passable && data.Category != TileData.Obstacle ? "проходим (из типа)" : "непроходим (из типа)");
        _inspector.ReadOnly("Выставление", data.Category switch
        {
            TileData.PlayerBase => "игрок может выставлять юнитов (база)",
            TileData.Deploy => "игрок может выставлять юнитов (зона выставления)",
            TileData.Spawn => "зона появления на краю (не ростер)",
            _ => "нельзя",
        });
        _inspector.ReadOnly("Цвет типа", data.Color, Color.FromHtml(data.Color));
        if (data.Passable && data.Category != TileData.Obstacle && !data.Waypoints)
        {
            _inspector.ReadOnly("ТД / КТД", "нельзя ставить (из типа), пройти можно");
        }

        if (data.Effects.Count == 0)
        {
            _inspector.ReadOnly("Эффекты типа", "нет");
        }
        else
        {
            _inspector.Section("Эффекты типа (общие для всех его гексов)");
            foreach (var effect in data.Effects)
            {
                _inspector.Text(HexTactics.Editor.Tiles.TilePreview.DescribeEffect(effect, _library), size: 12);
            }

            _inspector.Hint("Гекс карты ссылается на тип: изменить эффекты — в редакторе гексов («Гексы»), они изменятся на всех картах.");
        }
        var levels = new List<ChoiceOption> { new(string.Empty, $"как у типа ({data.Level})") };
        for (var l = 0; l <= 3; l++)
        {
            levels.Add(new ChoiceOption(l.ToString(CultureInfo.InvariantCulture), $"уровень {l}"));
        }

        _inspector.Choice("map.tile.level", "Высота / уровень", levels, tile.Level?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            v => _model.SetTileLevel(hex, v.Length == 0 ? null : int.Parse(v, CultureInfo.InvariantCulture)), "Только вид гекса: правила боя от высоты не меняются.");
        if (data.Category == TileData.Spawn)
        {
            _inspector.Number("map.tile.zone", "Номер зоны появления", tile.Zone ?? 1, 1, 9, 1, v => _model.SetTileZone(hex, (int)v));
            var zone = tile.Zone ?? 1;
            _inspector.LineText("map.zone.name", $"Название зоны {zone}", _model.Map.ZoneName(zone), v => _model.SetZoneName(zone, v));
        }

        var point = _model.Map.PointAt(o.Col, o.Row);
        _inspector.Check("map.tile.testpoint", point is null ? "Тестовая точка маршрута" : $"Тестовая точка «{point.Label}»", point is not null, on => _model.SetTestPoint(hex, on),
            "Проверка ищет проходимый маршрут между тестовыми точками по порядку (A → B → …).");
        _inspector.LineText("map.tile.note", "Комментарий разработчика", tile.Note ?? string.Empty, v => _model.SetTileNote(hex, v));
        _inspector.Button("map.tile.deselect", "Снять выделение (Esc)", () => _model.Select(null), ButtonKind.Flat);
    }

    private void BuildMapPanel()
    {
        var item = _model.Item;
        var map = _model.Map;
        _inspector.Section("Карта");
        _inspector.ReadOnly("ID", item.Id);
        _inspector.LineText("map.name", "Название", item.Name, v => _model.SetName(v));
        _inspector.LongText("map.description", "Описание", item.Description, v => _model.SetDescription(v));
        _inspector.LineText("map.basename", "Название базы игрока", map.BaseName, v => _model.SetBaseName(v));
        _inspector.Choice("map.ruleset", "Правила боя", _model.Rulesets().Select(r => new ChoiceOption(r.Id, r.Name)).ToList(), map.EffectiveRuleset, v =>
        {
            if (_model.SetRuleset(v) is { } refusal)
            {
                Message("✗ " + refusal, IssueSeverity.Error);
            }
        }, "Общие константы боя (K защиты A); без выбора — совместимые правила старых карт.");
        _inspector.Section("Рабочая область");
        var row = _inspector.Row();
        var columns = AutomationIds.Register(new SpinBox { MinValue = 1, MaxValue = 32, Value = map.Columns, Prefix = "ширина", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }, "map.columns");
        var rows = AutomationIds.Register(new SpinBox { MinValue = 1, MaxValue = 32, Value = map.Rows, Prefix = "высота", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }, "map.rows");
        row.AddChild(columns);
        row.AddChild(rows);
        _inspector.Button("map.resize", "Применить размер", () => Resize((int)columns.Value, (int)rows.Value));
        _inspector.Hint("По умолчанию 12 × 10, как карты частей 1–3. Больше 16 × 12 может не поместиться на игровой экран.");
        _inspector.Section("Сводка");
        _inspector.ReadOnly("Гексов", map.Tiles.Count.ToString(CultureInfo.InvariantCulture));
        _inspector.ReadOnly("Типы", string.Join(", ", map.Tiles.GroupBy(t => t.Tile).Select(g => $"{_library.Find(ContentTypeRegistry.Tile, g.Key)?.Name ?? g.Key} × {g.Count()}")));
        _inspector.ReadOnly("Тестовые точки", map.TestPoints.Count == 0 ? "нет" : string.Join(" → ", map.TestPoints.Select(p => $"{p.Label} ({p.Col},{p.Row})")));
        _inspector.Hint("Выберите гекс на поле, чтобы открыть его настройки.");
    }

    private void Resize(int columns, int rows)
    {
        var impact = _model.ResizeImpact(columns, rows);
        if (impact.Tiles == 0)
        {
            _model.Resize(columns, rows);
            Fit();
            return;
        }

        var text = $"Гексы вне новой области будут удалены ({impact.Tiles})" + (impact.Links.Count > 0 ? ":\n• " + string.Join("\n• ", impact.Links) : ".") + "\nCtrl+Z вернёт всё.";
        ModalDialog.Show(this, $"Изменить область на {columns}×{rows}?", text, new[]
        {
            new DialogButton("Отмена", "dialog.cancel", ButtonKind.Flat, () => _stamp++, IsCancel: true),
            new DialogButton("Изменить", "dialog.ok", ButtonKind.Danger, () =>
            {
                _model.Resize(columns, rows);
                Fit();
            }),
        });
    }

    private List<ChoiceOption> TileOptions()
    {
        var list = new List<ChoiceOption>();
        foreach (var t in _library.Items(ContentTypeRegistry.Tile))
        {
            var data = (TileData)t.Data;
            list.Add(new ChoiceOption(t.Id, t.IsBuiltIn ? t.Name : $"{t.Name} (свой)", Color.FromHtml(data.Color)));
        }

        return list;
    }

    private void Message(string text, IssueSeverity? severity)
    {
        _messages.Insert(0, (text, severity));
        if (_messageList is null)
        {
            return;
        }

        var label = UiKit.Label($"{DateTime.Now:HH:mm:ss}  {text}", 12, wrap: true);
        label.AddThemeColorOverride("font_color", severity switch
        {
            IssueSeverity.Error => UiKit.Error,
            IssueSeverity.Warning => UiKit.Warning,
            _ => UiKit.TextDim,
        });
        _messageList.AddChild(label);
        _messageList.MoveChild(label, 0);
        if (_messageList.GetChildCount() > 50)
        {
            _messageList.GetChild(_messageList.GetChildCount() - 1).QueueFree();
        }
    }

    private static IReadOnlyDictionary<string, TileVisual> VisualsOf(ContentLibrary library)
    {
        var visuals = new Dictionary<string, TileVisual>();
        foreach (var t in library.Items(ContentTypeRegistry.Tile))
        {
            visuals[t.Id] = RuntimeFactory.Visual(t);
        }

        return visuals;
    }

    // ------------------------------------------------------------------ automation

    public int RunAutomationStep(string step, AutomationRunner runner)
    {
        var parts = step.Split(':', 2);
        var arg = parts.Length > 1 ? parts[1] : string.Empty;
        switch (parts[0])
        {
            case "click":
                AutomationInput.Click(HexScreen(ParseHex(arg)));
                return 3;
            case "hover":
                AutomationInput.Motion(HexScreen(ParseHex(arg)));
                return 2;
            case "wheel":
                {
                    var center = GetViewport().GetVisibleRect().Size * 0.5f;
                    var button = arg == "down" ? MouseButton.WheelDown : MouseButton.WheelUp;
                    AutomationInput.Button(center, button, true);
                    AutomationInput.Button(center, button, false);
                    return 2;
                }

            case "expect":
                Expect(arg, runner);
                return 1;
            default:
                return -2;
        }
    }

    private void Expect(string arg, AutomationRunner runner)
    {
        var a = arg.Split(':');
        string actual;
        bool ok;
        switch (a[0])
        {
            case "tiles":
                actual = _model.Map.Tiles.Count.ToString(CultureInfo.InvariantCulture);
                ok = actual == a[1];
                break;
            case "map-ruleset":
                // expect:map-ruleset:<ruleset id or name> — the combat rules the map plays with
                actual = _model.Map.EffectiveRuleset;
                ok = actual == a[1] || _library.Find(ContentTypeRegistry.CombatRuleset, actual)?.Name == a[1];
                break;
            case "tile":
                {
                    // expect:tile:c,r:<tile id or tile type name>|none — the hex refers to that library type
                    actual = _model.TileAt(ParseHex(a[1]))?.Tile ?? "none";
                    ok = actual == a[2] || _library.Find(ContentTypeRegistry.Tile, actual)?.Name == a[2];
                    break;
                }

            case "hex":
                {
                    actual = _model.Selected is { } s ? $"{s.ToOffset().Col},{s.ToOffset().Row}" : "none";
                    ok = actual == a[1];
                    break;
                }

            case "delete-mode":
                actual = _model.DeleteMode ? "on" : "off";
                ok = actual == a[1];
                break;
            case "delete-count":
                actual = _model.DeleteSelection.Count.ToString(CultureInfo.InvariantCulture);
                ok = actual == a[1];
                break;
            case "dirty":
                actual = _model.IsDirty ? "yes" : "no";
                ok = actual == a[1];
                break;
            case "testpoints":
                actual = _model.Map.TestPoints.Count.ToString(CultureInfo.InvariantCulture);
                ok = actual == a[1];
                break;
            case "level":
                actual = _model.TileAt(ParseHex(a[1]))?.Level?.ToString(CultureInfo.InvariantCulture) ?? "type";
                ok = actual == a[2];
                break;
            case "map-size":
                actual = $"{_model.Map.Columns}x{_model.Map.Rows}";
                ok = actual == a[1];
                break;
            case "map-name":
                actual = _model.Item.Name;
                ok = actual == a[1];
                break;
            case "map-errors":
                actual = _model.Validate().Count(i => i.IsError).ToString(CultureInfo.InvariantCulture);
                ok = actual == a[1];
                break;
            default:
                actual = "unknown expectation";
                ok = false;
                break;
        }

        runner.ReportCheck(ok, $"expect:{arg} (actual {actual})");
    }

    private Vector2 HexScreen(HexCoord hex) =>
        _camera.Camera.UnprojectPosition(_layout.HexToPlane(hex).ToWorld(TilePalette.Current.HeightOf(_grid, hex)));

    private static HexCoord ParseHex(string text)
    {
        var cr = text.Split(',');
        return HexCoord.FromOffset(int.Parse(cr[0], CultureInfo.InvariantCulture), int.Parse(cr[1], CultureInfo.InvariantCulture));
    }
}
