using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Combat;
using HexTactics.Core.Hex;
using HexTactics.Core.Map;
using HexTactics.Core.Orders;
using HexTactics.Core.Scenarios;
using HexTactics.Core.Spawning;
using HexTactics.Core.Units;
using HexTactics.Tests.Shared;
using Xunit;

namespace HexTactics.Simulation.Tests;

/// <summary>The simple AI presets, mission objects and the defense goals (spec part 7 §3.3, §5; acceptance M7-05, M7-26…M7-30).</summary>
public class AiTests
{
    private static HexCoord C(int col, int row) => HexCoord.FromOffset(col, row);

    private static readonly UnitStats Fighter = new() { MaxHealth = 100, AttackPower = 10 };

    private static UnitId Enemy(GameSimulation sim, HexCoord cell, AiProfile ai, int health = 100)
    {
        var id = sim.Spawn(SpawnRequest.AtCell(cell, Side.Enemy, HexDirection.South) with
        {
            Profile = new UnitProfile("e", "Враг", Fighter with { MaxHealth = health }, System.Array.Empty<string>()),
        }).Unit!.Value;
        sim.State.SetAi(id, ai);
        return id;
    }

    private static UnitId Player(GameSimulation sim, HexCoord cell, int health = 1000) =>
        sim.Spawn(SpawnRequest.AtCell(cell, Side.Player, HexDirection.North) with
        {
            Profile = new UnitProfile("p", "Боец", Fighter with { MaxHealth = health, AttackPower = 0 }, System.Array.Empty<string>()),
        }).Unit!.Value;

    private static List<SimulationEvent> Run(GameSimulation sim, int ticks)
    {
        var all = new List<SimulationEvent>();
        for (var i = 0; i < ticks && !sim.IsScenarioOver; i++)
        {
            sim.Step();
            all.AddRange(sim.DrainEvents());
            var cells = sim.State.Units.Where(u => u.IsAlive).Select(u => u.Cell).ToList();
            Assert.Equal(cells.Count, cells.Distinct().Count());
        }

        return all;
    }

    [Fact]
    public void M7_05_a_guard_fights_inside_its_radius_never_chases_outside_and_comes_back()
    {
        var sim = new GameSimulation(TestMaps.Empty());
        var guard = Enemy(sim, C(5, 2), new AiProfile(AiPreset.Guard, GuardRadius: 2));
        var far = Player(sim, C(5, 7));
        Run(sim, 120);
        Assert.Equal(C(5, 2), sim.State.GetUnit(guard).Cell); // the player is 5 hexes from the post: no chase

        Assert.True(sim.ApplyOrder(new IndividualOrder(far, new[] { C(5, 4) }, HexDirection.North)).Success);
        Run(sim, 200);
        Assert.Equal(far, sim.State.GetUnit(guard).Ai!.Target); // inside the radius: engaged
        Assert.Equal(1, sim.State.GetUnit(guard).Cell.DistanceTo(sim.State.GetUnit(far).Cell));

        Assert.True(sim.ApplyOrder(new IndividualOrder(far, new[] { C(5, 9) }, HexDirection.South)).Success);
        Run(sim, 400);
        Assert.Null(sim.State.GetUnit(guard).Ai!.Target);
        Assert.Equal(C(5, 2), sim.State.GetUnit(guard).Cell); // back on its post
        Assert.True(sim.State.GetUnit(far).Cell.DistanceTo(C(5, 2)) > 2);
    }

    [Fact]
    public void M7_29_attack_units_goes_for_the_nearest_unit_and_keeps_it_attack_objective_goes_for_the_object()
    {
        var sim = new GameSimulation(TestMaps.Empty());
        var hunter = Enemy(sim, C(5, 0), new AiProfile(AiPreset.AttackUnits));
        var near = Player(sim, C(5, 4));
        Player(sim, C(9, 4)); // farther
        var events = Run(sim, 300);
        Assert.Equal(near, sim.State.GetUnit(hunter).Ai!.Target);
        Assert.Single(events.OfType<OrderApplied>().Where(o => o.Unit == hunter)); // one order, no switching between candidates
        Assert.True(sim.State.GetUnit(near).CurrentHealth < 1000);

        var siege = new GameSimulation(TestMaps.Empty(), new SimulationSettings
        {
            Scenario = new ScenarioRules { Defense = true, EliminateAllEnemies = false, DefeatWhenSquadLost = false, SurviveSeconds = 600, Objects = new[] { new MissionObject("gen", "Генератор", C(5, 8), 300) } },
        });
        var raider = Enemy(siege, C(5, 0), new AiProfile(AiPreset.AttackObjective));
        Player(siege, C(2, 4)); // a unit nearer than the object does not distract it
        Run(siege, 400);
        var gen = siege.State.Units.Single(u => u.Role == UnitRole.Objective);
        Assert.Equal(gen.Id, siege.State.GetUnit(raider).Ai!.Target);
        Assert.True(gen.CurrentHealth < 300);
    }

    [Fact]
    public void M7_30_a_dead_target_or_one_without_a_path_gives_a_fallback_never_a_stuck_unit()
    {
        var sim = new GameSimulation(TestMaps.Empty());
        var hunter = Enemy(sim, C(5, 0), new AiProfile(AiPreset.AttackUnits));
        var first = Player(sim, C(5, 3));
        var second = Player(sim, C(8, 3));
        Run(sim, 10);
        Assert.Equal(first, sim.State.GetUnit(hunter).Ai!.Target);
        sim.State.ApplyDamage(first, 100000);
        Run(sim, 60);
        Assert.Equal(second, sim.State.GetUnit(hunter).Ai!.Target); // the fallback, right after the loss

        // A walled-in unit is unreachable: the hunter reports it and takes the other one.
        var walled = new GameSimulation(TestMaps.Empty());
        var h2 = Enemy(walled, C(5, 0), new AiProfile(AiPreset.AttackUnits));
        var inside = Player(walled, C(5, 3));
        foreach (var n in C(5, 3).Neighbors())
        {
            walled.SetObstacle(n, true);
        }

        var outside = Player(walled, C(9, 7));
        Run(walled, 300);
        Assert.Equal(outside, walled.State.GetUnit(h2).Ai!.Target);
        Assert.True(walled.State.GetUnit(h2).Ai!.Unreachable.ContainsKey(inside) || walled.State.GetUnit(h2).Cell.DistanceTo(C(9, 7)) <= 1);
    }

    [Fact]
    public void M7_28_mission_objects_take_no_orders_and_only_a_critical_one_loses_the_battle()
    {
        ScenarioRules Rules(bool critical) => new()
        {
            Defense = true, EliminateAllEnemies = false, DefeatWhenSquadLost = false, SurviveSeconds = 600,
            Objects = new[] { new MissionObject("gen", "Генератор", C(5, 5), 50, critical) },
        };

        var calm = new GameSimulation(TestMaps.Empty(), new SimulationSettings { Scenario = Rules(false) });
        var obj = calm.State.Units.Single();
        Assert.Equal((UnitRole.Objective, Side.Player, false), (obj.Role, obj.Side, obj.IsCommandable));
        Assert.False(calm.ApplyOrder(new IndividualOrder(obj.Id, new[] { C(5, 7) }, HexDirection.South)).Success);
        calm.State.ApplyDamage(obj.Id, 1000);
        Run(calm, 5);
        Assert.Equal(ScenarioPhase.Running, calm.ScenarioPhase);

        var tense = new GameSimulation(TestMaps.Empty(), new SimulationSettings { Scenario = Rules(true) });
        tense.State.ApplyDamage(tense.State.Units.Single().Id, 1000);
        Run(tense, 5);
        Assert.Equal((ScenarioPhase.Defeat, ScenarioEndReason.ObjectLost), (tense.ScenarioPhase, tense.ScenarioEndReason!.Value));
    }

    [Fact]
    public void M7_26_M7_27_a_defense_without_any_player_base_is_won_after_60_running_seconds_with_the_squad_alive()
    {
        var map = TestMaps.Empty();
        Assert.Null(map.PlayerBase);
        var sim = new GameSimulation(map, new SimulationSettings
        {
            Scenario = new ScenarioRules { Defense = true, EliminateAllEnemies = false, DefeatWhenSquadLost = true, SurviveSeconds = 60 },
        });
        Player(sim, C(5, 5));
        sim.Advance(1.0 / 30);
        sim.SetPaused(true);
        for (var i = 0; i < 30 * 90; i++)
        {
            sim.Advance(1.0 / 30); // the pause adds nothing
        }

        Assert.Equal(ScenarioPhase.Running, sim.ScenarioPhase);
        sim.SetPaused(false);
        for (var i = 0; i < (30 * 60) - 3; i++)
        {
            sim.Advance(1.0 / 30); // 1 + 1797 ticks: 59.93 s of running time
        }

        Assert.Equal(ScenarioPhase.Running, sim.ScenarioPhase);
        for (var i = 0; i < 3; i++)
        {
            sim.Advance(1.0 / 30);
        }

        Assert.Equal((ScenarioPhase.Victory, ScenarioEndReason.Survived), (sim.ScenarioPhase, sim.ScenarioEndReason!.Value));
        Assert.Equal(60.0, sim.ScenarioTime, 1e-6);
    }

    [Fact]
    public void Defense_waves_come_out_of_a_source_hex_with_their_AI_and_repelling_them_all_is_the_victory()
    {
        var map = TestMaps.Empty();
        var source = new[] { C(5, 0), C(6, 0) };
        var wave = new WaveDefinition("w", "Натиск", WaveStart.AtTime, 0, new[]
        {
            new SpawnGroupDefinition("g", null, new UnitProfile("e", "Враг", Fighter with { MaxHealth = 30 }, System.Array.Empty<string>()), 3, IntervalSeconds: 0)
            {
                SourceCells = source, Ai = new AiProfile(AiPreset.AttackObjective),
            },
        });
        var sim = new GameSimulation(map, new SimulationSettings
        {
            AutoAttacks = true,
            Scenario = new ScenarioRules { Defense = true, EliminateAllEnemies = false, DefeatWhenSquadLost = false, Waves = new[] { wave }, Objects = new[] { new MissionObject("gen", "Генератор", C(5, 8), 5000) } },
        });
        sim.Step();
        Assert.Equal(2, sim.State.Units.Count(u => u.Side == Side.Enemy)); // two source hexes free: two at once, the third waits
        Assert.All(sim.State.Units.Where(u => u.Side == Side.Enemy), u => Assert.Equal(AiPreset.AttackObjective, u.Ai!.Profile.Preset));
        Run(sim, 60);
        foreach (var e in sim.State.Units.Where(u => u.Side == Side.Enemy).ToList())
        {
            sim.State.ApplyDamage(e.Id, 1000);
        }

        Run(sim, 120);
        foreach (var e in sim.State.Units.Where(u => u.Side == Side.Enemy).ToList())
        {
            sim.State.ApplyDamage(e.Id, 1000);
        }

        Run(sim, 10);
        Assert.Equal((ScenarioPhase.Victory, ScenarioEndReason.AllWavesCleared), (sim.ScenarioPhase, sim.ScenarioEndReason!.Value));
    }
}
