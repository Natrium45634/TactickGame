using System;
using System.Collections.Generic;

namespace HexTactics.Core.Hex;

/// <summary>Straight hex lines (cube linear interpolation), e.g. for previews or line-of-sight.</summary>
public static class HexLine
{
    /// <summary>
    /// Hexes on the straight line from <paramref name="a"/> to <paramref name="b"/>, both included.
    /// A tiny fixed nudge makes ties on hex edges resolve deterministically.
    /// </summary>
    public static IReadOnlyList<HexCoord> Between(HexCoord a, HexCoord b)
    {
        var n = a.DistanceTo(b);
        var result = new List<HexCoord>(n + 1);
        if (n == 0)
        {
            result.Add(a);
            return result;
        }

        const double eps = 1e-6;
        double aq = a.Q + eps, ar = a.R + eps;
        double bq = b.Q + eps, br = b.R + eps;
        for (var i = 0; i <= n; i++)
        {
            var t = (double)i / n;
            result.Add(HexCoord.Round(aq + ((bq - aq) * t), ar + ((br - ar) * t)));
        }

        return result;
    }

    /// <summary>Direction of the first step of the straight line, or null when a == b.</summary>
    public static HexDirection? FirstStepDirection(HexCoord a, HexCoord b)
    {
        if (a == b)
        {
            return null;
        }

        var line = Between(a, b);
        return a.DirectionTo(line[1]) ?? throw new InvalidOperationException("Hex line step is not adjacent.");
    }
}
