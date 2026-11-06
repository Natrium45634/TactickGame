using System;
using System.Collections.Generic;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Motion;

/// <summary>
/// Geometry of one hex transition <c>From → To</c> of a unit's <c>GraphPath</c> (spec part 3 §2): the curve the unit's
/// body follows while the simulation performs that step. Its first part lies inside <c>From</c>, its second part inside
/// <c>To</c>; both meet at the midpoint of the shared edge, which is exactly where the logical cell switches.
/// </summary>
/// <param name="Curve">The curve from the unit's current position to the end point of the step inside <c>To</c>.</param>
/// <param name="SwitchArc">Arc length at which the curve crosses into <c>To</c> (0 if already inside <c>To</c>).</param>
/// <param name="EndTangent">Unit tangent at the end point (direction of travel when the step ends).</param>
/// <param name="EndControl">Suggested control length to leave the end point smoothly in the same direction.</param>
/// <param name="Smoothed">False if the safe fallback through hex centres was used.</param>
public sealed record StepPlan(MotionCurve Curve, double SwitchArc, PlaneVector EndTangent, double EndControl, bool Smoothed)
{
    public PlaneVector End => Curve.End;
}

/// <summary>
/// Builds smooth, safe step curves (spec part 3 §2.2):
/// <list type="bullet">
/// <item>Turns are rounded inside the turning hex with the quadratic curve "entry edge midpoint → hex centre → exit edge
/// midpoint"; it lies in that hex (convex hull), so the unit never cuts through a neighbouring hex — a blocked inner corner
/// included — and visits exactly the hexes of its path, in order.</item>
/// <item>Waypoints (ТД) and the end of a route are hard anchors: the curve passes through their centre (no "cutting").</item>
/// <item>Every curve is checked along its whole length with the body radius against the allowed corridor; if the check
/// fails, the step falls back to the straight lines through the hex centres (correctness before elegance).</item>
/// <item>A new order mid-step continues from the current position and tangent — no jump back to a centre.</item>
/// </list>
/// </summary>
public static class StepGeometry
{
    /// <summary>Collision radius of a unit's body on the map plane (the cylinder radius of the prototype model).</summary>
    public const double DefaultBodyRadius = 0.28;

    /// <summary>
    /// Curve of the step <paramref name="from"/> → <paramref name="to"/> starting at <paramref name="start"/>.
    /// </summary>
    /// <param name="startTangent">Direction of travel at <paramref name="start"/> if the unit keeps moving, null from rest.</param>
    /// <param name="startControl">Control length leaving <paramref name="start"/> (from the previous step), or null.</param>
    /// <param name="lookahead">The hex after <paramref name="to"/> on the path, or null if the unit stops in <paramref name="to"/>
    /// (end of the route, a waypoint anchor, or unknown).</param>
    public static StepPlan Build(
        HexLayout layout,
        HexCoord from,
        HexCoord to,
        PlaneVector start,
        PlaneVector? startTangent,
        double? startControl,
        HexCoord? lookahead,
        double radius = DefaultBodyRadius)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var dir = from.DirectionTo(to) ?? throw new ArgumentException($"{from} and {to} are not neighbours.");
        var d = HexLayout.DirectionVector(dir);
        var edge = layout.EdgeMidpoint(from, dir);
        var (secondHalf, endTangent, endControl, smoothEnd) = SecondHalf(layout, to, edge, d, lookahead);

        var toEdge = edge - start;
        var tangent = startTangent ?? (toEdge.Length > 1e-9 ? toEdge * (1.0 / toEdge.Length) : d);
        var lead = startControl ?? (toEdge.Length / 3.0);
        var firstHalf = toEdge.Length < 1e-9
            ? CubicSegment.Line(start, edge)
            : CubicSegment.Hermite(start, tangent, Math.Min(lead, toEdge.Length), edge, d, layout.Apothem / 3.0);

        var plan = Make(new[] { firstHalf, secondHalf }, 0, endTangent, endControl, smoothEnd);
        var corridor = Corridor(from, to, lookahead);
        if (MotionCorridor.Contains(layout, plan.Curve, corridor, radius))
        {
            return plan;
        }

        // Safe fallback: straight lines through the hex centres (the unit may stop for a moment at the turn).
        var centreFrom = layout.HexToPlane(from);
        var centreTo = layout.HexToPlane(to);
        var segments = new List<CubicSegment>();
        if (start.DistanceTo(centreFrom) > 1e-6)
        {
            segments.Add(CubicSegment.Line(start, centreFrom));
        }

        segments.Add(CubicSegment.Line(centreFrom, edge));
        segments.Add(CubicSegment.Line(edge, centreTo));
        return Make(segments, segments.Count - 2, d, layout.Apothem / 3.0, false);
    }

    /// <summary>
    /// Re-plans the rest of a running step after its continuation changed (a new order, spec part 3 §2.2 step 6): the
    /// curve starts at the unit's current <paramref name="position"/> with its current <paramref name="tangent"/>.
    /// </summary>
    /// <param name="switched">True if the unit is already inside <paramref name="to"/>.</param>
    public static StepPlan Retarget(
        HexLayout layout,
        HexCoord from,
        HexCoord to,
        PlaneVector position,
        PlaneVector tangent,
        bool switched,
        HexCoord? lookahead,
        double radius = DefaultBodyRadius)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var dir = from.DirectionTo(to) ?? throw new ArgumentException($"{from} and {to} are not neighbours.");
        var d = HexLayout.DirectionVector(dir);
        var edge = layout.EdgeMidpoint(from, dir);
        var corridor = Corridor(from, to, lookahead);
        if (!switched)
        {
            // Still inside From: keep heading for the shared edge, then follow the new continuation.
            return Build(layout, from, to, position, tangent.Length > 1e-9 ? tangent : null, null, lookahead, radius);
        }

        var (endPoint, endTangent, endControl, smooth) = EndOf(layout, to, edge, d, lookahead);
        var span = endPoint - position;
        if (span.Length < 1e-6)
        {
            return Make(new[] { CubicSegment.Line(position, endPoint) }, -1, endTangent, endControl, smooth);
        }

        var t = tangent.Length > 1e-9 ? tangent : span * (1.0 / span.Length);
        var plan = Make(new[] { CubicSegment.Hermite(position, t, span.Length / 3.0, endPoint, endTangent, span.Length / 3.0) }, -1, endTangent, endControl, smooth);
        if (MotionCorridor.Contains(layout, plan.Curve, corridor, radius))
        {
            return plan;
        }

        return Make(new[] { CubicSegment.Line(position, layout.HexToPlane(to)) }, -1, d, layout.Apothem / 3.0, false);
    }

    /// <summary>
    /// The part of the step inside <paramref name="to"/>: from the shared edge midpoint to the step's end point. With a
    /// continuation it is the first half of the rounded turn through <paramref name="to"/>; otherwise it ends at the centre.
    /// </summary>
    private static (CubicSegment Segment, PlaneVector EndTangent, double EndControl, bool Smooth) SecondHalf(
        HexLayout layout, HexCoord to, PlaneVector edge, PlaneVector d, HexCoord? lookahead)
    {
        var centre = layout.HexToPlane(to);
        if (lookahead is { } next && to.DirectionTo(next) is { } exitDir && edge.DistanceTo(layout.EdgeMidpoint(to, exitDir)) > 1e-6)
        {
            var exit = layout.EdgeMidpoint(to, exitDir);
            var mid = (edge * 0.25) + (centre * 0.5) + (exit * 0.25);
            var chord = exit - edge;
            var tangent = chord * (1.0 / chord.Length);

            // First half of the quadratic (edge, centre, exit) — exactly, as a cubic.
            var segment = CubicSegment.FromQuadratic(edge, PlaneVector.Lerp(edge, centre, 0.5), mid);
            return (segment, tangent, chord.Length / 6.0, true);
        }

        return (CubicSegment.Line(edge, centre), d, layout.Apothem / 3.0, true);
    }

    private static (PlaneVector End, PlaneVector Tangent, double Control, bool Smooth) EndOf(
        HexLayout layout, HexCoord to, PlaneVector edge, PlaneVector d, HexCoord? lookahead)
    {
        var (segment, tangent, control, smooth) = SecondHalf(layout, to, edge, d, lookahead);
        return (segment.P3, tangent, control, smooth);
    }

    private static HashSet<HexCoord> Corridor(HexCoord from, HexCoord to, HexCoord? lookahead)
    {
        var corridor = new HashSet<HexCoord> { from, to };
        if (lookahead is { } next)
        {
            corridor.Add(next);
        }

        return corridor;
    }

    /// <param name="switchSegment">Index of the last segment inside From (-1: the whole curve is inside To).</param>
    private static StepPlan Make(IReadOnlyList<CubicSegment> segments, int switchSegment, PlaneVector endTangent, double endControl, bool smooth)
    {
        var curve = new MotionCurve(segments);
        var switchArc = switchSegment < 0 ? 0 : curve.ArcAtSegmentEnd(switchSegment);
        return new StepPlan(curve, switchArc, endTangent, endControl, smooth);
    }
}

/// <summary>
/// Corridor check for motion curves (spec part 3 §2.1, §2.2 step 4): the unit's body — a circle of the given radius
/// around the curve point — may only overlap hexes of the allowed corridor. Checked densely along the whole length,
/// not only at control points.
/// </summary>
public static class MotionCorridor
{
    public static bool Contains(HexLayout layout, MotionCurve curve, IReadOnlySet<HexCoord> allowed, double radius, double step = 0.03)
    {
        foreach (var p in curve.Sample(step))
        {
            if (!CircleInside(layout, p, allowed, radius))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>True if the circle at <paramref name="p"/> overlaps no hex outside <paramref name="allowed"/>.</summary>
    public static bool CircleInside(HexLayout layout, PlaneVector p, IReadOnlySet<HexCoord> allowed, double radius)
    {
        var home = layout.PlaneToHex(p);
        if (!allowed.Contains(home))
        {
            return false;
        }

        foreach (var dir in HexDirections.All)
        {
            var other = home.Neighbor(dir);
            if (!allowed.Contains(other) && DistanceToHex(layout, other, p) < radius - 1e-9)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Distance from <paramref name="p"/> to the hexagon of <paramref name="hex"/> (0 inside).</summary>
    public static double DistanceToHex(HexLayout layout, HexCoord hex, PlaneVector p)
    {
        if (layout.PlaneToHex(p) == hex)
        {
            return 0;
        }

        var best = double.MaxValue;
        for (var i = 0; i < 6; i++)
        {
            best = Math.Min(best, DistanceToSegment(p, layout.Corner(hex, i), layout.Corner(hex, (i + 1) % 6)));
        }

        return best;
    }

    private static double DistanceToSegment(PlaneVector p, PlaneVector a, PlaneVector b)
    {
        var ab = b - a;
        var len2 = (ab.X * ab.X) + (ab.Z * ab.Z);
        var t = len2 < 1e-12 ? 0 : Math.Clamp((((p.X - a.X) * ab.X) + ((p.Z - a.Z) * ab.Z)) / len2, 0, 1);
        return p.DistanceTo(a + (ab * t));
    }
}
