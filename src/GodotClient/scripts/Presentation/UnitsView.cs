using System.Collections.Generic;
using Godot;
using HexTactics.Core.Hex;
using HexTactics.Core.Units;

namespace HexTactics.Client.Presentation;

/// <summary>Visual layer 3: one <see cref="UnitView"/> per unit, synchronised from the model every frame.</summary>
public partial class UnitsView : Node3D
{
    private readonly Dictionary<UnitId, UnitView> _views = new();
    private HexLayout _layout = null!;
    private MeshInstance3D? _ghost;
    private StandardMaterial3D? _ghostMaterial;

    public void Initialize(HexLayout layout) => _layout = layout;

    public IReadOnlyDictionary<UnitId, UnitView> Views => _views;

    /// <summary>Creates/removes views to match the state and updates positions and facing.</summary>
    /// <param name="interpolationSeconds">Simulation time elapsed since the last tick (smooth rendering only).</param>
    /// <param name="groupMembers">Members of the team being formed in the command mode (captain first), or null.</param>
    public void Sync(GameState state, UnitId? selected, float delta, double interpolationSeconds, UnitId? previewUnit = null, HexDirection? previewFacing = null, IReadOnlyList<UnitId>? groupMembers = null)
    {
        var alive = new HashSet<UnitId>();
        var captains = new HashSet<UnitId>();
        foreach (var group in state.Groups)
        {
            captains.Add(group.Captain);
        }

        if (groupMembers is { Count: > 0 })
        {
            captains.Add(groupMembers[0]);
        }

        foreach (var unit in state.Units)
        {
            alive.Add(unit.Id);
            if (!_views.TryGetValue(unit.Id, out var view))
            {
                view = new UnitView();
                AddChild(view);
                view.Build(unit, state.Roster.FindByUnit(unit.Id)?.Name);
                _views.Add(unit.Id, view);
            }

            var facing = previewUnit == unit.Id && previewFacing is { } pf ? pf : unit.Facing;
            view.Sync(PositionOf(unit, interpolationSeconds), facing, delta);
            view.Selected = selected == unit.Id;
            view.GroupMember = groupMembers is not null && System.Linq.Enumerable.Contains(groupMembers, unit.Id);
            view.Captain = captains.Contains(unit.Id);
            view.Blocked = unit.Motion.Status == MovementStatus.Blocked;
        }

        foreach (var id in new List<UnitId>(_views.Keys))
        {
            if (!alive.Contains(id))
            {
                _views[id].QueueFree();
                _views.Remove(id);
            }
        }
    }

    /// <summary>
    /// Roster drag preview (spec part 2 §5.2): a translucent body over the hex under the cursor, green where the
    /// operative can be placed and red elsewhere. Hidden when <paramref name="cell"/> is null.
    /// </summary>
    public void SyncGhost(HexCoord? cell, bool valid)
    {
        if (_ghost is null)
        {
            _ghostMaterial = new StandardMaterial3D
            {
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                Roughness = 0.75f,
                RenderPriority = 6,
            };
            _ghost = new MeshInstance3D
            {
                Name = "PlacementGhost",
                Mesh = new CylinderMesh { TopRadius = UnitView.BodyRadius, BottomRadius = UnitView.BodyRadius, Height = UnitView.BodyHeight, RadialSegments = 32 },
                MaterialOverride = _ghostMaterial,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Visible = false,
            };
            AddChild(_ghost);
        }

        _ghost.Visible = cell is not null;
        if (cell is { } c)
        {
            _ghost.Position = _layout.HexToPlane(c).ToWorld(UnitView.BodyHeight * 0.5f);
            _ghostMaterial!.AlbedoColor = valid ? VisualConfig.GhostValid : VisualConfig.GhostInvalid;
        }
    }

    /// <summary>
    /// World position of a unit, taken from the simulation (spec part 3 §2.3): the point of its current step curve — the
    /// smoothed trajectory over its hex path — or its rest position. The view only extrapolates within the current tick
    /// for smoothness; it never moves the body on its own.
    /// </summary>
    public Vector3 PositionOf(UnitState unit, double interpolationSeconds = 0)
    {
        var m = unit.Motion;
        var plane = m.IsStepping
            ? m.PositionAtProgress(System.Math.Min(1.0, m.Progress + (unit.SpeedHexesPerSecond * interpolationSeconds)))
            : m.Position;
        var p = plane ?? _layout.HexToPlane(unit.Cell) * (1.0 / _layout.Size);
        return (p * _layout.Size).ToWorld(0f); // the simulation works with hex size 1
    }

    /// <summary>Current visual positions of all units on the plane (for path lines).</summary>
    public PlaneVector? VisualPlanePosition(UnitId id) => _views.TryGetValue(id, out var v) ? v.Position.ToPlane() : null;

    /// <summary>
    /// Returns the unit whose body (vertical cylinder) is hit by the camera ray, nearest to the camera.
    /// The ray part between body top and ground is projected onto the plane and compared to the radius.
    /// </summary>
    public UnitId? PickUnit(Vector3 rayOrigin, Vector3 rayDir)
    {
        if (Mathf.Abs(rayDir.Y) < 1e-6f)
        {
            return null;
        }

        UnitId? best = null;
        var bestT = float.MaxValue;
        foreach (var (id, view) in _views)
        {
            var c = view.Position;
            var tTop = (c.Y + UnitView.BodyHeight - rayOrigin.Y) / rayDir.Y;
            var tBottom = (c.Y - rayOrigin.Y) / rayDir.Y;
            var a = rayOrigin + (rayDir * tTop);
            var b = rayOrigin + (rayDir * tBottom);
            var d = DistanceToSegmentXZ(new Vector2(c.X, c.Z), new Vector2(a.X, a.Z), new Vector2(b.X, b.Z), out var s);
            if (d <= UnitView.PickRadius)
            {
                var t = Mathf.Lerp(tTop, tBottom, s);
                if (t < bestT)
                {
                    bestT = t;
                    best = id;
                }
            }
        }

        return best;
    }

    private static float DistanceToSegmentXZ(Vector2 p, Vector2 a, Vector2 b, out float s)
    {
        var ab = b - a;
        var len2 = ab.LengthSquared();
        s = len2 < 1e-9f ? 0f : Mathf.Clamp((p - a).Dot(ab) / len2, 0f, 1f);
        return p.DistanceTo(a + (ab * s));
    }
}
