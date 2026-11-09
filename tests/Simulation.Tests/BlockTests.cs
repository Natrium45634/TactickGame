using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Combat;
using HexTactics.Core.Effects;
using HexTactics.Core.Hex;
using HexTactics.Core.Orders;
using HexTactics.Core.Spawning;
using HexTactics.Core.Units;
using HexTactics.Tests.Shared;
using Xunit;

namespace HexTactics.Simulation.Tests;

/// <summary>Block capacity and block links (spec part 7 §7; acceptance M7-36…M7-42, M7-57).</summary>
public class BlockTests
{
    private static HexCoord C(int col, int row) => HexCoord.FromOffset(col, row);

    private static UnitId Unit(GameSimulation sim, HexCoord cell, Side side, int block = 0, BasicAttackDefinition? basic = null) =>
        sim.Spawn(SpawnRequest.AtCell(cell, side, HexDirection.North) with
        {
            Profile = new UnitProfile("u", "Юнит", new UnitStats
            {
                MaxHealth = 1000, BlockCapacity = block, BasicAttack = basic, AutoAttackEnabled = basic is not null, AttackRatePerSecond = 3,
            }, System.Array.Empty<string>()),
        }).Unit!.Value;

    private static void Go(GameSimulation sim, UnitId unit, HexCoord to) =>
        Assert.True(sim.ApplyOrder(new IndividualOrder(unit, new[] { to }, HexDirection.South)).Success);

    private static void Run(GameSimulation sim, int ticks)
    {
        for (var i = 0; i < ticks; i++)
        {
            sim.Step();
            var cells = sim.State.Units.Where(u => u.IsAlive).Select(u => u.Cell).ToList();
            Assert.Equal(cells.Count, cells.Distinct().Count()); // M7-57: never two units in one hex
        }
    }

    private static HexCoord Cell(GameSimulation sim, UnitId u) => sim.State.GetUnit(u).Cell;

    [Fact]
    public void M7_36_block_0_never_links_and_block_1_links_at_most_one()
    {
        var free = new GameSimulation(TestMaps.Empty());
        Unit(free, C(5, 5), Side.Player, block: 0);
        var walker = Unit(free, C(5, 4).Neighbor(HexDirection.NorthEast), Side.Enemy);
        Go(free, walker, C(5, 8));
        Run(free, 400);
        Assert.Equal(C(5, 8), Cell(free, walker));
        Assert.Empty(free.State.BlockLinks);

        var one = new GameSimulation(TestMaps.Empty());
        var blocker = Unit(one, C(5, 5), Side.Player, block: 1);
        var a = Unit(one, C(5, 5).Neighbor(HexDirection.NorthWest), Side.Enemy);
        var b = Unit(one, C(5, 5).Neighbor(HexDirection.NorthEast), Side.Enemy);
        Go(one, a, C(2, 9));
        Go(one, b, C(9, 9));
        Run(one, 400);
        Assert.Single(one.State.BlockLinks);
        var held = one.State.BlockLinks[0].Enemy;
        var passed = held == a ? b : a;
        Assert.Equal(blocker, one.State.BlockLinks[0].Blocker);
        Assert.Equal(1, Cell(one, held).DistanceTo(C(5, 5)));          // still next to the blocker
        Assert.Equal(passed == a ? C(2, 9) : C(9, 9), Cell(one, passed)); // the other went on
    }

    [Fact]
    public void M7_37_M7_38_block_3_holds_three_from_three_sides_on_their_own_hexes_and_they_go_on_after_release()
    {
        var sim = new GameSimulation(TestMaps.Empty());
        var blocker = Unit(sim, C(5, 5), Side.Player, block: 3);
        var dirs = new[] { HexDirection.North, HexDirection.SouthEast, HexDirection.SouthWest, HexDirection.NorthEast };
        var enemies = dirs.Select(d => Unit(sim, C(5, 5).Neighbor(d), Side.Enemy)).ToList();
        var goals = new[] { C(5, 0), C(11, 9), C(0, 9), C(11, 0) };
        for (var i = 0; i < 4; i++)
        {
            Go(sim, enemies[i], goals[i]);
        }

        Run(sim, 60);
        Assert.Equal(3, sim.State.BlockLinks.Count);
        var held = sim.State.BlockLinks.Select(l => l.Enemy).ToList();
        Assert.All(held, e => Assert.Equal(1, Cell(sim, e).DistanceTo(C(5, 5)))); // on their own hexes, never the blocker's
        Assert.Equal(C(5, 5), Cell(sim, blocker));
        Assert.DoesNotContain(enemies[3], held);                                    // the fourth: no free slot, it walks on

        Go(sim, blocker, C(5, 9)); // the blocker moves: every link ends, the enemies go on along their routes
        Run(sim, 600);
        Assert.Empty(sim.State.BlockLinks);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(goals[i], Cell(sim, enemies[i]));
        }
    }

    [Fact]
    public void M7_39_the_death_of_the_blocker_releases_its_links_once()
    {
        var sim = new GameSimulation(TestMaps.Empty());
        var blocker = Unit(sim, C(5, 5), Side.Player, block: 2);
        var a = Unit(sim, C(5, 5).Neighbor(HexDirection.North), Side.Enemy);
        var b = Unit(sim, C(5, 5).Neighbor(HexDirection.South), Side.Enemy);
        Go(sim, a, C(5, 0));
        Go(sim, b, C(5, 9));
        Run(sim, 10);
        Assert.Equal(2, sim.State.BlockLinks.Count);
        sim.DrainEvents();
        sim.State.ApplyDamage(blocker, 5000);
        Run(sim, 120);
        var released = sim.DrainEvents().OfType<BlockReleased>().ToList();
        Assert.Equal(2, released.Count);
        Assert.All(released, r => Assert.Equal(BlockReleaseReason.BlockerGone, r.Reason));
        Assert.Equal(C(5, 0), Cell(sim, a));
    }

    [Fact]
    public void M7_40_a_debuff_from_3_to_1_releases_the_two_newest_links()
    {
        var sim = new GameSimulation(TestMaps.Empty());
        var blocker = Unit(sim, C(5, 5), Side.Player, block: 3);
        var first = Unit(sim, C(5, 5).Neighbor(HexDirection.North), Side.Enemy);
        Go(sim, first, C(5, 0));
        Run(sim, 5);
        var second = Unit(sim, C(5, 5).Neighbor(HexDirection.SouthEast), Side.Enemy);
        var third = Unit(sim, C(5, 5).Neighbor(HexDirection.SouthWest), Side.Enemy);
        Go(sim, second, C(11, 9));
        Go(sim, third, C(0, 9));
        Run(sim, 5);
        Assert.Equal(3, sim.State.BlockLinks.Count);
        sim.DrainEvents();
        Assert.True(sim.State.AddStatus(blocker, new StatusEffect { Key = "shaken", Kind = StatusKind.Modifier, Stat = Stats.Block, Delta = -2, Duration = 30 }, StackingPolicy.Refresh));
        Run(sim, 2);
        Assert.Equal(1, BlockMath.Capacity(sim.State.GetUnit(blocker)));
        Assert.Equal(first, Assert.Single(sim.State.BlockLinks).Enemy); // the oldest stays
        Assert.Equal(2, sim.DrainEvents().OfType<BlockReleased>().Count(r => r.Reason == BlockReleaseReason.CapacityReduced));
    }

    [Fact]
    public void M7_41_crossing_paths_give_no_extra_slots_and_each_enemy_has_one_blocker()
    {
        var sim = new GameSimulation(TestMaps.Empty());
        var left = Unit(sim, C(4, 5), Side.Player, block: 1);
        var right = Unit(sim, C(6, 5), Side.Player, block: 1);
        var between = Unit(sim, C(5, 4), Side.Enemy); // odd-q: next to both blockers
        var other = Unit(sim, C(5, 5), Side.Enemy);   // next to both too, its path crosses the other one's
        Go(sim, between, C(5, 0));
        Go(sim, other, C(5, 9));
        Run(sim, 10);
        Assert.Equal(2, sim.State.BlockLinks.Count);
        Assert.Equal(2, sim.State.BlockLinks.Select(l => l.Enemy).Distinct().Count()); // one blocker per enemy
        Assert.Equal(2, sim.State.BlockLinks.Select(l => l.Blocker).Distinct().Count()); // each blocker one slot
        Assert.Contains(sim.State.BlockLinks, l => l.Blocker == left);
        Assert.Contains(sim.State.BlockLinks, l => l.Blocker == right);
    }

    [Fact]
    public void M7_42_block_0_does_not_stop_an_archer_from_shooting()
    {
        var sim = new GameSimulation(TestMaps.Empty(), new SimulationSettings { AutoAttacks = true });
        var bow = new BasicAttackDefinition { Id = "bow", Cells = new[] { new HexCoord(0, -2), new HexCoord(0, -3) } };
        Unit(sim, C(5, 5), Side.Player, block: 0, basic: bow);
        var target = Unit(sim, C(5, 3), Side.Enemy);
        Go(sim, target, C(5, 2));
        Run(sim, 31);
        Assert.Empty(sim.State.BlockLinks);
        Assert.True(sim.State.GetUnit(target).CurrentHealth < 1000);
    }
}
