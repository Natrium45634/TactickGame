using Godot;
using HexTactics.Client.Presentation;
using HexTactics.Core.Hex;
using HexTactics.Core.Map;

namespace HexTactics.Client.InputLayer;

/// <summary>Result of converting a screen position into map space.</summary>
/// <param name="Hex">The cell under the cursor, or null if the cursor is outside the map.</param>
/// <param name="PlanePoint">Intersection with the ground plane (valid even outside the map).</param>
/// <param name="HasPlanePoint">False if the ray never meets the plane (cannot happen with a tilted camera).</param>
public readonly record struct BoardPick(HexCoord? Hex, PlaneVector PlanePoint, bool HasPlanePoint);

/// <summary>
/// Screen → map conversion: a camera ray is intersected with the ground plane (and the tops of
/// raised blocked tiles) and the plane point is converted to a hex by <see cref="HexLayout"/>.
/// Works for any camera position/angle because it relies only on the camera projection.
/// </summary>
public sealed class BoardPicker
{
    private readonly HexGrid _grid;
    private readonly HexLayout _layout;

    public BoardPicker(HexGrid grid, HexLayout layout)
    {
        _grid = grid;
        _layout = layout;
    }

    public BoardPick Pick(Camera3D camera, Vector2 screenPosition)
    {
        var origin = camera.ProjectRayOrigin(screenPosition);
        var dir = camera.ProjectRayNormal(screenPosition);

        // Raised blocked columns occlude what is behind them.
        if (IntersectHorizontal(origin, dir, VisualConfig.BlockedHeight, out var top))
        {
            var h = _layout.PlaneToHex(top.ToPlane());
            if (_grid.Contains(h) && _grid[h].Type == TileType.Blocked)
            {
                return new BoardPick(h, top.ToPlane(), true);
            }
        }

        if (!IntersectHorizontal(origin, dir, 0f, out var ground))
        {
            return new BoardPick(null, default, false);
        }

        var plane = ground.ToPlane();
        var hex = _layout.PlaneToHex(plane);
        return new BoardPick(_grid.Contains(hex) ? hex : null, plane, true);
    }

    /// <summary>Hex containing a plane point, even if it lies outside of the map.</summary>
    public HexCoord HexAt(PlaneVector point) => _layout.PlaneToHex(point);

    /// <summary>Ground-plane point under the cursor regardless of raised tiles.</summary>
    public static bool GroundPoint(Camera3D camera, Vector2 screenPosition, out PlaneVector point)
    {
        var ok = IntersectHorizontal(camera.ProjectRayOrigin(screenPosition), camera.ProjectRayNormal(screenPosition), 0f, out var p);
        point = p.ToPlane();
        return ok;
    }

    public static bool IntersectHorizontal(Vector3 origin, Vector3 dir, float height, out Vector3 hit)
    {
        hit = default;
        if (Mathf.Abs(dir.Y) < 1e-6f)
        {
            return false;
        }

        var t = (height - origin.Y) / dir.Y;
        if (t < 0)
        {
            return false;
        }

        hit = origin + (dir * t);
        return true;
    }
}
