using System;

namespace HexTactics.Core.Hex;

/// <summary>
/// Geometry of regular pointy-top hexes on the map plane. Pure math, independent of any
/// camera: the client converts screen → plane (ray cast) and then plane → hex with this class.
/// </summary>
public sealed class HexLayout
{
    public static readonly double Sqrt3 = Math.Sqrt(3.0);

    public HexLayout(double size, PlaneVector origin = default)
    {
        if (size <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "Hex size must be positive.");
        }

        Size = size;
        Origin = origin;
    }

    /// <summary>Circumradius of a hex = length of one side.</summary>
    public double Size { get; }

    /// <summary>Plane position of the centre of hex (q=0, r=0).</summary>
    public PlaneVector Origin { get; }

    /// <summary>Distance between two opposite edges (horizontal extent of a pointy-top hex).</summary>
    public double HexWidth => Sqrt3 * Size;

    /// <summary>Distance between the top and bottom vertices.</summary>
    public double HexHeight => 2.0 * Size;

    /// <summary>Distance from centre to the middle of an edge.</summary>
    public double Apothem => Sqrt3 * 0.5 * Size;

    public double RowSpacing => 1.5 * Size;

    public PlaneVector HexToPlane(HexCoord hex)
    {
        var x = Size * ((Sqrt3 * hex.Q) + (Sqrt3 / 2.0 * hex.R));
        var z = Size * (1.5 * hex.R);
        return new PlaneVector(x + Origin.X, z + Origin.Z);
    }

    /// <summary>Fractional axial coordinates of a plane point (not rounded).</summary>
    public (double Q, double R) PlaneToFractionalHex(PlaneVector point)
    {
        var px = (point.X - Origin.X) / Size;
        var pz = (point.Z - Origin.Z) / Size;
        var q = (Sqrt3 / 3.0 * px) - (1.0 / 3.0 * pz);
        var r = 2.0 / 3.0 * pz;
        return (q, r);
    }

    /// <summary>The hex that contains the plane point (may be outside of any map).</summary>
    public HexCoord PlaneToHex(PlaneVector point)
    {
        var (q, r) = PlaneToFractionalHex(point);
        return HexCoord.Round(q, r);
    }

    /// <summary>
    /// Corner <paramref name="index"/> (0..5) of a hex. Corner 0 is the top vertex (north),
    /// continuing clockwise on screen: 1 = upper-right, 2 = lower-right, 3 = bottom, 4 = lower-left, 5 = upper-left.
    /// </summary>
    public PlaneVector Corner(HexCoord hex, int index) => HexToPlane(hex) + CornerOffset(index);

    public PlaneVector CornerOffset(int index)
    {
        // Angle measured clockwise on screen from "north" (-Z).
        var angle = Math.PI / 180.0 * (60.0 * index);
        return new PlaneVector(Size * Math.Sin(angle), -Size * Math.Cos(angle));
    }

    /// <summary>Unit vector on the plane pointing from a hex centre towards the neighbour in that direction.</summary>
    public static PlaneVector DirectionVector(HexDirection direction)
    {
        var a = direction.AngleDegrees() * Math.PI / 180.0;
        return new PlaneVector(Math.Cos(a), -Math.Sin(a));
    }
}
