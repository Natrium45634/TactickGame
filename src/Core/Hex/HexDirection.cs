using System;
using System.Collections.Generic;

namespace HexTactics.Core.Hex;

/// <summary>
/// The six discrete directions of a pointy-top hex, counter-clockwise starting from East
/// (screen "north" = up = decreasing row). This is also the unit facing type
/// (the spec's <c>FacingDirection</c>): a unit can only face one of these six neighbours.
/// The numeric order is the fixed neighbour processing order used by path finding.
/// </summary>
public enum HexDirection
{
    East = 0,
    NorthEast = 1,
    NorthWest = 2,
    West = 3,
    SouthWest = 4,
    SouthEast = 5,
}

public static class HexDirections
{
    public const int Count = 6;

    public static readonly IReadOnlyList<HexDirection> All = new[]
    {
        HexDirection.East,
        HexDirection.NorthEast,
        HexDirection.NorthWest,
        HexDirection.West,
        HexDirection.SouthWest,
        HexDirection.SouthEast,
    };

    public static HexDirection Opposite(this HexDirection direction) => (HexDirection)(((int)direction + 3) % Count);

    public static HexDirection RotateCounterClockwise(this HexDirection direction, int steps = 1) =>
        (HexDirection)((((int)direction + steps) % Count + Count) % Count);

    /// <summary>
    /// Angle of the direction on the map plane in degrees, counter-clockwise from East,
    /// with "north" (towards row 0) being +90°.
    /// </summary>
    public static double AngleDegrees(this HexDirection direction) => (int)direction * 60.0;

    /// <summary>
    /// Quantises an arbitrary vector on the map plane into one of the six directions.
    /// <paramref name="x"/> points east, <paramref name="z"/> points south (towards higher rows),
    /// matching <see cref="PlaneVector"/>. This uses map-plane geometry, never screen angles.
    /// Returns null for a (near) zero vector.
    /// </summary>
    public static HexDirection? FromPlaneVector(double x, double z, double epsilon = 1e-9)
    {
        if ((x * x) + (z * z) < epsilon * epsilon)
        {
            return null;
        }

        var angle = Math.Atan2(-z, x) * 180.0 / Math.PI; // north (negative z) is +90°
        var sector = (int)Math.Round(angle / 60.0, MidpointRounding.AwayFromZero);
        return (HexDirection)(((sector % Count) + Count) % Count);
    }

    public static string ShortName(this HexDirection direction) => direction switch
    {
        HexDirection.East => "E",
        HexDirection.NorthEast => "NE",
        HexDirection.NorthWest => "NW",
        HexDirection.West => "W",
        HexDirection.SouthWest => "SW",
        HexDirection.SouthEast => "SE",
        _ => direction.ToString(),
    };
}
