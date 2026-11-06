using System;

namespace HexTactics.Core.Hex;

/// <summary>
/// Geometry of regular <b>flat-top</b> hexes on the map plane (the spec's <c>HexLayoutFlatTop</c>):
/// every hex has a horizontal top and bottom edge and vertices pointing east and west. Pure math,
/// independent of any camera: the client converts screen → plane (ray cast) and then plane → hex here.
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

    /// <summary>Distance between the east and west vertices (horizontal extent of a flat-top hex).</summary>
    public double HexWidth => 2.0 * Size;

    /// <summary>Distance between the top and bottom edges (vertical extent of a flat-top hex).</summary>
    public double HexHeight => Sqrt3 * Size;

    /// <summary>Distance from the centre to the middle of an edge; also the distance between neighbour centres / 2.</summary>
    public double Apothem => Sqrt3 * 0.5 * Size;

    /// <summary>Horizontal distance between neighbouring columns.</summary>
    public double ColumnSpacing => 1.5 * Size;

    /// <summary>Distance between the centres of two adjacent hexes.</summary>
    public double NeighborDistance => Sqrt3 * Size;

    public PlaneVector HexToPlane(HexCoord hex)
    {
        var x = Size * 1.5 * hex.Q;
        var z = Size * ((Sqrt3 / 2.0 * hex.Q) + (Sqrt3 * hex.R));
        return new PlaneVector(x + Origin.X, z + Origin.Z);
    }

    /// <summary>Fractional axial coordinates of a plane point (not rounded).</summary>
    public (double Q, double R) PlaneToFractionalHex(PlaneVector point)
    {
        var px = (point.X - Origin.X) / Size;
        var pz = (point.Z - Origin.Z) / Size;
        var q = 2.0 / 3.0 * px;
        var r = (-1.0 / 3.0 * px) + (Sqrt3 / 3.0 * pz);
        return (q, r);
    }

    /// <summary>The hex that contains the plane point (may be outside of any map).</summary>
    public HexCoord PlaneToHex(PlaneVector point)
    {
        var (q, r) = PlaneToFractionalHex(point);
        return HexCoord.Round(q, r);
    }

    /// <summary>
    /// Corner <paramref name="index"/> (0..5) of a hex. Corner 0 is the east vertex, then
    /// counter-clockwise on screen: 1 = upper-right, 2 = upper-left, 3 = west, 4 = lower-left, 5 = lower-right.
    /// The flat top edge runs from corner 1 to corner 2.
    /// </summary>
    public PlaneVector Corner(HexCoord hex, int index) => HexToPlane(hex) + CornerOffset(index);

    public PlaneVector CornerOffset(int index)
    {
        var angle = Math.PI / 180.0 * (60.0 * index);
        return new PlaneVector(Size * Math.Cos(angle), -Size * Math.Sin(angle));
    }

    /// <summary>Unit vector on the plane from a hex centre towards the middle of the edge (and neighbour) in that direction.</summary>
    public static PlaneVector DirectionVector(HexDirection direction)
    {
        var a = direction.AngleDegrees() * Math.PI / 180.0;
        return new PlaneVector(Math.Cos(a), -Math.Sin(a));
    }

    /// <summary>Plane position of the middle of the hex edge in <paramref name="direction"/>.</summary>
    public PlaneVector EdgeMidpoint(HexCoord hex, HexDirection direction) => HexToPlane(hex) + (DirectionVector(direction) * Apothem);
}
