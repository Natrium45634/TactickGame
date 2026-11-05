using System.Collections.Generic;
using Godot;
using HexTactics.Core.Hex;
using HexTactics.Core.Orders;
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
    private const float MarkerRadius = 0.24f;

    private readonly List<(int Id, UnitId Unit, int Sequence, Vector3 Position)> _markers = new();
    private HexLayout _layout = null!;
    private Node3D _committed = null!;
    private MeshInstance3D _dragArrow = null!;
    private ImmediateMesh _dragMesh = null!;
    private StandardMaterial3D _dragMaterial = null!;
    private long _signature = long.MinValue;

    public void Initialize(HexLayout layout)
    {
        _layout = layout;
        _committed = new Node3D { Name = "Committed" };
        AddChild(_committed);

        _dragMesh = new ImmediateMesh();
        _dragMaterial = NewArrowMaterial(VisualConfig.ArrowValid, 4);
        _dragArrow = new MeshInstance3D
        {
            Name = "DragArrow",
            Mesh = _dragMesh,
            MaterialOverride = _dragMaterial,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_dragArrow);
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
            mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
            var from = _layout.HexToPlane(unit.Cell);
            foreach (var wp in unit.Orders.Items)
            {
                var to = _layout.HexToPlane(wp.Cell);
                AddArrow(mesh, from, to, startInset: 0.36f, endInset: MarkerRadius + 0.04f);
                from = to;
            }

            mesh.SurfaceEnd();
            _committed.AddChild(new MeshInstance3D
            {
                Name = $"Chain{unit.Id.Value}",
                Mesh = mesh,
                MaterialOverride = NewArrowMaterial(color, isSelected ? 3 : 2),
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
        marker.AddChild(new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = MarkerRadius, BottomRadius = MarkerRadius, Height = 0.05f, RadialSegments = 6 },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = unitSelected ? VisualConfig.WaypointSelectedUnit : VisualConfig.WaypointOtherUnit,
                Roughness = 0.7f,
            },
            Position = new Vector3(0, 0.03f, 0),
            Rotation = new Vector3(0, Mathf.Pi / 6f, 0),
        });

        if (waypointSelected)
        {
            marker.AddChild(new MeshInstance3D
            {
                Mesh = new TorusMesh { InnerRadius = MarkerRadius + 0.05f, OuterRadius = MarkerRadius + 0.09f, Rings = 32, RingSegments = 6 },
                MaterialOverride = new StandardMaterial3D { AlbedoColor = Colors.White, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded },
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
            Modulate = unitSelected ? new Color(1, 1, 1) : new Color(0.85f, 0.87f, 0.9f),
            OutlineModulate = new Color(0.12f, 0.13f, 0.16f),
            Position = new Vector3(0, 0.34f, 0),
            RenderPriority = 10,
            OutlineRenderPriority = 9,
        });
        _committed.AddChild(marker);
    }

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

        _dragMesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
        AddArrow(_dragMesh, drag.SourceCenter, pointer, startInset: drag.Mode == OrderMode.Replace ? 0.36f : MarkerRadius, endInset: 0f);
        _dragMesh.SurfaceEnd();
    }

    /// <summary>
    /// Appends a flat straight arrow from <paramref name="a"/> to <paramref name="b"/> on the plane:
    /// body of <see cref="ArrowBodyWidth"/> and a head; the head shrinks for very short arrows.
    /// </summary>
    private static void AddArrow(ImmediateMesh mesh, PlaneVector a, PlaneVector b, float startInset, float endInset)
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

        mesh.SurfaceSetNormal(Vector3.Up);
        mesh.SurfaceAddVertex(P(neck, -headHalf));
        mesh.SurfaceAddVertex(P(end, 0));
        mesh.SurfaceAddVertex(P(neck, headHalf));
    }

    private static void Quad(ImmediateMesh mesh, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        mesh.SurfaceSetNormal(Vector3.Up);
        mesh.SurfaceAddVertex(a);
        mesh.SurfaceAddVertex(b);
        mesh.SurfaceAddVertex(c);
        mesh.SurfaceAddVertex(a);
        mesh.SurfaceAddVertex(c);
        mesh.SurfaceAddVertex(d);
    }

    private static StandardMaterial3D NewArrowMaterial(Color color, int priority) => new()
    {
        AlbedoColor = color,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
        RenderPriority = priority,
    };
}
