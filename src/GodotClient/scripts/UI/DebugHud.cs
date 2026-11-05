using System;
using Godot;

namespace HexTactics.Client.UI;

/// <summary>
/// Debug / prototype HUD: a compact panel on the left side of the window. The camera fit
/// excludes <see cref="ScreenRect"/>, so the panel never covers the board.
/// The whole layer is scaled with the window height to stay readable on large screens.
/// </summary>
public partial class DebugHud : CanvasLayer
{
    public const float PanelWidth = 272f;
    public const float Margin = 12f;

    private PanelContainer _panel = null!;
    private ScrollContainer _scroll = null!;
    private VBoxContainer _root = null!;
    private Theme _theme = null!;

    public event Action? LayoutChanged;

    public float UiScale { get; private set; } = 1f;

    /// <summary>Panel rectangle in viewport pixels (after UI scaling).</summary>
    public Rect2 ScreenRect
    {
        get
        {
            var r = _panel.GetGlobalRect();
            return new Rect2(r.Position * UiScale, r.Size * UiScale);
        }
    }

    public override void _Ready()
    {
        Layer = 10;
        _theme = BuildTheme();

        _panel = new PanelContainer
        {
            Name = "Panel",
            Theme = _theme,
            Position = new Vector2(Margin, Margin),
            CustomMinimumSize = new Vector2(PanelWidth, 0),
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        _panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.09f, 0.095f, 0.11f, 0.88f),
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6,
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            ContentMarginLeft = 14,
            ContentMarginRight = 14,
            ContentMarginTop = 12,
            ContentMarginBottom = 12,
            BorderColor = new Color(1, 1, 1, 0.06f),
            BorderWidthBottom = 1,
            BorderWidthTop = 1,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
        });
        AddChild(_panel);

        // The panel never grows beyond the window: long content scrolls instead.
        _scroll = new ScrollContainer
        {
            Name = "Scroll",
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
        };
        _panel.AddChild(_scroll);

        _root = new VBoxContainer { Name = "Root", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _root.AddThemeConstantOverride("separation", 6);
        _scroll.AddChild(_root);
        _panel.Resized += () => LayoutChanged?.Invoke();

        GetViewport().SizeChanged += UpdateScale;
        UpdateScale();
    }

    public Label AddTitle(string text)
    {
        var label = new Label { Text = text };
        label.AddThemeFontSizeOverride("font_size", 19);
        label.AddThemeColorOverride("font_color", new Color(0.93f, 0.94f, 0.96f));
        _root.AddChild(label);
        return label;
    }

    public Label AddLabel(string text, bool dim = false, int fontSize = 14)
    {
        var label = new Label
        {
            Text = text,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(PanelWidth - 28, 0),
        };
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", dim ? new Color(0.62f, 0.65f, 0.70f) : new Color(0.86f, 0.88f, 0.91f));
        _root.AddChild(label);
        return label;
    }

    public void AddSeparator()
    {
        var sep = new HSeparator();
        sep.AddThemeConstantOverride("separation", 6);
        _root.AddChild(sep);
    }

    public Label AddHeader(string text)
    {
        var label = new Label { Text = text.ToUpperInvariant() };
        label.AddThemeFontSizeOverride("font_size", 11);
        label.AddThemeColorOverride("font_color", new Color(0.55f, 0.60f, 0.68f));
        _root.AddChild(label);
        return label;
    }

    public void AddLegendItem(Color color, string text)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        var swatch = new ColorRect { Color = color, CustomMinimumSize = new Vector2(14, 14), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        row.AddChild(swatch);
        var label = new Label { Text = text };
        label.AddThemeFontSizeOverride("font_size", 13);
        label.AddThemeColorOverride("font_color", new Color(0.80f, 0.82f, 0.86f));
        row.AddChild(label);
        _root.AddChild(row);
    }

    public Button AddButton(string text, Action onPressed)
    {
        var button = new Button { Text = text, FocusMode = Control.FocusModeEnum.None };
        button.Pressed += onPressed;
        _root.AddChild(button);
        return button;
    }

    public HBoxContainer AddRow()
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        _root.AddChild(row);
        return row;
    }

    public override void _Process(double delta)
    {
        // Content-margins of the panel style (12 + 12) and the outer margins.
        var maxHeight = (GetViewport().GetVisibleRect().Size.Y / UiScale) - (2 * Margin) - 24f;
        var height = Mathf.Max(40f, Mathf.Min(_root.GetCombinedMinimumSize().Y, maxHeight));
        if (Mathf.Abs(_scroll.CustomMinimumSize.Y - height) > 0.5f)
        {
            _scroll.CustomMinimumSize = new Vector2(PanelWidth - 28f, height);
            _panel.ResetSize();
        }
    }

    /// <summary>True if the viewport position lies over the HUD panel (board input must ignore it).</summary>
    public bool IsOverUi(Vector2 viewportPosition) => ScreenRect.HasPoint(viewportPosition);

    private void UpdateScale()
    {
        var size = GetViewport().GetVisibleRect().Size;
        UiScale = Mathf.Clamp(Mathf.Snapped(size.Y / 1080f, 0.25f), 1f, 2f);
        Scale = new Vector2(UiScale, UiScale);
        LayoutChanged?.Invoke();
    }

    private static Theme BuildTheme()
    {
        var theme = new Theme { DefaultFontSize = 14 };
        var normal = new StyleBoxFlat
        {
            BgColor = new Color(0.20f, 0.21f, 0.24f),
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            ContentMarginLeft = 10,
            ContentMarginRight = 10,
            ContentMarginTop = 5,
            ContentMarginBottom = 5,
        };
        var hover = (StyleBoxFlat)normal.Duplicate();
        hover.BgColor = new Color(0.27f, 0.29f, 0.33f);
        var pressed = (StyleBoxFlat)normal.Duplicate();
        pressed.BgColor = new Color(0.30f, 0.42f, 0.60f);
        theme.SetStylebox("normal", "Button", normal);
        theme.SetStylebox("hover", "Button", hover);
        theme.SetStylebox("pressed", "Button", pressed);
        theme.SetStylebox("hover_pressed", "Button", pressed);
        theme.SetColor("font_color", "Button", new Color(0.88f, 0.90f, 0.93f));
        theme.SetFontSize("font_size", "Button", 13);
        return theme;
    }
}
