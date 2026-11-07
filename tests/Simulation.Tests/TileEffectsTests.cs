using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Effects;
using HexTactics.Core.Hex;
using HexTactics.Core.Map;
using HexTactics.Core.Orders;
using HexTactics.Core.Spawning;
using HexTactics.Core.Units;
using HexTactics.Tests.Shared;
using Xunit;

namespace HexTactics.Simulation.Tests;

/// <summary>Effects of tile types on the unit's logical hex (spec part 4 §7; §12.3 p.21–22).</summary>
public class TileEffectsTests
{
    private const string Burn = "test.tile.burn";
    private const string Heal = "test.tile.heal";
    private const string Swamp = "test.tile.swamp";
    private const string Ford = "test.tile.ford";

    private static HexCoord C(int col, int row) => HexCoord.FromOffset(col, row);

    private static EffectSpec E(string type, params (string Key, object Value)[] p) =>
        new(type, p.ToDictionary(x => x.Key, x => x.Value));

    private static TileEffect Stay(string kind, double perSecond, string sides = TileSides.All) =>
        new(TileTrigger.Stay, E(EffectTypes.Periodic, ("trigger", "stay"), ("kind", kind), ("perSecond", perSecond), ("sides", sides)));

    /// <summary>A 12 × 10 field; <paramref name="tiles"/> turn hexes into instances of the given tile types.</summary>
    private static GameSimulation Sim(IReadOnlyDictionary<string, IReadOnlyList<TileEffect>> effects, params (HexCoord Cell, string Tile)[] tiles)
    {
        var map = TestMaps.Empty();
        foreach (var (cell, tile) in tiles)
        {
            map.Grid.SetTile(cell, TileType.Walkable, tile, 0);
        }

        return new GameSimulation(map, new SimulationSettings { TileEffects = effects });
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<TileEffect>> Surfaces() => new Dictionary<string, IReadOnlyList<TileEffect>>
    {
        [Burn] = new[] { Stay("damage", 5) },
        [Heal] = new[] { Stay("heal", 5) },
    };

    private static UnitId Spawn(GameSimulation sim, HexCoord cell, Side side = Side.Player) =>
        sim.Spawn(SpawnRequest.AtCell(cell, side, HexDirection.North)).Unit!.Value;

    private static double Hp(GameSimulation sim, UnitId id) => sim.State.GetUnit(id).CurrentHealth;

    [Theory]
    [InlineData(201)]
    [InlineData(67)]
    [InlineData(10)]
    [InlineData(13)]
    public void P21_two_game_seconds_on_a_burning_hex_take_exactly_10_hp_at_any_frame_rate(int frames)
    {
        var sim = Sim(Surfaces(), (C(5, 5), Burn));
        var unit = Spawn(sim, C(5, 5));
        for (var i = 0; i < frames; i++)
        {
            sim.Advance(2.01 / frames); // 60 ticks of 1/30 s in total, however the frames fall
        }

        Assert.Equal(60, sim.Tick);
        Assert.Equal(90, Hp(sim, unit), 9);
    }

    [Fact]
    public void P21_the_pause_freezes_the_surface()
    {
        var sim = Sim(Surfaces(), (C(5, 5), Burn));
        var unit = Spawn(sim, C(5, 5));
        for (var i = 0; i < 30; i++)
        {
            sim.Step();
        }

        Assert.Equal(95, Hp(sim, unit), 9);
        sim.SetPaused(true);
        sim.Advance(10);
        Assert.Equal(95, Hp(sim, unit), 9);
        sim.SetPaused(false);
        for (var i = 0; i < 30; i++)
        {
            sim.Step();
        }

        Assert.Equal(90, Hp(sim, unit), 9);
    }

    [Fact]
    public void P22_healing_surface_heals_5_per_second_never_above_the_maximum_and_stops_after_leaving()
    {
        var sim = Sim(Surfaces(), (C(5, 5), Heal));
        var unit = Spawn(sim, C(5, 5));
        sim.State.ApplyDamage(unit, 30);
        for (var i = 0; i < 60; i++)
        {
            sim.Step();
        }

        Assert.Equal(80, Hp(sim, unit), 9);
        for (var i = 0; i < 300; i++)
        {
            sim.Step();
        }

        Assert.Equal(100, Hp(sim, unit), 9); // capped at MaxHealth

        sim.State.ApplyDamage(unit, 50);
        Assert.True(sim.IssueMoveOrder(unit, C(5, 3), OrderMode.Replace).Success);
        var onHeal = 0;
        for (var i = 0; i < 150; i++)
        {
            sim.Step();
            if (sim.State.GetUnit(unit).Cell == C(5, 5))
            {
                onHeal++;
            }
        }

        Assert.Equal(C(5, 3), sim.State.GetUnit(unit).Cell);
        Assert.Equal(50 + (5.0 * onHeal / 30), Hp(sim, unit), 9); // healed only while the logical hex was the healing one
        var after = Hp(sim, unit);
        for (var i = 0; i < 90; i++)
        {
            sim.Step();
        }

        Assert.Equal(after, Hp(sim, unit), 9);
    }

    [Fact]
    public void Smoothed_routes_are_charged_by_the_logical_hex_only_never_twice()
    {
        // A burning strip with a turn in it: the body follows a smoothed curve, the surface counts ticks of the logical hex.
        var burning = new[] { C(4, 5), C(5, 5), C(5, 4), C(6, 4) };
        var sim = Sim(Surfaces(), burning.Select(c => (c, Burn)).ToArray());
        var unit = Spawn(sim, C(3, 6));
        Assert.True(sim.IssueMoveOrder(unit, C(7, 3), OrderMode.Replace).Success);
        var logicalTicks = 0;
        var visits = new List<HexCoord>();
        for (var i = 0; i < 400; i++)
        {
            sim.Step();
            var u = sim.State.GetUnit(unit);
            if (burning.Contains(u.Cell))
            {
                logicalTicks++;
            }

            if (visits.Count == 0 || visits[^1] != u.Cell)
            {
                visits.Add(u.Cell);
            }
        }

        Assert.Equal(C(7, 3), sim.State.GetUnit(unit).Cell);
        Assert.True(logicalTicks > 0);
        Assert.Equal(visits.Count, visits.Distinct().Count()); // every hex entered once — no double counting on a curve
        Assert.Equal(100 - (5.0 * logicalTicks / 30), Hp(sim, unit), 9);
    }

    [Fact]
    public void Enter_and_exit_triggers_fire_once_per_change_of_the_logical_hex()
    {
        var effects = new Dictionary<string, IReadOnlyList<TileEffect>>
        {
            [Swamp] = new[]
            {
                new TileEffect(TileTrigger.Enter, E(EffectTypes.Debuff, ("stat", Stats.MoveSpeed), ("mode", "percent"), ("percent", 0.5), ("duration", 3.0), ("stacking", "refresh"), ("sides", "all"))),
                new TileEffect(TileTrigger.Exit, E(EffectTypes.Damage, ("amount", 7.0), ("sides", "all"))),
            },
        };
        var sim = Sim(effects, (C(5, 4), Swamp));
        var unit = Spawn(sim, C(5, 5));
        Assert.True(sim.IssueMoveOrder(unit, C(5, 3), OrderMode.Replace).Success);
        var entered = false;
        for (var i = 0; i < 300 && sim.State.GetUnit(unit).Cell != C(5, 3); i++)
        {
            sim.Step();
            if (!entered && sim.State.GetUnit(unit).Cell == C(5, 4))
            {
                entered = true;
                Assert.Equal(0.5, sim.State.GetUnit(unit).Stats.MoveSpeedHexPerSecond, 9); // slowed on entering
            }
        }

        Assert.True(entered);
        Assert.Equal(93, Hp(sim, unit), 9); // the exit damage once
        Assert.Single(sim.State.GetUnit(unit).Statuses);
    }

    [Fact]
    public void A_side_filter_spares_the_other_side()
    {
        var effects = new Dictionary<string, IReadOnlyList<TileEffect>> { [Burn] = new[] { Stay("damage", 5, TileSides.Enemy) } };
        var sim = Sim(effects, (C(4, 4), Burn), (C(6, 4), Burn));
        var player = Spawn(sim, C(4, 4));
        var enemy = Spawn(sim, C(6, 4), Side.Enemy);
        for (var i = 0; i < 60; i++)
        {
            sim.Step();
        }

        Assert.Equal(100, Hp(sim, player), 9);
        Assert.Equal(90, Hp(sim, enemy), 9);
    }

    [Fact]
    public void A_unit_dying_on_a_burning_hex_is_removed_without_an_exit()
    {
        var effects = new Dictionary<string, IReadOnlyList<TileEffect>>
        {
            [Burn] = new[] { Stay("damage", 5), new TileEffect(TileTrigger.Exit, E(EffectTypes.Heal, ("amount", 50.0))) },
        };
        var sim = Sim(effects, (C(5, 5), Burn));
        var unit = Spawn(sim, C(5, 5));
        sim.State.ApplyDamage(unit, 99);
        for (var i = 0; i < 30; i++)
        {
            sim.Step();
        }

        Assert.Null(sim.State.FindUnit(unit));
    }

    [Fact]
    public void A_tile_type_may_forbid_waypoints_but_routes_still_cross_it()
    {
        var map = TestMaps.Empty();
        map.Grid.SetTile(C(5, 4), TileType.Walkable, Ford, 0, allowsWaypoints: false);
        var sim = new GameSimulation(map);
        var unit = Spawn(sim, C(5, 5));
        Assert.Equal(WaypointError.WaypointsForbidden, WaypointRules.ValidateTarget(sim.State, unit, C(5, 5), C(5, 4)));
        Assert.Equal(WaypointError.WaypointsForbidden, sim.IssueMoveOrder(unit, C(5, 4), OrderMode.Replace).Error);
        Assert.True(sim.IssueMoveOrder(unit, C(5, 3), OrderMode.Replace).Success);
        var crossed = false;
        for (var i = 0; i < 120; i++)
        {
            sim.Step();
            crossed |= sim.State.GetUnit(unit).Cell == C(5, 4);
        }

        Assert.True(crossed);
        Assert.Equal(C(5, 3), sim.State.GetUnit(unit).Cell);
    }

    [Fact]
    public void Without_tile_effects_nothing_changes()
    {
        var sim = Sim(new Dictionary<string, IReadOnlyList<TileEffect>>(), (C(5, 5), Burn));
        var unit = Spawn(sim, C(5, 5));
        for (var i = 0; i < 90; i++)
        {
            sim.Step();
        }

        Assert.Equal(100, Hp(sim, unit), 9);
    }
}
