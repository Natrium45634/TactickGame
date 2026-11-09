using System;
using System.Collections.Generic;
using Godot;
using HexTactics.Core.Hex;

namespace HexTactics.Client.Presentation;

/// <summary>One route to draw: its visits, a short label at the start and how it is shown.</summary>
public sealed record RouteDrawing(string Id, string Label, IReadOnlyList<HexCoord> Cells, bool Enabled, bool Selected, bool IsDraft = false);

/// <summary>
/// The red lines of enemy routes (spec part 7 §4.1, §4.2 p.10): a polyline through the centres of the visits with arrowheads
/// along it (the order of the walk) and the route's number at its start. Drawn without the depth test, i.e. over tall models;
/// different routes are shifted a little sideways so shared parts stay readable. It only shows the route data — it is never the
/// source of a route. Distinct from the red skill arrow of part 3: thinner, with chevrons and a number.
/// </summary>
public partial class RouteLayer : Node3D
{
    private const int Priority = 18; // under the order arrows (20+): the player's own orders stay on top
    private static readonly Color Line = new(0.86f, 0.12f, 0.10f, 0.92f);
    private static readonly Color LineSelected = new(1f, 0.28f, 0.18f, 1f);
    private static readonly Color LineOff = new(0.55f, 0.25f, 0.24f, 0.55f);
    private static readonly Color LineDraft = new(1f, 0.55f, 0.25f, 0.95f);

    private MeshInstance3D? _mesh;
    private Node3D? _labels;

    public override void _Ready()
    {
        _mesh = new MeshInstance3D
        {
            Name = "Lines",
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D
            {
                NoDepthTest = true,
                VertexColorUseAsAlbedo = true,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
                RenderPriority = Priority,
            },
        };
        AddChild(_mesh);
        _labels = new Node3D { Name = "Labels" };
        AddChild(_labels);
    }

    /// <summary>Redraws every route; <paramref name="height"/> gives the top of the tile at a hex.</summary>
    public void Show(IReadOnlyList<RouteDrawing> routes, HexLayout layout, Func<HexCoord, float> height)
    {
        if (_mesh is null || _labels is null)
        {
            return;
        }

        foreach (var child in _labels.GetChildren())
        {
            child.QueueFree();
        }

        var vertices = new List<(Vector3, Color)>();
        var size = (float)layout.Size;
        for (var i = 0; i < routes.Count; i++)
        {
            var route = routes[i];
            if (route.Cells.Count == 0)
            {
                continue;
            }

            var color = route.IsDraft ? LineDraft : !route.Enabled ? LineOff : route.Selected ? LineSelected : Line;
            var width = size * (route.Selected || route.IsDraft ? 0.11f : 0.075f);
            var shift = route.IsDraft ? 0f : (i - ((routes.Count - 1) * 0.5f)) * size * 0.09f;
            var points = new List<Vector3>(route.Cells.Count);
            foreach (var c in route.Cells)
            {
                var p = layout.HexToPlane(c);
                points.Add(new Vector3((float)p.X, height(c) + 0.03f, (float)p.Z));
            }

            for (var k = 1; k < points.Count; k++)
            {
                AddSegment(vertices, points[k - 1], points[k], width, shift, color, size);
            }

            if (points.Count == 1)
            {
                AddSegment(vertices, points[0] + new Vector3(-size * 0.15f, 0, 0), points[0] + new Vector3(size * 0.15f, 0, 0), width, 0, color, 0);
            }

            var label = new Label3D
            {
                Text = route.Label,
                FontSize = 56,
                PixelSize = 0.0075f,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                NoDepthTest = true,
                Modulate = VisualConfig.ToRenderColor(color with { A = 1f }),
                OutlineModulate = new Color(0.08f, 0.02f, 0.02f, 1f),
                OutlineSize = 10,
                RenderPriority = Priority + 2,
                OutlineRenderPriority = Priority + 1,
                Position = points[0] + new Vector3(0, 0.32f, 0),
            };
            _labels.AddChild(label);
        }

        var mesh = new ImmediateMesh();
        if (vertices.Count > 0)
        {
            mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
            foreach (var (v, c) in vertices)
            {
                mesh.SurfaceSetColor(c);
                mesh.SurfaceAddVertex(v);
            }

            mesh.SurfaceEnd();
        }

        _mesh.Mesh = mesh;
    }

    /// <summary>A band from <paramref name="a"/> to <paramref name="b"/>, shifted sideways, with a chevron in its middle.</summary>
    private static void AddSegment(List<(Vector3, Color)> output, Vector3 a, Vector3 b, float width, float shift, Color color, float size)
    {
        var d = new Vector2(b.X - a.X, b.Z - a.Z);
        var len = d.Length();
        if (len < 1e-4f)
        {
            return;
        }

        var u = d / len;
        var n = new Vector2(-u.Y, u.X);
        var off = new Vector3(n.X * shift, 0, n.Y * shift);
        a += off;
        b += off;
        var half = width * 0.5f;
        var side = new Vector3(n.X * half, 0, n.Y * half);
        var c = VisualConfig.ToRenderColor(color);
        foreach (var v in new[] { a + side, b + side, b - side, a + side, b - side, a - side })
        {
            output.Add((v, c));
        }

        if (size <= 0)
        {
            return;
        }

        // Chevron (arrowhead) in the middle of the step: the direction of the walk.
        var mid = (a + b) * 0.5f;
        var along = new Vector3(u.X, 0, u.Y);
        var across = new Vector3(n.X, 0, n.Y);
        var head = size * 0.2f;
        var tip = mid + (along * head * 0.6f);
        var back = mid - (along * head * 0.6f);
        foreach (var v in new[] { tip, back + (across * head * 0.7f), back - (across * head * 0.7f) })
        {
            output.Add((v, c));
        }
    }
}
