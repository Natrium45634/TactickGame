using System;
using System.Collections.Generic;
using Godot;
using HexTactics.Core.Hex;
using HexTactics.Core.Units;

namespace HexTactics.Client.Presentation;

/// <summary>
/// Motion diagnostics (spec part 3 §8, toggled with F3): for every unit the technical <c>GraphPath</c> (outlined hexes),
/// the logical cell, the curve of the running step and the trail actually travelled. Thin lines on top of everything;
/// purely visual, reads the simulation only.
/// </summary>
public partial class MotionDebugView : Node3D
{
    private const int TrailLength = 150;

    private readonly Dictionary<UnitId, Queue<Vector3>> _trails = new();
    private ImmediateMesh _mesh = null!;
    private HexLayout _layout = null!;

    public bool Enabled { get; set; }

    public void Initialize(HexLayout layout)
    {
        _layout = layout;
        _mesh = new ImmediateMesh();
        AddChild(new MeshInstance3D
        {
            Name = "MotionDebugLines",
            Mesh = _mesh,
            MaterialOverride = new StandardMaterial3D
            {
                NoDepthTest = true,
                VertexColorUseAsAlbedo = true,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                RenderPriority = 35,
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
    }

    public void Sync(GameState state, Func<UnitState, Vector3> positionOf)
    {
        // Trails are recorded even while hidden, so switching on shows the recent past.
        var alive = new HashSet<UnitId>();
        foreach (var unit in state.Units)
        {
            alive.Add(unit.Id);
            if (!_trails.TryGetValue(unit.Id, out var trail))
            {
                trail = new Queue<Vector3>();
                _trails[unit.Id] = trail;
            }

            var p = positionOf(unit);
            if (trail.Count == 0 || trail.Count > 0 && LastOf(trail).DistanceTo(p) > 0.01f)
            {
                trail.Enqueue(p);
                while (trail.Count > TrailLength)
                {
                    trail.Dequeue();
                }
            }
        }

        foreach (var id in new List<UnitId>(_trails.Keys))
        {
            if (!alive.Contains(id))
            {
                _trails.Remove(id);
            }
        }

        _mesh.ClearSurfaces();
        Visible = Enabled;
        if (!Enabled)
        {
            return;
        }

        var lines = new List<(Vector3, Color)>();
        foreach (var unit in state.Units)
        {
            var m = unit.Motion;
            foreach (var cell in m.Path)
            {
                AddHexOutline(lines, cell, 0.80f, new Color(1f, 0.85f, 0.2f, 0.85f)); // GraphPath
            }

            AddHexOutline(lines, unit.Cell, 0.92f, new Color(1f, 1f, 1f, 0.95f)); // logical cell
            if (m.Plan is { } plan)
            {
                Vector3? prev = null;
                foreach (var q in plan.Curve.Sample(0.05))
                {
                    var v = (q * _layout.Size).ToWorld(0.09f);
                    if (prev is { } a)
                    {
                        lines.Add((a, new Color(1f, 0.2f, 0.9f)));
                        lines.Add((v, new Color(1f, 0.2f, 0.9f)));
                    }

                    prev = v;
                }
            }

            Vector3? last = null;
            foreach (var t in _trails[unit.Id])
            {
                var v = t + new Vector3(0, 0.08f, 0);
                if (last is { } a)
                {
                    lines.Add((a, new Color(1f, 0.5f, 0.1f)));
                    lines.Add((v, new Color(1f, 0.5f, 0.1f)));
                }

                last = v;
            }
        }

        if (lines.Count == 0)
        {
            return;
        }

        _mesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
        foreach (var (v, c) in lines)
        {
            _mesh.SurfaceSetColor(VisualConfig.ToRenderColor(c));
            _mesh.SurfaceAddVertex(v);
        }

        _mesh.SurfaceEnd();
    }

    private static Vector3 LastOf(Queue<Vector3> q)
    {
        var last = Vector3.Zero;
        foreach (var v in q)
        {
            last = v;
        }

        return last;
    }

    private void AddHexOutline(List<(Vector3, Color)> lines, HexCoord hex, float scale, Color color)
    {
        var c = _layout.HexToPlane(hex);
        for (var i = 0; i < 6; i++)
        {
            var a = c + (_layout.CornerOffset(i) * scale);
            var b = c + (_layout.CornerOffset((i + 1) % 6) * scale);
            lines.Add((a.ToWorld(0.07f), color));
            lines.Add((b.ToWorld(0.07f), color));
        }
    }
}
