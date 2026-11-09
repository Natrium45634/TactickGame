using System.Linq;
using HexTactics.Core.Combat;
using HexTactics.Core.Hex;
using HexTactics.Core.Roster;
using HexTactics.Core.Scenarios;
using HexTactics.Core.Spawning;
using HexTactics.Core.Units;
using HexTactics.Tests.Shared;
using Xunit;

namespace HexTactics.Simulation.Tests;

/// <summary>Goals of the ordinary battle (spec part 7 §2.3, §3.2; acceptance M7-02…M7-04).</summary>
public class ScenarioObjectiveTests
{
    private static HexCoord C(int col, int row) => HexCoord.FromOffset(col, row);

    private static GameSimulation Sim(ScenarioRules? rules = null, RosterState? roster = null) =>
        new(TestMaps.Empty(), new SimulationSettings { Scenario = rules ?? new ScenarioRules() }, roster ?? RosterState.Empty());

    private static UnitId Enemy(GameSimulation sim, HexCoord cell) =>
        sim.Spawn(SpawnRequest.AtCell(cell, Side.Enemy, HexDirection.South)).Unit!.Value;

    private static UnitId Player(GameSimulation sim, HexCoord cell) =>
        sim.Spawn(SpawnRequest.AtCell(cell, Side.Player, HexDirection.North)).Unit!.Value;

    private static void Kill(GameSimulation sim, UnitId unit)
    {
        sim.State.ApplyDamage(unit, 100000);
        sim.Step();
    }

    [Fact]
    public void M7_02_victory_comes_only_after_the_last_enemy_falls()
    {
        var sim = Sim();
        var a = Enemy(sim, C(5, 2));
        var b = Enemy(sim, C(7, 2));
        Player(sim, C(5, 7));
        Assert.Equal(ScenarioPhase.Preparing, sim.ScenarioPhase);
        sim.Step();
        Assert.Equal((ScenarioPhase.Running, 2), (sim.ScenarioPhase, sim.EnemiesLeft));
        Kill(sim, a);
        Assert.Equal((ScenarioPhase.Running, 1), (sim.ScenarioPhase, sim.EnemiesLeft));
        sim.DrainEvents();
        Kill(sim, b);
        Assert.Equal((ScenarioPhase.Victory, ScenarioEndReason.AllEnemiesDefeated), (sim.ScenarioPhase, sim.ScenarioEndReason!.Value));
        Assert.Single(sim.DrainEvents().OfType<ScenarioEnded>());
        sim.Step();
        Assert.Empty(sim.DrainEvents().OfType<ScenarioEnded>()); // decided once
    }

    [Fact]
    public void M7_03_M7_04_no_defeat_while_a_reserve_card_can_still_be_deployed_defeat_when_none_is_left()
    {
        var sim = Sim(roster: RosterState.Prototype());
        Enemy(sim, C(5, 2));
        for (var i = 0; i < 60; i++)
        {
            sim.Step();
        }

        Assert.Equal(ScenarioPhase.Running, sim.ScenarioPhase); // nobody deployed yet, five cards wait: not a defeat

        var lone = new GameSimulation(TestMaps.LoadPrototype(), new SimulationSettings { Scenario = new ScenarioRules() }, new RosterState(new[] { "Альфа" }));
        Enemy(lone, C(3, 3));
        var card = lone.PlaceFromRoster(1, C(1, 4)).Unit!.Value; // a deployment hex of the prototype
        lone.Step();
        Assert.Equal(ScenarioPhase.Running, lone.ScenarioPhase);
        Kill(lone, card);
        Assert.Equal((ScenarioPhase.Defeat, ScenarioEndReason.SquadLost), (lone.ScenarioPhase, lone.ScenarioEndReason!.Value));
    }

    [Fact]
    public void A_time_limit_counts_running_game_time_only_and_victory_wins_a_tie()
    {
        var sim = Sim(new ScenarioRules { TimeLimitSeconds = 2 });
        Enemy(sim, C(5, 2));
        Player(sim, C(5, 7));
        sim.SetPaused(true);
        sim.Advance(30); // a paused half minute changes nothing
        Assert.Equal(ScenarioPhase.Preparing, sim.ScenarioPhase);
        sim.SetPaused(false);
        for (var i = 0; i < 59; i++)
        {
            sim.Step();
        }

        Assert.Equal(ScenarioPhase.Running, sim.ScenarioPhase);
        sim.Step();
        Assert.Equal((ScenarioPhase.Defeat, ScenarioEndReason.TimeUp), (sim.ScenarioPhase, sim.ScenarioEndReason!.Value));

        var tie = Sim();
        var e = Enemy(tie, C(5, 2));
        var p = Player(tie, C(5, 7));
        tie.Step();
        tie.State.ApplyDamage(e, 100000);
        tie.State.ApplyDamage(p, 100000);
        tie.Step();
        Assert.Equal(ScenarioPhase.Victory, tie.ScenarioPhase);
    }

    [Fact]
    public void Free_play_has_no_goals()
    {
        var sim = new GameSimulation(TestMaps.Empty());
        var e = Enemy(sim, C(5, 2));
        Kill(sim, e);
        Assert.Equal(ScenarioPhase.Preparing, sim.ScenarioPhase);
    }
}
