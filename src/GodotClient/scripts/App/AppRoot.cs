using System;
using System.Globalization;
using Godot;
using HexTactics.Client.Debug;
using HexTactics.Client.Game;
using HexTactics.Client.InputLayer;
using HexTactics.Client.MainMenu;
using HexTactics.Client.UI;
using HexTactics.Content.Library;
using HexTactics.Content.Runtime;

namespace HexTactics.Client.App;

/// <summary>
/// Root of the application (spec part 4 §2): the start menu with the two modes, switching between «Играть»
/// (<see cref="GameMode"/>) and «Разработчик» (<see cref="DeveloperMode.DeveloperMode"/>), the shared content library and
/// the automation script runner. Only one mode lives at a time: the game does not run behind the editors.
/// </summary>
public partial class AppRoot : Node
{
    public enum Screen
    {
        Menu = 0,
        Play = 1,
        Developer = 2,
    }

    private AutomationRunner _automation = null!;
    private ContentLibrary _library = null!;
    private StartMenu? _menu;
    private GameMode? _game;
    private DeveloperMode.DeveloperMode? _developer;
    private ContentItem? _pendingTest;
    private string _mapId = RuntimeFactory.DefaultMapId;
    private string _squadUnit = RuntimeFactory.DefaultUnitId;

    public Screen Current { get; private set; }

    public override void _Notification(int what)
    {
        if (what == NotificationWMCloseRequest)
        {
            AppExit.Quit(GetTree()); // the window's close button: same clean exit as «Выход»
        }
    }

    public override void _Ready()
    {
        GetTree().AutoAcceptQuit = false;
        GD.Print(BuildInfo.Full);
        if (!BuildInfo.VersionsMatch)
        {
            GD.PushWarning($"project.godot version {BuildInfo.ProjectVersion} differs from assembly version {BuildInfo.Version}");
        }

        InputBindings.Register();
        _automation = new AutomationRunner { Name = "Automation", StepRequested = RunAutomationStep };
        AddChild(_automation);
        _library = ContentService.Load(_automation.Get("user-root"));

        switch (_automation.Get("start"))
        {
            case "play":
                StartGame();
                break;
            case "dev":
                OpenDeveloper();
                break;
            default:
                ShowMenu();
                break;
        }
    }

    // ------------------------------------------------------------------ screens

    private void ShowMenu()
    {
        CloseModes();
        _menu = new StartMenu
        {
            Name = "StartMenu",
            Footer = $"Пользовательский контент: {_library.User?.Root}" + (_library.Problems.Count > 0 ? $"\nНе загружено файлов: {_library.Problems.Count} (подробности — в режиме разработчика)" : string.Empty),
            Maps = PlayableMaps(),
            SelectedMap = _mapId,
            Units = PlayableUnits(),
            SelectedUnit = _squadUnit,
        };
        _menu.PlayPressed += () => CallDeferred(MethodName.StartGame);
        _menu.DeveloperPressed += () => CallDeferred(MethodName.OpenDeveloper);
        _menu.QuitPressed += () => AppExit.Quit(GetTree());
        AddChild(_menu);
        Current = Screen.Menu;
    }

    private System.Collections.Generic.List<(string Id, string Name)> PlayableMaps()
    {
        var list = new System.Collections.Generic.List<(string Id, string Name)>();
        foreach (var map in _library.Items(HexTactics.Content.Registry.ContentTypeRegistry.Map))
        {
            if (!map.HasErrors)
            {
                list.Add((map.Id, map.IsBuiltIn ? map.Name : $"{map.Name} (своя)"));
            }
        }

        return list;
    }

    private System.Collections.Generic.List<(string Id, string Name)> PlayableUnits()
    {
        var list = new System.Collections.Generic.List<(string Id, string Name)>();
        foreach (var unit in _library.Items(HexTactics.Content.Registry.ContentTypeRegistry.Unit))
        {
            if (!unit.HasErrors && unit.Data is HexTactics.Content.Kinds.UnitData { Side: "player" })
            {
                list.Add((unit.Id, unit.IsBuiltIn ? $"{unit.Name} × 5" : $"{unit.Name} (свой) × 5"));
            }
        }

        return list;
    }

    private void StartGame()
    {
        if (_menu?.SelectedMap is { } chosen && _library.Find(HexTactics.Content.Registry.ContentTypeRegistry.Map, chosen) is not null)
        {
            _mapId = chosen;
        }

        if (_menu?.SelectedUnit is { } unitChoice && _library.Find(HexTactics.Content.Registry.ContentTypeRegistry.Unit, unitChoice) is not null)
        {
            _squadUnit = unitChoice;
        }

        CloseModes();
        var squad = new[] { _squadUnit, _squadUnit, _squadUnit, _squadUnit, _squadUnit };
        var content = RuntimeFactory.BuildGame(_library, _library.Find(HexTactics.Content.Registry.ContentTypeRegistry.Map, _mapId) is not null ? _mapId : RuntimeFactory.DefaultMapId, squad: squad);
        foreach (var issue in content.Issues)
        {
            GD.PushWarning($"[content] {issue}");
        }

        _game = new GameMode { Name = "Game", Automation = _automation };
        _game.Configure(content, content.Map.Name);
        _game.ExitRequested += () => CallDeferred(MethodName.ShowMenu);
        AddChild(_game);
        Current = Screen.Play;
    }

    private void OpenDeveloper()
    {
        CloseModes();
        _developer = new DeveloperMode.DeveloperMode { Name = "Developer" };
        _developer.Configure(_library);
        _developer.ExitRequested += () => CallDeferred(MethodName.ShowMenu);
        _developer.TestRequested += item =>
        {
            _pendingTest = item;
            CallDeferred(MethodName.StartTest);
        };
        AddChild(_developer);
        Current = Screen.Developer;
    }

    /// <summary>
    /// «Тест карты» (spec part 4 §4.4): the developer mode is taken out of the tree (kept intact, not running), a
    /// temporary copy is played in the game mode with the same simulation; leaving the test brings the editor back as it
    /// was. Nothing of the test match is written into the map.
    /// </summary>
    private void StartTest()
    {
        if (_pendingTest is not { } item || _developer is null)
        {
            return;
        }

        _pendingTest = null;
        ModalDialog.Open?.Close();
        RemoveChild(_developer);
        var content = item.Kind.Id switch
        {
            // Unit test: the prototype map, the edited unit is operative «Альфа», the others are the default unit.
            HexTactics.Content.Registry.ContentTypeRegistry.Unit => RuntimeFactory.BuildGame(_library, _library.Find(HexTactics.Content.Registry.ContentTypeRegistry.Map, RuntimeFactory.DefaultMapId)!, squad: new[] { item.Id }, extraUnits: new[] { item }),
            // Skill test: the prototype map, every operative has the tested skill first.
            HexTactics.Content.Registry.ContentTypeRegistry.Skill => RuntimeFactory.BuildGame(_library, _library.Find(HexTactics.Content.Registry.ContentTypeRegistry.Map, RuntimeFactory.DefaultMapId)!, testSkill: item),
            HexTactics.Content.Registry.ContentTypeRegistry.Tile => RuntimeFactory.BuildGame(_library, _library.Find(HexTactics.Content.Registry.ContentTypeRegistry.Map, RuntimeFactory.DefaultMapId)!, testTile: item),
            _ => RuntimeFactory.BuildGame(_library, item),
        };
        _game = new GameMode { Name = "TestGame", Automation = _automation };
        _game.Configure(content, $"тест «{item.Name}»", isTest: true);
        _game.ExitRequested += () => CallDeferred(MethodName.EndTest);
        AddChild(_game);
        Current = Screen.Play;
    }

    private void EndTest()
    {
        if (_game is not null)
        {
            RemoveChild(_game);
            _game.QueueFree();
            _game = null;
        }

        if (_developer is not null)
        {
            AddChild(_developer);
            Current = Screen.Developer;
        }
    }

    private void CloseModes()
    {
        ModalDialog.Open?.Close();
        foreach (Node? node in new Node?[] { _menu, _game, _developer })
        {
            if (node is not null)
            {
                RemoveChild(node);
                node.QueueFree();
            }
        }

        _menu = null;
        _game = null;
        _developer = null;
    }

    // ------------------------------------------------------------------ automation

    /// <summary>
    /// Steps of the whole application: <c>ui:ID</c> (press a registered control like a user), <c>field:ID=value</c>,
    /// <c>key:ctrl+z</c>, <c>row:ID</c> (click a library row), <c>expect:screen|dialog|…</c>; other steps go to the game.
    /// </summary>
    private int RunAutomationStep(string step)
    {
        var parts = step.Split(':', 2);
        var arg = parts.Length > 1 ? parts[1] : string.Empty;

        // After an editor opens or closes (also a nested one), its old panels leave the tree only at the end of the frame; a
        // click in the same place must not reach them. Pointer steps wait a few frames for the new screen (v0.25.0).
        if (parts[0] is "ui" or "field" or "row")
        {
            var openEditor = _developer?.ActiveEditor;
            if (!ReferenceEquals(openEditor, _settledEditor))
            {
                _settledEditor = openEditor;
                _settleFrames = 4;
            }

            if (_settleFrames > 0)
            {
                _settleFrames--;
                return AutomationRunner.Repeat;
            }
        }

        switch (parts[0])
        {
            case "ui":
                {
                    if (AutomationIds.Find(arg) is not { } control)
                    {
                        if (TryReveal(arg))
                        {
                            return AutomationRunner.Repeat;
                        }

                        _automation.ReportCheck(false, $"ui:{arg} (control not found or hidden)");
                        return 1;
                    }

                    _revealedFor = null;

                    // A control inside a scrolled panel is scrolled into view first (like a user would), then clicked
                    // on the next frame when the layout has moved it.
                    if (_scrolledFor != arg && control.GetParent() is not null && FindScroll(control) is { } scroll)
                    {
                        scroll.EnsureControlVisible(control);
                        _scrolledFor = arg;
                        return AutomationRunner.Repeat;
                    }

                    _scrolledFor = null;
                    AutomationInput.Click(AutomationIds.ScreenCenter(control));
                    return 3;
                }

            case "field":
                {
                    var eq = arg.IndexOf('=');
                    var id = eq > 0 ? arg[..eq] : arg;
                    var value = eq > 0 ? arg[(eq + 1)..] : string.Empty;
                    if (AutomationIds.Find(id) is null && TryReveal(id))
                    {
                        return AutomationRunner.Repeat;
                    }

                    _revealedFor = null;
                    _automation.ReportCheck(SetField(id, value), $"field:{id}={value}");
                    return 2;
                }

            case "key":
                AutomationInput.Key(arg);
                return 2;
            case "row":
                {
                    var id = arg.StartsWith("name=", StringComparison.Ordinal) ? _developer?.IdByName(arg[5..]) : arg;
                    if (id is not null && _developer is { } developer)
                    {
                        // A row below the fold is scrolled into view first; the click comes a frame later at its new place (D-169).
                        var point = developer.RowScreenPoint(id, allowScroll: _rowScrolledFor != id, out var scrolled);
                        if (scrolled)
                        {
                            _rowScrolledFor = id;
                            return AutomationRunner.Repeat;
                        }

                        _rowScrolledFor = null;
                        if (point is { } p)
                        {
                            AutomationInput.Click(p);
                            return 3;
                        }
                    }

                    _automation.ReportCheck(false, $"row:{arg} (no such row)");
                    return 1;
                }

            case "expect":
                if (RunExpectation(arg))
                {
                    return 1;
                }

                break;
            case "wait":
                return int.Parse(arg, CultureInfo.InvariantCulture);
        }

        if (Current == Screen.Developer && _developer?.ActiveEditor is { } editor)
        {
            var handled = editor.RunAutomationStep(step, _automation);
            if (handled != -2)
            {
                return handled;
            }
        }

        if (_game is not null && Current == Screen.Play)
        {
            return _game.RunAutomationStep(step);
        }

        GD.PushWarning($"[automation] step '{step}' needs the game screen");
        _automation.ReportCheck(false, $"{step} (not on the game screen)");
        return 1;
    }

    private string? _scrolledFor;
    private string? _rowScrolledFor;
    private string? _revealedFor;
    private object? _settledEditor;
    private int _settleFrames;
    private int _revealFrames;

    private static ScrollContainer? FindScroll(Node node)
    {
        for (var n = node.GetParent(); n is not null; n = n.GetParent())
        {
            if (n is ScrollContainer scroll)
            {
                return scroll;
            }
        }

        return null;
    }

    /// <summary>
    /// A control in a collapsed section of the open editor: the editor opens that section (as a user would), the step runs
    /// again on the next frames. Once per step, so a missing control still fails.
    /// </summary>
    private bool TryReveal(string id)
    {
        if (_revealedFor == id)
        {
            return ++_revealFrames < 6; // the editor rebuilds its panel within a frame or two
        }

        if (_developer?.ActiveEditor is not { } editor || !editor.Reveal(id))
        {
            return false;
        }

        _revealedFor = id;
        _revealFrames = 0;
        return true;
    }

    private static bool SetField(string id, string value)
    {
        switch (AutomationIds.Find(id))
        {
            case LineEdit line:
                line.Text = value;
                line.EmitSignal(LineEdit.SignalName.TextChanged, value);
                if (line.HasMeta("hx_commit_on_submit"))
                {
                    line.EmitSignal(LineEdit.SignalName.TextSubmitted, value); // editor fields commit like Enter
                }

                return true;
            case TextEdit text:
                text.Text = value;
                text.EmitSignal(TextEdit.SignalName.TextChanged);
                return true;
            case SpinBox spin:
                spin.Value = double.Parse(value, CultureInfo.InvariantCulture);
                return true;
            case CheckButton toggle:
                toggle.ButtonPressed = value is "true" or "1" or "yes";
                return true;
            case CheckBox check:
                check.ButtonPressed = value is "true" or "1" or "yes";
                return true;
            case ColorPickerButton color:
                color.Color = Color.FromHtml(value);
                color.EmitSignal(ColorPickerButton.SignalName.ColorChanged, color.Color);
                return true;
            case OptionButton option:
                for (var i = 0; i < option.ItemCount; i++)
                {
                    if (option.GetItemText(i) == value || option.GetItemMetadata(i).AsString() == value)
                    {
                        option.Select(i);
                        option.EmitSignal(OptionButton.SignalName.ItemSelected, i);
                        return true;
                    }
                }

                return false;
            default:
                return false;
        }
    }

    /// <summary>Application-level checks; returns false for checks of the game screen (handled by the game).</summary>
    private bool RunExpectation(string arg)
    {
        var a = arg.Split(':');
        string actual;
        bool ok;
        switch (a[0])
        {
            case "screen":
                actual = Current switch { Screen.Play => "play", Screen.Developer => "dev", _ => "menu" };
                ok = actual == a[1];
                break;
            case "dialog":
                actual = ModalDialog.Open?.Title ?? "none";
                ok = a[1] == "none" ? actual == "none" : actual.StartsWith(a[1], StringComparison.Ordinal);
                break;
            case "category":
                actual = _developer?.Category ?? "none";
                ok = actual == a[1];
                break;
            case "on-screen":
                {
                    // expect:on-screen:<automation id>:yes|no — the control is visible and lies wholly inside the window
                    var c = AutomationIds.Find(a[1]);
                    var view = GetViewport().GetVisibleRect();
                    if (c is null)
                    {
                        actual = "no";
                    }
                    else
                    {
                        var rect = c.GetGlobalRect();
                        var t = c.GetCanvasTransform();
                        var screen = new Rect2(t * rect.Position, t.BasisXform(rect.Size));
                        actual = view.Encloses(screen) ? "yes" : $"no ({screen.Position.X:0},{screen.Position.Y:0} {screen.Size.X:0}×{screen.Size.Y:0} in {view.Size.X:0}×{view.Size.Y:0})";
                    }

                    ok = actual == a[2];
                    break;
                }

            case "control":
                // expect:control:<automation id>:yes|no — the control exists and is visible
                actual = AutomationIds.Find(a[1]) is null ? "no" : "yes";
                ok = actual == a[2];
                break;
            case "rows":
                actual = _developer?.RowCount.ToString(CultureInfo.InvariantCulture) ?? "none";
                ok = actual == a[1];
                break;
            case "selected":
                {
                    var id = _developer?.SelectedId;
                    actual = id is null ? "none" : _library.Find(_developer!.Category, id) is { } item ? $"{item.Id}|{item.Name}" : id;
                    ok = a[1] == "none" ? id is null : actual.Split('|').AsSpan().Contains(a[1]);
                    break;
                }

            case "item":
                {
                    // expect:item:KIND:NAME:yes|no — an object with this name exists in the library
                    var found = false;
                    foreach (var item in _library.Items(a[1]))
                    {
                        found |= item.Name == a[2];
                    }

                    actual = found ? "yes" : "no";
                    ok = actual == a[3];
                    break;
                }

            case "editor":
                actual = Current == Screen.Developer ? _developer?.ActiveEditor?.Kind ?? "none" : "none";
                ok = actual == a[1];
                break;
            case "undo":
                actual = _developer?.UndoCount.ToString(CultureInfo.InvariantCulture) ?? "none";
                ok = actual == a[1];
                break;
            case "import":
                {
                    // expect:import:TEXT — the summary of the last import preview contains TEXT
                    actual = _developer?.LastImportSummary ?? "none";
                    ok = actual.Contains(arg[(arg.IndexOf(':') + 1)..], StringComparison.Ordinal);
                    break;
                }

            case "message":
                {
                    // expect:message:TEXT — the newest developer message contains TEXT
                    var messages = _developer?.Messages;
                    actual = messages is { Count: > 0 } ? messages[0].Text : "none";
                    ok = actual.Contains(arg[(arg.IndexOf(':') + 1)..], StringComparison.Ordinal);
                    break;
                }

            default:
                return false;
        }

        _automation.ReportCheck(ok, $"expect:{arg} (actual {actual})");
        return true;
    }
}
