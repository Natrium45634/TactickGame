using System.Collections.Generic;
using Godot;
using HexTactics.Core.Hex;
using HexTactics.Core.Orders;
using HexTactics.Core.Pathfinding;
using HexTactics.Core.Units;
using HexTactics.Interaction;

namespace HexTactics.Client.Presentation;

/// <summary>
/// Visual layer 2: order arrows and waypoints (ТД). Shows committed chains (straight thick arrows
/// unit → TД1 → TД2 …, numbered markers) and the arrow currently being dragged. Reads the model and the
/// state machine only; the queue itself lives in <see cref="OrderQueue"/>, never in these meshes.
/// </summary>
public partial class OrdersView : Node3D
{
    /// <summary>Arrow body width on the map plane: half of the hex side (spec §5.1).</summary>
    public const float ArrowBodyWidth = VisualConfig.HexSize * 0.5f;
    public const float ArrowHeadWidth = VisualConfig.HexSize * 0.95f;
    public const float ArrowHeadLength = VisualConfig.HexSize * 0.62f;
    public const float WaypointPickRadius = 0.42f;

    private const float ArrowLift = 0.035f;

    // Render priorities of the top overlay (all without depth test): chains < drag arrow < route lines < markers.
    private const int LayerChainOther = 20;
    private const int LayerChainSelected = 21;
    private const int LayerDragArrow = 22;
    private const int LayerRouteLines = 23;
    private const int LayerMarkers = 24;
    private const float PathLift = 0.05f;
    private const float PathWidth = 0.085f;
    private const float MarkerRadius = 0.24f;

    private readonly List<(int Id, UnitId Unit, int Sequence, Vector3 Position)> _markers = new();
    private HexLayout _layout = null!;
    private Node3D _committed = null!;
    private MeshInstance3D _dragArrow = null!;
    private ImmediateMesh _dragMesh = null!;
    private StandardMaterial3D _dragMaterial = null!;
    private long _signature = long.MinValue;
    private ImmediateMesh _pathMesh = null!;
    private MeshInstance3D _pathLines = null!;
    private readonly Dictionary<(int From, int To), IReadOnlyList<HexCoord>?> _segmentCache = new();
    private readonly List<(Vector3 Vertex, Color Color)> _pathVertices = new();
    private long _segmentCacheVersion = -1;

    public void Initialize(HexLayout layout)
    {
        _layout = layout;
        _committed = new Node3D { Name = "Committed" };
        AddChild(_committed);

        _dragMesh = new ImmediateMesh();
        _dragMaterial = NewArrowMaterial(VisualConfig.ArrowValid, LayerDragArrow);
        _dragArrow = new MeshInstance3D
        {
            Name = "DragArrow",
            Mesh = _dragMesh,
            MaterialOverride = _dragMaterial,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_dragArrow);

        // Thin lines of the computed routes (around obstacles), separate from the straight order arrows.
        _pathMesh = new ImmediateMesh();
        _pathLines = new MeshInstance3D
        {
            Name = "PathLines",
            Mesh = _pathMesh,
            MaterialOverride = new StandardMaterial3D
            {
                VertexColorUseAsAlbedo = true,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
                NoDepthTest = true,
                RenderPriority = LayerRouteLines,
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_pathLines);
    }

    /// <summary>
    /// Draws the computed routes every frame: the remaining path of each unit from its current visual position
    /// through all its waypoints, and the preview path of an active drag. Routes come from the model
    /// (<see cref="UnitMotion.Path"/>) or from the path finder, never from the straight arrows.
    /// </summary>
    public void SyncPaths(GameState state, UnitId? selected, DragPreview? drag, System.Func<UnitId, PlaneVector?> visualPosition)
    {
        if (_segmentCacheVersion != state.TopologyVersion)
        {
            _segmentCache.Clear();
            _segmentCacheVersion = state.TopologyVersion;
        }

        _pathMesh.ClearSurfaces();
        _pathVertices.Clear();
        foreach (var unit in state.Units)
        {
            if (unit.Orders.IsEmpty)
            {
                continue;
            }

            var color = unit.Id == selected ? VisualConfig.PathSelected : VisualConfig.PathOther;
            var points = new List<PlaneVector> { visualPosition(unit.Id) ?? _layout.HexToPlane(unit.Cell) };
            var m = unit.Motion;
            var first = unit.Orders.Items[0];
            if (m.PathWaypointId == first.Id && m.Path.Count > 0)
            {
                foreach (var c in m.Path)
                {
                    points.Add(_layout.HexToPlane(c));
                }
            }
            else if (m.Status != MovementStatus.Blocked)
            {
                AppendSegment(points, state, unit.Id, unit.Motion.StepTo ?? unit.Cell, first.Cell, -unit.Id.Value, first.Id);
            }

            for (var i = 1; i < unit.Orders.Count; i++)
            {
                var a = unit.Orders.Items[i - 1];
                var b = unit.Orders.Items[i];
                if (points.Count == 0 || !AppendSegment(points, state, unit.Id, a.Cell, b.Cell, a.Id, b.Id))
                {
                    AddPolyline(_pathVertices, points, PathWidth, color);
                    points = new List<PlaneVector> { _layout.HexToPlane(b.Cell) };
                }
            }

            AddPolyline(_pathVertices, points, PathWidth, color);
        }

        if (drag?.Path is { Count: > 1 } preview)
        {
            var pts = new List<PlaneVector>();
            foreach (var c in preview)
            {
                pts.Add(_layout.HexToPlane(c));
            }

            AddPolyline(_pathVertices, pts, PathWidth * 1.25f, VisualConfig.PathPreview);
        }

        if (_pathVertices.Count == 0)
        {
            return;
        }

        _pathMesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
        foreach (var (vertex, color) in _pathVertices)
        {
            _pathMesh.SurfaceSetColor(color);
            _pathMesh.SurfaceAddVertex(vertex);
        }

        _pathMesh.SurfaceEnd();
    }

    /// <summary>Appends the cached shortest path a → b (without a) to <paramref name="points"/>.</summary>
    private bool AppendSegment(List<PlaneVector> points, GameState state, UnitId unit, HexCoord a, HexCoord b, int fromKey, int toKey)
    {
        if (!_segmentCache.TryGetValue((fromKey, toKey), out var path))
        {
            path = Pathfinder.FindPath(state.Grid, a, b, state.ObstaclesFor(unit));
            _segmentCache[(fromKey, toKey)] = path;
        }

        if (path is null)
        {
            return false;
        }

        if (points.Count == 0 || points[^1].DistanceTo(_layout.HexToPlane(a)) > 1e-6)
        {
            points.Add(_layout.HexToPlane(a));
        }

        for (var i = 1; i < path.Count; i++)
        {
            points.Add(_layout.HexToPlane(path[i]));
        }

        return true;
    }

    private static void AddPolyline(List<(Vector3, Color)> output, List<PlaneVector> points, float width, Color color)
    {
        var half = width * 0.5f;
        for (var i = 1; i < points.Count; i++)
        {
            var a = points[i - 1];
            var b = points[i];
            var d = b - a;
            var len = (float)d.Length;
            if (len < 1e-4f)
            {
                continue;
            }

            var px = (float)(-d.Z / len) * half;
            var pz = (float)(d.X / len) * half;
            var a0 = new Vector3((float)a.X + px, PathLift, (float)a.Z + pz);
            var a1 = new Vector3((float)a.X - px, PathLift, (float)a.Z - pz);
            var b0 = new Vector3((float)b.X + px, PathLift, (float)b.Z + pz);
            var b1 = new Vector3((float)b.X - px, PathLift, (float)b.Z - pz);
            foreach (var v in new[] { a0, b0, b1, a0, b1, a1 })
            {
                output.Add((v, VisualConfig.ToRenderColor(color)));
            }
        }
    }

    /// <summary>Rebuilds committed chains when any queue or the selection changed; redraws the drag arrow every frame.</summary>
    public void Sync(GameState state, UnitId? selected, int? selectedWaypoint, DragPreview? drag)
    {
        var signature = Signature(state, selected, selectedWaypoint);
        if (signature != _signature)
        {
            _signature = signature;
            RebuildCommitted(state, selected, selectedWaypoint);
        }

        DrawDrag(drag);
    }

    /// <summary>Waypoint marker under a plane point: prefers the selected unit's, then the highest sequence.</summary>
    public int? PickWaypoint(PlaneVector point, UnitId? selected)
    {
        int? best = null;
        var bestScore = double.MaxValue;
        foreach (var m in _markers)
        {
            var d = new Vector2(m.Position.X - (float)point.X, m.Position.Z - (float)point.Z).Length();
            if (d > WaypointPickRadius)
            {
                continue;
            }

            var score = (m.Unit == selected ? 0 : 1000) - (m.Sequence * 0.01) + d;
            if (score < bestScore)
            {
                bestScore = score;
                best = m.Id;
            }
        }

        return best;
    }

    private static long Signature(GameState state, UnitId? selected, int? selectedWaypoint)
    {
        long h = 17;
        foreach (var unit in state.Units)
        {
            h = (h * 31) + unit.Id.Value;
            h = (h * 31) + unit.Orders.Version;
            h = (h * 31) + unit.Cell.GetHashCode();
            h = (h * 31) + (unit.Motion.StepTo?.GetHashCode() ?? 0);
        }

        h = (h * 31) + (selected?.Value ?? -1);
        h = (h * 31) + (selectedWaypoint ?? -1);
        return h;
    }

    private void RebuildCommitted(GameState state, UnitId? selected, int? selectedWaypoint)
    {
        foreach (var child in _committed.GetChildren())
        {
            child.QueueFree();
        }

        _markers.Clear();
        foreach (var unit in state.Units)
        {
            if (unit.Orders.IsEmpty)
            {
                continue;
            }

            var isSelected = unit.Id == selected;
            var color = isSelected ? VisualConfig.ChainSelected : VisualConfig.ChainOther;
            var mesh = new ImmediateMesh();
            var tris = new List<Vector3>();
            var from = _layout.HexToPlane(unit.Motion.StepTo ?? unit.Cell);
            foreach (var wp in unit.Orders.Items)
            {
                var to = _layout.HexToPlane(wp.Cell);
                AddArrow(tris, from, to, startInset: 0.36f, endInset: MarkerRadius + 0.04f);
                from = to;
            }

            CommitTriangles(mesh, tris);
            _committed.AddChild(new MeshInstance3D
            {
                Name = $"Chain{unit.Id.Value}",
                Mesh = mesh,
                MaterialOverride = NewArrowMaterial(color, isSelected ? LayerChainSelected : LayerChainOther),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            });

            foreach (var wp in unit.Orders.Items)
            {
                AddMarker(unit, wp, isSelected, wp.Id == selectedWaypoint);
            }
        }
    }

    private void AddMarker(UnitState unit, Waypoint wp, bool unitSelected, bool waypointSelected)
    {
        var pos = _layout.HexToPlane(wp.Cell).ToWorld(0f);
        _markers.Add((wp.Id, unit.Id, wp.Sequence, pos));

        var marker = new Node3D { Name = $"Waypoint{wp.Id}", Position = pos };
        // White flat-top hex with a dark rim (readable on the light board); drawn above scene objects.
        marker.AddChild(MarkerDisc(MarkerRadius + 0.05f, VisualConfig.WaypointRim, LayerMarkers, 0.02f));
        marker.AddChild(MarkerDisc(MarkerRadius, unitSelected ? VisualConfig.WaypointSelectedUnit : VisualConfig.WaypointOtherUnit, LayerMarkers + 1, 0.03f));

        if (waypointSelected)
        {
            marker.AddChild(new MeshInstance3D
            {
                Mesh = new TorusMesh { InnerRadius = MarkerRadius + 0.05f, OuterRadius = MarkerRadius + 0.09f, Rings = 32, RingSegments = 6 },
                MaterialOverride = new StandardMaterial3D
                {
                    AlbedoColor = VisualConfig.WaypointSelectedRing,
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    NoDepthTest = true,
                    RenderPriority = LayerMarkers + 2,
                },
                Scale = new Vector3(1, 0.3f, 1),
                Position = new Vector3(0, 0.05f, 0),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            });
        }

        marker.AddChild(new Label3D
        {
            Text = wp.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            NoDepthTest = true,
            FontSize = 44,
            OutlineSize = 12,
            PixelSize = 0.006f,
            Modulate = unitSelected ? new Color(0.12f, 0.13f, 0.16f) : new Color(0.25f, 0.27f, 0.30f),
            OutlineModulate = new Color(1f, 1f, 1f, 0.9f),
            Position = new Vector3(0, 0.34f, 0),
            RenderPriority = LayerMarkers + 4,
            OutlineRenderPriority = LayerMarkers + 3,
        });
        _committed.AddChild(marker);
    }

    private static MeshInstance3D MarkerDisc(float radius, Color color, int priority, float height) => new()
    {
        Mesh = new CylinderMesh { TopRadius = radius, BottomRadius = radius, Height = 0.02f, RadialSegments = 6 },
        MaterialOverride = new StandardMaterial3D
        {
            AlbedoColor = color,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            NoDepthTest = true,
            RenderPriority = priority,
        },
        Position = new Vector3(0, height, 0),
        Rotation = new Vector3(0, Mathf.Pi / 6f, 0), // flat-top like the board hexes
        CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
    };

    private void DrawDrag(DragPreview? drag)
    {
        _dragMesh.ClearSurfaces();
        if (drag?.Pointer is not { } pointer)
        {
            return;
        }

        var color = drag.Status switch
        {
            DragTargetStatus.Valid => VisualConfig.ArrowValid,
            DragTargetStatus.RotateOnly => VisualConfig.ArrowRotate,
            DragTargetStatus.SourceDeadZone or DragTargetStatus.SourceHasOrders => VisualConfig.ArrowIdle,
            _ => VisualConfig.ArrowInvalid,
        };
        _dragMaterial.AlbedoColor = color;

        var length = (pointer - drag.SourceCenter).Length;
        if (length < 0.05)
        {
            return;
        }

        var tris = new List<Vector3>();
        AddArrow(tris, drag.SourceCenter, pointer, startInset: drag.Mode == OrderMode.Replace ? 0.36f : MarkerRadius, endInset: 0f);
        CommitTriangles(_dragMesh, tris);
    }

    /// <summary>
    /// Appends a flat straight arrow from <paramref name="a"/> to <paramref name="b"/> on the plane:
    /// body of <see cref="ArrowBodyWidth"/> and a head; the head shrinks for very short arrows.
    /// </summary>
    private static void AddArrow(List<Vector3> mesh, PlaneVector a, PlaneVector b, float startInset, float endInset)
    {
        var dir = b - a;
        var len = (float)dir.Length;
        if (len < 1e-4f)
        {
            return;
        }

        var ux = (float)(dir.X / len);
        var uz = (float)(dir.Z / len);
        var px = -uz;
        var pz = ux;

        var start = Mathf.Min(startInset, len * 0.5f);
        var end = len - Mathf.Min(endInset, len * 0.4f);
        if (end - start < 0.05f)
        {
            return;
        }

        var headLen = Mathf.Min(ArrowHeadLength, (end - start) * 0.6f);
        var headHalf = ArrowHeadWidth * 0.5f * (headLen / ArrowHeadLength);
        var bodyHalf = Mathf.Min(ArrowBodyWidth * 0.5f, headHalf * 0.8f);
        var neck = end - headLen;

        Vector3 P(float along, float side) => new((float)a.X + (ux * along) + (px * side), ArrowLift, (float)a.Z + (uz * along) + (pz * side));

        if (neck > start)
        {
            Quad(mesh, P(start, -bodyHalf), P(neck, -bodyHalf), P(neck, bodyHalf), P(start, bodyHalf));
        }

        mesh.Add(P(neck, -headHalf));
        mesh.Add(P(end, 0));
        mesh.Add(P(neck, headHalf));
    }

    private static void Quad(List<Vector3> mesh, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        mesh.Add(a);
        mesh.Add(b);
        mesh.Add(c);
        mesh.Add(a);
        mesh.Add(c);
        mesh.Add(d);
    }

    /// <summary>Writes triangles into an ImmediateMesh (skips empty surfaces, which Godot rejects).</summary>
    private static void CommitTriangles(ImmediateMesh mesh, List<Vector3> triangles)
    {
        if (triangles.Count == 0)
        {
            return;
        }

        mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
        foreach (var v in triangles)
        {
            mesh.SurfaceSetNormal(Vector3.Up);
            mesh.SurfaceAddVertex(v);
        }

        mesh.SurfaceEnd();
    }

    /// <summary>
    /// Arrow material: drawn WITHOUT depth test in the transparent pass, i.e. on top of every scene object
    /// (units, obstacles, future buildings) — spec part 2 §3.1. HUD (CanvasLayer) is still drawn above.
    /// </summary>
    private static StandardMaterial3D NewArrowMaterial(Color color, int priority) => new()
    {
        NoDepthTest = true,
        AlbedoColor = color,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
        RenderPriority = priority,
    };
}
