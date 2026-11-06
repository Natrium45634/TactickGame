using System;
using System.Collections.Generic;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Motion;

/// <summary>Cubic Bézier segment on the map plane.</summary>
public readonly record struct CubicSegment(PlaneVector P0, PlaneVector P1, PlaneVector P2, PlaneVector P3)
{
    public PlaneVector At(double t)
    {
        var u = 1 - t;
        return (P0 * (u * u * u)) + (P1 * (3 * u * u * t)) + (P2 * (3 * u * t * t)) + (P3 * (t * t * t));
    }

    /// <summary>Derivative (not normalised).</summary>
    public PlaneVector Derivative(double t)
    {
        var u = 1 - t;
        return ((P1 - P0) * (3 * u * u)) + ((P2 - P1) * (6 * u * t)) + ((P3 - P2) * (3 * t * t));
    }

    public static CubicSegment Line(PlaneVector a, PlaneVector b) => new(a, PlaneVector.Lerp(a, b, 1.0 / 3), PlaneVector.Lerp(a, b, 2.0 / 3), b);

    /// <summary>The same curve as the quadratic Bézier (a, c, b).</summary>
    public static CubicSegment FromQuadratic(PlaneVector a, PlaneVector c, PlaneVector b) =>
        new(a, a + ((c - a) * (2.0 / 3)), b + ((c - b) * (2.0 / 3)), b);

    /// <summary>Hermite-style segment from <paramref name="a"/> (direction <paramref name="ta"/>) to <paramref name="b"/> (arriving along <paramref name="tb"/>).</summary>
    public static CubicSegment Hermite(PlaneVector a, PlaneVector ta, double la, PlaneVector b, PlaneVector tb, double lb) =>
        new(a, a + (ta * la), b - (tb * lb), b);
}

/// <summary>
/// A continuous path made of cubic segments, parametrised by arc length (spec part 3 §2.2 step 6). Arc length is
/// tabulated per segment, so equal time steps give an even speed along curves and straight parts alike.
/// </summary>
public sealed class MotionCurve
{
    private const int Samples = 24;

    private readonly List<CubicSegment> _segments;
    private readonly double[][] _arc; // per segment: cumulative length at t = i / Samples
    private readonly double[] _start; // arc length at the start of every segment

    public MotionCurve(IReadOnlyList<CubicSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (segments.Count == 0)
        {
            throw new ArgumentException("A curve needs at least one segment.", nameof(segments));
        }

        _segments = new List<CubicSegment>(segments);
        _arc = new double[segments.Count][];
        _start = new double[segments.Count];
        var total = 0.0;
        for (var i = 0; i < segments.Count; i++)
        {
            _start[i] = total;
            var table = new double[Samples + 1];
            var prev = segments[i].At(0);
            for (var k = 1; k <= Samples; k++)
            {
                var p = segments[i].At((double)k / Samples);
                table[k] = table[k - 1] + p.DistanceTo(prev);
                prev = p;
            }

            _arc[i] = table;
            total += table[Samples];
        }

        Length = total;
    }

    public IReadOnlyList<CubicSegment> Segments => _segments;

    public double Length { get; }

    public PlaneVector Start => _segments[0].P0;

    public PlaneVector End => _segments[^1].P3;

    /// <summary>Arc length at which segment <paramref name="index"/> ends.</summary>
    public double ArcAtSegmentEnd(int index) => _start[index] + _arc[index][Samples];

    public PlaneVector PointAtArc(double s)
    {
        var (i, t) = Locate(s);
        return _segments[i].At(t);
    }

    /// <summary>Unit tangent at arc length <paramref name="s"/> (zero vector for a degenerate curve).</summary>
    public PlaneVector TangentAtArc(double s)
    {
        var (i, t) = Locate(s);
        var d = _segments[i].Derivative(t);
        if (d.Length < 1e-9)
        {
            d = _segments[i].P3 - _segments[i].P0;
        }

        return d.Length < 1e-9 ? default : d * (1.0 / d.Length);
    }

    /// <summary>Points every <paramref name="step"/> units of arc length, including both ends.</summary>
    public IEnumerable<PlaneVector> Sample(double step)
    {
        var n = Math.Max(1, (int)Math.Ceiling(Length / step));
        for (var k = 0; k <= n; k++)
        {
            yield return PointAtArc(Length * k / n);
        }
    }

    private (int Segment, double T) Locate(double s)
    {
        s = Math.Clamp(s, 0, Length);
        var i = _segments.Count - 1;
        while (i > 0 && s < _start[i])
        {
            i--;
        }

        var local = s - _start[i];
        var table = _arc[i];
        if (table[Samples] < 1e-12)
        {
            return (i, 0);
        }

        var k = 1;
        while (k < Samples && table[k] < local)
        {
            k++;
        }

        var span = table[k] - table[k - 1];
        var frac = span < 1e-12 ? 0 : (local - table[k - 1]) / span;
        return (i, ((k - 1) + Math.Clamp(frac, 0, 1)) / Samples);
    }
}
