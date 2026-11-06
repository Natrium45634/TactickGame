using System;
using Godot;
using HexTactics.Client.Presentation;
using HexTactics.Core.Hex;
using HexTactics.Interaction;

namespace HexTactics.Client.UI;

/// <summary>
/// Compact context menu next to a waypoint (spec part 3 §6.1 step 2): the skill icon, and — if a skill is planned
/// there — a button to remove it. It is UI above the order overlay; clicks on it never reach the map (no waypoint is
/// created by accident). Shown only while the state machine has a waypoint menu open.
/// </summary>
public partial class WaypointMenuPanel : CanvasLayer
{
    private PanelContainer _panel = null!;
    private Button _skill = null!;
    private Button _clear = null!;

    /// <summary>The skill icon was pressed.</summary>
    public event Action? SkillPressed;

    /// <summary>"Remove skill" was pressed.</summary>
    public event Action? ClearPressed;

    public float UiScale { get; set; } = 1f;

    public bool IsOpen => _panel.Visible;

    public override void _Ready()
    {
        Layer = 11;
        _panel = new PanelContainer { Name = "WaypointMenu", Visible = false, MouseFilter = Control.MouseFilterEnum.Stop };
        _panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.09f, 0.095f, 0.11f, 0.94f),
            BorderColor = new Color(0.92f, 0.20f, 0.18f, 0.9f),
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6,
            ContentMarginLeft = 6,
            ContentMarginRight = 6,
            ContentMarginTop = 4,
            ContentMarginBottom = 4,
        });
        AddChild(_panel);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 4);
        _panel.AddChild(row);

        _skill = MakeButton("✦ Линейный удар", new Color(0.78f, 0.16f, 0.14f), "Назначить «Линейный удар» в этой ТД: направление — красной стрелкой");
        _skill.Pressed += () => SkillPressed?.Invoke();
        row.AddChild(_skill);

        _clear = MakeButton("✕", new Color(0.25f, 0.26f, 0.30f), "Убрать навык этой ТД");
        _clear.Pressed += () => ClearPressed?.Invoke();
        row.AddChild(_clear);
    }

    /// <summary>Places the menu next to the waypoint (above-right of its marker) or hides it.</summary>
    public void Sync(WaypointMenuView? menu, Camera3D camera, HexLayout layout)
    {
        Scale = new Vector2(UiScale, UiScale);
        _panel.Visible = menu is not null;
        if (menu is null)
        {
            return;
        }

        _clear.Visible = menu.Skill is not null;
        _panel.ResetSize();
        var screen = camera.UnprojectPosition(layout.HexToPlane(menu.Cell).ToWorld(0f)) / UiScale;
        var size = _panel.GetCombinedMinimumSize();
        var view = GetViewport().GetVisibleRect().Size / UiScale;
        var x = Mathf.Clamp(screen.X + 22f, 4f, view.X - size.X - 4f);
        var y = Mathf.Clamp(screen.Y - size.Y - 26f, 4f, view.Y - size.Y - 4f);
        _panel.Position = new Vector2(x, y);
    }

    public bool IsOverUi(Vector2 viewportPosition)
    {
        if (!_panel.Visible)
        {
            return false;
        }

        var r = _panel.GetGlobalRect();
        return new Rect2(r.Position * UiScale, r.Size * UiScale).HasPoint(viewportPosition);
    }

    /// <summary>Centre of the skill icon in viewport pixels (automation presses the real button).</summary>
    public Vector2 SkillButtonCenter => Centre(_skill);

    public Vector2 ClearButtonCenter => Centre(_clear);

    private Vector2 Centre(Control c)
    {
        var r = c.GetGlobalRect();
        return (r.Position + (r.Size * 0.5f)) * UiScale;
    }

    private static Button MakeButton(string text, Color background, string tooltip)
    {
        var button = new Button { Text = text, FocusMode = Control.FocusModeEnum.None, TooltipText = tooltip };
        var normal = new StyleBoxFlat
        {
            BgColor = background,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
            ContentMarginLeft = 8,
            ContentMarginRight = 8,
            ContentMarginTop = 3,
            ContentMarginBottom = 3,
        };
        var hover = (StyleBoxFlat)normal.Duplicate();
        hover.BgColor = background.Lightened(0.15f);
        button.AddThemeStyleboxOverride("normal", normal);
        button.AddThemeStyleboxOverride("hover", hover);
        button.AddThemeStyleboxOverride("pressed", hover);
        button.AddThemeColorOverride("font_color", new Color(0.97f, 0.96f, 0.95f));
        button.AddThemeFontSizeOverride("font_size", 13);
        return button;
    }
}
