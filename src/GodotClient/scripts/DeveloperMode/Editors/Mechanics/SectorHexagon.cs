using System;
using System.Globalization;
using Godot;
using HexTactics.Client.UI;
using HexTactics.Core.Combat;

namespace HexTactics.Client.DeveloperMode.Editors.Mechanics;

/// <summary>
/// The six sides of a defender as a flat-top hexagon (spec part 6 §9.5 «визуальный шестиугольник с шестью интерактивными
/// секторами»): the target in the centre looks up (its front), each outer sector shows the surprise chance of that side and is
/// tinted by it. A click selects a side. Only a view of the ruleset: the numbers come from the model, nothing is computed here.
/// Automation presses a side through an anchor registered as <c>&lt;prefix&gt;hex.&lt;n&gt;</c> at its centre.
/// </summary>
public partial class SectorHexagon : Control
{
    private static readonly Color Cold = new(0.22f, 0.24f, 0.28f);
    private static readonly Color Hot = new(0.85f, 0.38f, 0.26f);
    private readonly Control[] _anchors = new Control[6];
    private double[] _chances = new double[6];

    public SectorHexagon(string idPrefix)
    {
        CustomMinimumSize = new Vector2(0, 210);
        MouseFilter = MouseFilterEnum.Stop;
        TooltipText = "Стороны цели относительно её взгляда (вверх — спереди); щелчок выбирает сторону";
        for (var i = 0; i < 6; i++)
        {
            _anchors[i] = AutomationIds.Register(new Control { MouseFilter = MouseFilterEnum.Ignore, Size = new Vector2(2, 2) }, $"{idPrefix}hex.{i}");
            AddChild(_anchors[i]);
        }

        Resized += PlaceAnchors;
    }

    /// <summary>Raised with the side 0…5 (clockwise from the front) the user clicked.</summary>
    public event Action<int>? SectorPressed;

    /// <summary>The selected side or −1.</summary>
    public int Selected { get; private set; } = -1;

    public void Show(double[] chances, int selected)
    {
        _chances = chances.Length == 6 ? (double[])chances.Clone() : new double[6];
        Selected = selected is >= 0 and <= 5 ? selected : -1;
        QueueRedraw();
    }

    private Vector2 Centre => Size * 0.5f;

    private float Outer => Math.Max(20, (Math.Min(Size.X, Size.Y) * 0.5f) - 6);

    private float Inner => Outer * 0.40f;

    /// <summary>Screen angle of the middle of a side's edge: 0 — up (front), then clockwise every 60°.</summary>
    private static float Angle(int sector) => Mathf.DegToRad(-90 + (60 * sector));

    private static Vector2 Polar(Vector2 c, float radius, float angle) => c + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;

    private Vector2[] Sector(int i)
    {
        var a = Angle(i);
        var half = Mathf.DegToRad(30);
        return new[] { Polar(Centre, Inner, a - half), Polar(Centre, Outer, a - half), Polar(Centre, Outer, a + half), Polar(Centre, Inner, a + half) };
    }

    private void PlaceAnchors()
    {
        for (var i = 0; i < 6; i++)
        {
            _anchors[i].Position = Polar(Centre, (Inner + Outer) * 0.5f, Angle(i)) - new Vector2(1, 1);
        }

        QueueRedraw();
    }

    public override void _Draw()
    {
        var font = GetThemeDefaultFont();
        for (var i = 0; i < 6; i++)
        {
            var poly = Sector(i);
            var t = (float)Math.Clamp(_chances[i] / 100, 0, 1);
            DrawColoredPolygon(poly, Cold.Lerp(Hot, t));
            var outline = new[] { poly[0], poly[1], poly[2], poly[3], poly[0] };
            DrawPolyline(outline, i == Selected ? UiKit.Accent.Lightened(0.3f) : UiKit.Background, i == Selected ? 3 : 1.5f);
            var text = _chances[i].ToString("0.##", CultureInfo.InvariantCulture) + " %";
            var size = font.GetStringSize(text, HorizontalAlignment.Center, -1, 14);
            DrawString(font, Polar(Centre, (Inner + Outer) * 0.5f, Angle(i)) + new Vector2(-size.X * 0.5f, 5), text, HorizontalAlignment.Left, -1, 14, UiKit.Text);
        }

        // The defender: the inner hexagon with its facing towards the front side.
        var inner = new Vector2[6];
        for (var k = 0; k < 6; k++)
        {
            inner[k] = Polar(Centre, Inner - 3, Mathf.DegToRad(60 * k));
        }

        DrawColoredPolygon(inner, UiKit.PanelLight);
        var tip = Centre + new Vector2(0, -(Inner - 10));
        DrawColoredPolygon(new[] { tip, Centre + new Vector2(-9, 2), Centre + new Vector2(9, 2) }, UiKit.Text);
        var label = "цель";
        var ls = font.GetStringSize(label, HorizontalAlignment.Center, -1, 11);
        DrawString(font, Centre + new Vector2(-ls.X * 0.5f, 18), label, HorizontalAlignment.Left, -1, 11, UiKit.TextDim);
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click)
        {
            return;
        }

        var v = click.Position - Centre;
        if (v.Length() < Inner * 0.8f || v.Length() > Outer * 1.1f)
        {
            return;
        }

        // Up is the front; the side is the nearest of the six edge directions, clockwise.
        var degrees = Mathf.RadToDeg(Mathf.Atan2(v.Y, v.X)) + 90;
        var sector = ((int)Math.Round(degrees / 60) % 6 + 6) % 6;
        AcceptEvent();
        SectorPressed?.Invoke(sector);
    }
}
