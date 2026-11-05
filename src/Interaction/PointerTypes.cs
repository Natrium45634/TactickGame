using System;
using HexTactics.Core.Hex;
using HexTactics.Core.Units;

namespace HexTactics.Interaction;

/// <summary>Position in window pixels (only used for click/drag thresholds).</summary>
public readonly record struct ScreenPoint(double X, double Y)
{
    public double DistanceTo(ScreenPoint other) => Math.Sqrt(((X - other.X) * (X - other.X)) + ((Y - other.Y) * (Y - other.Y)));
}

public enum PointerButton
{
    Primary = 0,
    Secondary = 1,
}

/// <summary>
/// What lies under the pointer, as computed by the client from the camera ray.
/// </summary>
/// <param name="Screen">Window position.</param>
/// <param name="Plane">Point on the map plane (null if the ray misses the plane).</param>
/// <param name="Hex">Hex containing <paramref name="Plane"/>; may be outside of the map (the state machine checks bounds).</param>
/// <param name="Unit">Unit whose body is under the pointer, if any.</param>
/// <param name="WaypointId">Waypoint marker under the pointer, if any.</param>
public readonly record struct PointerSample(ScreenPoint Screen, PlaneVector? Plane, HexCoord? Hex, UnitId? Unit = null, int? WaypointId = null);
