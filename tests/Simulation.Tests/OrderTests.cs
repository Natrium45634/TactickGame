using System.Linq;
using HexTactics.Core.Hex;
using HexTactics.Core.Orders;
using HexTactics.Core.Spawning;
using HexTactics.Core.Units;
using HexTactics.Simulation;
using HexTactics.Tests.Shared;
using Xunit;

namespace HexTactics.Simulation.Tests;

public class OrderTests
{
    private readonly GameSimulation _sim = new(TestMaps.LoadSmall());
    private readonly UnitId _unit;

    public OrderTests()
    {
        _unit = _sim.Spawn(SpawnRequest.AtCell(HexCoord.FromOffset(1, 1))).Unit!.Value;
    }

    private static HexCoord C(int col, int row) => HexCoord.FromOffset(col, row);

    [Fact]
    public void Replace_creates_first_waypoint_with_sequence_one()
    {
        var r = _sim.IssueMoveOrder(_unit, C(4, 2), OrderMode.Replace);
        Assert.True(r.Success);
        Assert.Equal(1, r.Waypoint!.Sequence);
        Assert.Equal(C(4, 2), Assert.Single(_sim.State.GetUnit(_unit).Orders.Items).Cell);
    }

    [Fact]
    public void Append_builds_chain_in_order_without_limit()
    {
        _sim.IssueMoveOrder(_unit, C(4, 2), OrderMode.Replace);
        _sim.IssueMoveOrder(_unit, C(5, 3), OrderMode.Append);
        _sim.IssueMoveOrder(_unit, C(3, 3), OrderMode.Append);
        for (var i = 0; i < 20; i++)
        {
            _sim.IssueMoveOrder(_unit, i % 2 == 0 ? C(4, 0) : C(5, 0), OrderMode.Append);
        }

        var items = _sim.State.GetUnit(_unit).Orders.Items;
        Assert.Equal(23, items.Count);
        Assert.Equal(new[] { C(4, 2), C(5, 3), C(3, 3) }, items.Take(3).Select(w => w.Cell));
        Assert.Equal(Enumerable.Range(1, 23), items.Select(w => w.Sequence));
        Assert.Equal(23, items.Select(w => w.Id).Distinct().Count());
    }

    [Fact]
    public void Replace_discards_pending_queue_and_restarts_numbering()
    {
        _sim.IssueMoveOrder(_unit, C(4, 2), OrderMode.Replace);
        _sim.IssueMoveOrder(_unit, C(5, 3), OrderMode.Append);
        var r = _sim.IssueMoveOrder(_unit, C(3, 3), OrderMode.Replace);
        var wp = Assert.Single(_sim.State.GetUnit(_unit).Orders.Items);
        Assert.Equal(C(3, 3), wp.Cell);
        Assert.Equal(1, wp.Sequence);
        Assert.Equal(wp, r.Waypoint);
    }

    [Fact]
    public void Append_on_empty_queue_starts_from_unit()
    {
        var r = _sim.IssueMoveOrder(_unit, C(4, 2), OrderMode.Append);
        Assert.True(r.Success);
        Assert.Equal(1, r.Waypoint!.Sequence);
    }

    [Theory]
    [InlineData(-1, 0, WaypointError.OutOfBounds)]
    [InlineData(6, 3, WaypointError.OutOfBounds)]
    [InlineData(2, 1, WaypointError.NotWalkable)]
    [InlineData(1, 1, WaypointError.SameAsSource)]
    public void Invalid_targets_are_rejected_and_queue_unchanged(int col, int row, WaypointError expected)
    {
        _sim.IssueMoveOrder(_unit, C(4, 2), OrderMode.Replace);
        var before = _sim.State.GetUnit(_unit).Orders.Items.ToList();
        var r = _sim.IssueMoveOrder(_unit, C(col, row), col == 1 && row == 1 ? OrderMode.Replace : OrderMode.Append);
        Assert.False(r.Success);
        Assert.Equal(expected, r.Error);
        Assert.Equal(before, _sim.State.GetUnit(_unit).Orders.Items);
    }

    [Fact]
    public void Waypoint_on_cell_occupied_by_another_unit_is_rejected()
    {
        _sim.Spawn(SpawnRequest.AtCell(C(4, 2)));
        var r = _sim.IssueMoveOrder(_unit, C(4, 2), OrderMode.Replace);
        Assert.Equal(WaypointError.Occupied, r.Error);
        Assert.True(_sim.State.GetUnit(_unit).Orders.IsEmpty);
    }

    [Fact]
    public void Chain_may_return_to_units_own_cell_but_not_repeat_previous_waypoint()
    {
        _sim.IssueMoveOrder(_unit, C(4, 2), OrderMode.Replace);
        Assert.Equal(WaypointError.SameAsSource, _sim.IssueMoveOrder(_unit, C(4, 2), OrderMode.Append).Error);
        Assert.True(_sim.IssueMoveOrder(_unit, C(1, 1), OrderMode.Append).Success);
    }

    [Fact]
    public void Rotate_in_place_only_without_pending_waypoints()
    {
        Assert.True(_sim.RotateInPlace(_unit, HexDirection.SouthWest).Success);
        Assert.Equal(HexDirection.SouthWest, _sim.State.GetUnit(_unit).Facing);
        Assert.Equal(C(1, 1), _sim.State.GetUnit(_unit).Cell);

        _sim.IssueMoveOrder(_unit, C(4, 2), OrderMode.Replace);
        Assert.False(_sim.RotateInPlace(_unit, HexDirection.East).Success);
        Assert.Equal(HexDirection.SouthWest, _sim.State.GetUnit(_unit).Facing);
    }

    [Fact]
    public void Orders_emit_events()
    {
        _sim.DrainEvents();
        _sim.IssueMoveOrder(_unit, C(4, 2), OrderMode.Replace);
        _sim.IssueMoveOrder(_unit, C(2, 1), OrderMode.Append);
        var events = _sim.DrainEvents();
        Assert.IsType<OrderIssued>(events[0]);
        Assert.Equal(WaypointError.NotWalkable, Assert.IsType<OrderRejected>(events[1]).Error);
    }
}
