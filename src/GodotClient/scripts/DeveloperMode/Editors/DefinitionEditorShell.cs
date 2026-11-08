using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using HexTactics.Client.Debug;
using HexTactics.Client.DeveloperMode.SharedInspector;
using HexTactics.Client.Presentation;
using HexTactics.Client.UI;
using HexTactics.Content.Library;
using HexTactics.Core.Hex;
using HexTactics.Content.Model;
using HexTactics.Content.Schema;
using HexTactics.Editor.Definitions;

namespace HexTactics.Client.DeveloperMode.Editors;

/// <summary>
/// Shared frame of the definition editors (units, skills, tile types; spec part 4 §3.1): top bar (object, state
/// «Сохранено / Есть изменения / Ошибка», save, undo/redo, check, test, back), the property panel on the left built
/// from field schemas, a preview canvas in the centre, a preview/validation panel on the right and messages at the
/// bottom; drafts, Ctrl+S / Ctrl+Z / Ctrl+Y. Subclasses fill the panels and the canvas.
/// </summary>
public abstract partial class DefinitionEditorShell : Node3D, IContentEditor
{
    private const float DraftSeconds = 2f;

    private Control _root = null!;
    private Label _title = null!;
    private Label _status = null!;
    private Button _undo = null!;
    private Button _redo = null!;
    private PanelContainer _top = null!;
    private PanelContainer _left = null!;
    private PanelContainer _right = null!;
    private PanelContainer _bottom = null!;
    private VBoxContainer _messageList = null!;
    private double _draftTimer;
    private string _lastDraft = string.Empty;
    private int _shownStamp = -1;

    protected ContentLibrary Library { get; private set; } = null!;

    protected DefinitionEditorModel Model { get; private set; } = null!;

    protected Inspector Inspector { get; private set; } = null!;

    protected Inspector Preview { get; private set; } = null!;

    protected CameraRig CameraRig { get; private set; } = null!;

    protected HexLayout Layout { get; } = new(VisualConfig.HexSize);

    /// <summary>Incremented on every change that needs the panels and the canvas rebuilt.</summary>
    protected int Stamp { get; set; }

    public abstract string Kind { get; }

    /// <summary>Prefix of automation ids, e.g. <c>unit.</c>.</summary>
    protected abstract string IdPrefix { get; }

    /// <summary>Title of the object kind in the top bar, e.g. «Юнит».</summary>
    protected abstract string KindTitle { get; }

    /// <summary>Text of the test button (null = no test run).</summary>
    protected virtual string? TestButtonText => null;

    public string ObjectName => Model.Item.Name;

    public bool IsDirty => Model.IsDirty;

    public event Action? CloseRequested;

    public event Action<ContentItem>? TestRequested;

    public event Action<ContentItem, Action<ContentItem?>>? NestedRequested;

    /// <summary>Asks the host to edit <paramref name="item"/> on top of this editor (spec part 5 §7.3).</summary>
    protected void RequestNested(ContentItem item, Action<ContentItem?> done) => NestedRequested?.Invoke(item, done);

    /// <summary>Editors with collapsed sections open the one holding the control (automation, like a user would).</summary>
    public virtual bool Reveal(string automationId) => false;

    protected void Attach(ContentLibrary library, DefinitionEditorModel model)
    {
        Library = library;
        Model = model;
        Model.Changed += () => Stamp++;
    }

    public override void _Ready()
    {
        AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = VisualConfig.Background,
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.88f, 0.89f, 0.91f),
                AmbientLightEnergy = 0.3f,
                TonemapMode = Godot.Environment.ToneMapper.Linear,
            },
        });
        AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-52f, -32f, 0f), LightEnergy = 0.4f, ShadowEnabled = true, DirectionalShadowMode = DirectionalLight3D.ShadowMode.Orthogonal, DirectionalShadowMaxDistance = 60f });
        CameraRig = new CameraRig { Name = "Camera" };
        AddChild(CameraRig);
        BuildUi();
        BuildCanvas();
        Refresh();
        CallDeferred(MethodName.OnViewportResized);
        Message(Model.Item.IsBuiltIn ? $"Встроенный объект: изменения сохранятся как ваша копия с новым ID." : $"Правки применяются по Enter или при выходе из поля; Ctrl+Z — отменить, Ctrl+S — сохранить.", null);
    }

    public override void _EnterTree()
    {
        GetViewport().SizeChanged += OnViewportResized;
        if (CameraRig is not null)
        {
            Stamp++;
            CallDeferred(MethodName.OnViewportResized);
        }
    }

    public override void _ExitTree() => GetViewport().SizeChanged -= OnViewportResized;

    public override void _Process(double delta)
    {
        if (Stamp != _shownStamp)
        {
            _shownStamp = Stamp;
            Refresh();
        }

        _draftTimer += delta;
        if (_draftTimer >= DraftSeconds)
        {
            _draftTimer = 0;
            if (Model.IsDirty && Library.User is not null)
            {
                var text = Model.Snapshot();
                if (text != _lastDraft)
                {
                    _lastDraft = text;
                    Library.User.SaveDraft(Model.Item.Kind.Folder, Model.Item.Id, text);
                }
            }
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (ModalDialog.Open is not null || @event is not InputEventKey { Pressed: true, Echo: false } key)
        {
            HandleCanvasInput(@event);
            return;
        }

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
        else if (!HandleKey(key))
        {
            return;
        }

        GetViewport().SetInputAsHandled();
    }

    // ------------------------------------------------------------------ IContentEditor

    public ContentOperationResult Save()
    {
        var wasBuiltIn = Model.Item.IsBuiltIn;
        var result = Model.Save();
        if (result.Ok)
        {
            _lastDraft = string.Empty;
            Message(wasBuiltIn ? $"Сохранено как копия «{Model.Item.Name}» ({Model.Item.Id})." : $"Сохранено: «{Model.Item.Name}», редакция {Model.Item.Revision}.", null);
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

        Stamp++;
        return result;
    }

    public void Discard()
    {
        Library.User?.DeleteDraft(Model.Item.Kind.Folder, Model.Item.Id);
        _lastDraft = string.Empty;
    }

    // ------------------------------------------------------------------ for subclasses

    protected abstract void BuildCanvas();

    /// <summary>Rebuilds the canvas after a change.</summary>
    protected abstract void UpdateCanvas();

    /// <summary>Fills the left panel.</summary>
    protected abstract void BuildProperties(Inspector inspector);

    /// <summary>Fills the right panel (computed preview).</summary>
    protected abstract void BuildPreview(Inspector preview);

    /// <summary>Plane bounds the camera keeps in view.</summary>
    protected abstract Aabb CanvasBounds();

    protected virtual void HandleCanvasInput(InputEvent @event)
    {
    }

    protected virtual bool HandleKey(InputEventKey key) => false;

    /// <summary>Applies a schema field value from user input; refusals are explained in the message panel.</summary>
    protected void ApplyField(FieldSchema field, object? input)
    {
        if (Model.TrySetField(field, input) is { } issue)
        {
            Message("✗ " + issue.Message, IssueSeverity.Error);
            Stamp++; // rebuild: the field shows the unchanged value again
        }
    }

    protected void Message(string text, IssueSeverity? severity)
    {
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
        LastMessage = text;
        if (_messageList.GetChildCount() > 50)
        {
            _messageList.GetChild(_messageList.GetChildCount() - 1).QueueFree();
        }
    }

    /// <summary>The newest message (automation).</summary>
    public string LastMessage { get; private set; } = string.Empty;

    protected void RequestTest(ContentItem copy) => TestRequested?.Invoke(copy);

    /// <summary>
    /// Choices of reference fields: the library objects of a kind (damage types for effects and attacks, spec part 5 §8);
    /// legacy damage types are offered only where they are already used.
    /// </summary>
    protected IReadOnlyList<ChoiceOption> ReferenceOptions(string kind, string? current)
    {
        var list = new List<ChoiceOption>();
        foreach (var item in Library.Items(kind))
        {
            if (item.Data is HexTactics.Content.Kinds.DamageTypeData { Legacy: true } && item.Id != current)
            {
                continue;
            }

            list.Add(new ChoiceOption(item.Id, item.IsBuiltIn ? item.Name : $"{item.Name} (свой)",
                item.Data is HexTactics.Content.Kinds.DamageTypeData d ? Color.FromHtml(d.Color) : null));
        }

        return list;
    }

    // ------------------------------------------------------------------ frame

    private void Undo()
    {
        var what = Model.History.UndoDescription;
        if (Model.Undo())
        {
            Message($"Отменено: {what}", null);
        }
    }

    private void Redo()
    {
        var what = Model.History.RedoDescription;
        if (Model.Redo())
        {
            Message($"Возвращено: {what}", null);
        }
    }

    private void ValidateNow()
    {
        var issues = Model.Validate();
        Message($"Проверка: ошибок {issues.Count(i => i.IsError)}, замечаний {issues.Count(i => !i.IsError)}.", issues.Any(i => i.IsError) ? IssueSeverity.Error : null);
        foreach (var issue in issues)
        {
            Message((issue.IsError ? "✗ " : "⚠ ") + issue, issue.IsError ? IssueSeverity.Error : IssueSeverity.Warning);
        }
    }

    private void Test()
    {
        var errors = Model.Validate().Where(i => i.IsError).ToList();
        if (errors.Count > 0)
        {
            Message("Тест невозможен: есть ошибки — «Проверить».", IssueSeverity.Error);
            return;
        }

        Message("Тест: временная копия в игровом режиме — бой ничего не меняет в определении.", null);
        RequestTest(Model.Item.Clone());
    }

    private void Refresh()
    {
        if (_title is null)
        {
            return;
        }

        var item = Model.Item;
        _title.Text = $"{KindTitle}: {item.Name}" + (item.IsBuiltIn ? " 🔒" : string.Empty);
        var errors = Model.Validate().Count(i => i.IsError);
        (_status.Text, var color) = errors > 0
            ? ($"Ошибка ({errors}) · {(Model.IsDirty ? "есть изменения" : "сохранено")}", UiKit.Error)
            : Model.IsDirty ? ("Есть изменения", UiKit.Warning) : ("Сохранено", UiKit.Ok);
        _status.AddThemeColorOverride("font_color", color);
        _undo.Disabled = !Model.History.CanUndo;
        _redo.Disabled = !Model.History.CanRedo;
        _undo.TooltipText = Model.History.UndoDescription is { } u ? $"Отменить: {u} (Ctrl+Z)" : "Нечего отменять";
        _redo.TooltipText = Model.History.RedoDescription is { } r ? $"Вернуть: {r} (Ctrl+Y)" : "Нечего возвращать";
        Inspector.Clear();
        Inspector.Section("Основное");
        Inspector.ReadOnly("ID", item.Id);
        Inspector.LineText(IdPrefix + "name", "Название", item.Name, v => Model.SetName(v));
        Inspector.LongText(IdPrefix + "description", "Описание", item.Description, v => Model.SetDescription(v));
        BuildProperties(Inspector);
        Preview.Clear();
        BuildPreview(Preview);
        var issues = Model.Validate();
        Preview.Section("Проверка");
        if (issues.Count == 0)
        {
            Preview.Text("ошибок нет").AddThemeColorOverride("font_color", UiKit.Ok);
        }

        foreach (var issue in issues)
        {
            Preview.Text((issue.IsError ? "✗ " : "⚠ ") + issue).AddThemeColorOverride("font_color", issue.IsError ? UiKit.Error : UiKit.Warning);
        }

        UpdateCanvas();
    }

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
        bar.AddChild(UiKit.Button("Сохранить", () => Save(), ButtonKind.Primary, IdPrefix + "save", "Проверить и сохранить (Ctrl+S)"));
        _undo = UiKit.Button("↶", Undo, ButtonKind.Normal, IdPrefix + "undo");
        _redo = UiKit.Button("↷", Redo, ButtonKind.Normal, IdPrefix + "redo");
        bar.AddChild(_undo);
        bar.AddChild(_redo);
        bar.AddChild(UiKit.Button("Проверить", ValidateNow, ButtonKind.Normal, IdPrefix + "validate"));
        if (TestButtonText is { } test)
        {
            bar.AddChild(UiKit.Button(test, Test, ButtonKind.Normal, IdPrefix + "test", "Запустить временную копию в игровом режиме"));
        }

        bar.AddChild(UiKit.Button("← Каталог", () => CloseRequested?.Invoke(), ButtonKind.Flat, IdPrefix + "back", "Вернуться в каталог (спросит про несохранённые изменения)"));

        _left = SidePanel(360, out var left);
        Inspector = new Inspector(left) { References = ReferenceOptions };
        _right = SidePanel(340, out var right);
        Preview = new Inspector(right) { References = ReferenceOptions };

        _bottom = UiKit.Panel(UiKit.PanelColor, 8);
        _root.AddChild(_bottom);
        var messages = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _bottom.AddChild(messages);
        _messageList = UiKit.Column(2);
        _messageList.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        messages.AddChild(_messageList);
        LayoutUi();
    }

    private PanelContainer SidePanel(float width, out VBoxContainer column)
    {
        var panel = UiKit.Panel(UiKit.PanelColor, 14);
        panel.CustomMinimumSize = new Vector2(width, 0);
        _root.AddChild(panel);
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        panel.AddChild(scroll);
        column = UiKit.Column(6);
        column.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        scroll.AddChild(column);
        return panel;
    }

    private void OnViewportResized()
    {
        LayoutUi();
        FitCamera();
    }

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
        var height = size.Y - 104 - 10 - topEnd;
        _left.Position = new Vector2(12, topEnd);
        _left.Size = new Vector2(360, height);
        _right.Position = new Vector2(size.X - 12 - 340, topEnd);
        _right.Size = new Vector2(340, height);
    }

    protected void FitCamera()
    {
        if (CameraRig?.Camera is null || _left is null || !IsInsideTree())
        {
            return;
        }

        var viewport = GetViewport().GetVisibleRect().Size;
        var scale = UiKit.ScaleFor(viewport);
        var leftEdge = (_left.GetGlobalRect().End.X + 12) * scale;
        var rightEdge = (_right.GetGlobalRect().Position.X - 12) * scale;
        var top = (_top.GetGlobalRect().End.Y + 12) * scale;
        var bottom = (_bottom.GetGlobalRect().Position.Y - 12) * scale;
        CameraRig.SetFocus(CanvasBounds());
        CameraRig.Fit(new Rect2(leftEdge, top, rightEdge - leftEdge, bottom - top), viewport);
    }

    // ------------------------------------------------------------------ automation

    public virtual int RunAutomationStep(string step, AutomationRunner runner)
    {
        var parts = step.Split(':', 2);
        if (parts[0] != "expect")
        {
            return -2;
        }

        var a = parts[1].Split(':');
        string actual;
        bool ok;
        switch (a[0])
        {
            case "dirty":
                actual = Model.IsDirty ? "yes" : "no";
                ok = actual == a[1];
                break;
            case "name":
                actual = Model.Item.Name;
                ok = actual == a[1];
                break;
            case "errors":
                actual = Model.Validate().Count(i => i.IsError).ToString(CultureInfo.InvariantCulture);
                ok = actual == a[1];
                break;
            case "editor-message":
                actual = LastMessage;
                ok = actual.Contains(parts[1][(parts[1].IndexOf(':') + 1)..], StringComparison.Ordinal);
                break;
            case "sandbox":
                // expect:sandbox:text — the last «Проверить на ударе» report contains text
                actual = Sandbox?.Text.Replace('\n', ' ') ?? "none";
                ok = actual.Contains(parts[1][(parts[1].IndexOf(':') + 1)..], StringComparison.Ordinal);
                break;
            default:
                if (Expect(a, out actual, out ok))
                {
                    break;
                }

                return -2;
        }

        runner.ReportCheck(ok, $"expect:{parts[1]} (actual {actual})");
        return 1;
    }

    /// <summary>The editor's «Проверить на ударе» panel, if it has one (spec part 5 §6.5).</summary>
    protected virtual SharedInspector.SandboxPanel? Sandbox => null;

    /// <summary>Editor-specific checks; return false if the check is unknown.</summary>
    protected virtual bool Expect(string[] a, out string actual, out bool ok)
    {
        actual = "unknown";
        ok = false;
        return false;
    }
}
