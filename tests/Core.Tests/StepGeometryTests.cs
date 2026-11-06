using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Hex;
using HexTactics.Core.Motion;
using Xunit;

namespace HexTactics.Core.Tests;

/// <summary>Geometry of smoothed hex steps (spec part 3 §2.1, §2.2).</summary>
public class StepGeometryTests
{
    private readonly HexLayout _layout = new(1.0);

    private static readonly HexCoord Centre = new(0, 0);

    public static IEnumerable<object[]> Turns()
    {
        // Entering the centre hex from direction (d+3) and leaving towards d+k, k = 0 (straight), ±1 (60°), ±2 (120°).
        foreach (var d in HexDirections.All)
        {
            foreach (var k in new[] { 0, 1, -1, 2, -2 })
            {
                yield return new object[] { d, k };
            }
        }
    }

    [Fact]
    public void Straight_step_is_the_line_between_centres_and_switches_half_way()
    {
        var from = new HexCoord(0, 0);
        var to = from.Neighbor(HexDirection.SouthEast);
        var plan = StepGeometry.Build(_layout, from, to, _layout.HexToPlane(from), null, null, null);

        Assert.True(plan.Smoothed);
        Assert.Equal(_layout.NeighborDistance, plan.Curve.Length, 3);
        Assert.Equal(plan.Curve.Length / 2, plan.SwitchArc, 3);
        Assert.Equal(_layout.HexToPlane(to), plan.End);
    }

    [Theory]
    [MemberData(nameof(Turns))]
    public void Turn_through_a_hex_stays_inside_the_path_hexes_even_with_every_other_neighbour_blocked(HexDirection d, int k)
    {
        var from = Centre.Neighbor(d.Opposite());
        var next = Centre.Neighbor(d.RotateClockwise(k));
        var corridor = new HashSet<HexCoord> { from, Centre, next };

        // Step into the centre hex turning towards `next`, then the step out of it, continuing smoothly.
        var first = StepGeometry.Build(_layout, from, Centre, _layout.HexToPlane(from), null, null, next);
        var second = StepGeometry.Build(_layout, Centre, next, first.End, first.EndTangent, first.EndControl, null);

        Assert.True(first.Smoothed);
        Assert.True(second.Smoothed);
        Assert.True(MotionCorridor.Contains(_layout, first.Curve, corridor, StepGeometry.DefaultBodyRadius));
        Assert.True(MotionCorridor.Contains(_layout, second.Curve, corridor, StepGeometry.DefaultBodyRadius));

        // The hex sequence of the body centre is exactly from → centre → next (no skipped or extra hex).
        var hexes = first.Curve.Sample(0.02).Concat(second.Curve.Sample(0.02)).Select(_layout.PlaneToHex).ToList();
        var order = hexes.Where((h, i) => i == 0 || h != hexes[i - 1]).ToList();
        Assert.Equal(new[] { from, Centre, next }, order);

        if (k != 0)
        {
            // A real turn is rounded: the body does not pass through the centre of the turning hex.
            Assert.True(first.End.DistanceTo(_layout.HexToPlane(Centre)) > 0.1);
        }

        // Smooth: the tangent leaving the turning hex continues the tangent that ended the previous step.
        Assert.True((second.Curve.TangentAtArc(0) - first.EndTangent).Length < 0.05);
    }

    [Fact]
    public void Edge_crossing_is_the_shared_edge_midpoint_and_the_logical_switch_point()
    {
        var from = new HexCoord(2, 1);
        var to = from.Neighbor(HexDirection.North);
        var next = to.Neighbor(HexDirection.NorthEast);
        var plan = StepGeometry.Build(_layout, from, to, _layout.HexToPlane(from), null, null, next);

        var atSwitch = plan.Curve.PointAtArc(plan.SwitchArc);
        Assert.True(atSwitch.DistanceTo(_layout.EdgeMidpoint(from, HexDirection.North)) < 1e-6);
        Assert.Equal(from, _layout.PlaneToHex(plan.Curve.PointAtArc(plan.SwitchArc - 0.01)));
        Assert.Equal(to, _layout.PlaneToHex(plan.Curve.PointAtArc(plan.SwitchArc + 0.01)));
    }

    [Fact]
    public void Without_a_continuation_the_step_ends_exactly_in_the_centre_waypoint_anchor()
    {
        var from = new HexCoord(0, 0);
        var to = from.Neighbor(HexDirection.NorthWest);
        var plan = StepGeometry.Build(_layout, from, to, _layout.HexToPlane(from), null, null, null);
        Assert.True(plan.End.DistanceTo(_layout.HexToPlane(to)) < 1e-9);
    }

    [Fact]
    public void A_curve_leaving_the_corridor_falls_back_to_the_lines_through_the_centres()
    {
        var from = new HexCoord(0, 0);
        var to = from.Neighbor(HexDirection.North);

        // Start near the south-east edge heading away from the target: the smooth curve would swing out of the hex.
        var start = _layout.HexToPlane(from) + (HexLayout.DirectionVector(HexDirection.SouthEast) * 0.5);
        var plan = StepGeometry.Build(_layout, from, to, start, HexLayout.DirectionVector(HexDirection.SouthEast), 2.0, null);

        Assert.False(plan.Smoothed);
        Assert.True(MotionCorridor.Contains(_layout, plan.Curve, new HashSet<HexCoord> { from, to }, StepGeometry.DefaultBodyRadius));
        Assert.Contains(plan.Curve.Segments, seg => seg.P3.DistanceTo(_layout.HexToPlane(from)) < 1e-9); // via the centre
    }

    [Fact]
    public void Retarget_continues_from_the_current_point_without_a_jump()
    {
        var from = new HexCoord(0, 0);
        var to = from.Neighbor(HexDirection.North);
        var plan = StepGeometry.Build(_layout, from, to, _layout.HexToPlane(from), null, null, to.Neighbor(HexDirection.NorthEast));
        var s = plan.SwitchArc + 0.2; // already inside `to`
        var here = plan.Curve.PointAtArc(s);
        var re = StepGeometry.Retarget(_layout, from, to, here, plan.Curve.TangentAtArc(s), true, to.Neighbor(HexDirection.NorthWest));

        Assert.True(re.Curve.Start.DistanceTo(here) < 1e-9);
        Assert.Equal(0, re.SwitchArc);
        Assert.True(MotionCorridor.Contains(_layout, re.Curve, new HashSet<HexCoord> { from, to, to.Neighbor(HexDirection.NorthWest) }, StepGeometry.DefaultBodyRadius));
    }

    [Fact]
    public void Corridor_check_counts_the_body_radius_not_only_the_centre_point()
    {
        var hex = new HexCoord(0, 0);
        var only = new HashSet<HexCoord> { hex };
        var nearEdge = _layout.HexToPlane(hex) + (HexLayout.DirectionVector(HexDirection.North) * 0.75); // 0.116 from the edge
        Assert.Equal(hex, _layout.PlaneToHex(nearEdge));
        Assert.False(MotionCorridor.CircleInside(_layout, nearEdge, only, StepGeometry.DefaultBodyRadius));
        Assert.True(MotionCorridor.CircleInside(_layout, _layout.HexToPlane(hex), only, StepGeometry.DefaultBodyRadius));
    }
}
