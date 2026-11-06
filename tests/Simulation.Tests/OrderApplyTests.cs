using System.Linq;
using HexTactics.Core.Hex;
using HexTactics.Core.Orders;
using HexTactics.Core.Spawning;
using HexTactics.Core.Units;
using HexTactics.Simulation;
using HexTactics.Tests.Shared;
using Xunit;

namespace HexTactics.Simulation.Tests;

/// <summary>Transactional orders with a mandatory final facing (spec part 2 §4.2, §4.3, §8.2, §9.5).</summary>
public class OrderApplyTests
{
    private static HexCoord C(int col, int row) => HexCoord.FromOffset(col, row);

    private static (GameSimulation Sim, UnitId Unit) Setup(HexCoord at, HexDirection facing = HexDirection.North)
    {
        var sim = new GameSimulation(TestMaps.LoadPrototype());
        var id = sim.Spawn(SpawnRequest.AtCell(at, facing: facing)).Unit!.Value;
        return (sim, id);
    }

    private static void RunUntilIdle(GameSimulation sim, UnitId id)
    {
        var u = sim.State.GetUnit(id);
        for (var i = 0; i < 5000 && !(u.Orders.IsEmpty && !u.Motion.IsStepping && u.Orders.FinalFacing is null); i++)
        {
            sim.Step();
        }
    }

    [Fact]
    public void Apply_replaces_queue_atomically_and_stores_final_facing()
    {
        var (sim, id) = Setup(C(1, 8));
        sim.IssueMoveOrder(id, C(3, 8), OrderMode.Replace);
        var r = sim.ApplyOrder(new IndividualOrder(id, new[] { C(2, 1), C(10, 1), C(10, 8) }, HexDirection.SouthWest));
        Assert.True(r.Success);
        var u = sim.State.GetUnit(id);
        Assert.Equal(new[] { C(2, 1), C(10, 1), C(10, 8) }, u.Orders.Items.Select(w => w.Cell));
        Assert.Equal(new[] { 1, 2, 3 }, u.Orders.Items.Select(w => w.Sequence));
        Assert.Equal(HexDirection.SouthWest, u.Orders.FinalFacing);
    }

    [Fact]
    public void Invalid_segment_anywhere_rejects_whole_order_and_keeps_previous_route()
    {
        var (sim, id) = Setup(C(1, 8));
        Assert.True(sim.ApplyOrder(new IndividualOrder(id, new[] { C(3, 8) }, HexDirection.North)).Success);
        sim.DrainEvents();

        var r = sim.ApplyOrder(new IndividualOrder(id, new[] { C(2, 1), C(8, 3), C(10, 8) }, HexDirection.South)); // (8,3) is isolated
        Assert.False(r.Success);
        Assert.Equal(1, r.FailedIndex);
        Assert.Equal(WaypointError.Unreachable, r.Error);
        var u = sim.State.GetUnit(id);
        Assert.Equal(new[] { C(3, 8) }, u.Orders.Items.Select(w => w.Cell));
        Assert.Equal(HexDirection.North, u.Orders.FinalFacing);
        Assert.IsType<OrderApplyRejected>(Assert.Single(sim.DrainEvents()));
    }

    [Fact]
    public void Final_facing_is_applied_on_arrival_regardless_of_last_step_direction()
    {
        var (sim, id) = Setup(C(2, 4));
        Assert.True(sim.ApplyOrder(new IndividualOrder(id, new[] { C(2, 6) }, HexDirection.NorthWest)).Success); // last step goes South
        RunUntilIdle(sim, id);
        var u = sim.State.GetUnit(id);
        Assert.Equal(C(2, 6), u.Cell);
        Assert.Equal(HexDirection.NorthWest, u.Facing);
        Assert.Null(u.Orders.FinalFacing);
    }

    [Fact]
    public void While_moving_facing_follows_steps_and_final_facing_comes_last()
    {
        var (sim, id) = Setup(C(2, 4));
        sim.ApplyOrder(new IndividualOrder(id, new[] { C(2, 7) }, HexDirection.NorthEast));
        var u = sim.State.GetUnit(id);
        for (var i = 0; i < 20; i++)
        {
            sim.Step();
        }

        Assert.True(u.Motion.IsStepping);
        Assert.Equal(HexDirection.South, u.Facing); // actual movement direction while moving
        RunUntilIdle(sim, id);
        Assert.Equal(HexDirection.NorthEast, u.Facing);
    }

    [Fact]
    public void Turn_in_place_does_not_change_the_hex()
    {
        var (sim, id) = Setup(C(6, 6));
        Assert.True(sim.ApplyOrder(IndividualOrder.TurnInPlace(id, HexDirection.SouthEast)).Success);
        sim.Step();
        var u = sim.State.GetUnit(id);
        Assert.Equal(HexDirection.SouthEast, u.Facing);
        Assert.Equal(C(6, 6), u.Cell);
        Assert.False(u.Motion.IsStepping);
    }

    [Fact]
    public void Turn_in_place_of_a_moving_unit_finishes_the_step_then_turns()
    {
        var (sim, id) = Setup(C(2, 4));
        sim.ApplyOrder(new IndividualOrder(id, new[] { C(2, 8) }, HexDirection.North));
        var u = sim.State.GetUnit(id);
        for (var i = 0; i < 5; i++)
        {
            sim.Step();
        }

        var stepTo = u.Motion.StepTo!.Value;
        Assert.True(sim.ApplyOrder(IndividualOrder.TurnInPlace(id, HexDirection.NorthWest)).Success);
        RunUntilIdle(sim, id);
        Assert.Equal(stepTo, u.Cell);
        Assert.Equal(HexDirection.NorthWest, u.Facing);
    }

    [Fact]
    public void Truncate_removes_waypoint_and_following_and_keeps_final_facing()
    {
        var (sim, id) = Setup(C(1, 8));
        sim.ApplyOrder(new IndividualOrder(id, new[] { C(2, 1), C(10, 1), C(10, 8) }, HexDirection.South));
        var u = sim.State.GetUnit(id);
        var second = u.Orders.Items[1].Id;
        Assert.True(sim.TruncateOrders(id, second).Success);
        Assert.Equal(new[] { C(2, 1) }, u.Orders.Items.Select(w => w.Cell));
        Assert.Equal(HexDirection.South, u.Orders.FinalFacing);
        RunUntilIdle(sim, id);
        Assert.Equal(C(2, 1), u.Cell);
        Assert.Equal(HexDirection.South, u.Facing);
        Assert.False(sim.TruncateOrders(id, 9999).Success);
    }

    [Fact]
    public void Orders_given_during_pause_wait_for_resume()
    {
        var (sim, id) = Setup(C(2, 4));
        sim.SetPaused(true);
        Assert.True(sim.ApplyOrder(new IndividualOrder(id, new[] { C(2, 6) }, HexDirection.North)).Success);
        for (var i = 0; i < 100; i++)
        {
            sim.Advance(1.0 / 30);
        }

        var u = sim.State.GetUnit(id);
        Assert.Equal(C(2, 4), u.Cell);
        Assert.False(u.Motion.IsStepping);
        sim.SetPaused(false);
        for (var i = 0; i < 200; i++)
        {
            sim.Advance(1.0 / 30);
        }

        Assert.Equal(C(2, 6), u.Cell);
        Assert.Equal(HexDirection.North, u.Facing);
    }
}
