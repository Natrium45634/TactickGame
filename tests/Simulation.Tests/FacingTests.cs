using System;
using System.Linq;
using HexTactics.Core.Hex;
using HexTactics.Core.Spawning;
using HexTactics.Simulation;
using HexTactics.Tests.Shared;
using Xunit;

namespace HexTactics.Simulation.Tests;

public class FacingTests
{
    [Fact]
    public void There_are_exactly_six_facings_and_each_points_to_a_distinct_neighbor()
    {
        Assert.Equal(6, Enum.GetValues<HexDirection>().Length);
        var c = HexCoord.FromOffset(5, 5);
        Assert.Equal(6, HexDirections.All.Select(c.Neighbor).Distinct().Count());
    }

    [Fact]
    public void Unit_can_be_turned_to_each_of_six_directions_without_moving()
    {
        var sim = new GameSimulation(TestMaps.Empty());
        var cell = HexCoord.FromOffset(5, 5);
        var id = sim.Spawn(SpawnRequest.AtCell(cell)).Unit!.Value;
        foreach (var dir in HexDirections.All)
        {
            Assert.True(sim.SetFacing(id, dir).Success);
            Assert.Equal(dir, sim.State.GetUnit(id).Facing);
            Assert.Equal(cell, sim.State.GetUnit(id).Cell);
        }
    }

    [Fact]
    public void Invalid_facing_or_unit_is_rejected()
    {
        var sim = new GameSimulation(TestMaps.Empty());
        var id = sim.Spawn(SpawnRequest.AtCell(HexCoord.FromOffset(1, 1), facing: HexDirection.West)).Unit!.Value;
        Assert.False(sim.SetFacing(id, (HexDirection)6).Success);
        Assert.False(sim.SetFacing(new Core.Units.UnitId(999), HexDirection.East).Success);
        Assert.Equal(HexDirection.West, sim.State.GetUnit(id).Facing);
    }

    [Fact]
    public void Facing_change_emits_event_once()
    {
        var sim = new GameSimulation(TestMaps.Empty());
        var id = sim.Spawn(SpawnRequest.AtCell(HexCoord.FromOffset(1, 1), facing: HexDirection.East)).Unit!.Value;
        sim.DrainEvents();
        sim.SetFacing(id, HexDirection.SouthWest);
        sim.SetFacing(id, HexDirection.SouthWest);
        var e = Assert.Single(sim.DrainEvents());
        Assert.Equal(new FacingChanged(0, id, HexDirection.SouthWest), e);
    }

    [Theory]
    [InlineData(0, 5, HexDirection.East)]   // left edge looks east
    [InlineData(11, 4, HexDirection.West)]  // right edge looks west
    public void Default_facing_points_towards_map_center(int col, int row, HexDirection expected)
    {
        var sim = new GameSimulation(TestMaps.Empty());
        var id = sim.Spawn(SpawnRequest.AtCell(HexCoord.FromOffset(col, row))).Unit!.Value;
        Assert.Equal(expected, sim.State.GetUnit(id).Facing);
    }

    [Fact]
    public void Explicit_facing_in_request_is_used()
    {
        var sim = new GameSimulation(TestMaps.Empty());
        var id = sim.Spawn(SpawnRequest.AtCell(HexCoord.FromOffset(0, 5), facing: HexDirection.NorthWest)).Unit!.Value;
        Assert.Equal(HexDirection.NorthWest, sim.State.GetUnit(id).Facing);
    }
}
