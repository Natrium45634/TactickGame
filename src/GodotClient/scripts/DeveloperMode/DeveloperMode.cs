using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using HexTactics.Client.DeveloperMode.Editors;
using HexTactics.Client.DeveloperMode.Editors.Defense;
using HexTactics.Client.DeveloperMode.Editors.Map;
using HexTactics.Client.DeveloperMode.Editors.Skill;
using HexTactics.Client.DeveloperMode.Editors.Tile;
using HexTactics.Client.DeveloperMode.Editors.Unit;
using HexTactics.Client.UI;
using HexTactics.Content.Library;
using HexTactics.Content.Model;
using HexTactics.Content.Transfer;
using HexTactics.Editor.History;
using HexTactics.Editor.Library;
using HexTactics.Editor.Transfer;

namespace HexTactics.Client.DeveloperMode;

/// <summary>
/// Developer mode (spec part 4 §2.2, §3.1): the catalog of content categories taken from the registry, the library list
/// of the chosen category (search, sort, id, date, version, errors), details of the selected object and the library
/// operations — duplicate, rename, delete with dependency check — all undoable (Ctrl+Z / Ctrl+Y). Editors of the
/// categories are registered in <see cref="Editors"/>; a category without an editor is listed but cannot be opened yet.
/// Nothing of the game simulation runs here.
/// </summary>
public partial class DeveloperMode : Node
{
    private readonly CommandHistory _history = new();
    private readonly List<(string Text, IssueSeverity? Severity)> _messages = new();
    private readonly Dictionary<string, Button> _categoryButtons = new();
    private ContentLibrary _library = null!;
    private LibraryBrowser _browser = null!;
    private string? _selectedId;
    private int _shownVersion = -1;

    private CanvasLayer _catalogLayer = null!;
    private Node? _editorNode;
    private Control _root = null!;
    private Label _status = null!;
    private Button _undo = null!;
    private Button _redo = null!;
    private LineEdit _search = null!;
    private OptionButton _sort = null!;
    private Tree _tree = null!;
    private Label _listTitle = null!;
    private Control _listPanel = null!;
    private Control _hubPanel = null!;
    private VBoxContainer _hubItems = null!;
    private readonly Dictionary<string, Button> _groupButtons = new(StringComparer.Ordinal);

    /// <summary>
    /// Catalog groups shown as one entry with a first screen (spec part 5 §6.1 «Механики», part 6 §9.1 «Происхождение и
    /// теги»): title, tooltip and the explanation of the first screen.
    /// </summary>
    private static readonly Dictionary<string, (string Title, string Hint, string Text)> Groups = new(StringComparer.Ordinal)
    {
        [HexTactics.Content.Registry.ContentTypeRegistry.MechanicsGroup] = ("Механики", "Типы урона, правила защиты, профили, правила боя, характеристики и проверка на ударе",
            "Из чего складывается урон и защита. Числа и правила отсюда видны только в режиме «Разработчик»; в игре — лишь последствия боя."),
        [HexTactics.Content.Registry.ContentTypeRegistry.OriginGroup] = ("Происхождение и теги", "Дерево видов и общие теги юнитов",
            "Вид — одна ветка дерева (Лунный волк — это и Волк, и Зверь), его эффекты наследуются один раз. Теги — общие особенности для многих юнитов: «Вожак», «Матёрый», «Раненый». Юниту назначаются в «Особенностях»."),
    };

    private Label _hubTitle = null!;
    private Label _hubText = null!;
    private readonly Stack<(Node Editor, Action<ContentItem?> Done, string ChildKind, string ChildId)> _parents = new();
    private string? _hubGroup;

    private bool _hub => _hubGroup is not null;
    private VBoxContainer _details = null!;
    private VBoxContainer _messageList = null!;
    private Button _create = null!;
    private Button _open = null!;
    private Button _duplicate = null!;
    private Button _rename = null!;
    private Button _delete = null!;
    private Button _import = null!;
    private Button _export = null!;

    /// <summary>The user wants to go back to the start menu.</summary>
    public event Action? ExitRequested;

    /// <summary>
    /// Editors by category id (spec part 4 §9.2 <c>EditorRegistry</c>): a factory that opens an object (and optionally a
    /// restored draft of it). Categories without an editor are listed but cannot be opened yet.
    /// </summary>
    public Dictionary<string, Func<ContentItem, ContentItem?, Node>> Editors { get; } = new();

    /// <summary>The open editor, if any.</summary>
    public IContentEditor? ActiveEditor => _editorNode as IContentEditor;

    /// <summary>An editor asked for a test run of a temporary copy (the root runs it in the game mode).</summary>
    public event Action<ContentItem>? TestRequested;


    public string? SelectedId => _selectedId;

    public string Category => _hubGroup ?? _browser.Kind;

    public int RowCount => _hub ? 0 : _browser.Rows().Count;

    public IReadOnlyList<(string Text, IssueSeverity? Severity)> Messages => _messages;

    public void Configure(ContentLibrary library)
    {
        _library = library;
        Editors["map"] = (item, draft) =>
        {
            var editor = new MapEditor { Name = "MapEditor" };
            editor.Configure(_library, item, draft);
            return editor;
        };
        Editors["unit"] = (item, draft) =>
        {
            var editor = new UnitEditor { Name = "UnitEditor" };
            editor.Configure(_library, item, draft);
            return editor;
        };
        Editors["skill"] = (item, draft) =>
        {
            var editor = new SkillEditor { Name = "SkillEditor" };
            editor.Configure(_library, item, draft);
            return editor;
        };
        Editors["tile"] = (item, draft) =>
        {
            var editor = new TileEditor { Name = "TileEditor" };
            editor.Configure(_library, item, draft);
            return editor;
        };
        Editors["defense_rule"] = (item, draft) =>
        {
            var editor = new DefenseRuleEditor { Name = "DefenseRuleEditor" };
            editor.Configure(_library, item, draft);
            return editor;
        };
        Editors["damage_type"] = (item, draft) =>
        {
            var editor = new HexTactics.Client.DeveloperMode.Editors.Mechanics.DamageTypeEditor { Name = "DamageTypeEditor" };
            editor.Configure(_library, item, draft);
            return editor;
        };
        Editors["resistance_profile"] = (item, draft) =>
        {
            var editor = new HexTactics.Client.DeveloperMode.Editors.Mechanics.ResistanceProfileEditor { Name = "ResistanceProfileEditor" };
            editor.Configure(_library, item, draft);
            return editor;
        };
        Editors["unit_stat"] = (item, draft) =>
        {
            var editor = new HexTactics.Client.DeveloperMode.Editors.Mechanics.UnitStatEditor { Name = "UnitStatEditor" };
            editor.Configure(_library, item, draft);
            return editor;
        };
        Editors["species"] = (item, draft) =>
        {
            var editor = new HexTactics.Client.DeveloperMode.Editors.Mechanics.SpeciesEditor { Name = "SpeciesEditor" };
            editor.Configure(_library, item, draft);
            return editor;
        };
        Editors["trait"] = (item, draft) =>
        {
            var editor = new HexTactics.Client.DeveloperMode.Editors.Mechanics.TraitEditor { Name = "TraitEditor" };
            editor.Configure(_library, item, draft);
            return editor;
        };
        Editors["combat_ruleset"] = (item, draft) =>
        {
            var editor = new HexTactics.Client.DeveloperMode.Editors.Mechanics.CombatRulesetEditor { Name = "CombatRulesetEditor" };
            editor.Configure(_library, item, draft);
            return editor;
        };
        _browser = new LibraryBrowser(library);
        foreach (var problem in library.Problems)
        {
            AddMessage($"Не загружен {problem.Path}: {string.Join("; ", problem.Issues)}", IssueSeverity.Error);
        }
    }

    public override void _Ready()
    {
        var layer = new CanvasLayer { Name = "DeveloperUi", Layer = 10 };
        _catalogLayer = layer;
        AddChild(layer);
        _root = new Control { Name = "Root", Theme = UiKit.Theme, MouseFilter = Control.MouseFilterEnum.Ignore };
        layer.AddChild(_root);
        var background = new ColorRect { Color = UiKit.Background, MouseFilter = Control.MouseFilterEnum.Ignore };
        background.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(background);

        var outer = new MarginContainer();
        outer.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        foreach (var side in new[] { "left", "right", "top", "bottom" })
        {
            outer.AddThemeConstantOverride("margin_" + side, 12);
        }

        _root.AddChild(outer);
        var column = UiKit.Column(10);
        outer.AddChild(column);
        column.AddChild(BuildTopBar());

        var body = UiKit.Row(10);
        body.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        column.AddChild(body);
        body.AddChild(BuildCategories());
        body.AddChild(_listPanel = BuildList());
        body.AddChild(_hubPanel = BuildHub());
        body.AddChild(BuildDetails());
        column.AddChild(BuildMessages());

        _history.Changed += () => CallDeferred(MethodName.Refresh);
        Layout();
        AddMessage("Режим разработчика: выберите категорию слева. Встроенные объекты только для чтения — их можно дублировать.", null);
        Refresh();
        CallDeferred(MethodName.OfferDrafts);
    }

    public override void _EnterTree() => GetViewport().SizeChanged += Layout;

    public override void _ExitTree() => GetViewport().SizeChanged -= Layout;

    public override void _Process(double delta)
    {
        if (_library.Version != _shownVersion)
        {
            Refresh();
        }
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true } key || ModalDialog.Open is not null || _editorNode is not null)
        {
            return;
        }

        if (key.CtrlPressed && key.Keycode == Key.Z && !key.ShiftPressed)
        {
            Undo();
        }
        else if (key.CtrlPressed && (key.Keycode == Key.Y || (key.Keycode == Key.Z && key.ShiftPressed)))
        {
            Redo();
        }
        else if (key.Keycode == Key.Escape)
        {
            if (_search.HasFocus())
            {
                _search.ReleaseFocus();
            }
            else
            {
                Select(null); // Esc ends the current tool / clears the selection, never loses data
            }
        }
        else
        {
            return;
        }

        GetViewport().SetInputAsHandled();
    }

    // ------------------------------------------------------------------ layout

    private Control BuildTopBar()
    {
        var bar = UiKit.Panel(UiKit.PanelColor, 10);
        var row = UiKit.Row(8);
        bar.AddChild(row);
        row.AddChild(UiKit.Label("HexTactics · Разработчик", 18));
        _status = UiKit.Label(string.Empty, 13, dim: true);
        _status.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _status.ClipText = true; // never pushes «← Меню» out of the window (v0.24.1)
        _status.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        _status.VerticalAlignment = VerticalAlignment.Center;
        row.AddChild(_status);
        _undo = UiKit.Button("↶ Отменить", Undo, ButtonKind.Normal, "dev.undo", "Отменить последнее действие (Ctrl+Z)");
        _redo = UiKit.Button("↷ Вернуть", Redo, ButtonKind.Normal, "dev.redo", "Вернуть отменённое (Ctrl+Y, Ctrl+Shift+Z)");
        row.AddChild(_undo);
        row.AddChild(_redo);
        row.AddChild(UiKit.Button("← Меню", () => ExitRequested?.Invoke(), ButtonKind.Flat, "dev.back", "Вернуться к выбору режима"));
        return bar;
    }

    private Control BuildCategories()
    {
        var panel = UiKit.Panel(UiKit.PanelColor, 12);
        panel.CustomMinimumSize = new Vector2(230, 0);
        var column = UiKit.Column(6);
        panel.AddChild(column);
        column.AddChild(UiKit.Header("Категории"));
        foreach (var entry in _browser.Categories())
        {
            var kind = entry.Kind.Id;
            var group = entry.Kind.Group is { } g && Groups.ContainsKey(g) ? g : null;
            var grouped = group is not null;
            if (group is not null && !_groupButtons.ContainsKey(group))
            {
                // Spec part 5 §6.1, part 6 §9.1: a group is one entry with its own first screen; its sections stay one click away below it.
                var groupButton = UiKit.Button(Groups[group].Title, () => SelectCategory(group), ButtonKind.Flat, "dev.cat." + group, Groups[group].Hint, 15);
                groupButton.Alignment = HorizontalAlignment.Left;
                groupButton.ToggleMode = true;
                groupButton.CustomMinimumSize = new Vector2(0, 38);
                _groupButtons[group] = groupButton;
                column.AddChild(groupButton);
            }

            var button = UiKit.Button(entry.Kind.Title, () => SelectCategory(kind), ButtonKind.Flat, "dev.cat." + kind, entry.Kind.Description, grouped ? 13 : 15);
            button.Alignment = HorizontalAlignment.Left;
            button.ToggleMode = true;
            button.CustomMinimumSize = new Vector2(0, grouped ? 30 : 38);
            _categoryButtons[kind] = button;
            if (grouped)
            {
                var indent = new MarginContainer();
                indent.AddThemeConstantOverride("margin_left", 18);
                indent.AddChild(button);
                column.AddChild(indent);
            }
            else
            {
                column.AddChild(button);
            }
        }

        column.AddChild(new HSeparator());
        column.AddChild(UiKit.Label("Другие объекты (статусы, волны, предметы…) появятся здесь вместе с их механиками — категории регистрируются без переделки редакторов.", 12, dim: true, wrap: true));
        return panel;
    }

    private Control BuildList()
    {
        var panel = UiKit.Panel(UiKit.PanelColor, 12);
        panel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var column = UiKit.Column(8);
        panel.AddChild(column);
        _listTitle = UiKit.Label(string.Empty, 16);
        _listTitle.AutowrapMode = TextServer.AutowrapMode.WordSmart; // long category descriptions wrap instead of widening the catalog (v0.24.1)
        _listTitle.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        column.AddChild(_listTitle);

        var tools = UiKit.Row(6);
        column.AddChild(tools);
        _search = AutomationIds.Register(new LineEdit { PlaceholderText = "Поиск: название, ID, описание", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClearButtonEnabled = true }, "dev.search");
        _search.TextChanged += text =>
        {
            _browser.Search = text;
            Refresh();
        };
        tools.AddChild(_search);
        _sort = AutomationIds.Register(new OptionButton { FocusMode = Control.FocusModeEnum.None }, "dev.sort");
        _sort.AddItem("По названию", (int)LibrarySort.Name);
        _sort.AddItem("По дате изменения", (int)LibrarySort.Modified);
        _sort.AddItem("По ID", (int)LibrarySort.Id);
        _sort.ItemSelected += index =>
        {
            _browser.Sort = (LibrarySort)_sort.GetItemId((int)index);
            Refresh();
        };
        tools.AddChild(_sort);
        var descending = UiKit.Button("↓", null, ButtonKind.Normal, "dev.sortdir", "Обратный порядок");
        descending.ToggleMode = true;
        descending.Toggled += on =>
        {
            _browser.Descending = on;
            Refresh();
        };
        tools.AddChild(descending);

        _tree = AutomationIds.Register(new Tree
        {
            Columns = 6,
            HideRoot = true,
            ColumnTitlesVisible = true,
            SelectMode = Tree.SelectModeEnum.Row,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            FocusMode = Control.FocusModeEnum.None,
        }, "dev.list");
        var titles = new[] { "Название", "Тип / описание", "ID", "Изменён", "Ред.", "Проверка" };
        var expand = new[] { true, true, false, false, false, false };
        var widths = new[] { 180, 220, 190, 120, 60, 110 };
        for (var i = 0; i < titles.Length; i++)
        {
            _tree.SetColumnTitle(i, titles[i]);
            _tree.SetColumnTitleAlignment(i, HorizontalAlignment.Left);
            _tree.SetColumnExpand(i, expand[i]);
            _tree.SetColumnCustomMinimumWidth(i, widths[i]);
            _tree.SetColumnClipContent(i, true);
        }

        _tree.ItemSelected += () => Select(_tree.GetSelected() is { } selectedRow ? selectedRow.GetMetadata(0).AsString() : null);
        _tree.ItemActivated += Open;
        column.AddChild(_tree);

        var actions = UiKit.Row(6);
        column.AddChild(actions);
        _create = UiKit.Button("＋ Создать", Create, ButtonKind.Primary, "dev.create");
        _open = UiKit.Button("Открыть", Open, ButtonKind.Normal, "dev.open");
        _duplicate = UiKit.Button("Дублировать", Duplicate, ButtonKind.Normal, "dev.duplicate", "Сохранить копию с новым ID (встроенные объекты тоже можно)");
        _rename = UiKit.Button("Переименовать", Rename, ButtonKind.Normal, "dev.rename", "Меняется только название — ID и ссылки остаются");
        _delete = UiKit.Button("Удалить", Delete, ButtonKind.Normal, "dev.delete", "Удалить объект из библиотеки (с проверкой ссылок; отменяется Ctrl+Z)");
        _import = UiKit.Button("Импорт…", () => OpenImport(), ButtonKind.Normal, "dev.import", "Импорт объекта (.json) или пакета (.hxp): предпросмотр, проверка, решение конфликтов");
        _export = UiKit.Button("Экспорт…", OpenExport, ButtonKind.Normal, "dev.export", "Экспорт выбранного объекта (.json) или пакета с зависимостями (.hxp)");
        var validate = UiKit.Button("Проверить всё", ValidateAll, ButtonKind.Normal, "dev.validate", "Проверить все объекты библиотеки и ссылки между ними");
        foreach (var b in new[] { _create, _open, _duplicate, _rename, _delete, _import, _export })
        {
            actions.AddChild(b);
        }

        var spacer = new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        actions.AddChild(spacer);
        actions.AddChild(validate);
        return panel;
    }

    /// <summary>
    /// The first screen of «Механики» (spec part 5 §6.1): large actions — damage types, defense rules, profiles, combat rules —
    /// built from the registry, so a section registered later appears here too.
    /// </summary>
    private Control BuildHub()
    {
        var panel = UiKit.Panel(UiKit.PanelColor, 16);
        panel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        panel.Visible = false;
        var column = UiKit.Column(14);
        panel.AddChild(column);
        column.AddChild(_hubTitle = UiKit.Label("Механики", 20));
        column.AddChild(_hubText = UiKit.Label(string.Empty, 13, dim: true, wrap: true));
        _hubItems = UiKit.Column(12);
        column.AddChild(_hubItems);
        return panel;
    }

    private void FillHub()
    {
        foreach (var child in _hubItems.GetChildren())
        {
            child.QueueFree();
        }

        _hubTitle.Text = Groups[_hubGroup!].Title;
        _hubText.Text = Groups[_hubGroup!].Text;
        foreach (var entry in _browser.Categories().Where(c => c.Kind.Group == _hubGroup))
        {
            var kind = entry.Kind.Id;
            var card = UiKit.Panel(UiKit.Background, 12);
            var row = UiKit.Row(14);
            card.AddChild(row);
            var open = UiKit.Button(entry.Kind.Title, () => SelectCategory(kind), ButtonKind.Primary, "mech." + kind, entry.Kind.Description, 17);
            open.CustomMinimumSize = new Vector2(260, 56);
            row.AddChild(open);
            var text = UiKit.Column(4);
            text.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            text.AddChild(UiKit.Label(entry.Kind.Description, 13, wrap: true));
            var own = _library.Items(kind).Count(i => !i.IsBuiltIn);
            text.AddChild(UiKit.Label($"объектов: {entry.Count} · своих: {own}" + (entry.WithErrors > 0 ? $" · с ошибками: {entry.WithErrors}" : string.Empty), 12, dim: true));
            row.AddChild(text);
            _hubItems.AddChild(card);
        }

        if (_hubGroup == HexTactics.Content.Registry.ContentTypeRegistry.MechanicsGroup)
        {
            _hubItems.AddChild(UiKit.Label("«Проверить на ударе» — в редакторе правила, типа урона и юнита: один удар в отдельной песочнице с разбором по стадиям; библиотека, матч и файлы не меняются.", 12, dim: true, wrap: true));
        }
    }

    private Control BuildDetails()
    {
        var panel = UiKit.Panel(UiKit.PanelColor, 14);
        panel.CustomMinimumSize = new Vector2(320, 0);
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        panel.AddChild(scroll);
        _details = UiKit.Column(6);
        _details.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        scroll.AddChild(_details);
        return panel;
    }

    private Control BuildMessages()
    {
        var panel = UiKit.Panel(UiKit.PanelColor, 10);
        panel.CustomMinimumSize = new Vector2(0, 118);
        var column = UiKit.Column(4);
        panel.AddChild(column);
        column.AddChild(UiKit.Header("Сообщения и проверки"));
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        column.AddChild(scroll);
        _messageList = UiKit.Column(2);
        _messageList.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        scroll.AddChild(_messageList);
        return panel;
    }

    private void Layout()
    {
        if (_root is null)
        {
            return;
        }

        var viewport = GetViewport().GetVisibleRect().Size;
        var scale = UiKit.ScaleFor(viewport);
        _root.Scale = new Vector2(scale, scale);
        _root.Size = viewport / scale;
    }

    // ------------------------------------------------------------------ state → view

    private ContentItem? Selected => _selectedId is null ? null : _library.Find(_browser.Kind, _selectedId);

    private void SelectCategory(string kind)
    {
        _hubGroup = Groups.ContainsKey(kind) ? kind : null;
        if (!_hub)
        {
            _browser.Kind = kind;
        }

        _selectedId = null;
        Refresh();
    }

    private void Select(string? id)
    {
        if (_selectedId == id)
        {
            return;
        }

        _selectedId = id;
        if (id is null)
        {
            _tree.DeselectAll();
        }

        UpdateSelection(); // the tree itself is not rebuilt inside its own selection event
    }

    private void Refresh()
    {
        if (_tree is null)
        {
            return;
        }

        _shownVersion = _library.Version;
        var categories = _browser.Categories();
        foreach (var c in categories)
        {
            var b = _categoryButtons[c.Kind.Id];
            b.Text = c.WithErrors > 0 ? $"{c.Kind.Title}   {c.Count}  ⚠{c.WithErrors}" : $"{c.Kind.Title}   {c.Count}";
            b.SetPressedNoSignal(!_hub && c.Kind.Id == _browser.Kind);
        }

        foreach (var (group, groupButton) in _groupButtons)
        {
            var members = categories.Where(c => c.Kind.Group == group).ToList();
            groupButton.Text = $"{Groups[group].Title}   {members.Sum(c => c.Count)}" + (members.Sum(c => c.WithErrors) is > 0 and var e ? $"  ⚠{e}" : string.Empty);
            groupButton.SetPressedNoSignal(_hubGroup == group || (!_hub && members.Any(c => c.Kind.Id == _browser.Kind)));
        }

        _listPanel.Visible = !_hub;
        _hubPanel.Visible = _hub;
        if (_hub)
        {
            FillHub();
        }

        var kind = _library.Registry.Find(_browser.Kind)!;
        _listTitle.Text = $"{kind.Title}: {kind.Description}";
        var total = categories.Sum(c => c.Count);
        var user = categories.Sum(c => _library.Items(c.Kind.Id).Count(i => !i.IsBuiltIn));
        _status.Text = $"Объектов: {total} · своих: {user} · изменения библиотеки сохраняются сразу, Ctrl+Z их отменяет";

        _tree.Clear();
        var root = _tree.CreateItem();
        var rows = _browser.Rows();
        if (_selectedId is not null && rows.All(r => r.Id != _selectedId))
        {
            _selectedId = null;
        }

        foreach (var row in rows)
        {
            var item = _tree.CreateItem(root);
            item.SetText(0, (row.IsBuiltIn ? "🔒 " : row.Item.Archived ? "🗄 " : string.Empty) + row.Name);
            item.SetText(1, row.Summary);
            item.SetText(2, row.Id);
            item.SetText(3, row.Modified);
            item.SetText(4, $"{row.Revision} / с{row.SchemaVersion}");
            item.SetText(5, row.Errors > 0 ? $"ошибок: {row.Errors}" : row.Warnings > 0 ? $"замечаний: {row.Warnings}" : "ок");
            item.SetCustomColor(5, row.Errors > 0 ? UiKit.Error : row.Warnings > 0 ? UiKit.Warning : UiKit.Ok);
            item.SetCustomColor(2, UiKit.TextDim);
            item.SetCustomColor(3, UiKit.TextDim);
            item.SetTooltipText(0, row.Description.Length > 0 ? row.Description : row.Name);
            item.SetMetadata(0, row.Id);
            if (row.Id == _selectedId)
            {
                item.Select(0);
            }
        }

        UpdateSelection();
    }

    /// <summary>Buttons, top bar and details for the current selection (no list rebuild).</summary>
    private void UpdateSelection()
    {
        var kind = _library.Registry.Find(_browser.Kind)!;
        var hasEditor = Editors.ContainsKey(kind.Id);
        var selected = Selected;
        _create.Disabled = !hasEditor;
        _create.TooltipText = hasEditor ? $"Создать: {kind.Singular}" : $"Редактор категории «{kind.Title}» появится в одной из следующих версий (v0.16.0–v0.19.0)";
        _open.Disabled = selected is null || !hasEditor;
        _open.TooltipText = hasEditor ? "Открыть в редакторе (двойной клик)" : _create.TooltipText;
        _duplicate.Disabled = selected is null;
        _rename.Disabled = selected is null || selected.IsBuiltIn;
        _delete.Disabled = selected is null || selected.IsBuiltIn;
        _import.Disabled = _library.User is null;
        _export.Disabled = selected is null || _library.User is null;
        _undo.Disabled = !_history.CanUndo;
        _redo.Disabled = !_history.CanRedo;
        _undo.TooltipText = _history.UndoDescription is { } u ? $"Отменить: {u} (Ctrl+Z)" : "Нечего отменять";
        _redo.TooltipText = _history.RedoDescription is { } r ? $"Вернуть: {r} (Ctrl+Y)" : "Нечего возвращать";
        ShowDetails(selected);
    }

    private void ShowDetails(ContentItem? item)
    {
        foreach (var child in _details.GetChildren())
        {
            child.QueueFree();
        }

        if (item is null)
        {
            _details.AddChild(UiKit.Header("Объект"));
            _details.AddChild(UiKit.Label("Выберите объект в списке, чтобы увидеть его свойства, ссылки и проверку.", 13, dim: true, wrap: true));
            return;
        }

        _details.AddChild(UiKit.Header(item.Kind.Singular));
        _details.AddChild(UiKit.Label(item.Name, 18, wrap: true));
        _details.AddChild(UiKit.Label(item.IsBuiltIn ? "встроенный · только чтение" : "свой · " + (item.SourcePath ?? "не сохранён"), 12, dim: true, wrap: true));
        if (item.Description.Length > 0)
        {
            _details.AddChild(UiKit.Label(item.Description, 13, wrap: true));
        }

        _details.AddChild(UiKit.Label($"ID: {item.Id}", 12, dim: true, wrap: true));
        _details.AddChild(UiKit.Label($"Редакция {item.Revision} · схема {item.SchemaVersion} · {item.Modified?.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture) ?? "—"}", 12, dim: true));
        _details.AddChild(UiKit.Label(item.Data.Summary(), 13, wrap: true));

        _details.AddChild(new HSeparator());
        _details.AddChild(UiKit.Header("Ссылается на"));
        var refs = item.References.ToList();
        if (refs.Count == 0)
        {
            _details.AddChild(UiKit.Label("—", 12, dim: true));
        }

        foreach (var r in refs)
        {
            var target = _library.Find(r);
            var label = UiKit.Label(target is null ? $"✗ {r.Kind}:{r.Id} — нет в библиотеке" : $"{target.Kind.Singular}: {target.Name}", 12, wrap: true);
            label.AddThemeColorOverride("font_color", target is null ? UiKit.Error : UiKit.Text);
            _details.AddChild(label);
        }

        if (item.Archived)
        {
            _details.AddChild(UiKit.Label("В архиве: ссылки работают, в новых выборах объект не предлагается.", 12, dim: true, wrap: true));
            _details.AddChild(UiKit.Button("Вернуть из архива", Unarchive, ButtonKind.Normal, "dev.unarchive"));
        }

        _details.AddChild(UiKit.Header("Используется в"));
        var users = _library.UsedBy(item.Ref);
        if (users.Count == 0)
        {
            _details.AddChild(UiKit.Label("—", 12, dim: true));
        }

        foreach (var u in users)
        {
            _details.AddChild(UiKit.Label($"{u.Kind.Singular}: {u.Name}", 12, wrap: true));
        }

        _details.AddChild(UiKit.Header("Проверка"));
        var issues = _library.Validate(item);
        if (issues.Count == 0)
        {
            var ok = UiKit.Label("ошибок нет", 12);
            ok.AddThemeColorOverride("font_color", UiKit.Ok);
            _details.AddChild(ok);
        }

        foreach (var issue in issues)
        {
            var label = UiKit.Label((issue.IsError ? "✗ " : "⚠ ") + issue, 12, wrap: true);
            label.AddThemeColorOverride("font_color", issue.IsError ? UiKit.Error : UiKit.Warning);
            _details.AddChild(label);
        }
    }

    public void AddMessage(string text, IssueSeverity? severity)
    {
        _messages.Insert(0, (text, severity));
        if (_messages.Count > 60)
        {
            _messages.RemoveAt(_messages.Count - 1);
        }

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
        if (_messageList.GetChildCount() > 60)
        {
            _messageList.GetChild(_messageList.GetChildCount() - 1).QueueFree();
        }
    }

    // ------------------------------------------------------------------ actions

    private void Run(IEditorCommand command, string done)
    {
        try
        {
            _history.Execute(command);
            AddMessage(done, null);
        }
        catch (EditorCommandException e)
        {
            AddMessage($"{command.Description}: {e.Message}", IssueSeverity.Error);
        }
    }

    private void Undo()
    {
        try
        {
            var description = _history.UndoDescription;
            if (_history.Undo())
            {
                AddMessage($"Отменено: {description}", null);
            }
        }
        catch (EditorCommandException e)
        {
            AddMessage($"Отменить нельзя: {e.Message}", IssueSeverity.Error);
        }
    }

    private void Redo()
    {
        try
        {
            var description = _history.RedoDescription;
            if (_history.Redo())
            {
                AddMessage($"Возвращено: {description}", null);
            }
        }
        catch (EditorCommandException e)
        {
            AddMessage($"Вернуть нельзя: {e.Message}", IssueSeverity.Error);
        }
    }

    private void Create()
    {
        var kind = _library.Registry.Find(_browser.Kind)!;
        if (!Editors.ContainsKey(kind.Id))
        {
            AddMessage("Редактор этой категории ещё не подключён.", IssueSeverity.Warning);
            return;
        }

        if (kind.Id == HexTactics.Content.Registry.ContentTypeRegistry.DamageType)
        {
            ChooseDamageTypeBase();
            return;
        }

        var name = kind.Id switch
        {
            "map" => "Новая карта",
            "unit" => "Новый юнит",
            "skill" => "Новый навык",
            "tile" => "Новый тип гекса",
            "defense_rule" => "Новое правило защиты",
            "resistance_profile" => "Новый набор сопротивлений",
            _ => "Новый объект",
        };
        OpenEditor(_library.Create(kind.Id, name), null);
        AddMessage($"Создан черновик: {kind.Singular} «{name}». Он появится в библиотеке после сохранения.", null);
    }

    /// <summary>Spec part 5 §6.2: «Создать тип» → on the basis of physical / magical / true / a new family → the editor.</summary>
    private void ChooseDamageTypeBase()
    {
        var buttons = HexTactics.Editor.Mechanics.DamageTypeEditorModel.Bases
            .Select(b => new DialogButton(b.Value switch { "physical" => "На основе физического", "magical" => "На основе магического", "true" => "На основе чистого", _ => "Новое семейство" },
                "dialog.base." + b.Value, ButtonKind.Normal, () =>
            {
                OpenEditor(HexTactics.Editor.Mechanics.DamageTypeEditorModel.CreateFrom(_library, b.Value, "Новый тип урона"), null);
                AddMessage($"Создан черновик типа урона ({b.Label}). Он появится в списках после сохранения.", null);
            }))
            .Append(new DialogButton("Отмена", "dialog.cancel", ButtonKind.Flat, null, IsCancel: true))
            .ToList();
        ModalDialog.Show(this, "Создать тип урона", "Стратегия расчёта всегда явная: подтип берёт её у семейства, новое семейство выбирает само. Название не влияет на расчёт.", buttons, width: 820);
    }

    private void Open()
    {
        if (Selected is not { } item)
        {
            return;
        }

        if (!Editors.ContainsKey(item.Kind.Id))
        {
            AddMessage($"«{item.Name}»: редактор категории «{item.Kind.Title}» появится в следующих версиях — сейчас доступны просмотр, копия, переименование и удаление.", IssueSeverity.Warning);
            return;
        }

        OpenEditor(item, null);
    }

    /// <summary>Hides the catalog and shows the editor of <paramref name="item"/> (with a restored draft, if given).</summary>
    public void OpenEditor(ContentItem item, ContentItem? draft)
    {
        if (_editorNode is not null || !Editors.TryGetValue(item.Kind.Id, out var factory))
        {
            return;
        }

        _editorNode = factory(item, draft);
        var editor = (IContentEditor)_editorNode;
        editor.CloseRequested += RequestCloseEditor;
        editor.NestedRequested += OpenNested;
        editor.TestRequested += copy => TestRequested?.Invoke(copy);
        _catalogLayer.Visible = false;
        AddChild(_editorNode);
    }

    /// <summary>Leaving the editor: unsaved changes → «Сохранить / Не сохранять / Отмена» (spec part 4 §2.1).</summary>
    public void RequestCloseEditor()
    {
        if (ActiveEditor is not { } editor)
        {
            return;
        }

        if (!editor.IsDirty)
        {
            CloseEditor();
            return;
        }

        ModalDialog.Show(this, $"Сохранить изменения «{editor.ObjectName}»?", "Есть несохранённые изменения. Черновик хранится до решения.", new[]
        {
            new DialogButton("Отмена", "dialog.cancel", ButtonKind.Flat, null, IsCancel: true),
            new DialogButton("Не сохранять", "dialog.discard", ButtonKind.Normal, () =>
            {
                editor.Discard();
                CloseEditor();
            }),
            new DialogButton("Сохранить", "dialog.ok", ButtonKind.Primary, () =>
            {
                if (editor.Save().Ok)
                {
                    CloseEditor();
                }
            }),
        });
    }

    /// <summary>
    /// Edits <paramref name="item"/> on top of the open editor (spec part 5 §7.3): the parent editor is set aside with its
    /// unsaved state and comes back when the nested one closes; <paramref name="done"/> gets the saved object or null.
    /// </summary>
    public void OpenNested(ContentItem item, Action<ContentItem?> done)
    {
        if (_editorNode is null || !Editors.TryGetValue(item.Kind.Id, out var factory))
        {
            return;
        }

        var parent = _editorNode;
        RemoveChild(parent);
        _parents.Push((parent, done, item.Kind.Id, item.Id));
        _editorNode = factory(item, null);
        var editor = (IContentEditor)_editorNode;
        editor.CloseRequested += RequestCloseEditor;
        editor.TestRequested += copy => TestRequested?.Invoke(copy);
        editor.NestedRequested += OpenNested;
        AddChild(_editorNode);
    }

    private void CloseEditor()
    {
        if (_editorNode is null)
        {
            return;
        }

        if (_parents.Count > 0)
        {
            var (parent, done, childKind, childId) = _parents.Pop();
            RemoveChild(_editorNode);
            _editorNode.QueueFree();
            _editorNode = parent;
            AddChild(parent);
            done(_library.Find(childKind, childId));
            return;
        }

        var kind = ActiveEditor!.Kind;
        var name = ActiveEditor.ObjectName;
        RemoveChild(_editorNode);
        _editorNode.QueueFree();
        _editorNode = null;
        _catalogLayer.Visible = true;
        _browser.Kind = kind;
        _selectedId = IdByName(name);
        Refresh();
    }

    /// <summary>Unsaved drafts found at start are offered for restoration (the saved version is never overwritten).</summary>
    private void OfferDrafts()
    {
        if (_library.User is null || _editorNode is not null || ModalDialog.Open is not null)
        {
            return;
        }

        foreach (var draftInfo in _library.User.ListDrafts())
        {
            var kind = _library.Registry.All.FirstOrDefault(k => k.Folder == draftInfo.Folder);
            if (kind is null || !Editors.ContainsKey(kind.Id))
            {
                continue;
            }

            var text = _library.User.ReadDraft(draftInfo.Folder, draftInfo.Id);
            var draft = text is null ? null : ContentSerializer.Read(text, _library.Registry, ContentOrigin.User).Item;
            if (draft is null)
            {
                _library.User.DeleteDraft(draftInfo.Folder, draftInfo.Id);
                continue;
            }

            var saved = _library.Find(kind.Id, draft.Id);
            ModalDialog.Show(this, $"Восстановить черновик «{draft.Name}»?", $"Найдены несохранённые изменения ({kind.Singular}, {draftInfo.Saved.ToLocalTime():dd.MM.yyyy HH:mm}). " +
                (saved is null ? "Объект ещё ни разу не сохранялся." : "Сохранённая версия не изменится, пока вы не сохраните черновик."), new[]
            {
                new DialogButton("Позже", "dialog.cancel", ButtonKind.Flat, null, IsCancel: true),
                new DialogButton("Удалить черновик", "dialog.discard", ButtonKind.Normal, () =>
                {
                    _library.User.DeleteDraft(draftInfo.Folder, draftInfo.Id);
                    CallDeferred(MethodName.OfferDrafts);
                }),
                new DialogButton("Восстановить", "dialog.ok", ButtonKind.Primary, () =>
                {
                    var baseItem = saved ?? _library.Create(kind.Id, draft.Name);
                    if (saved is null)
                    {
                        baseItem = draft.CopyAs(draft.Id, ContentOrigin.User, draft.Name);
                        baseItem.Revision = 0;
                    }

                    OpenEditor(baseItem, saved is null ? null : draft);
                }),
            });
            return;
        }
    }

    private void Duplicate()
    {
        if (Selected is not { } item)
        {
            return;
        }

        Run(LibraryCommands.Duplicate(_library, item, $"{item.Name} (копия)", copy => _selectedId = copy.Id), $"Создана копия «{item.Name}» с новым ID.");
    }

    // ------------------------------------------------------------------ export / import (spec part 4 §8.3)

    private void OpenExport()
    {
        if (Selected is not { } item || _library.User is null)
        {
            return;
        }

        var plan = new ExportPlan(_library, item);
        var single = plan.Single.SelfContained
            ? "«Объект .json» — один самодостаточный файл."
            : $"«Объект .json» НЕ самодостаточен: ему нужны {string.Join(", ", plan.Single.NotIncluded.Select(i => $"{i.Kind.Singular} «{i.Name}»"))} — в другой установке их не будет. Для переноса со всеми зависимостями выберите пакет.";
        var checks = new List<(ContentRef Ref, CheckBox Box)>();
        Label? summary = null;
        void Sync()
        {
            foreach (var (r, box) in checks)
            {
                box.SetPressedNoSignal(plan.IsIncluded(r));
                box.Disabled = plan.IsRequired(r);
            }

            if (summary is not null)
            {
                summary.Text = $"В пакете: {plan.PackageItems.Count} объект(ов).";
            }
        }

        ModalDialog.Show(this, $"Экспорт: {item.Kind.Singular} «{item.Name}»", single + "\nНиже — название пакета (.hxp).", new[]
        {
            new DialogButton("Отмена", "export.cancel", ButtonKind.Flat, null, IsCancel: true),
            new DialogButton("Папка экспорта", "export.folder", ButtonKind.Flat, () => OS.ShellOpen(_library.User.ExportsDir)),
            new DialogButton("Объект .json", "export.json", ButtonKind.Normal, () => Export(() => plan.WriteObject(), $"{item.Kind.Singular} «{item.Name}»")),
            new DialogButton("Пакет .hxp", "export.hxp", ButtonKind.Primary, () =>
            {
                plan.PackageName = ModalDialog.LastInput;
                Export(() => plan.WritePackage(HexTactics.Client.App.BuildInfo.Version, DateTimeOffset.UtcNow), $"пакет, объектов {plan.PackageItems.Count}");
            }),
        }, plan.PackageName, box =>
        {
            box.AddChild(UiKit.Label("Объекты пакета: отмеченное и всё, что ему нужно (недоступные для снятия — зависимости). Встроенные объекты есть в каждой установке и не копируются.", 12, dim: true, wrap: true));
            foreach (var candidate in plan.Candidates)
            {
                var r = candidate.Ref;
                var check = AutomationIds.Register(new CheckBox { Text = $"{candidate.Kind.Singular}: {candidate.Name}" }, "export.pick." + candidate.Name);
                check.Toggled += on =>
                {
                    if (plan.Toggle(r, on) is { } refusal)
                    {
                        AddMessage(refusal, IssueSeverity.Warning);
                    }

                    Sync();
                };
                box.AddChild(check);
                checks.Add((r, check));
            }

            summary = UiKit.Label(string.Empty, 13);
            box.AddChild(summary);
            Sync();
        }, width: 640);
    }

    private void Export(Func<string> write, string what)
    {
        try
        {
            var path = write();
            AddMessage($"Экспорт: {what} → {path}", null);
        }
        catch (Exception e) when (e is HexTactics.Content.Storage.ContentSaveException or InvalidOperationException or System.IO.IOException)
        {
            AddMessage($"Экспорт не выполнен: {e.Message}", IssueSeverity.Error);
        }
    }

    private void OpenImport(string path = "")
    {
        if (_library.User is null)
        {
            return;
        }

        ModalDialog.Show(this, "Импорт", $"Файл объекта (.json) или пакета (.hxp): полный путь или имя файла из папки экспорта этой установки ({_library.User.ExportsDir}). Сначала покажем, что в нём, — ничего не изменится до «Установить».", new[]
        {
            new DialogButton("Отмена", "import.cancel", ButtonKind.Flat, null, IsCancel: true),
            new DialogButton("Обзор…", "import.browse", ButtonKind.Normal, BrowseImport),
            new DialogButton("Показать", "import.preview", ButtonKind.Primary, () =>
            {
                var session = new ImportSession(_library);
                session.Inspect(ModalDialog.LastInput);
                ShowImportPreview(session);
            }),
        }, path, width: 640);
    }

    private void BrowseImport()
    {
        var dialog = new FileDialog
        {
            FileMode = FileDialog.FileModeEnum.OpenFile,
            Access = FileDialog.AccessEnum.Filesystem,
            UseNativeDialog = false,
            Filters = new[] { "*.hxp ; Пакет HexTactics", "*.json ; Объект HexTactics" },
            CurrentDir = _library.User!.ExportsDir,
            Title = "Файл для импорта",
        };
        AddChild(dialog);
        dialog.FileSelected += p =>
        {
            dialog.QueueFree();
            OpenImport(p);
        };
        dialog.Canceled += () =>
        {
            dialog.QueueFree();
            OpenImport();
        };
        dialog.PopupCentered(new Vector2I(900, 600));
    }

    private void ShowImportPreview(ImportSession session)
    {
        var preview = session.Preview!;
        var head = preview.Manifest is { } m ? $"Пакет «{m.Name}» (игра v{m.GameVersion}). " : string.Empty;
        LastImportSummary = preview.Summary;
        ModalDialog.Show(this, "Импорт: предпросмотр", head + preview.Summary, new[]
        {
            new DialogButton("Отмена", "import.cancel", ButtonKind.Flat, () => AddMessage("Импорт отменён — ничего не изменилось.", null), IsCancel: true),
            new DialogButton("Установить", "import.install", ButtonKind.Primary, () =>
            {
                var result = session.Install();
                if (result.Ok)
                {
                    _history.Clear();
                    AddMessage($"Импорт: установлено {result.Installed.Count} — {string.Join(", ", result.Installed.Select(i => $"{i.Kind.Singular} «{i.Name}»"))}.", null);
                }
                else
                {
                    AddMessage("Импорт не выполнен: " + result.Message, IssueSeverity.Error);
                }

                Refresh();
            }),
        }, null, box =>
        {
            foreach (var problem in preview.Problems)
            {
                var label = UiKit.Label((problem.IsError ? "✗ " : "⚠ ") + problem.Message, 12, wrap: true);
                label.AddThemeColorOverride("font_color", problem.IsError ? UiKit.Error : UiKit.Warning);
                box.AddChild(label);
            }

            for (var i = 0; i < preview.Entries.Count; i++)
            {
                var entry = preview.Entries[i];
                box.AddChild(UiKit.Label(ImportSession.Describe(entry), 13, wrap: true));
                var choice = AutomationIds.Register(new OptionButton(), $"import.res.{i}");
                foreach (var option in entry.Options)
                {
                    choice.AddItem(ContentImporter.Word(option));
                    choice.SetItemMetadata(choice.ItemCount - 1, option.ToString().ToLowerInvariant());
                    if (option == entry.Resolution)
                    {
                        choice.Select(choice.ItemCount - 1);
                    }
                }

                var index = i;
                var options = entry.Options;
                choice.ItemSelected += selected =>
                {
                    if (session.SetResolution(index, options[(int)selected]) is { } refusal)
                    {
                        AddMessage(refusal, IssueSeverity.Warning);
                    }
                };
                box.AddChild(choice);
                foreach (var issue in entry.Issues.Where(x => x.IsError || x.Severity == IssueSeverity.Warning))
                {
                    var label = UiKit.Label((issue.IsError ? "✗ " : "⚠ ") + issue.Message, 12, dim: !issue.IsError, wrap: true);
                    if (issue.IsError)
                    {
                        label.AddThemeColorOverride("font_color", UiKit.Error);
                    }

                    box.AddChild(label);
                }
            }
        }, width: 680);
    }

    /// <summary>The summary of the last import preview (automation).</summary>
    public string LastImportSummary { get; private set; } = string.Empty;

    private void Rename()
    {
        if (Selected is not { } item || item.IsBuiltIn)
        {
            return;
        }

        ModalDialog.Show(this, "Переименовать", $"Новое название. ID «{item.Id}» и все ссылки на объект не изменятся.", new[]
        {
            new DialogButton("Отмена", "dialog.cancel", ButtonKind.Flat, null, IsCancel: true),
            new DialogButton("Сохранить", "dialog.ok", ButtonKind.Primary, () =>
            {
                var name = ModalDialog.LastInput.Trim();
                if (name.Length == 0 || name == item.Name)
                {
                    return;
                }

                Run(LibraryCommands.Rename(_library, item, name), $"«{item.Name}» → «{name}».");
            }),
        }, item.Name);
    }

    private void Delete()
    {
        if (Selected is not { } item || item.IsBuiltIn)
        {
            return;
        }

        var users = _library.UsedBy(item.Ref);
        if (users.Count > 0)
        {
            AskDependents(item);
            return;
        }

        ModalDialog.Show(this, $"Удалить «{item.Name}»?", "Объект будет убран из библиотеки. Отменить удаление можно кнопкой «Отменить» (Ctrl+Z).", new[]
        {
            new DialogButton("Отмена", "dialog.cancel", ButtonKind.Flat, null, IsCancel: true),
            new DialogButton("Удалить", "dialog.ok", ButtonKind.Danger, () => Run(LibraryCommands.Delete(_library, item), $"Удалено: «{item.Name}».")),
        });
    }

    /// <summary>
    /// Deleting an object others depend on (spec part 6 §2.4): the dependents are listed, and the choice is cancel, replace
    /// every reference by a compatible object and delete, or archive — never a silent re-binding.
    /// </summary>
    private void AskDependents(ContentItem item)
    {
        var replacements = LibraryCommands.Replacements(_library, item);
        var pick = replacements.FirstOrDefault()?.Id;
        var buttons = new List<DialogButton> { new("Отмена", "dialog.cancel", ButtonKind.Flat, null, IsCancel: true) };
        if (replacements.Count > 0)
        {
            buttons.Add(new DialogButton("Заменить ссылки и удалить", "dialog.replace", ButtonKind.Danger, () =>
            {
                if (pick is not null)
                {
                    Run(LibraryCommands.ReplaceAndDelete(_library, item, pick), $"Ссылки на «{item.Name}» заменены на «{_library.Find(item.Kind.Id, pick)?.Name}», объект удалён.");
                }
            }));
        }

        buttons.Add(new DialogButton("В архив", "dialog.archive", ButtonKind.Normal, () =>
            Run(LibraryCommands.Archive(_library, item, true), $"«{item.Name}» в архиве: ссылки работают, для новых выборов он скрыт.")));
        ModalDialog.Show(this, $"Удалить «{item.Name}»?", $"На объект ссылаются — {LibraryCommands.DependencyReport(_library, item)}. Молча перепривязывать ссылки нельзя: выберите замену или архив.", buttons,
            content: box =>
            {
                if (replacements.Count == 0)
                {
                    box.AddChild(UiKit.Label("Совместимой замены нет — можно только отправить в архив.", 13, dim: true, wrap: true));
                    return;
                }

                box.AddChild(UiKit.Label("Заменить на:", 13));
                var option = AutomationIds.Register(new OptionButton { FocusMode = Control.FocusModeEnum.None }, "dialog.replace.pick");
                for (var i = 0; i < replacements.Count; i++)
                {
                    option.AddItem(replacements[i].Name, i);
                    option.SetItemMetadata(i, replacements[i].Id);
                }

                option.ItemSelected += index => pick = option.GetItemMetadata((int)index).AsString();
                box.AddChild(option);
            }, width: 640);
    }

    private void Unarchive()
    {
        if (Selected is { Archived: true } item)
        {
            Run(LibraryCommands.Archive(_library, item, false), $"«{item.Name}» снова предлагается в списках.");
        }
    }

    private void ValidateAll()
    {
        _library.Revalidate();
        var errors = 0;
        var warnings = 0;
        foreach (var kind in _library.Registry.Visible)
        {
            foreach (var item in _library.Items(kind.Id))
            {
                foreach (var issue in item.Issues)
                {
                    if (issue.IsError)
                    {
                        errors++;
                        AddMessage($"{item.Kind.Singular} «{item.Name}»: {issue}", IssueSeverity.Error);
                    }
                    else
                    {
                        warnings++;
                    }
                }
            }
        }

        AddMessage($"Проверка библиотеки: ошибок {errors}, замечаний {warnings}, не загружено файлов {_library.Problems.Count}.", errors > 0 ? IssueSeverity.Error : null);
    }

    // ------------------------------------------------------------------ automation

    /// <summary>Screen point of the list row of an object (automation clicks it like a user).</summary>
    public Vector2? RowScreenPoint(string id)
    {
        var item = _tree.GetRoot()?.GetFirstChild();
        while (item is not null)
        {
            if (item.GetMetadata(0).AsString() == id)
            {
                _tree.ScrollToItem(item);
                var rect = _tree.GetItemAreaRect(item, 0);
                return _tree.GetCanvasTransform() * (_tree.GetGlobalTransform() * (rect.Position + (rect.Size * 0.5f)));
            }

            item = item.GetNext();
        }

        return null;
    }

    /// <summary>Id of the object of the current category with this name (for scenarios that do not know generated ids).</summary>
    public string? IdByName(string name) => _library.Items(_browser.Kind).FirstOrDefault(i => i.Name == name)?.Id;

    public bool IsDirty => _history.IsDirty;

    public int UndoCount => _history.UndoCount;
}
