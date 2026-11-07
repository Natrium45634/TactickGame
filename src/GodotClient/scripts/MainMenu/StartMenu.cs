using System;
using System.Collections.Generic;
using Godot;
using HexTactics.Client.App;
using HexTactics.Client.UI;

namespace HexTactics.Client.MainMenu;

/// <summary>
/// Start screen (spec part 4 §2.1): two clearly labelled modes — «Играть» (the tactical game of parts 1–3) and
/// «Разработчик» (the content editors) — plus «Выход». Nothing of the game runs behind it.
/// </summary>
public partial class StartMenu : CanvasLayer
{
    private Control _root = null!;
    private PanelContainer _panel = null!;
    private Label _footer = null!;

    public event Action? PlayPressed;

    public event Action? DeveloperPressed;

    public event Action? QuitPressed;

    /// <summary>A line under the buttons (where user content is stored, load problems).</summary>
    public string Footer { get; set; } = string.Empty;

    /// <summary>Maps that can be played (id, name); the first is preselected unless <see cref="SelectedMap"/> is set.</summary>
    public IReadOnlyList<(string Id, string Name)> Maps { get; set; } = Array.Empty<(string, string)>();

    /// <summary>The map chosen for «Играть».</summary>
    public string? SelectedMap { get; set; }

    public override void _Ready()
    {
        Layer = 20;
        _root = new Control { Name = "Root", Theme = UiKit.Theme, MouseFilter = Control.MouseFilterEnum.Ignore };
        AddChild(_root);

        _panel = UiKit.Panel(UiKit.PanelColor, 36);
        _panel.Name = "MenuPanel";
        _root.AddChild(_panel);
        var column = UiKit.Column(14);
        _panel.AddChild(column);

        var title = UiKit.Label("HexTactics", 44);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        column.AddChild(title);
        var subtitle = UiKit.Label($"Тактика на гексах в реальном времени с паузой · v{BuildInfo.Version}", 14, dim: true);
        subtitle.HorizontalAlignment = HorizontalAlignment.Center;
        column.AddChild(subtitle);
        column.AddChild(new Control { CustomMinimumSize = new Vector2(0, 10) });

        column.AddChild(BigButton("▶  Играть", "Тактический бой: карта, юниты из ростера, приказы ТД/КТД, пауза.", ButtonKind.Primary, "menu.play", () => PlayPressed?.Invoke()));
        var mapRow = UiKit.Row(8);
        mapRow.AddChild(UiKit.Label("Карта:", 13, dim: true));
        var maps = AutomationIds.Register(new OptionButton { FocusMode = Control.FocusModeEnum.None, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }, "menu.map");
        for (var i = 0; i < Maps.Count; i++)
        {
            maps.AddItem(Maps[i].Name, i);
            maps.SetItemMetadata(i, Maps[i].Id);
            if (Maps[i].Id == SelectedMap)
            {
                maps.Select(i);
            }
        }

        SelectedMap ??= Maps.Count > 0 ? Maps[0].Id : null;
        maps.ItemSelected += index => SelectedMap = maps.GetItemMetadata((int)index).AsString();
        mapRow.AddChild(maps);
        column.AddChild(mapRow);
        column.AddChild(BigButton("✎  Разработчик", "Редакторы карт, юнитов, навыков и типов гексов; библиотека контента.", ButtonKind.Normal, "menu.dev", () => DeveloperPressed?.Invoke()));
        column.AddChild(new Control { CustomMinimumSize = new Vector2(0, 4) });
        var quit = UiKit.Button("Выход", () => QuitPressed?.Invoke(), ButtonKind.Flat, "menu.quit");
        quit.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
        column.AddChild(quit);

        _footer = UiKit.Label(Footer, 11, dim: true, wrap: true);
        _footer.HorizontalAlignment = HorizontalAlignment.Center;
        _footer.CustomMinimumSize = new Vector2(420, 0);
        column.AddChild(_footer);

        GetViewport().SizeChanged += Layout;
        Layout();
    }

    public override void _ExitTree() => GetViewport().SizeChanged -= Layout;

    private static Control BigButton(string text, string hint, ButtonKind kind, string id, Action pressed)
    {
        var box = UiKit.Column(4);
        var button = UiKit.Button(text, pressed, kind, id, hint, 22);
        button.CustomMinimumSize = new Vector2(420, 64);
        box.AddChild(button);
        var label = UiKit.Label(hint, 12, dim: true, wrap: true);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.CustomMinimumSize = new Vector2(420, 0);
        box.AddChild(label);
        return box;
    }

    private void Layout()
    {
        var viewport = GetViewport().GetVisibleRect().Size;
        var scale = UiKit.ScaleFor(viewport);
        _root.Scale = new Vector2(scale, scale);
        _root.Size = viewport / scale;
        _footer.Text = Footer;
        _panel.ResetSize();
        _panel.Position = ((_root.Size - _panel.GetCombinedMinimumSize()) * 0.5f).Floor();
    }
}
