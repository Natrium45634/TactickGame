using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Combat;
using HexTactics.Core.Hex;
using HexTactics.Core.Orders;
using HexTactics.Core.Roster;
using HexTactics.Core.Spawning;
using HexTactics.Core.Units;
using HexTactics.Simulation;
using HexTactics.Tests.Shared;
using Xunit;

namespace HexTactics.Simulation.Tests;

/// <summary>Health, death and the normal attack of a chosen target (spec part 3 §3, §4, §7; acceptance 7–11).</summary>
public class CombatTests
{
    private readonly GameSimulation _sim = new(TestMaps.LoadPrototype(), roster: RosterState.Prototype());

    private static HexCoord C(int col, int row) => HexCoord.FromOffset(col, row);

    private UnitId Spawn(int col, int row, Side side = Side.Player) =>
        _sim.Spawn(SpawnRequest.AtCell(C(col, row), side, HexDirection.North)).Unit!.Value;

    private UnitState U(UnitId id) => _sim.State.GetUnit(id);

    private List<SimulationEvent> RunTicks(int ticks)
    {
        var events = new List<SimulationEvent>();
        for (var i = 0; i < ticks; i++)
        {
            _sim.Step();
            events.AddRange(_sim.DrainEvents());
        }

        return events;
    }

    private IndividualOrder AttackOrder(UnitId attacker, UnitId target, params HexCoord[] waypoints) =>
        IndividualOrder.Attack(attacker, waypoints, target, HexDirection.North);

    [Fact]
    public void P7_new_allied_and_enemy_units_get_the_base_characteristics()
    {
        var ally = U(Spawn(2, 2));
        var enemy = U(Spawn(3, 3, Side.Enemy));
        var placed = U(_sim.PlaceFromRoster(1, C(1, 4)).Unit!.Value);
        foreach (var u in new[] { ally, enemy, placed })
        {
            Assert.Equal(100, u.MaxHealth);
            Assert.Equal(100, u.CurrentHealth);
            Assert.Equal(1.0, u.SpeedHexesPerSecond);
            Assert.Equal(3.0, u.Stats.AttackRatePerSecond);
            Assert.True(u.IsAlive);
        }

        // PROTOTYPE_DEFAULT values (not yet set by the author) are configuration, not code in the input layer.
        Assert.Equal(10, UnitStats.Default.AttackPower);
        Assert.Equal(1, UnitStats.Default.NormalAttackRangeHex);
    }

    [Fact]
    public void P8_100_damage_kills_the_unit_which_then_frees_its_hex_and_takes_no_orders()
    {
        var victim = Spawn(4, 4);
        _sim.State.ApplyDamage(victim, 60);
        Assert.Equal(40, U(victim).CurrentHealth);
        Assert.Equal(40, _sim.State.ApplyDamage(victim, 70)); // never below 0
        Assert.Equal(0, U(victim).CurrentHealth);
        Assert.Equal(LifeState.Dead, U(victim).LifeState);

        var events = RunTicks(1);
        Assert.Contains(events, e => e is UnitDied d && d.Unit == victim && d.Cell == C(4, 4));
        Assert.Null(_sim.State.FindUnit(victim));
        Assert.False(_sim.State.IsOccupied(C(4, 4)));
        Assert.Contains(_sim.State.Fallen, u => u.Id == victim);
        Assert.False(_sim.ApplyOrder(new IndividualOrder(victim, new[] { C(4, 6) }, HexDirection.North)).Success);
        Assert.True(_sim.Spawn(SpawnRequest.AtCell(C(4, 4))).Success); // the hex is free for others
    }

    [Fact]
    public void P9_adjacent_static_target_receives_exactly_nine_hits_in_three_game_seconds_and_pause_adds_none()
    {
        var attacker = Spawn(4, 4);
        var target = Spawn(4, 3, Side.Enemy);
        Assert.True(_sim.ApplyOrder(AttackOrder(attacker, target)).Success);

        var events = RunTicks(45); // 1.5 s
        _sim.SetPaused(true);
        _sim.Advance(10);
        _sim.SetPaused(false);
        for (var i = 0; i < 45; i++)
        {
            _sim.Advance(1.0 / 30);
            events.AddRange(_sim.DrainEvents());
        }

        var hits = events.OfType<DamageApplied>().Where(d => d.Source == attacker).ToList();
        Assert.Equal(9, hits.Count);
        Assert.All(hits, h => Assert.Equal(10, h.Amount));
        Assert.Equal(10, U(target).CurrentHealth);
        Assert.Equal(9, U(attacker).Combat.HitsDelivered);

        // Not more often than once per 1/3 s: hits are 10 ticks apart, the first one after a full interval.
        var ticks = hits.Select(h => h.Tick).ToList();
        Assert.All(ticks.Zip(ticks.Skip(1), (a, b) => b - a), gap => Assert.Equal(10, gap));
        Assert.Equal(AttackStatus.Attacking, U(attacker).Combat.Status);
    }

    [Fact]
    public void Killing_the_target_ends_the_attack_and_the_dead_never_hit_back()
    {
        var attacker = Spawn(4, 4);
        var target = Spawn(4, 3, Side.Enemy);
        Assert.True(_sim.ApplyOrder(AttackOrder(attacker, target)).Success);
        var events = RunTicks(120);

        Assert.Equal(10, events.OfType<DamageApplied>().Count(d => d.Target == target));
        Assert.Contains(events, e => e is UnitDied d && d.Unit == target);
        Assert.Contains(events, e => e is AttackTargetLost l && l.Unit == attacker && l.Reason == AttackLostReason.TargetDied);
        Assert.Null(U(attacker).Orders.AttackTarget);
        Assert.Equal(AttackStatus.None, U(attacker).Combat.Status);
        Assert.Equal(100, U(attacker).CurrentHealth);
    }

    [Fact]
    public void A_unit_killed_in_a_tick_does_not_deliver_its_own_hit_in_that_tick()
    {
        var a = Spawn(4, 4);
        var b = Spawn(4, 3, Side.Enemy);
        Assert.True(_sim.ApplyOrder(AttackOrder(a, b)).Success);
        Assert.True(_sim.ApplyOrder(AttackOrder(b, a)).Success);
        _sim.State.ApplyDamage(b, 90); // one hit left
        var events = RunTicks(10); // both timers fire on the same tick; a (lower id) acts first

        Assert.Contains(events, e => e is DamageApplied d && d.Target == b && d.HealthAfter == 0);
        Assert.DoesNotContain(events, e => e is DamageApplied d && d.Source == b);
        Assert.Equal(100, U(a).CurrentHealth);
    }

    [Fact]
    public void P10_attack_order_turns_the_unit_to_the_target_at_once_also_on_pause_without_damage()
    {
        var attacker = Spawn(2, 6);
        var target = Spawn(4, 8, Side.Enemy);
        _sim.SetPaused(true);
        Assert.True(_sim.ApplyOrder(AttackOrder(attacker, target)).Success);
        Assert.Equal(HexDirections.Toward(C(2, 6), C(4, 8)), U(attacker).Facing);
        Assert.Equal(target, U(attacker).Orders.AttackTarget);
        Assert.Null(U(attacker).Orders.FinalFacing); // no player-chosen facing for an attack order

        _sim.Advance(5);
        Assert.Equal(100, U(target).CurrentHealth);
        Assert.Equal(0, _sim.Tick);
    }

    [Fact]
    public void P11_out_of_range_target_is_approached_to_the_nearest_free_hex_never_into_its_hex()
    {
        var attacker = Spawn(1, 1);
        var target = Spawn(4, 3, Side.Enemy);
        Assert.True(_sim.ApplyOrder(AttackOrder(attacker, target)).Success);
        var visited = new HashSet<HexCoord>();
        var events = new List<SimulationEvent>();
        for (var i = 0; i < 300 && U(attacker).Combat.Status != AttackStatus.Attacking; i++)
        {
            _sim.Step();
            events.AddRange(_sim.DrainEvents());
            visited.Add(U(attacker).Cell);
        }

        Assert.DoesNotContain(C(4, 3), visited);
        Assert.Equal(1, U(attacker).Cell.DistanceTo(C(4, 3)));
        Assert.Contains(events, e => e is AttackStarted s && s.Unit == attacker);

        // The chosen hex is the nearest by path length (deterministic among equals).
        var distances = HexTactics.Core.Pathfinding.Pathfinder.DistanceField(new GameSimulation(TestMaps.LoadPrototype()).State.Grid, C(1, 1));
        var nearest = HexDirections.All.Select(d => C(4, 3).Neighbor(d)).Where(distances.ContainsKey).Min(c => distances[c]);
        Assert.Equal(nearest, distances[U(attacker).Cell]);
    }

    [Fact]
    public void Unreachable_target_gives_an_explicit_state_and_the_unit_waits_without_teleporting()
    {
        var attacker = Spawn(6, 3);
        var target = Spawn(8, 3, Side.Enemy); // the closed pocket: every neighbour is a rock
        Assert.True(_sim.ApplyOrder(AttackOrder(attacker, target)).Success);
        var events = RunTicks(90);

        Assert.Equal(AttackStatus.TargetUnreachable, U(attacker).Combat.Status);
        Assert.Single(events.OfType<AttackTargetUnreachable>());
        Assert.Equal(C(6, 3), U(attacker).Cell);
        Assert.Equal(100, U(target).CurrentHealth);
    }

    [Fact]
    public void Waypoints_before_the_attack_are_walked_first()
    {
        var attacker = Spawn(2, 6);
        var target = Spawn(4, 3, Side.Enemy);
        Assert.True(_sim.ApplyOrder(AttackOrder(attacker, target, C(2, 8), C(1, 4))).Success);
        var reached = new List<HexCoord>();
        for (var i = 0; i < 600 && U(attacker).Combat.Status != AttackStatus.Attacking; i++)
        {
            _sim.Step();
            reached.AddRange(_sim.DrainEvents().OfType<WaypointReached>().Where(w => !w.Waypoint.IsDetour).Select(w => w.Waypoint.Cell));
        }

        Assert.Equal(new[] { C(2, 8), C(1, 4) }, reached);
        Assert.Equal(AttackStatus.Attacking, U(attacker).Combat.Status);
    }

    [Fact]
    public void Attack_orders_need_a_living_enemy_and_a_new_order_cancels_the_attack()
    {
        var attacker = Spawn(4, 4);
        var friend = Spawn(4, 3);
        var enemy = Spawn(3, 3, Side.Enemy);
        Assert.Equal(WaypointError.InvalidTarget, _sim.ApplyOrder(AttackOrder(attacker, friend)).Error);

        Assert.True(_sim.ApplyOrder(AttackOrder(attacker, enemy)).Success);
        RunTicks(15);
        Assert.True(_sim.ApplyOrder(new IndividualOrder(attacker, new[] { C(4, 6) }, HexDirection.South)).Success);
        Assert.Null(U(attacker).Orders.AttackTarget);
        var events = RunTicks(90);
        Assert.DoesNotContain(events, e => e is DamageApplied d && d.Source == attacker);
    }

    [Fact]
    public void A_dead_roster_operative_makes_its_card_unavailable()
    {
        var unit = _sim.PlaceFromRoster(2, C(0, 4)).Unit!.Value;
        _sim.SetFacing(unit, HexDirection.North);
        _sim.State.ApplyDamage(unit, 100);
        RunTicks(1);
        Assert.Equal(RosterEntryState.Unavailable, _sim.State.Roster.Find(2)!.State);
    }

    [Fact]
    public void Facing_toward_a_target_uses_the_nearest_direction_with_a_stable_tie_rule()
    {
        var o = C(5, 5);
        foreach (var d in HexDirections.All)
        {
            Assert.Equal(d, HexDirections.Toward(o, o.Neighbor(d)));
        }

        // Exactly between N and NE (two steps NE then … ): a fixed choice, the first in N, NE, SE, S, SW, NW order.
        var between = o.Neighbor(HexDirection.North).Neighbor(HexDirection.NorthEast);
        Assert.Equal(HexDirection.North, HexDirections.Toward(o, between));
        Assert.Null(HexDirections.Toward(o, o));
    }
}
