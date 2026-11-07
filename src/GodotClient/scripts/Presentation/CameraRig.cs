using System;
using Godot;

namespace HexTactics.Client.Presentation;

/// <summary>
/// Orthographic camera looking at the map plane with a fixed elevation angle
/// (90° = straight down; the prototype uses 60°). The camera size is adapted to the window so
/// that the whole board fits into the area not covered by UI. Zoom/pan can be added later:
/// picking always goes through <see cref="Camera"/> projection, so it stays correct.
/// </summary>
public partial class CameraRig : Node3D
{
    private const float Distance = 30f;

    private Aabb _focus;

    public Camera3D Camera { get; private set; } = null!;

    public float ElevationDegrees { get; set; } = VisualConfig.CameraElevationDegrees;

    public override void _Ready()
    {
        Camera = new Camera3D
        {
            Name = "Camera",
            Projection = Camera3D.ProjectionType.Orthogonal,
            KeepAspect = Camera3D.KeepAspectEnum.Height,
            Near = 0.1f,
            Far = 200f,
            Current = true,
        };
        AddChild(Camera);
    }

    public void SetFocus(Aabb focus) => _focus = focus;

    /// <summary>Fits <see cref="SetFocus"/> bounds into <paramref name="available"/> (viewport pixels).</summary>
    public void Fit(Rect2 available, Vector2 viewportSize)
    {
        if (viewportSize.X < 1 || viewportSize.Y < 1 || available.Size.X < 1 || available.Size.Y < 1)
        {
            return;
        }

        var e = Mathf.DegToRad(ElevationDegrees);
        var forward = new Vector3(0, -MathF.Sin(e), -MathF.Cos(e));
        var right = Vector3.Right;
        var up = new Vector3(0, MathF.Cos(e), -MathF.Sin(e));

        var c0 = _focus.GetCenter();
        float minR = float.MaxValue, maxR = float.MinValue, minU = float.MaxValue, maxU = float.MinValue;
        for (var i = 0; i < 8; i++)
        {
            var p = _focus.GetEndpoint(i) - c0;
            var pr = p.Dot(right);
            var pu = p.Dot(up);
            minR = MathF.Min(minR, pr);
            maxR = MathF.Max(maxR, pr);
            minU = MathF.Min(minU, pu);
            maxU = MathF.Max(maxU, pu);
        }

        // World units per screen pixel needed to fit the board into the free area.
        var k = MathF.Max((maxR - minR) / available.Size.X, (maxU - minU) / available.Size.Y) * (1f + VisualConfig.CameraFitMargin);
        Camera.Size = k * viewportSize.Y;

        var target = c0 + (right * ((minR + maxR) * 0.5f)) + (up * ((minU + maxU) * 0.5f));
        var availCenter = available.GetCenter();
        var dx = availCenter.X - (viewportSize.X * 0.5f);
        var dy = availCenter.Y - (viewportSize.Y * 0.5f);
        var screenCenterWorld = target - (right * (dx * k)) + (up * (dy * k));

        Camera.Rotation = new Vector3(-e, 0, 0);
        Camera.GlobalPosition = screenCenterWorld - (forward * Distance);
        _worldPerPixel = k;
    }

    private float _worldPerPixel = 0.02f;

    /// <summary>
    /// Zooms by <paramref name="factor"/> (&lt; 1 = closer) keeping the map point under <paramref name="screenPoint"/> in
    /// place (editor canvas, spec part 4 §3.2: wheel = zoom).
    /// </summary>
    public void Zoom(float factor, Vector2 screenPoint, Vector2 viewportSize)
    {
        var e = Mathf.DegToRad(ElevationDegrees);
        var right = Vector3.Right;
        var up = new Vector3(0, MathF.Cos(e), -MathF.Sin(e));
        var k = _worldPerPixel;
        var k2 = Mathf.Clamp(k * factor, 0.002f, 0.2f);
        var dx = screenPoint.X - (viewportSize.X * 0.5f);
        var dy = screenPoint.Y - (viewportSize.Y * 0.5f);
        // The point under the cursor: centre + right·dx·k − up·dy·k (screen y grows downwards). Keep it fixed.
        Camera.GlobalPosition += (right * (dx * (k - k2))) - (up * (dy * (k - k2)));
        _worldPerPixel = k2;
        Camera.Size = k2 * viewportSize.Y;
    }

    /// <summary>Moves the view by a mouse drag of <paramref name="pixelDelta"/> (the map follows the cursor).</summary>
    public void Pan(Vector2 pixelDelta)
    {
        var e = Mathf.DegToRad(ElevationDegrees);
        var right = Vector3.Right;
        var up = new Vector3(0, MathF.Cos(e), -MathF.Sin(e));
        Camera.GlobalPosition += (-right * (pixelDelta.X * _worldPerPixel)) + (up * (pixelDelta.Y * _worldPerPixel));
    }
}
