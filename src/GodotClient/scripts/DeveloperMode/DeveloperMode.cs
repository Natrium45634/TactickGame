using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using HexTactics.Client.UI;
using HexTactics.Content.Library;
using HexTactics.Content.Model;
using HexTactics.Editor.History;
using HexTactics.Editor.Library;

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

    private Control _root = null!;
    private Label _status = null!;
    private Button _undo = null!;
    private Button _redo = null!;
    private LineEdit _search = null!;
    private OptionButton _sort = null!;
    private Tree _tree = null!;
    private Label _listTitle = null!;
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

    /// <summary>Editors by category id (spec part 4 §9.2 <c>EditorRegistry</c>); filled as editors appear.</summary>
    public Dictionary<string, string> Editors { get; } = new();

    public string? SelectedId => _selectedId;

    public string Category => _browser.Kind;

    public int RowCount => _browser.Rows().Count;

    public IReadOnlyList<(string Text, IssueSeverity? Severity)> Messages => _messages;

    public void Configure(ContentLibrary library)
    {
        _library = library;
        _browser = new LibraryBrowser(library);
        foreach (var problem in library.Problems)
        {
            AddMessage($"Не загружен {problem.Path}: {string.Join("; ", problem.Issues)}", IssueSeverity.Error);
        }
    }

    public override void _Ready()
    {
        var layer = new CanvasLayer { Name = "DeveloperUi", Layer = 10 };
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
        body.AddChild(BuildList());
        body.AddChild(BuildDetails());
        column.AddChild(BuildMessages());

        _history.Changed += () => CallDeferred(MethodName.Refresh);
        GetViewport().SizeChanged += Layout;
        Layout();
        AddMessage("Режим разработчика: выберите категорию слева. Встроенные объекты только для чтения — их можно дублировать.", null);
        Refresh();
    }

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
        if (@event is not InputEventKey { Pressed: true } key || ModalDialog.Open is not null)
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
            var button = UiKit.Button(entry.Kind.Title, () => SelectCategory(kind), ButtonKind.Flat, "dev.cat." + kind, entry.Kind.Description, 15);
            button.Alignment = HorizontalAlignment.Left;
            button.ToggleMode = true;
            button.CustomMinimumSize = new Vector2(0, 38);
            _categoryButtons[kind] = button;
            column.AddChild(button);
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
        _import = UiKit.Button("Импорт…", null, ButtonKind.Normal, "dev.import", "Импорт JSON и пакетов — в версии v0.20.0");
        _export = UiKit.Button("Экспорт…", null, ButtonKind.Normal, "dev.export", "Экспорт JSON и пакетов — в версии v0.20.0");
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
        var viewport = GetViewport().GetVisibleRect().Size;
        var scale = UiKit.ScaleFor(viewport);
        _root.Scale = new Vector2(scale, scale);
        _root.Size = viewport / scale;
    }

    // ------------------------------------------------------------------ state → view

    private ContentItem? Selected => _selectedId is null ? null : _library.Find(_browser.Kind, _selectedId);

    private void SelectCategory(string kind)
    {
        _browser.Kind = kind;
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
            b.SetPressedNoSignal(c.Kind.Id == _browser.Kind);
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
            item.SetText(0, row.IsBuiltIn ? "🔒 " + row.Name : row.Name);
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
        _import.Disabled = true;
        _export.Disabled = true;
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

    private void Create() => AddMessage("Редактор этой категории ещё не подключён.", IssueSeverity.Warning);

    private void Open()
    {
        if (Selected is { } item && !Editors.ContainsKey(item.Kind.Id))
        {
            AddMessage($"«{item.Name}»: редактор категории «{item.Kind.Title}» появится в следующих версиях — сейчас доступны просмотр, копия, переименование и удаление.", IssueSeverity.Warning);
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
            AddMessage($"«{item.Name}» нельзя удалить: на него ссылаются {string.Join(", ", users.Select(u => $"{u.Kind.Singular} «{u.Name}»"))}. Сначала уберите ссылки.", IssueSeverity.Error);
            return;
        }

        ModalDialog.Show(this, $"Удалить «{item.Name}»?", "Объект будет убран из библиотеки. Отменить удаление можно кнопкой «Отменить» (Ctrl+Z).", new[]
        {
            new DialogButton("Отмена", "dialog.cancel", ButtonKind.Flat, null, IsCancel: true),
            new DialogButton("Удалить", "dialog.ok", ButtonKind.Danger, () => Run(LibraryCommands.Delete(_library, item), $"Удалено: «{item.Name}».")),
        });
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
