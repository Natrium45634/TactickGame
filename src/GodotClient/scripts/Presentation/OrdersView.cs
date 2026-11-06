using System;
using System.Collections.Generic;
using Godot;
using HexTactics.Core.Hex;
using HexTactics.Core.Orders;
using HexTactics.Core.Pathfinding;
using HexTactics.Core.Units;
using HexTactics.Interaction;

namespace HexTactics.Client.Presentation;

/// <summary>
/// Visual layer 2 — the top overlay of orders. Everything here is drawn WITHOUT depth test, i.e. above units,
/// obstacles and any other scene object (spec part 2 §3.1):
/// <list type="bullet">
/// <item>confirmed chains: straight wide white arrows unit → ТД1 → ТД2 …, numbered white markers, the stored final
/// facing as a small white arrow at the edge of the last ТД;</item>
/// <item>the open draft: its arrows and amber-rimmed markers, the live arrow from the source to the cursor, the
/// facing-sector preview at the source edge;</item>
/// <item>thin route lines (computed paths) and the green preview path to the cursor.</item>
/// </list>
/// It only reads the model and the input state machine; the queue itself lives in <see cref="OrderQueue"/>.
/// </summary>
public partial class OrdersView : Node3D
{
    /// <summary>Arrow body width on the map plane: half of the hex side (spec part 2 §3.1).</summary>
    public const float ArrowBodyWidth = VisualConfig.HexSize * 0.5f;
    public const float ArrowHeadWidth = VisualConfig.HexSize * 0.95f;
    public const float ArrowHeadLength = VisualConfig.HexSize * 0.62f;
    public const float WaypointPickRadius = 0.42f;

    private const float ArrowLift = 0.035f;
    private const float PathLift = 0.05f;
    private const float PathWidth = 0.085f;
    private const float MarkerRadius = 0.24f;

    // Render priorities of the top overlay (all without depth test).
    private const int LayerChainOther = 20;
    private const int LayerChainSelected = 21;
    private const int LayerLiveArrow = 23;
    private const int LayerRouteLines = 24;
    private const int LayerFacingMarks = 25;
    private const int LayerMarkers = 26;

    private readonly List<(int Id, UnitId Unit, int Sequence, Vector3 Position)> _markers = new();
    private readonly Dictionary<(int From, int To), IReadOnlyList<HexCoord>?> _segmentCache = new();
    private readonly List<(Vector3 Vertex, Color Color)> _pathVertices = new();
    private readonly List<(Vector3 Vertex, Color Color)> _overlayVertices = new();
    private HexLayout _layout = null!;
    private Node3D _committed = null!;
    private Node3D _draftMarkers = null!;
    private ImmediateMesh _overlayMesh = null!;
    private ImmediateMesh _pathMesh = null!;
    private long _signature = long.MinValue;
    private string _draftSignature = string.Empty;
    private long _segmentCacheVersion = -1;
    private string _draftPathKey = string.Empty;
    private IReadOnlyList<IReadOnlyList<HexCoord>?> _draftPaths = Array.Empty<IReadOnlyList<HexCoord>?>();

    public void Initialize(HexLayout layout)
    {
        _layout = layout;
        _committed = new Node3D { Name = "Committed" };
        AddChild(_committed);
        _draftMarkers = new Node3D { Name = "DraftMarkers" };
        AddChild(_draftMarkers);

        _overlayMesh = new ImmediateMesh();
        AddChild(new MeshInstance3D
        {
            Name = "LiveOverlay",
            Mesh = _overlayMesh,
            MaterialOverride = VertexColorMaterial(LayerLiveArrow),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });

        _pathMesh = new ImmediateMesh();
        AddChild(new MeshInstance3D
        {
            Name = "PathLines",
            Mesh = _pathMesh,
            MaterialOverride = VertexColorMaterial(LayerRouteLines),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
    }

    /// <summary>Per-frame update.</summary>
    /// <param name="anchorOf">Command anchor of a unit (the hex its new orders start from).</param>
    /// <param name="visualPosition">Current interpolated plane position of a unit.</param>
    public void Sync(
        GameState state,
        UnitId? selected,
        int? selectedWaypoint,
        DraftView? draft,
        HexDirection? hoverTurn,
        Func<UnitId, HexCoord> anchorOf,
        Func<UnitId, PlaneVector?> visualPosition)
    {
        var signature = Signature(state, selected, selectedWaypoint, draft?.Unit);
        if (signature != _signature)
        {
            _signature = signature;
            RebuildCommitted(state, selected, selectedWaypoint, draft?.Unit, anchorOf);
        }

        RebuildDraftMarkers(draft);
        DrawOverlay(state, selected, draft, hoverTurn, anchorOf);
        DrawPaths(state, selected, draft, visualPosition);
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

    // ------------------------------------------------------------------ confirmed chains

    private static long Signature(GameState state, UnitId? selected, int? selectedWaypoint, UnitId? draftOwner)
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
        h = (h * 31) + (draftOwner?.Value ?? -1);
        return h;
    }

    private void RebuildCommitted(GameState state, UnitId? selected, int? selectedWaypoint, UnitId? draftOwner, Func<UnitId, HexCoord> anchorOf)
    {
        foreach (var child in _committed.GetChildren())
        {
            child.QueueFree();
        }

        _markers.Clear();
        foreach (var unit in state.Units)
        {
            var isSelected = unit.Id == selected;
            var ghost = unit.Id == draftOwner; // the old route stays visible but faded while it is being re-planned
            var anchor = anchorOf(unit.Id);
            if (unit.Orders.FinalFacing is { } pendingFacing)
            {
                var at = unit.Orders.Last?.Cell ?? anchor;
                var tris = new List<Vector3>();
                AddDart(tris, _layout.EdgeMidpoint(at, pendingFacing), pendingFacing, 0.14f);
                _committed.AddChild(TriangleMesh("FinalFacing", tris, ghost ? VisualConfig.ChainGhost : VisualConfig.FinalFacingMark, LayerFacingMarks));
            }

            if (unit.Orders.IsEmpty)
            {
                continue;
            }

            var chain = new List<Vector3>();
            var from = _layout.HexToPlane(anchor);
            foreach (var wp in unit.Orders.Items)
            {
                var to = _layout.HexToPlane(wp.Cell);
                AddArrow(chain, from, to, startInset: 0.36f, endInset: MarkerRadius + 0.04f);
                from = to;
            }

            var color = ghost ? VisualConfig.ChainGhost : isSelected ? VisualConfig.ChainSelected : VisualConfig.ChainOther;
            _committed.AddChild(TriangleMesh($"Chain{unit.Id.Value}", chain, color, isSelected ? LayerChainSelected : LayerChainOther));

            foreach (var wp in unit.Orders.Items)
            {
                var pos = _layout.HexToPlane(wp.Cell).ToWorld(0f);
                _markers.Add((wp.Id, unit.Id, wp.Sequence, pos));
                _committed.AddChild(Marker(
                    $"Waypoint{wp.Id}",
                    pos,
                    wp.Sequence,
                    isSelected ? VisualConfig.WaypointSelectedUnit : VisualConfig.WaypointOtherUnit,
                    VisualConfig.WaypointRim,
                    wp.Id == selectedWaypoint,
                    ghost));
            }
        }
    }

    // ------------------------------------------------------------------ draft

    private void RebuildDraftMarkers(DraftView? draft)
    {
        var signature = draft is null ? string.Empty : $"{draft.Unit}:{string.Join(';', draft.Waypoints)}";
        if (signature == _draftSignature)
        {
            return;
        }

        _draftSignature = signature;
        foreach (var child in _draftMarkers.GetChildren())
        {
            child.QueueFree();
        }

        if (draft is null)
        {
            return;
        }

        for (var i = 0; i < draft.Waypoints.Count; i++)
        {
            _draftMarkers.AddChild(Marker($"Draft{i + 1}", _layout.HexToPlane(draft.Waypoints[i]).ToWorld(0f), i + 1, VisualConfig.WaypointSelectedUnit, VisualConfig.DraftRim, false, false));
        }
    }

    /// <summary>Draft arrows, the live arrow to the cursor and the facing previews (rebuilt every frame).</summary>
    private void DrawOverlay(GameState state, UnitId? selected, DraftView? draft, HexDirection? hoverTurn, Func<UnitId, HexCoord> anchorOf)
    {
        _overlayMesh.ClearSurfaces();
        _overlayVertices.Clear();

        if (draft is not null)
        {
            var tris = new List<Vector3>();
            var from = _layout.HexToPlane(draft.Anchor);
            foreach (var wp in draft.Waypoints)
            {
                var to = _layout.HexToPlane(wp);
                AddArrow(tris, from, to, startInset: 0.36f, endInset: MarkerRadius + 0.04f);
                from = to;
            }

            AddColored(tris, VisualConfig.DraftChain);

            if (draft.Status == DraftTargetStatus.FacingSector && draft.SectorFacing is { } sector)
            {
                AddFacingPreview(draft.Source, sector);
            }
            else if (draft.Pointer is { } pointer && draft.Status != DraftTargetStatus.Neutral)
            {
                var live = new List<Vector3>();
                var startInset = draft.Waypoints.Count == 0 ? 0.36f : MarkerRadius;
                AddArrow(live, draft.SourceCenter, pointer, startInset, 0f);
                AddColored(live, draft.Status switch
                {
                    DraftTargetStatus.Valid => VisualConfig.ArrowValid,
                    DraftTargetStatus.AttackTarget => VisualConfig.ArrowAttack,
                    _ => VisualConfig.ArrowInvalid,
                });
            }
        }
        else if (selected is { } unit && hoverTurn is { } facing && state.FindUnit(unit) is not null)
        {
            AddFacingPreview(anchorOf(unit), facing);
        }

        Commit(_overlayMesh, _overlayVertices);
    }

    /// <summary>White mini-arrow at the edge of the hex in the previewed direction, with a dark outline.</summary>
    private void AddFacingPreview(HexCoord hex, HexDirection facing)
    {
        var edge = _layout.EdgeMidpoint(hex, facing);
        var outline = new List<Vector3>();
        AddDart(outline, edge, facing, 0.26f, lift: 0.002f);
        AddColored(outline, VisualConfig.FacingIndicatorOutline);
        var fill = new List<Vector3>();
        AddDart(fill, edge, facing, 0.21f, lift: 0.004f);
        AddColored(fill, VisualConfig.FacingPreview);
    }

    // ------------------------------------------------------------------ route lines

    private void DrawPaths(GameState state, UnitId? selected, DraftView? draft, Func<UnitId, PlaneVector?> visualPosition)
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
            if (unit.Orders.IsEmpty || unit.Id == draft?.Unit)
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
                if (!AppendSegment(points, state, unit.Id, a.Cell, b.Cell, a.Id, b.Id))
                {
                    AddPolyline(_pathVertices, points, PathWidth, color);
                    points = new List<PlaneVector> { _layout.HexToPlane(b.Cell) };
                }
            }

            AddPolyline(_pathVertices, points, PathWidth, color);
        }

        if (draft is not null)
        {
            // Planned route of the draft chain (unit → ТД1 → …), then the green preview to the cursor.
            var key = $"{draft.Unit}:{draft.Anchor}:{string.Join(';', draft.Waypoints)}:{state.TopologyVersion}";
            if (key != _draftPathKey)
            {
                _draftPathKey = key;
                _draftPaths = OrderDraftValidator.SegmentPaths(state, draft.Unit, draft.Anchor, draft.Waypoints);
            }

            foreach (var path in _draftPaths)
            {
                if (path is { Count: > 1 })
                {
                    AddPolyline(_pathVertices, ToPlane(path), PathWidth, VisualConfig.PathSelected);
                }
            }

            if (draft.Path is { Count: > 1 } preview)
            {
                AddPolyline(_pathVertices, ToPlane(preview), PathWidth * 1.25f, VisualConfig.PathPreview);
            }
        }

        Commit(_pathMesh, _pathVertices);
    }

    private List<PlaneVector> ToPlane(IReadOnlyList<HexCoord> path)
    {
        var pts = new List<PlaneVector>(path.Count);
        foreach (var c in path)
        {
            pts.Add(_layout.HexToPlane(c));
        }

        return pts;
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

    // ------------------------------------------------------------------ geometry helpers

    private void AddColored(List<Vector3> triangles, Color color)
    {
        var c = VisualConfig.ToRenderColor(color);
        foreach (var v in triangles)
        {
            _overlayVertices.Add((v, c));
        }
    }

    private static void Commit(ImmediateMesh mesh, List<(Vector3 Vertex, Color Color)> vertices)
    {
        if (vertices.Count == 0)
        {
            return;
        }

        mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
        foreach (var (vertex, color) in vertices)
        {
            mesh.SurfaceSetColor(color);
            mesh.SurfaceAddVertex(vertex);
        }

        mesh.SurfaceEnd();
    }

    private static void AddPolyline(List<(Vector3, Color)> output, List<PlaneVector> points, float width, Color color)
    {
        var half = width * 0.5f;
        var c = VisualConfig.ToRenderColor(color);
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
                output.Add((v, c));
            }
        }
    }

    /// <summary>
    /// Flat straight arrow from <paramref name="a"/> to <paramref name="b"/> on the plane: body of
    /// <see cref="ArrowBodyWidth"/> and a head; the head shrinks for very short arrows.
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
            mesh.Add(P(start, -bodyHalf));
            mesh.Add(P(neck, -bodyHalf));
            mesh.Add(P(neck, bodyHalf));
            mesh.Add(P(start, -bodyHalf));
            mesh.Add(P(neck, bodyHalf));
            mesh.Add(P(start, bodyHalf));
        }

        mesh.Add(P(neck, -headHalf));
        mesh.Add(P(end, 0));
        mesh.Add(P(neck, headHalf));
    }

    /// <summary>Small dart (arrowhead with a notch) whose tip touches <paramref name="edge"/>, pointing outwards.</summary>
    private static void AddDart(List<Vector3> mesh, PlaneVector edge, HexDirection direction, float size, float lift = 0f)
    {
        var d = HexLayout.DirectionVector(direction);
        var ux = (float)d.X;
        var uz = (float)d.Z;
        var px = -uz;
        var pz = ux;
        var y = ArrowLift + 0.01f + lift;
        Vector3 P(float along, float side) => new((float)edge.X + (ux * along) + (px * side), y, (float)edge.Z + (uz * along) + (pz * side));
        var tip = P(-0.04f, 0);
        var left = P(-0.04f - (size * 1.5f), size);
        var right = P(-0.04f - (size * 1.5f), -size);
        var notch = P(-0.04f - (size * 1.05f), 0);
        mesh.Add(tip);
        mesh.Add(left);
        mesh.Add(notch);
        mesh.Add(tip);
        mesh.Add(notch);
        mesh.Add(right);
    }

    private static MeshInstance3D TriangleMesh(string name, List<Vector3> triangles, Color color, int priority)
    {
        var mesh = new ImmediateMesh();
        if (triangles.Count > 0)
        {
            mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
            foreach (var v in triangles)
            {
                mesh.SurfaceSetNormal(Vector3.Up);
                mesh.SurfaceAddVertex(v);
            }

            mesh.SurfaceEnd();
        }

        return new MeshInstance3D
        {
            Name = name,
            Mesh = mesh,
            MaterialOverride = OverlayMaterial(color, priority),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
    }

    private static Node3D Marker(string name, Vector3 position, int number, Color fill, Color rim, bool selected, bool ghost)
    {
        var marker = new Node3D { Name = name, Position = position };
        var alpha = ghost ? 0.35f : 1f;
        marker.AddChild(MarkerDisc(MarkerRadius + 0.05f, rim with { A = rim.A * alpha }, LayerMarkers, 0.02f));
        marker.AddChild(MarkerDisc(MarkerRadius, fill with { A = fill.A * alpha }, LayerMarkers + 1, 0.03f));
        if (selected)
        {
            marker.AddChild(new MeshInstance3D
            {
                Mesh = new TorusMesh { InnerRadius = MarkerRadius + 0.08f, OuterRadius = MarkerRadius + 0.12f, Rings = 32, RingSegments = 6 },
                MaterialOverride = OverlayMaterial(VisualConfig.WaypointSelectedRing, LayerMarkers + 2),
                Scale = new Vector3(1, 0.3f, 1),
                Position = new Vector3(0, 0.05f, 0),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            });
        }

        marker.AddChild(new Label3D
        {
            Text = number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            NoDepthTest = true,
            FontSize = 44,
            OutlineSize = 12,
            PixelSize = 0.006f,
            Modulate = new Color(0.12f, 0.13f, 0.16f, alpha),
            OutlineModulate = new Color(1f, 1f, 1f, 0.9f * alpha),
            Position = new Vector3(0, 0.34f, 0),
            RenderPriority = LayerMarkers + 4,
            OutlineRenderPriority = LayerMarkers + 3,
        });
        return marker;
    }

    private static MeshInstance3D MarkerDisc(float radius, Color color, int priority, float height) => new()
    {
        Mesh = new CylinderMesh { TopRadius = radius, BottomRadius = radius, Height = 0.02f, RadialSegments = 6 },
        MaterialOverride = OverlayMaterial(color, priority),
        Position = new Vector3(0, height, 0),
        Rotation = new Vector3(0, Mathf.Pi / 6f, 0), // flat-top like the board hexes
        CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
    };

    /// <summary>
    /// Overlay material: drawn WITHOUT depth test in the transparent pass, i.e. on top of every scene object
    /// (units, obstacles, future buildings) — spec part 2 §3.1. The HUD (CanvasLayer) is still drawn above.
    /// </summary>
    private static StandardMaterial3D OverlayMaterial(Color color, int priority) => new()
    {
        NoDepthTest = true,
        AlbedoColor = color,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
        RenderPriority = priority,
    };

    private static StandardMaterial3D VertexColorMaterial(int priority) => new()
    {
        NoDepthTest = true,
        VertexColorUseAsAlbedo = true,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
        RenderPriority = priority,
    };
}
