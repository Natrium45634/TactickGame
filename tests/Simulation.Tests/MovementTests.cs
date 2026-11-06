using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Hex;
using HexTactics.Core.Map;
using HexTactics.Core.Orders;
using HexTactics.Core.Pathfinding;
using HexTactics.Core.Spawning;
using HexTactics.Core.Units;
using HexTactics.Simulation;
using HexTactics.Tests.Shared;
using Xunit;

namespace HexTactics.Simulation.Tests;

public class MovementTests
{
    private static HexCoord C(int col, int row) => HexCoord.FromOffset(col, row);

    private static UnitId Spawn(GameSimulation sim, HexCoord cell, HexDirection facing = HexDirection.NorthEast) =>
        sim.Spawn(SpawnRequest.AtCell(cell, facing: facing)).Unit!.Value;

    /// <summary>Steps until the unit has no orders (or the limit), checking invariants on every tick.</summary>
    private static List<SimulationEvent> RunUntilIdle(GameSimulation sim, UnitId id, int maxTicks = 3000)
    {
        var events = new List<SimulationEvent>();
        for (var i = 0; i < maxTicks; i++)
        {
            sim.Step();
            events.AddRange(sim.DrainEvents());
            AssertInvariants(sim);
            var u = sim.State.GetUnit(id);
            if (u.Orders.IsEmpty && !u.Motion.IsStepping)
            {
                return events;
            }
        }

        throw new Xunit.Sdk.XunitException("unit did not finish its orders");
    }

    /// <summary>Each unit is in exactly one logical cell and no two units share a cell.</summary>
    private static void AssertInvariants(GameSimulation sim)
    {
        var cells = sim.State.Units.Select(u => u.Cell).ToList();
        Assert.Equal(cells.Count, cells.Distinct().Count());
        foreach (var u in sim.State.Units)
        {
            Assert.Same(u, sim.State.UnitAt(u.Cell));
            Assert.True(sim.State.Grid.IsWalkable(u.Cell));
        }
    }

    [Fact]
    public void Unit_moves_to_waypoint_at_one_hex_per_second()
    {
        var sim = new GameSimulation(TestMaps.Empty());
        var id = Spawn(sim, C(2, 5));
        Assert.True(sim.IssueMoveOrder(id, C(6, 5), OrderMode.Replace).Success);

        var ticks = 0;
        while (!sim.State.GetUnit(id).Orders.IsEmpty)
        {
            sim.Step();
            ticks++;
        }

        // 4 hexes at 1 hex/s (spec part 3 §2.4) = 4 s = 120 ticks of 1/30 s.
        Assert.InRange(ticks, 119, 121);
        Assert.Equal(C(6, 5), sim.State.GetUnit(id).Cell);
        Assert.Equal(MovementStatus.Idle, sim.State.GetUnit(id).Motion.Status);
    }

    [Fact]
    public void Logical_cell_switches_exactly_when_the_body_crosses_the_shared_edge()
    {
        var sim = new GameSimulation(TestMaps.Empty());
        var id = Spawn(sim, C(2, 5));
        sim.IssueMoveOrder(id, C(3, 5), OrderMode.Replace);
        var u = sim.State.GetUnit(id);
        while (true)
        {
            sim.Step();
            Assert.Equal(0.5, u.Motion.SwitchAt, 6); // a straight step crosses the edge half-way
            Assert.Equal(u.Cell, sim.Layout.PlaneToHex(sim.PositionOf(u))); // drawn position and logical cell agree
            if (u.Motion.IsStepping && !u.Motion.CellSwitched)
            {
                Assert.True(u.Motion.Progress < u.Motion.SwitchAt + 1e-6);
                Assert.Equal(C(2, 5), u.Cell);
                Assert.True(sim.State.IsReserved(C(3, 5)));
            }
            else if (u.Motion.IsStepping)
            {
                Assert.True(u.Motion.Progress >= u.Motion.SwitchAt - 1e-6, "switched at the edge, not earlier");
                Assert.Equal(C(3, 5), u.Cell);
                Assert.True(sim.State.IsReserved(C(2, 5)), "origin stays reserved until the body has left");
            }
            else
            {
                break;
            }
        }

        Assert.Equal(C(3, 5), u.Cell);
        Assert.False(sim.State.IsReserved(C(2, 5)));
        Assert.False(sim.State.IsReserved(C(3, 5)));
    }

    [Fact]
    public void Waypoints_are_visited_strictly_in_order()
    {
        var sim = new GameSimulation(TestMaps.LoadPrototype());
        var id = Spawn(sim, C(1, 8));
        var wps = new[] { C(3, 1), C(10, 0), C(10, 8), C(2, 7) };
        sim.IssueMoveOrder(id, wps[0], OrderMode.Replace);
        foreach (var w in wps.Skip(1))
        {
            Assert.True(sim.IssueMoveOrder(id, w, OrderMode.Append).Success, w.ToString());
        }

        var events = RunUntilIdle(sim, id);
        var reached = events.OfType<WaypointReached>().Select(e => e.Waypoint.Cell).ToList();
        Assert.Equal(wps, reached);
        Assert.Equal(new[] { 1, 2, 3, 4 }, events.OfType<WaypointReached>().Select(e => e.Waypoint.Sequence));
    }

    [Fact]
    public void Unit_detours_around_obstacle_along_a_shortest_path()
    {
        var sim = new GameSimulation(TestMaps.LoadPrototype());
        var start = C(2, 4);
        var goal = C(6, 4);
        var id = Spawn(sim, start);
        sim.IssueMoveOrder(id, goal, OrderMode.Replace);
        var events = RunUntilIdle(sim, id);
        var visited = events.OfType<UnitEnteredCell>().Select(e => e.To).ToList();
        Assert.Equal(goal, visited[^1]);
        Assert.Equal(Pathfinder.FindPath(sim.State.Grid, start, goal)!.Count - 1, visited.Count);
        Assert.DoesNotContain(visited, c => sim.State.Grid[c].Type == TileType.Blocked);
    }

    [Fact]
    public void Facing_follows_each_step()
    {
        var sim = new GameSimulation(TestMaps.LoadPrototype());
        var id = Spawn(sim, C(2, 4), HexDirection.SouthWest);
        sim.IssueMoveOrder(id, C(6, 4), OrderMode.Replace);
        var u = sim.State.GetUnit(id);
        for (var i = 0; i < 600 && !u.Orders.IsEmpty; i++)
        {
            sim.Step();
            if (u.Motion.IsStepping)
            {
                Assert.Equal(u.Motion.StepFrom!.Value.DirectionTo(u.Motion.StepTo!.Value), u.Facing);
            }
        }
    }

    [Fact]
    public void Unreachable_waypoint_is_rejected()
    {
        var sim = new GameSimulation(TestMaps.LoadPrototype());
        var id = Spawn(sim, C(0, 0));
        var r = sim.IssueMoveOrder(id, C(8, 3), OrderMode.Replace); // isolated pocket
        Assert.Equal(WaypointError.Unreachable, r.Error);
        Assert.True(sim.State.GetUnit(id).Orders.IsEmpty);
    }

    [Fact]
    public void When_path_disappears_unit_stops_keeps_orders_and_resumes_later()
    {
        var sim = new GameSimulation(TestMaps.Empty(6, 3));
        var id = Spawn(sim, C(0, 1));
        sim.IssueMoveOrder(id, C(5, 1), OrderMode.Replace);
        sim.Step();

        // Wall off column 3 completely while the unit is on its way.
        foreach (var r in new[] { 0, 1, 2 })
        {
            sim.State.Grid.SetTile(C(3, r), TileType.Blocked);
        }

        var u = sim.State.GetUnit(id);
        var events = new List<SimulationEvent>();
        for (var i = 0; i < 200; i++)
        {
            sim.Step();
            events.AddRange(sim.DrainEvents());
            AssertInvariants(sim);
        }

        Assert.Equal(MovementStatus.Blocked, u.Motion.Status);
        Assert.Equal(BlockReason.NoPath, u.Motion.BlockReason);
        Assert.False(u.Motion.IsStepping, "stops on a hex centre");
        Assert.Single(u.Orders.Items);
        Assert.Single(events.OfType<UnitBlocked>());
        Assert.True(u.Cell.ToOffset().Col < 3);

        // Re-open a gap: the unit continues without teleporting.
        sim.State.Grid.SetTile(C(3, 2), TileType.Walkable);
        events.AddRange(RunUntilIdle(sim, id));
        Assert.Contains(events, e => e is UnitUnblocked);
        Assert.Equal(C(5, 1), u.Cell);
    }

    [Fact]
    public void Occupied_waypoint_blocks_with_reason_until_freed()
    {
        var sim = new GameSimulation(TestMaps.Empty());
        var a = Spawn(sim, C(1, 1));
        sim.IssueMoveOrder(a, C(6, 1), OrderMode.Replace);
        var b = Spawn(sim, C(6, 1)); // something stands on the waypoint after the order was given
        for (var i = 0; i < 200; i++)
        {
            sim.Step();
        }

        var ua = sim.State.GetUnit(a);
        Assert.Equal(MovementStatus.Blocked, ua.Motion.Status);
        Assert.Equal(BlockReason.TargetOccupied, ua.Motion.BlockReason);
        Assert.Single(ua.Orders.Items);

        sim.IssueMoveOrder(b, C(6, 6), OrderMode.Replace);
        RunUntilIdle(sim, a);
        Assert.Equal(C(6, 1), ua.Cell);
    }

    [Fact]
    public void Units_never_share_a_hex_while_crossing()
    {
        var sim = new GameSimulation(TestMaps.Empty());
        var a = Spawn(sim, C(1, 5));
        var b = Spawn(sim, C(9, 5));
        sim.IssueMoveOrder(a, C(9, 4), OrderMode.Replace);
        sim.IssueMoveOrder(b, C(1, 4), OrderMode.Replace);
        for (var i = 0; i < 1000; i++)
        {
            sim.Step();
            AssertInvariants(sim);
        }

        Assert.Equal(C(9, 4), sim.State.GetUnit(a).Cell);
        Assert.Equal(C(1, 4), sim.State.GetUnit(b).Cell);
    }

    [Fact]
    public void New_order_from_unit_while_moving_applies_from_next_hex_centre()
    {
        var sim = new GameSimulation(TestMaps.Empty());
        var id = Spawn(sim, C(2, 5));
        sim.IssueMoveOrder(id, C(8, 5), OrderMode.Replace);
        for (var i = 0; i < 40; i++)
        {
            sim.Step(); // 1 1/3 steps at 1 hex/s
        }

        var u = sim.State.GetUnit(id);
        Assert.True(u.Motion.IsStepping);
        var stepTo = u.Motion.StepTo!.Value;
        var progress = u.Motion.Progress;
        Assert.Equal(stepTo, sim.CommandAnchor(id));

        Assert.True(sim.IssueMoveOrder(id, C(4, 8), OrderMode.Replace).Success);
        Assert.Equal(stepTo, u.Motion.StepTo);           // the running step is not broken
        Assert.Equal(progress, u.Motion.Progress);

        Assert.False(u.Motion.CellSwitched);
        sim.DrainEvents(); // forget the events of the first, already finished step
        var events = RunUntilIdle(sim, id);
        var entered = events.OfType<UnitEnteredCell>().Select(e => e.To).ToList();
        Assert.Equal(stepTo, entered[0]); // first completes the step it was in, no jump

        Assert.Equal(C(4, 8), u.Cell);
        Assert.DoesNotContain(events.OfType<WaypointReached>(), e => e.Waypoint.Cell == C(8, 5));
    }

    [Fact]
    public void Appending_while_moving_extends_the_queue()
    {
        var sim = new GameSimulation(TestMaps.Empty());
        var id = Spawn(sim, C(2, 5));
        sim.IssueMoveOrder(id, C(5, 5), OrderMode.Replace);
        for (var i = 0; i < 10; i++)
        {
            sim.Step();
        }

        Assert.True(sim.IssueMoveOrder(id, C(5, 8), OrderMode.Append).Success);
        var events = RunUntilIdle(sim, id);
        Assert.Equal(new[] { C(5, 5), C(5, 8) }, events.OfType<WaypointReached>().Select(e => e.Waypoint.Cell));
    }

    [Fact]
    public void Rotate_only_is_rejected_while_moving()
    {
        var sim = new GameSimulation(TestMaps.Empty());
        var id = Spawn(sim, C(2, 5));
        sim.IssueMoveOrder(id, C(3, 5), OrderMode.Replace);
        sim.Step();
        Assert.False(sim.RotateInPlace(id, HexDirection.SouthWest).Success);
    }

    [Fact]
    public void Spawning_on_a_hex_a_unit_is_stepping_into_is_rejected()
    {
        var sim = new GameSimulation(TestMaps.Empty());
        var id = Spawn(sim, C(2, 5));
        sim.IssueMoveOrder(id, C(3, 5), OrderMode.Replace);
        sim.Step();
        var r = sim.Spawn(SpawnRequest.AtCell(C(3, 5)));
        Assert.Equal(SpawnError.Occupied, r.Error);
        Assert.Equal(id, r.Occupant);
    }

    [Fact]
    public void Simulation_is_frame_rate_independent()
    {
        static GameSimulation Run(double frame, int frames)
        {
            var sim = new GameSimulation(TestMaps.LoadPrototype());
            var id = sim.Spawn(SpawnRequest.AtCell(C(1, 8))).Unit!.Value;
            sim.IssueMoveOrder(id, C(10, 1), OrderMode.Replace);
            for (var i = 0; i < frames; i++)
            {
                sim.Advance(frame);
            }

            return sim;
        }

        var at60 = Run(1.0 / 60, 120);   // 2 s at 60 FPS
        var at30 = Run(1.0 / 30, 60);    // 2 s at 30 FPS
        var at144 = Run(1.0 / 144, 288); // 2 s at 144 FPS
        var u60 = at60.State.Units.Single();
        Assert.Equal(60, at60.Tick);
        Assert.Equal(at60.Tick, at30.Tick);
        Assert.Equal(at60.Tick, at144.Tick);
        foreach (var other in new[] { at30.State.Units.Single(), at144.State.Units.Single() })
        {
            Assert.Equal(u60.Cell, other.Cell);
            Assert.Equal(u60.Motion.Progress, other.Motion.Progress, 9);
            Assert.Equal(u60.Facing, other.Facing);
        }
    }

    [Fact]
    public void Clock_caps_ticks_after_a_long_hitch()
    {
        var clock = new SimulationClock(1.0 / 30, maxTicksPerAdvance: 8);
        Assert.Equal(8, clock.Accumulate(5.0));
        Assert.Equal(0, clock.Accumulate(0));
        Assert.Equal(1, clock.Accumulate(1.0 / 30));
        Assert.InRange(clock.Alpha, 0, 1);
    }
}
