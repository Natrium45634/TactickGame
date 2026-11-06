using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Hex;
using HexTactics.Core.Motion;
using HexTactics.Core.Orders;
using HexTactics.Core.Spawning;
using HexTactics.Core.Units;
using HexTactics.Simulation;
using HexTactics.Tests.Shared;
using Xunit;

namespace HexTactics.Simulation.Tests;

/// <summary>Continuous, smoothed movement over the discrete hex path (spec part 3 §2, acceptance 1–6).</summary>
public class MotionTests
{
    private static HexCoord C(int col, int row) => HexCoord.FromOffset(col, row);

    private static UnitId Spawn(GameSimulation sim, int col, int row, Side side = Side.Player) =>
        sim.Spawn(SpawnRequest.AtCell(C(col, row), side, HexDirection.North)).Unit!.Value;

    /// <summary>Runs until the unit is idle, recording its position and logical cell every tick.</summary>
    private static List<(PlaneVector Position, HexCoord Cell)> Record(GameSimulation sim, UnitId id, double maxSeconds = 60)
    {
        var u = sim.State.GetUnit(id);
        var trace = new List<(PlaneVector, HexCoord)> { (sim.PositionOf(u), u.Cell) };
        for (var t = 0.0; t < maxSeconds && !(u.Orders.IsEmpty && !u.Motion.IsStepping && u.Orders.FinalFacing is null); t += 1.0 / 30)
        {
            sim.Step();
            trace.Add((sim.PositionOf(u), u.Cell));
        }

        return trace;
    }

    private static double MaxStep(IReadOnlyList<(PlaneVector Position, HexCoord Cell)> trace) =>
        trace.Zip(trace.Skip(1), (a, b) => a.Position.DistanceTo(b.Position)).DefaultIfEmpty(0).Max();

    [Fact]
    public void P1_route_with_turns_is_smooth_and_does_not_pass_the_centres_of_turning_hexes()
    {
        var sim = new GameSimulation(TestMaps.LoadPrototype());
        var id = Spawn(sim, 3, 5);
        Assert.True(sim.ApplyOrder(new IndividualOrder(id, new[] { C(7, 5) }, HexDirection.North)).Success); // around the wall end
        var path = new List<HexCoord>();
        var trace = Record(sim, id);

        // Logical cells in order (consecutive duplicates removed).
        foreach (var (_, cell) in trace)
        {
            if (path.Count == 0 || path[^1] != cell)
            {
                path.Add(cell);
            }
        }

        Assert.Equal(C(7, 5), path[^1]);
        var turning = new List<HexCoord>();
        for (var i = 1; i < path.Count - 1; i++)
        {
            if (path[i - 1].DirectionTo(path[i]) != path[i].DirectionTo(path[i + 1]))
            {
                turning.Add(path[i]);
            }
        }

        Assert.NotEmpty(turning);
        foreach (var hex in turning)
        {
            var centre = sim.Layout.HexToPlane(hex);
            var closest = trace.Min(p => p.Position.DistanceTo(centre));
            Assert.True(closest > 0.1, $"the body passed the centre of turning hex {hex} ({closest:0.000})");
        }

        // No jumps and no sharp corners between ticks (1 hex/s ≈ 0.058 per tick on a straight line).
        Assert.True(MaxStep(trace) < 0.07);
        for (var i = 2; i < trace.Count; i++)
        {
            var a = trace[i - 1].Position - trace[i - 2].Position;
            var b = trace[i].Position - trace[i - 1].Position;
            if (a.Length > 1e-6 && b.Length > 1e-6)
            {
                var cos = ((a.X * b.X) + (a.Z * b.Z)) / (a.Length * b.Length);
                Assert.True(cos > Math.Cos(15 * Math.PI / 180), $"sharp turn at tick {i}");
            }
        }
    }

    [Fact]
    public void P2_P3_body_never_touches_an_obstacle_or_a_hex_off_its_path_and_cells_follow_the_graph_path()
    {
        var sim = new GameSimulation(TestMaps.LoadPrototype());
        var id = Spawn(sim, 4, 2);
        var u = sim.State.GetUnit(id);

        // Around both ends of the wall and through the gap next to the rocks.
        Assert.True(sim.ApplyOrder(new IndividualOrder(id, new[] { C(6, 2), C(4, 8), C(10, 8) }, HexDirection.South)).Success);
        var visited = new List<HexCoord> { u.Cell };
        var layout = sim.Layout;
        for (var t = 0.0; t < 60 && !(u.Orders.IsEmpty && !u.Motion.IsStepping); t += 1.0 / 30)
        {
            sim.Step();
            var p = sim.PositionOf(u);

            // The body (radius 0.28) only overlaps the hexes of the running step and the hex it turns towards.
            var corridor = new HashSet<HexCoord> { u.Cell };
            if (u.Motion.StepFrom is { } f) corridor.Add(f);
            if (u.Motion.StepTo is { } to) corridor.Add(to);
            if (u.Motion.StepLookahead is { } la) corridor.Add(la);
            Assert.True(MotionCorridor.CircleInside(layout, p, corridor, StepGeometry.DefaultBodyRadius), $"body off the corridor at {p}");
            Assert.All(corridor, c => Assert.True(sim.State.Grid.IsWalkable(c)));

            // Drawn position and logical cell agree (one rule: crossing the shared edge).
            Assert.Equal(u.Cell, layout.PlaneToHex(p));
            if (visited[^1] != u.Cell)
            {
                Assert.Equal(1, visited[^1].DistanceTo(u.Cell)); // neighbours only: no hex is skipped
                visited.Add(u.Cell);
            }
        }

        Assert.Equal(C(10, 8), u.Cell);
        Assert.All(visited, c => Assert.True(sim.State.Grid.IsWalkable(c)));
    }

    [Fact]
    public void P4_waypoints_are_anchors_the_body_reaches_their_centres_in_order()
    {
        var sim = new GameSimulation(TestMaps.Empty());
        var id = Spawn(sim, 1, 1);
        var u = sim.State.GetUnit(id);
        var wps = new[] { C(4, 3), C(6, 1), C(8, 4) };
        Assert.True(sim.ApplyOrder(new IndividualOrder(id, wps, HexDirection.South)).Success);

        var reachedAt = new List<(HexCoord Cell, PlaneVector Position)>();
        for (var t = 0.0; t < 30 && !(u.Orders.IsEmpty && !u.Motion.IsStepping); t += 1.0 / 30)
        {
            sim.Step();
            foreach (var e in sim.DrainEvents())
            {
                if (e is WaypointReached r)
                {
                    reachedAt.Add((r.Waypoint.Cell, sim.PositionOf(u)));
                }
            }
        }

        Assert.Equal(wps, reachedAt.Select(r => r.Cell));
        Assert.All(reachedAt, r => Assert.True(r.Position.DistanceTo(sim.Layout.HexToPlane(r.Cell)) < 1e-9));
    }

    [Fact]
    public void P5_new_route_on_pause_continues_from_the_actual_position_without_a_jump_or_entering_a_taken_hex()
    {
        var sim = new GameSimulation(TestMaps.Empty());
        var id = Spawn(sim, 2, 5);
        var blocker = Spawn(sim, 4, 4); // stands next to the new route
        var u = sim.State.GetUnit(id);
        Assert.True(sim.ApplyOrder(new IndividualOrder(id, new[] { C(6, 2) }, HexDirection.North)).Success);

        // Pause in the middle of a step (after the cell switch), then turn the unit somewhere else.
        while (!(u.Motion.IsStepping && u.Motion.CellSwitched && u.Motion.Progress > 0.7))
        {
            sim.Advance(1.0 / 30);
        }

        sim.SetPaused(true);
        var frozen = sim.PositionOf(u);
        sim.Advance(5);
        Assert.Equal(frozen, sim.PositionOf(u));
        Assert.True(sim.ApplyOrder(new IndividualOrder(id, new[] { C(5, 6) }, HexDirection.South)).Success);
        Assert.Equal(frozen, sim.PositionOf(u)); // re-planning does not move the body
        sim.SetPaused(false);

        var trace = new List<(PlaneVector Position, HexCoord Cell)> { (frozen, u.Cell) };
        for (var t = 0.0; t < 20 && !(u.Orders.IsEmpty && !u.Motion.IsStepping); t += 1.0 / 30)
        {
            sim.Advance(1.0 / 30);
            trace.Add((sim.PositionOf(u), u.Cell));
            Assert.NotEqual(C(4, 4), u.Cell);
            Assert.True(MotionCorridor.DistanceToHex(sim.Layout, C(4, 4), sim.PositionOf(u)) >= StepGeometry.DefaultBodyRadius - 1e-9);
        }

        Assert.True(MaxStep(trace) < 0.07, "no jump after the new route");
        Assert.Equal(C(5, 6), u.Cell);
        Assert.Equal(C(4, 4), sim.State.GetUnit(blocker).Cell);
    }

    [Theory]
    [InlineData(1.0 / 30)]
    [InlineData(1.0 / 60)]
    [InlineData(1.0 / 144)]
    [InlineData(1.0 / 7)]
    public void P6_three_transitions_at_one_hex_per_second_take_three_game_seconds_at_any_frame_rate(double frame)
    {
        var sim = new GameSimulation(TestMaps.Empty());
        var id = Spawn(sim, 2, 5);
        var u = sim.State.GetUnit(id);
        Assert.Equal(1.0, u.SpeedHexesPerSecond);

        // A route with a turn (3 transitions) — rounding the corner does not make it faster.
        Assert.True(sim.ApplyOrder(new IndividualOrder(id, new[] { C(4, 4), C(4, 3) }, HexDirection.North)).Success);
        double? arrived = null;
        for (var i = 0; i < 100000 && arrived is null; i++)
        {
            sim.Advance(frame);
            if (u.Orders.IsEmpty && !u.Motion.IsStepping)
            {
                arrived = sim.GameTime;
            }
        }

        Assert.Equal(3.0, arrived!.Value, 2);
    }

    [Fact]
    public void Unit_comes_to_rest_in_the_centre_of_its_last_hex()
    {
        var sim = new GameSimulation(TestMaps.Empty());
        var id = Spawn(sim, 1, 1);
        Assert.True(sim.ApplyOrder(new IndividualOrder(id, new[] { C(5, 6) }, HexDirection.North)).Success);
        Record(sim, id);
        var u = sim.State.GetUnit(id);
        Assert.True(sim.PositionOf(u).DistanceTo(sim.Layout.HexToPlane(C(5, 6))) < 1e-9);
    }

    [Fact]
    public void Group_members_also_follow_smoothed_paths_and_keep_one_unit_per_hex()
    {
        var sim = new GameSimulation(TestMaps.LoadPrototype());
        var team = new[] { Spawn(sim, 2, 6), Spawn(sim, 3, 6), Spawn(sim, 1, 7) };
        Assert.True(sim.ApplyGroupOrder(new GroupOrder(team[0], team, new[] { C(3, 3), C(7, 1) }, HexDirection.South)).Success);
        for (var t = 0.0; t < 40; t += 1.0 / 30)
        {
            sim.Step();
            var cells = team.Select(m => sim.State.GetUnit(m).Cell).ToArray();
            Assert.Equal(cells.Length, cells.Distinct().Count());
            foreach (var m in team)
            {
                var unit = sim.State.GetUnit(m);
                Assert.Equal(unit.Cell, sim.Layout.PlaneToHex(sim.PositionOf(unit)));
            }
        }

        Assert.Equal(C(7, 1), sim.State.GetUnit(team[0]).Cell);
    }
}
