using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Combat;
using HexTactics.Core.Hex;
using HexTactics.Core.Orders;
using HexTactics.Core.Roster;
using HexTactics.Core.Scenarios;
using HexTactics.Core.Spawning;
using HexTactics.Core.Units;
using HexTactics.Tests.Shared;
using Xunit;

namespace HexTactics.Simulation.Tests;

/// <summary>«Исследование» — entry, exit, exit rules and extra tasks (spec part 7 §6; acceptance M7-32…M7-34).</summary>
public class ExplorationTests
{
    private static HexCoord C(int col, int row) => HexCoord.FromOffset(col, row);

    private static readonly UnitStats Walker = new() { MaxHealth = 100, AttackPower = 0 };

    private static readonly HexCoord[] Entry = { C(0, 4), C(0, 5), C(1, 5) };

    private static readonly HexCoord[] Exit = { C(5, 1), C(6, 1) };

    private static GameSimulation Explore(ExitRule rule = ExitRule.KeyUnit, int key = 1, int[]? required = null, ExtraGoal extra = ExtraGoal.None)
    {
        var rules = new ScenarioRules
        {
            Exploration = true,
            EliminateAllEnemies = false,
            EntryCells = Entry,
            ExitCells = Exit,
            ExitRule = rule,
            KeyEntry = key,
            RequiredEntries = required ?? Array.Empty<int>(),
            ExtraGoal = extra,
        };
        var squad = Enumerable.Range(1, 3).Select(i => ($"Боец {i}", (UnitProfile?)new UnitProfile("p", "Боец", Walker, Array.Empty<string>())));
        var sim = new GameSimulation(TestMaps.Empty(8, 6), new SimulationSettings { Scenario = rules }, new RosterState(squad));
        sim.SetPaused(false);
        return sim;
    }

    private static UnitId Deploy(GameSimulation sim, int entry, HexCoord cell)
    {
        var unit = sim.PlaceFromRoster(entry, cell).Unit!.Value;
        Assert.True(sim.SetFacing(unit, HexDirection.North).Success);
        return unit;
    }

    private static void Go(GameSimulation sim, UnitId unit, HexCoord to) =>
        Assert.True(sim.ApplyOrder(new IndividualOrder(unit, new[] { to }, HexDirection.North)).Success);

    private static List<SimulationEvent> Run(GameSimulation sim, int ticks)
    {
        var all = new List<SimulationEvent>();
        for (var i = 0; i < ticks && !sim.IsScenarioOver; i++)
        {
            sim.Step();
            all.AddRange(sim.DrainEvents());
        }

        return all;
    }

    private static UnitId Enemy(GameSimulation sim, HexCoord cell) =>
        sim.Spawn(SpawnRequest.AtCell(cell, Side.Enemy, HexDirection.South) with
        {
            Profile = new UnitProfile("e", "Страж", Walker, Array.Empty<string>()),
        }).Unit!.Value;

    [Fact]
    public void The_squad_comes_in_only_through_the_entry_hexes()
    {
        var sim = Explore();
        Assert.Equal(Entry.OrderBy(c => c.ToOffset().Row).ThenBy(c => c.ToOffset().Col), PlacementRules.AllowedCells(sim.State).OrderBy(c => c.ToOffset().Row).ThenBy(c => c.ToOffset().Col));
        Assert.Equal(PlacementError.NotAllowedHere, PlacementRules.ValidateCell(sim.State, C(3, 3)));
        Assert.False(sim.PlaceFromRoster(1, C(3, 3)).Success);
        Assert.True(sim.PlaceFromRoster(1, C(0, 4)).Success);
    }

    [Fact]
    public void M7_32_the_key_unit_reaching_the_exit_alive_wins_and_the_exit_is_reported_once()
    {
        var sim = Explore();
        var key = Deploy(sim, 1, C(0, 4));
        Go(sim, key, C(5, 1));
        var events = Run(sim, 900);
        Assert.Equal((ScenarioPhase.Victory, ScenarioEndReason.ExitReached), (sim.ScenarioPhase, sim.ScenarioEndReason!.Value));
        var reached = Assert.Single(events.OfType<ExitReached>());
        Assert.Equal((key, (int?)1), (reached.Unit, reached.Entry));
    }

    [Fact]
    public void A_unit_that_is_not_the_key_does_not_win_but_any_survivor_does()
    {
        var keyRule = Explore();
        Deploy(keyRule, 1, C(0, 4));
        var other = Deploy(keyRule, 2, C(0, 5));
        Go(keyRule, other, C(5, 1));
        Run(keyRule, 900);
        Assert.Equal(ScenarioPhase.Running, keyRule.ScenarioPhase);
        Assert.Contains(other, keyRule.ExitArrived);
        Assert.False(keyRule.Exploration!.ExitDone);
        Assert.Equal(new[] { 1 }, keyRule.Exploration.Waiting);

        var any = Explore(ExitRule.AnySurvivor);
        Deploy(any, 1, C(0, 4));
        var second = Deploy(any, 2, C(0, 5));
        Go(any, second, C(5, 1));
        Run(any, 900);
        Assert.Equal((ScenarioPhase.Victory, ScenarioEndReason.ExitReached), (any.ScenarioPhase, any.ScenarioEndReason!.Value));
    }

    [Fact]
    public void M7_33_with_all_required_the_first_arrival_does_not_end_and_an_arrival_is_remembered_after_leaving()
    {
        var sim = Explore(ExitRule.AllRequired, required: new[] { 1, 2 });
        var first = Deploy(sim, 1, C(0, 4));
        var second = Deploy(sim, 2, C(0, 5));
        Go(sim, first, C(5, 1));
        Run(sim, 900);
        Assert.Equal(ScenarioPhase.Running, sim.ScenarioPhase); // one arrival is not enough
        Assert.Equal(new[] { 2 }, sim.Exploration!.Waiting);

        Go(sim, first, C(3, 3)); // leaves the exit: its arrival stays counted
        Run(sim, 300);
        Assert.Equal(C(3, 3), sim.State.GetUnit(first).Cell);
        Go(sim, second, C(6, 1));
        Run(sim, 900);
        Assert.Equal((ScenarioPhase.Victory, ScenarioEndReason.ExitReached), (sim.ScenarioPhase, sim.ScenarioEndReason!.Value));
    }

    [Fact]
    public void M7_34_living_enemies_do_not_stop_the_victory_unless_the_marked_ones_are_an_extra_task()
    {
        var free = Explore();
        Enemy(free, C(7, 5));
        var key = Deploy(free, 1, C(0, 4));
        Go(free, key, C(5, 1));
        Run(free, 900);
        Assert.Equal(ScenarioPhase.Victory, free.ScenarioPhase); // killing every enemy is not a goal by itself
        Assert.Equal(1, free.EnemiesLeft);

        var marked = Explore(extra: ExtraGoal.KillMarked);
        var target = Enemy(marked, C(7, 5));
        Enemy(marked, C(7, 4));
        marked.MarkTarget(target);
        var walker = Deploy(marked, 1, C(0, 4));
        Go(marked, walker, C(5, 1));
        var events = Run(marked, 900);
        Assert.Single(events.OfType<ExitReached>());
        Assert.Equal(ScenarioPhase.Running, marked.ScenarioPhase); // at the exit, the task says what is left
        Assert.Equal((true, 1, false), (marked.Exploration!.ExitDone, marked.Exploration.MarkedLeft, marked.Exploration.ExtraDone));

        marked.State.ApplyDamage(target, 100000);
        Run(marked, 2);
        Assert.Equal((ScenarioPhase.Victory, ScenarioEndReason.ExitReached), (marked.ScenarioPhase, marked.ScenarioEndReason!.Value));
        Assert.Equal(1, marked.EnemiesLeft); // the unmarked enemy may live

        var clear = Explore(extra: ExtraGoal.ClearMap);
        var last = Enemy(clear, C(7, 5));
        Go(clear, Deploy(clear, 1, C(0, 4)), C(5, 1));
        Run(clear, 900);
        Assert.Equal((ScenarioPhase.Running, 1), (clear.ScenarioPhase, clear.Exploration!.EnemiesLeft));
        clear.State.ApplyDamage(last, 100000);
        Run(clear, 2);
        Assert.Equal(ScenarioPhase.Victory, clear.ScenarioPhase);
    }

    [Fact]
    public void The_death_of_the_key_unit_or_of_a_required_member_is_a_defeat_even_with_others_alive()
    {
        var keyRule = Explore(key: 2);
        Deploy(keyRule, 1, C(0, 4));
        var key = Deploy(keyRule, 2, C(0, 5));
        Run(keyRule, 2);
        keyRule.State.ApplyDamage(key, 100000);
        Run(keyRule, 2);
        Assert.Equal((ScenarioPhase.Defeat, ScenarioEndReason.KeyUnitLost), (keyRule.ScenarioPhase, keyRule.ScenarioEndReason!.Value));

        var all = Explore(ExitRule.AllRequired, required: new[] { 1, 3 });
        Deploy(all, 1, C(0, 4));
        var third = Deploy(all, 3, C(1, 5));
        Run(all, 2);
        all.State.ApplyDamage(third, 100000);
        Run(all, 2);
        Assert.Equal((ScenarioPhase.Defeat, ScenarioEndReason.RequiredLost), (all.ScenarioPhase, all.ScenarioEndReason!.Value));

        var any = Explore(ExitRule.AnySurvivor);
        var only = Deploy(any, 1, C(0, 4));
        Run(any, 2);
        any.State.ApplyDamage(only, 100000);
        Run(any, 2);
        Assert.Equal(ScenarioPhase.Running, any.ScenarioPhase); // two cards still wait in the roster
    }
}
