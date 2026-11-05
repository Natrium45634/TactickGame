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

    public void Initialize(HexLayout layout) => _layout = layout;

    public IReadOnlyDictionary<UnitId, UnitView> Views => _views;

    /// <summary>Creates/removes views to match the state and updates positions and facing.</summary>
    public void Sync(GameState state, UnitId? selected, float delta)
    {
        var alive = new HashSet<UnitId>();
        foreach (var unit in state.Units)
        {
            alive.Add(unit.Id);
            if (!_views.TryGetValue(unit.Id, out var view))
            {
                view = new UnitView();
                AddChild(view);
                view.Build(unit);
                _views.Add(unit.Id, view);
            }

            view.Sync(PositionOf(unit), unit.Facing, delta);
            view.Selected = selected == unit.Id;
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

    /// <summary>World position of a unit (cell centre on the ground). Movement interpolation hooks in here.</summary>
    public virtual Vector3 PositionOf(UnitState unit) => _layout.HexToPlane(unit.Cell).ToWorld(0f);

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
