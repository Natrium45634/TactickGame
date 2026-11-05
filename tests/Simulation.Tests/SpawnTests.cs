using System.Linq;
using HexTactics.Core.Hex;
using HexTactics.Core.Spawning;
using HexTactics.Core.Units;
using HexTactics.Simulation;
using HexTactics.Tests.Shared;
using Xunit;

namespace HexTactics.Simulation.Tests;

public class SpawnTests
{
    private static GameSimulation NewSim() => new(TestMaps.LoadSmall());

    [Fact]
    public void Spawn_at_player_base_uses_first_free_base_cell()
    {
        var sim = NewSim();
        var r1 = sim.Spawn(SpawnRequest.AtBase());
        var r2 = sim.Spawn(SpawnRequest.AtBase());
        Assert.True(r1.Success);
        Assert.True(r2.Success);
        Assert.Equal(HexCoord.FromOffset(0, 2), r1.Cell);
        Assert.Equal(HexCoord.FromOffset(0, 3), r2.Cell);
    }

    [Fact]
    public void Spawn_in_edge_zone()
    {
        var sim = NewSim();
        var r = sim.Spawn(SpawnRequest.AtZone(1));
        Assert.True(r.Success);
        Assert.Equal(HexCoord.FromOffset(0, 0), r.Cell);
        Assert.Equal(TileTypeOf(sim, r.Cell!.Value), Core.Map.TileType.SpawnZone);
    }

    [Fact]
    public void Direct_placement_on_valid_cell()
    {
        var sim = NewSim();
        var cell = HexCoord.FromOffset(4, 2);
        var r = sim.Spawn(SpawnRequest.AtCell(cell));
        Assert.True(r.Success);
        Assert.Equal(cell, sim.State.GetUnit(r.Unit!.Value).Cell);
    }

    [Fact]
    public void All_three_methods_create_units_with_unique_ids_side_cell_facing_speed_and_empty_queue()
    {
        var sim = NewSim();
        var ids = new[]
        {
            sim.Spawn(SpawnRequest.AtBase()).Unit!.Value,
            sim.Spawn(SpawnRequest.AtZone(1)).Unit!.Value,
            sim.Spawn(SpawnRequest.AtCell(HexCoord.FromOffset(4, 2), Side.Enemy)).Unit!.Value,
        };
        Assert.Equal(3, ids.Distinct().Count());
        Assert.All(ids, id =>
        {
            var u = sim.State.GetUnit(id);
            Assert.Equal(2.0, u.SpeedHexesPerSecond);
            Assert.True(u.Orders.IsEmpty);
            Assert.Same(u, sim.State.UnitAt(u.Cell));
        });
        Assert.Equal(Side.Enemy, sim.State.GetUnit(ids[2]).Side);
    }

    [Fact]
    public void Placement_on_occupied_cell_is_rejected_without_changes()
    {
        var sim = NewSim();
        var cell = HexCoord.FromOffset(3, 3);
        var first = sim.Spawn(SpawnRequest.AtCell(cell));
        var version = sim.State.OccupancyVersion;

        var second = sim.Spawn(SpawnRequest.AtCell(cell));

        Assert.False(second.Success);
        Assert.Equal(SpawnError.Occupied, second.Error);
        Assert.Equal(first.Unit, second.Occupant);
        Assert.Equal(1, sim.State.UnitCount);
        Assert.Equal(cell, sim.State.GetUnit(first.Unit!.Value).Cell);
        Assert.Equal(version, sim.State.OccupancyVersion);
    }

    [Fact]
    public void Placement_on_blocked_cell_is_rejected()
    {
        var sim = NewSim();
        var r = sim.Spawn(SpawnRequest.AtCell(HexCoord.FromOffset(2, 1)));
        Assert.Equal(SpawnError.NotWalkable, r.Error);
        Assert.Equal(0, sim.State.UnitCount);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(6, 0)]
    [InlineData(0, 4)]
    [InlineData(100, -100)]
    public void Placement_outside_map_is_rejected(int col, int row)
    {
        var sim = NewSim();
        var r = sim.Spawn(SpawnRequest.AtCell(HexCoord.FromOffset(col, row)));
        Assert.Equal(SpawnError.OutOfBounds, r.Error);
        Assert.Equal(0, sim.State.UnitCount);
    }

    [Fact]
    public void Full_zone_and_full_base_are_rejected_and_nothing_is_relocated()
    {
        var sim = NewSim();
        Assert.True(sim.Spawn(SpawnRequest.AtZone(1)).Success);
        Assert.True(sim.Spawn(SpawnRequest.AtZone(1)).Success);
        var before = sim.State.Units.Select(u => (u.Id, u.Cell)).ToList();

        var zoneFull = sim.Spawn(SpawnRequest.AtZone(1));
        Assert.Equal(SpawnError.ZoneFull, zoneFull.Error);

        Assert.True(sim.Spawn(SpawnRequest.AtBase()).Success);
        Assert.True(sim.Spawn(SpawnRequest.AtBase()).Success);
        Assert.Equal(SpawnError.BaseFull, sim.Spawn(SpawnRequest.AtBase()).Error);

        Assert.Equal(before, sim.State.Units.Take(2).Select(u => (u.Id, u.Cell)).ToList());
        Assert.Equal(4, sim.State.UnitCount);
    }

    [Fact]
    public void Explicit_cell_must_belong_to_requested_zone()
    {
        var sim = NewSim();
        Assert.Equal(SpawnError.CellNotInZone, sim.Spawn(SpawnRequest.AtZone(1, cell: HexCoord.FromOffset(3, 0))).Error);
        Assert.Equal(SpawnError.CellNotInZone, sim.Spawn(SpawnRequest.AtBase(cell: HexCoord.FromOffset(3, 3))).Error);
        Assert.True(sim.Spawn(SpawnRequest.AtZone(1, cell: HexCoord.FromOffset(1, 0))).Success);
        Assert.Equal(SpawnError.Occupied, sim.Spawn(SpawnRequest.AtZone(1, cell: HexCoord.FromOffset(1, 0))).Error);
    }

    [Fact]
    public void Unknown_zone_and_missing_base_are_rejected()
    {
        var sim = new GameSimulation(TestMaps.Empty(4, 4));
        Assert.Equal(SpawnError.UnknownZone, sim.Spawn(SpawnRequest.AtZone(7)).Error);
        Assert.Equal(SpawnError.NoPlayerBase, sim.Spawn(SpawnRequest.AtBase()).Error);
        Assert.Equal(SpawnError.MissingCell, sim.Spawn(new SpawnRequest(SpawnMethod.DirectPlacement, Side.Player)).Error);
    }

    [Fact]
    public void Spawn_emits_events()
    {
        var sim = NewSim();
        var ok = sim.Spawn(SpawnRequest.AtBase());
        sim.Spawn(SpawnRequest.AtCell(HexCoord.FromOffset(2, 1)));
        var events = sim.DrainEvents();
        Assert.Collection(
            events,
            e => Assert.Equal(new UnitSpawned(0, ok.Unit!.Value, ok.Cell!.Value, SpawnMethod.PlayerBase), e),
            e => Assert.Equal(SpawnError.NotWalkable, Assert.IsType<SpawnRejected>(e).Error));
        Assert.Empty(sim.DrainEvents());
    }

    [Fact]
    public void Prototype_map_supports_all_spawn_methods()
    {
        var sim = new GameSimulation(TestMaps.LoadPrototype());
        Assert.True(sim.Spawn(SpawnRequest.AtBase()).Success);
        foreach (var zone in sim.State.Map.SpawnZones)
        {
            Assert.True(sim.Spawn(SpawnRequest.AtZone(zone.Id)).Success);
        }

        Assert.True(sim.Spawn(SpawnRequest.AtCell(HexCoord.FromOffset(6, 6))).Success);
    }

    private static Core.Map.TileType TileTypeOf(GameSimulation sim, HexCoord c) => sim.State.Grid[c].Type;
}
