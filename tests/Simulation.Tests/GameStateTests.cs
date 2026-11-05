using System;
using HexTactics.Core.Hex;
using HexTactics.Core.Units;
using HexTactics.Tests.Shared;
using Xunit;

namespace HexTactics.Simulation.Tests;

public class GameStateTests
{
    [Fact]
    public void One_unit_per_hex_is_enforced()
    {
        var state = new GameState(TestMaps.Empty());
        var c = HexCoord.FromOffset(2, 2);
        state.CreateUnit(Side.Player, c, HexDirection.East, 2);
        Assert.Throws<InvalidOperationException>(() => state.CreateUnit(Side.Player, c, HexDirection.East, 2));
        Assert.Equal(1, state.UnitCount);
    }

    [Fact]
    public void Move_transfers_occupancy_atomically()
    {
        var state = new GameState(TestMaps.Empty());
        var a = HexCoord.FromOffset(2, 2);
        var b = HexCoord.FromOffset(3, 2);
        var u = state.CreateUnit(Side.Player, a, HexDirection.East, 2);
        var v0 = state.OccupancyVersion;
        state.MoveUnit(u.Id, b);
        Assert.False(state.IsOccupied(a));
        Assert.Same(u, state.UnitAt(b));
        Assert.Equal(b, u.Cell);
        Assert.True(state.OccupancyVersion > v0);
    }

    [Fact]
    public void Move_into_occupied_or_blocked_cell_throws_and_keeps_state()
    {
        var state = new GameState(TestMaps.LoadSmall());
        var u1 = state.CreateUnit(Side.Player, HexCoord.FromOffset(3, 1), HexDirection.East, 2);
        var u2 = state.CreateUnit(Side.Player, HexCoord.FromOffset(4, 1), HexDirection.East, 2);
        Assert.Throws<InvalidOperationException>(() => state.MoveUnit(u1.Id, u2.Cell));
        Assert.Throws<InvalidOperationException>(() => state.MoveUnit(u1.Id, HexCoord.FromOffset(2, 1)));
        Assert.Equal(HexCoord.FromOffset(3, 1), u1.Cell);
        Assert.Same(u1, state.UnitAt(u1.Cell));
    }

    [Fact]
    public void Units_iterate_in_id_order()
    {
        var state = new GameState(TestMaps.Empty());
        state.CreateUnit(Side.Player, HexCoord.FromOffset(5, 5), HexDirection.East, 2);
        state.CreateUnit(Side.Player, HexCoord.FromOffset(0, 0), HexDirection.East, 2);
        state.CreateUnit(Side.Player, HexCoord.FromOffset(3, 1), HexDirection.East, 2);
        Assert.Equal(new[] { 1, 2, 3 }, System.Linq.Enumerable.Select(state.Units, u => u.Id.Value));
    }
}
