using System;
using System.Collections.Generic;

namespace HexTactics.Core.Hex;

/// <summary>
/// The six discrete directions of a <b>flat-top</b> hex. Each direction points from the hex centre to
/// the middle of one of its six edges (and to the neighbour behind that edge): the flat top edge is
/// <see cref="North"/>. Order: clockwise starting at North. This is also the unit facing type (the
/// spec's <c>FacingDirection</c>) and the fixed neighbour processing order used by path finding.
/// </summary>
public enum HexDirection
{
    North = 0,
    NorthEast = 1,
    SouthEast = 2,
    South = 3,
    SouthWest = 4,
    NorthWest = 5,
}

public static class HexDirections
{
    public const int Count = 6;

    public static readonly IReadOnlyList<HexDirection> All = new[]
    {
        HexDirection.North,
        HexDirection.NorthEast,
        HexDirection.SouthEast,
        HexDirection.South,
        HexDirection.SouthWest,
        HexDirection.NorthWest,
    };

    public static HexDirection Opposite(this HexDirection direction) => (HexDirection)(((int)direction + 3) % Count);

    /// <summary>Rotates by <paramref name="steps"/> × 60° clockwise (negative = counter-clockwise).</summary>
    public static HexDirection RotateClockwise(this HexDirection direction, int steps = 1) =>
        (HexDirection)((((int)direction + steps) % Count + Count) % Count);

    /// <summary>
    /// Angle of the direction on the map plane in degrees, counter-clockwise from East with "north"
    /// (towards row 0, the top edge) being +90°: N 90°, NE 30°, SE −30°, S −90°, SW −150°, NW 150°.
    /// </summary>
    public static double AngleDegrees(this HexDirection direction) => 90.0 - ((int)direction * 60.0);

    /// <summary>
    /// Quantises an arbitrary vector on the map plane into one of the six edge directions.
    /// <paramref name="x"/> points east, <paramref name="z"/> points south (towards higher rows),
    /// matching <see cref="PlaneVector"/>. Uses map-plane geometry, never screen angles.
    /// Returns null for a (near) zero vector.
    /// </summary>
    public static HexDirection? FromPlaneVector(double x, double z, double epsilon = 1e-9)
    {
        if ((x * x) + (z * z) < epsilon * epsilon)
        {
            return null;
        }

        var angle = Math.Atan2(-z, x) * 180.0 / Math.PI; // north (negative z) is +90°
        var sector = (int)Math.Round((90.0 - angle) / 60.0, MidpointRounding.AwayFromZero);
        return (HexDirection)(((sector % Count) + Count) % Count);
    }

    public static string ShortName(this HexDirection direction) => direction switch
    {
        HexDirection.North => "N",
        HexDirection.NorthEast => "NE",
        HexDirection.SouthEast => "SE",
        HexDirection.South => "S",
        HexDirection.SouthWest => "SW",
        HexDirection.NorthWest => "NW",
        _ => direction.ToString(),
    };
}
