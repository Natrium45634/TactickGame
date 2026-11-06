using System;
using System.Collections.Generic;
using Godot;
using HexTactics.Client.Presentation;
using HexTactics.Core.Roster;

namespace HexTactics.Client.UI;

/// <summary>
/// Bottom panel with the roster (spec part 2 §5.1): one card per operative showing its placement state. Pressing LMB on
/// an available card starts dragging it onto the map; a click on a placed operative's card selects that unit.
/// The panel never covers the board: the camera fit excludes <see cref="ScreenRect"/>.
/// </summary>
public partial class RosterBar : CanvasLayer
{
    public const float BarHeight = 96f;
    public const float CardWidth = 112f;

    private readonly List<(int EntryId, PanelContainer Card, Label State, StyleBoxFlat Style)> _cards = new();
    private PanelContainer _panel = null!;
    private HBoxContainer _row = null!;
    private RosterState _roster = null!;
    private int _version = -1;

    /// <summary>LMB pressed on a card: (entry id).</summary>
    public event Action<int>? CardPressed;

    /// <summary>LMB pressed on the "Команда" card (spec part 2 §6.1: a separate tool next to the roster).</summary>
    public event Action? CommandPressed;

    private PanelContainer _commandCard = null!;
    private StyleBoxFlat _commandStyle = null!;
    private Label _commandState = null!;
    private string _commandText = string.Empty;

    public float UiScale { get; set; } = 1f;

    /// <summary>Left edge in viewport pixels (right of the side HUD).</summary>
    public float LeftEdge { get; set; }

    /// <summary>Bar rectangle in viewport pixels.</summary>
    public Rect2 ScreenRect
    {
        get
        {
            var r = _panel.GetGlobalRect();
            return new Rect2(r.Position * UiScale, r.Size * UiScale);
        }
    }

    public void Initialize(RosterState roster)
    {
        _roster = roster;
    }

    public override void _Ready()
    {
        Layer = 10;
        _panel = new PanelContainer { Name = "RosterPanel", MouseFilter = Control.MouseFilterEnum.Stop };
        _panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.09f, 0.095f, 0.11f, 0.88f),
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6,
            ContentMarginLeft = 10,
            ContentMarginRight = 10,
            ContentMarginTop = 8,
            ContentMarginBottom = 8,
            BorderColor = new Color(1, 1, 1, 0.06f),
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
        });
        AddChild(_panel);

        var outer = new HBoxContainer();
        outer.AddThemeConstantOverride("separation", 10);
        _panel.AddChild(outer);

        var titleBox = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        titleBox.AddThemeConstantOverride("separation", 4);
        var title = new Label { Text = "РОСТЕР" };
        title.AddThemeFontSizeOverride("font_size", 11);
        title.AddThemeColorOverride("font_color", new Color(0.55f, 0.60f, 0.68f));
        titleBox.AddChild(title);
        var hint = new Label { Text = "ЛКМ на карточке\nи тянуть на\nзелёную клетку" };
        hint.AddThemeFontSizeOverride("font_size", 11);
        hint.AddThemeColorOverride("font_color", new Color(0.62f, 0.65f, 0.70f));
        titleBox.AddChild(hint);
        outer.AddChild(titleBox);

        _row = new HBoxContainer();
        _row.AddThemeConstantOverride("separation", 8);
        outer.AddChild(_row);

        foreach (var entry in _roster.Entries)
        {
            AddCard(entry);
        }

        var separator = new VSeparator();
        separator.AddThemeConstantOverride("separation", 10);
        outer.AddChild(separator);
        AddCommandCard(outer);
    }

    /// <summary>Centre of the "Команда" card in viewport pixels (automation).</summary>
    public Vector2 CommandCenter
    {
        get
        {
            var r = _commandCard.GetGlobalRect();
            return (r.Position + (r.Size * 0.5f)) * UiScale;
        }
    }

    /// <summary>Highlights the card while the command mode is on and shows its progress.</summary>
    public void SetCommandState(bool active, string text)
    {
        if (text == _commandText && _commandStyle.BorderWidthTop == (active ? 2 : 1))
        {
            return;
        }

        _commandText = text;
        _commandState.Text = text;
        _commandStyle.BgColor = active ? new Color(0.10f, 0.30f, 0.38f) : new Color(0.20f, 0.22f, 0.26f);
        _commandStyle.BorderColor = active ? VisualConfig.MemberRing : new Color(0.30f, 0.85f, 1.0f, 0.45f);
        var width = active ? 2 : 1;
        _commandStyle.BorderWidthTop = width;
        _commandStyle.BorderWidthBottom = width;
        _commandStyle.BorderWidthLeft = width;
        _commandStyle.BorderWidthRight = width;
    }

    private void AddCommandCard(HBoxContainer parent)
    {
        _commandStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.20f, 0.22f, 0.26f),
            CornerRadiusTopLeft = 5,
            CornerRadiusTopRight = 5,
            CornerRadiusBottomLeft = 5,
            CornerRadiusBottomRight = 5,
            ContentMarginLeft = 8,
            ContentMarginRight = 8,
            ContentMarginTop = 6,
            ContentMarginBottom = 6,
            BorderColor = new Color(0.30f, 0.85f, 1.0f, 0.45f),
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
        };
        _commandCard = new PanelContainer
        {
            Name = "CommandCard",
            CustomMinimumSize = new Vector2(CardWidth + 8, BarHeight - 16),
            MouseFilter = Control.MouseFilterEnum.Stop,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
        };
        _commandCard.AddThemeStyleboxOverride("panel", _commandStyle);

        var box = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        box.AddThemeConstantOverride("separation", 2);
        _commandCard.AddChild(box);

        // Team symbol: a larger captain disc followed by two smaller companions, in the command colour.
        var head = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        head.AddThemeConstantOverride("separation", 3);
        foreach (var size in new[] { 18, 12, 12 })
        {
            var dot = new Panel
            {
                CustomMinimumSize = new Vector2(size, size),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            dot.AddThemeStyleboxOverride("panel", new StyleBoxFlat
            {
                BgColor = VisualConfig.MemberRing,
                CornerRadiusTopLeft = size / 2,
                CornerRadiusTopRight = size / 2,
                CornerRadiusBottomLeft = size / 2,
                CornerRadiusBottomRight = size / 2,
            });
            head.AddChild(dot);
        }

        var name = new Label { Text = " Команда", MouseFilter = Control.MouseFilterEnum.Ignore };
        name.AddThemeFontSizeOverride("font_size", 15);
        name.AddThemeColorOverride("font_color", new Color(0.80f, 0.95f, 1.0f));
        head.AddChild(name);
        box.AddChild(head);

        _commandState = new Label { Text = "клик — собрать", MouseFilter = Control.MouseFilterEnum.Ignore };
        _commandState.AddThemeFontSizeOverride("font_size", 11);
        _commandState.AddThemeColorOverride("font_color", new Color(0.70f, 0.80f, 0.86f));
        box.AddChild(_commandState);

        _commandCard.GuiInput += e =>
        {
            if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
            {
                CommandPressed?.Invoke();
                _commandCard.AcceptEvent();
            }
        };

        parent.AddChild(_commandCard);
    }

    /// <summary>Extra control placed after the cards (the "Команда" button from v0.9.0).</summary>
    public void AddTrailing(Control control) => _row.AddChild(control);

    /// <summary>Centre of a card in viewport pixels (for automation that presses the real card).</summary>
    public Vector2 CardCenter(int entryId)
    {
        foreach (var (id, card, _, _) in _cards)
        {
            if (id == entryId)
            {
                var r = card.GetGlobalRect();
                return (r.Position + (r.Size * 0.5f)) * UiScale;
            }
        }

        return Vector2.Zero;
    }

    public bool IsOverUi(Vector2 viewportPosition) => ScreenRect.HasPoint(viewportPosition);

    public override void _Process(double delta)
    {
        Scale = new Vector2(UiScale, UiScale);
        var size = GetViewport().GetVisibleRect().Size / UiScale;
        var width = _panel.GetCombinedMinimumSize().X;
        var left = (LeftEdge / UiScale) + DebugHud.Margin;
        var free = size.X - left - DebugHud.Margin;
        _panel.Position = new Vector2(left + Mathf.Max(0, (free - width) * 0.5f), size.Y - BarHeight - DebugHud.Margin);

        if (_roster.Version != _version)
        {
            _version = _roster.Version;
            foreach (var (id, _, stateLabel, style) in _cards)
            {
                var entry = _roster.Find(id)!;
                stateLabel.Text = StateText(entry.State);
                var available = entry.State == RosterEntryState.AvailableInRoster;
                style.BgColor = entry.State switch
                {
                    RosterEntryState.AvailableInRoster => new Color(0.20f, 0.22f, 0.26f),
                    RosterEntryState.PlacementPreview => new Color(0.30f, 0.42f, 0.30f),
                    RosterEntryState.PlacedPendingFacing => new Color(0.36f, 0.32f, 0.18f),
                    RosterEntryState.Active => new Color(0.14f, 0.16f, 0.19f),
                    _ => new Color(0.12f, 0.12f, 0.13f),
                };
                style.BorderColor = available ? new Color(0.55f, 0.75f, 0.55f, 0.9f) : new Color(1, 1, 1, 0.08f);
            }
        }
    }

    public static string StateText(RosterEntryState state) => state switch
    {
        RosterEntryState.AvailableInRoster => "доступен — тяните",
        RosterEntryState.PlacementPreview => "выставляется…",
        RosterEntryState.PlacedPendingFacing => "ждёт взгляда",
        RosterEntryState.Active => "на поле",
        _ => "недоступен",
    };

    private void AddCard(RosterEntry entry)
    {
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.20f, 0.22f, 0.26f),
            CornerRadiusTopLeft = 5,
            CornerRadiusTopRight = 5,
            CornerRadiusBottomLeft = 5,
            CornerRadiusBottomRight = 5,
            ContentMarginLeft = 8,
            ContentMarginRight = 8,
            ContentMarginTop = 6,
            ContentMarginBottom = 6,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
        };
        var card = new PanelContainer
        {
            Name = $"Card{entry.Id}",
            CustomMinimumSize = new Vector2(CardWidth, BarHeight - 16),
            MouseFilter = Control.MouseFilterEnum.Stop,
            MouseDefaultCursorShape = Control.CursorShape.Drag,
        };
        card.AddThemeStyleboxOverride("panel", style);

        var box = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        box.AddThemeConstantOverride("separation", 2);
        card.AddChild(box);

        var head = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        head.AddThemeConstantOverride("separation", 6);
        var icon = new Panel { CustomMinimumSize = new Vector2(22, 22), MouseFilter = Control.MouseFilterEnum.Ignore };
        icon.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = VisualConfig.UnitPlayer,
            CornerRadiusTopLeft = 11,
            CornerRadiusTopRight = 11,
            CornerRadiusBottomLeft = 11,
            CornerRadiusBottomRight = 11,
            BorderColor = new Color(1, 1, 1, 0.85f),
            BorderWidthTop = 2,
            BorderWidthBottom = 2,
            BorderWidthLeft = 2,
            BorderWidthRight = 2,
        });
        head.AddChild(icon);
        var name = new Label { Text = entry.Name, MouseFilter = Control.MouseFilterEnum.Ignore };
        name.AddThemeFontSizeOverride("font_size", 15);
        name.AddThemeColorOverride("font_color", new Color(0.93f, 0.94f, 0.96f));
        head.AddChild(name);
        box.AddChild(head);

        var state = new Label { Text = StateText(entry.State), MouseFilter = Control.MouseFilterEnum.Ignore };
        state.AddThemeFontSizeOverride("font_size", 11);
        state.AddThemeColorOverride("font_color", new Color(0.70f, 0.74f, 0.80f));
        box.AddChild(state);

        var id = entry.Id;
        card.GuiInput += e =>
        {
            if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
            {
                CardPressed?.Invoke(id);
                card.AcceptEvent();
            }
        };

        _row.AddChild(card);
        _cards.Add((entry.Id, card, state, style));
    }
}
