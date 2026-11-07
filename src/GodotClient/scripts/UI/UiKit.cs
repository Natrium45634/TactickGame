using System;
using System.Collections.Generic;
using Godot;

namespace HexTactics.Client.UI;

/// <summary>Visual weight of a button.</summary>
public enum ButtonKind
{
    Normal = 0,

    /// <summary>The main action of a screen (blue).</summary>
    Primary = 1,

    /// <summary>Destructive action (red), e.g. «Удалить гексы».</summary>
    Danger = 2,

    /// <summary>Low-emphasis action (transparent).</summary>
    Flat = 3,
}

/// <summary>
/// Controls addressable by automation scripts (<c>ui:&lt;id&gt;</c> presses the real control with injected mouse events,
/// <c>field:&lt;id&gt;=…</c> edits it). Registration is by stable id, so scenarios do not depend on layout.
/// </summary>
public static class AutomationIds
{
    private static readonly Dictionary<string, WeakReference<Control>> Controls = new(StringComparer.Ordinal);

    public static T Register<T>(T control, string id)
        where T : Control
    {
        Controls[id] = new WeakReference<Control>(control);
        control.SetMeta("hx_id", id);
        return control;
    }

    /// <summary>The control registered under <paramref name="id"/> if it still exists and is visible on screen.</summary>
    public static Control? Find(string id) =>
        Controls.TryGetValue(id, out var weak) && weak.TryGetTarget(out var c) && GodotObject.IsInstanceValid(c) && c.IsInsideTree() && c.IsVisibleInTree() ? c : null;

    /// <summary>Centre of a control in viewport pixels (layer transforms / UI scaling included).</summary>
    public static Vector2 ScreenCenter(Control control)
    {
        var rect = control.GetGlobalRect();
        return control.GetCanvasTransform() * (rect.Position + (rect.Size * 0.5f));
    }
}

/// <summary>Shared look of the start menu and the developer mode (minimal dark UI, same palette as the game HUD).</summary>
public static class UiKit
{
    public static readonly Color Background = new(0.141f, 0.149f, 0.169f);
    public static readonly Color PanelColor = new(0.09f, 0.095f, 0.11f, 0.94f);
    public static readonly Color PanelLight = new(0.13f, 0.14f, 0.16f, 0.96f);
    public static readonly Color Text = new(0.88f, 0.90f, 0.93f);
    public static readonly Color TextDim = new(0.60f, 0.64f, 0.70f);
    public static readonly Color Accent = new(0.30f, 0.50f, 0.80f);
    public static readonly Color DangerColor = new(0.80f, 0.20f, 0.18f);
    public static readonly Color Ok = new(0.45f, 0.78f, 0.50f);
    public static readonly Color Warning = new(0.95f, 0.78f, 0.38f);
    public static readonly Color Error = new(0.95f, 0.42f, 0.38f);

    private static Theme? _theme;

    /// <summary>UI scale for the current window height (same rule as the game HUD).</summary>
    public static float ScaleFor(Vector2 viewport) => Mathf.Clamp(Mathf.Snapped(viewport.Y / 1080f, 0.25f), 1f, 2f);

    public static StyleBoxFlat Box(Color bg, int radius = 6, int margin = 12, Color? border = null)
    {
        var box = new StyleBoxFlat
        {
            BgColor = bg,
            CornerRadiusBottomLeft = radius,
            CornerRadiusBottomRight = radius,
            CornerRadiusTopLeft = radius,
            CornerRadiusTopRight = radius,
            ContentMarginLeft = margin,
            ContentMarginRight = margin,
            ContentMarginTop = margin * 0.7f,
            ContentMarginBottom = margin * 0.7f,
        };
        if (border is { } b)
        {
            box.BorderColor = b;
            box.BorderWidthBottom = 1;
            box.BorderWidthTop = 1;
            box.BorderWidthLeft = 1;
            box.BorderWidthRight = 1;
        }

        return box;
    }

    public static Theme Theme => _theme ??= BuildTheme();

    public static PanelContainer Panel(Color? bg = null, int margin = 12)
    {
        var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop };
        panel.AddThemeStyleboxOverride("panel", Box(bg ?? PanelColor, 6, margin, new Color(1, 1, 1, 0.06f)));
        return panel;
    }

    public static Label Label(string text, int size = 14, bool dim = false, bool wrap = false)
    {
        var label = new Label { Text = text };
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", dim ? TextDim : Text);
        if (wrap)
        {
            label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        }

        return label;
    }

    public static Label Header(string text) => Label(text.ToUpperInvariant(), 11, dim: true);

    public static Button Button(string text, Action? pressed = null, ButtonKind kind = ButtonKind.Normal, string? id = null, string? tooltip = null, int fontSize = 13)
    {
        var button = new Button { Text = text, FocusMode = Control.FocusModeEnum.None, TooltipText = tooltip ?? string.Empty };
        button.AddThemeFontSizeOverride("font_size", fontSize);
        if (kind != ButtonKind.Normal)
        {
            var (bg, hover) = kind switch
            {
                ButtonKind.Primary => (Accent, Accent.Lightened(0.15f)),
                ButtonKind.Danger => (DangerColor, DangerColor.Lightened(0.15f)),
                _ => (new Color(0, 0, 0, 0), new Color(1, 1, 1, 0.08f)),
            };
            var normal = Box(bg, 4, 10);
            var hoverBox = Box(hover, 4, 10);
            var pressedBox = Box(hover.Lightened(0.1f), 4, 10, new Color(1, 1, 1, 0.85f));
            button.AddThemeStyleboxOverride("normal", normal);
            button.AddThemeStyleboxOverride("hover", hoverBox);
            button.AddThemeStyleboxOverride("pressed", pressedBox);
            button.AddThemeStyleboxOverride("hover_pressed", pressedBox);
            button.AddThemeColorOverride("font_color", new Color(0.98f, 0.98f, 0.99f));
        }

        if (pressed is not null)
        {
            button.Pressed += pressed;
        }

        if (id is not null)
        {
            AutomationIds.Register(button, id);
        }

        return button;
    }

    public static HBoxContainer Row(int separation = 6) => WithSeparation(new HBoxContainer(), separation);

    public static VBoxContainer Column(int separation = 6) => WithSeparation(new VBoxContainer(), separation);

    private static T WithSeparation<T>(T box, int separation)
        where T : BoxContainer
    {
        box.AddThemeConstantOverride("separation", separation);
        return box;
    }

    private static Theme BuildTheme()
    {
        var theme = new Theme { DefaultFontSize = 14 };
        var normal = Box(new Color(0.20f, 0.21f, 0.24f), 4, 10);
        var hover = Box(new Color(0.27f, 0.29f, 0.33f), 4, 10);
        var pressed = Box(new Color(0.30f, 0.42f, 0.60f), 4, 10);
        var disabled = Box(new Color(0.16f, 0.17f, 0.19f), 4, 10);
        theme.SetStylebox("normal", "Button", normal);
        theme.SetStylebox("hover", "Button", hover);
        theme.SetStylebox("pressed", "Button", pressed);
        theme.SetStylebox("hover_pressed", "Button", pressed);
        theme.SetStylebox("disabled", "Button", disabled);
        theme.SetStylebox("focus", "Button", new StyleBoxEmpty());
        theme.SetColor("font_color", "Button", Text);
        theme.SetColor("font_disabled_color", "Button", new Color(0.45f, 0.47f, 0.52f));
        theme.SetFontSize("font_size", "Button", 13);

        var field = Box(new Color(0.06f, 0.065f, 0.08f), 4, 8, new Color(1, 1, 1, 0.10f));
        theme.SetStylebox("normal", "LineEdit", field);
        theme.SetStylebox("focus", "LineEdit", Box(new Color(0.06f, 0.065f, 0.08f), 4, 8, Accent));
        theme.SetColor("font_color", "LineEdit", Text);
        theme.SetColor("font_placeholder_color", "LineEdit", new Color(0.45f, 0.48f, 0.54f));
        theme.SetStylebox("normal", "TextEdit", field);
        theme.SetStylebox("focus", "TextEdit", Box(new Color(0.06f, 0.065f, 0.08f), 4, 8, Accent));
        theme.SetColor("font_color", "TextEdit", Text);

        theme.SetStylebox("panel", "Tree", Box(new Color(0.07f, 0.075f, 0.09f), 4, 4, new Color(1, 1, 1, 0.06f)));
        theme.SetStylebox("selected", "Tree", Box(new Color(0.24f, 0.36f, 0.55f), 2, 2));
        theme.SetStylebox("selected_focus", "Tree", Box(new Color(0.26f, 0.40f, 0.62f), 2, 2));
        theme.SetStylebox("focus", "Tree", new StyleBoxEmpty());
        theme.SetStylebox("title_button_normal", "Tree", Box(new Color(0.13f, 0.14f, 0.16f), 0, 6));
        theme.SetStylebox("title_button_hover", "Tree", Box(new Color(0.16f, 0.17f, 0.20f), 0, 6));
        theme.SetStylebox("title_button_pressed", "Tree", Box(new Color(0.16f, 0.17f, 0.20f), 0, 6));
        theme.SetColor("font_color", "Tree", Text);
        theme.SetColor("title_button_color", "Tree", TextDim);
        theme.SetFontSize("font_size", "Tree", 13);
        theme.SetFontSize("title_button_font_size", "Tree", 12);
        theme.SetConstant("v_separation", "Tree", 6);

        theme.SetStylebox("normal", "OptionButton", normal);
        theme.SetStylebox("hover", "OptionButton", hover);
        theme.SetStylebox("pressed", "OptionButton", pressed);
        theme.SetStylebox("focus", "OptionButton", new StyleBoxEmpty());
        theme.SetColor("font_color", "OptionButton", Text);
        theme.SetStylebox("panel", "PopupMenu", Box(new Color(0.12f, 0.13f, 0.15f), 4, 6, new Color(1, 1, 1, 0.1f)));
        theme.SetColor("font_color", "PopupMenu", Text);
        theme.SetStylebox("panel", "TooltipPanel", Box(new Color(0.12f, 0.13f, 0.15f), 4, 8, new Color(1, 1, 1, 0.12f)));
        theme.SetColor("font_color", "TooltipLabel", Text);
        theme.SetColor("font_color", "Label", Text);
        theme.SetColor("font_color", "CheckBox", Text);
        theme.SetStylebox("separator", "HSeparator", Box(new Color(1, 1, 1, 0.08f), 0, 0));
        return theme;
    }
}
