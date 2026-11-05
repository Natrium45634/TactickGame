using System;
using System.Collections.Generic;

namespace HexTactics.Core.Hex;

/// <summary>
/// Axial hex coordinate (pointy-top). The implicit third cube coordinate is
/// <see cref="S"/> = -Q - R. All game rules (neighbours, distance, path finding)
/// work in this space; offset coordinates are only used for storage / display.
/// </summary>
public readonly record struct HexCoord(int Q, int R) : IComparable<HexCoord>
{
    public int S => -Q - R;

    /// <summary>Axial offsets for the six neighbours, indexed by <see cref="HexDirection"/>.</summary>
    private static readonly HexCoord[] DirectionOffsets =
    {
        new(+1, 0),  // East
        new(+1, -1), // NorthEast
        new(0, -1),  // NorthWest
        new(-1, 0),  // West
        new(-1, +1), // SouthWest
        new(0, +1),  // SouthEast
    };

    public static HexCoord operator +(HexCoord a, HexCoord b) => new(a.Q + b.Q, a.R + b.R);

    public static HexCoord operator -(HexCoord a, HexCoord b) => new(a.Q - b.Q, a.R - b.R);

    public static HexCoord DirectionOffset(HexDirection direction) => DirectionOffsets[(int)direction];

    public HexCoord Neighbor(HexDirection direction) => this + DirectionOffsets[(int)direction];

    /// <summary>All six neighbours in the fixed order of <see cref="HexDirections.All"/>.</summary>
    public IEnumerable<HexCoord> Neighbors()
    {
        foreach (var offset in DirectionOffsets)
        {
            yield return this + offset;
        }
    }

    /// <summary>Number of steps between two hexes on an unobstructed grid (cube distance).</summary>
    public int DistanceTo(HexCoord other)
    {
        var d = this - other;
        return (Math.Abs(d.Q) + Math.Abs(d.R) + Math.Abs(d.S)) / 2;
    }

    /// <summary>
    /// Returns the direction if <paramref name="other"/> is an adjacent hex, otherwise null.
    /// </summary>
    public HexDirection? DirectionTo(HexCoord other)
    {
        var d = other - this;
        for (var i = 0; i < DirectionOffsets.Length; i++)
        {
            if (DirectionOffsets[i] == d)
            {
                return (HexDirection)i;
            }
        }

        return null;
    }

    /// <summary>Converts odd-r offset coordinates (odd rows shifted right by half a hex).</summary>
    public static HexCoord FromOffset(OffsetCoord offset)
    {
        var q = offset.Col - ((offset.Row - (offset.Row & 1)) / 2);
        return new HexCoord(q, offset.Row);
    }

    public static HexCoord FromOffset(int col, int row) => FromOffset(new OffsetCoord(col, row));

    public OffsetCoord ToOffset()
    {
        var col = Q + ((R - (R & 1)) / 2);
        return new OffsetCoord(col, R);
    }

    /// <summary>Rounds fractional cube coordinates to the nearest hex.</summary>
    public static HexCoord Round(double q, double r)
    {
        var s = -q - r;
        var rq = Math.Round(q, MidpointRounding.AwayFromZero);
        var rr = Math.Round(r, MidpointRounding.AwayFromZero);
        var rs = Math.Round(s, MidpointRounding.AwayFromZero);

        var dq = Math.Abs(rq - q);
        var dr = Math.Abs(rr - r);
        var ds = Math.Abs(rs - s);

        if (dq > dr && dq > ds)
        {
            rq = -rr - rs;
        }
        else if (dr > ds)
        {
            rr = -rq - rs;
        }

        return new HexCoord((int)rq, (int)rr);
    }

    /// <summary>Row-major order (by offset row, then column) — used for deterministic iteration.</summary>
    public int CompareTo(HexCoord other)
    {
        var a = ToOffset();
        var b = other.ToOffset();
        var byRow = a.Row.CompareTo(b.Row);
        return byRow != 0 ? byRow : a.Col.CompareTo(b.Col);
    }

    public override string ToString()
    {
        var o = ToOffset();
        return $"({o.Col},{o.Row})";
    }
}
