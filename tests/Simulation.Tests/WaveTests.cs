using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Combat;
using HexTactics.Core.Hex;
using HexTactics.Core.Map;
using HexTactics.Core.Scenarios;
using HexTactics.Core.Spawning;
using HexTactics.Core.Units;
using HexTactics.Tests.Shared;
using Xunit;

namespace HexTactics.Simulation.Tests;

/// <summary>Waves, spawn queues, base lives and Tower Defense goals (spec part 7 §4.5, §4.6, §11.2; acceptance M7-17…M7-23).</summary>
public class WaveTests
{
    private static HexCoord C(int col, int row) => HexCoord.FromOffset(col, row);

    /// <summary>R1: base 1 (2,0) straight down to player base 1 (2,8); R2: base 2 (8,0) down to player base 2 (8,8).</summary>
    private static readonly EnemyRoute R1 = Line("R1", 1, 2);
    private static readonly EnemyRoute R2 = Line("R2", 2, 8);

    private static EnemyRoute Line(string id, int number, int col) =>
        new(id, id, number, number, Enumerable.Range(0, 9).Select(r => C(col, r)).ToList());

    private static readonly UnitProfile Infantry = new("inf", "Пехотинец", new UnitStats { MaxHealth = 100 }, System.Array.Empty<string>());

    private static GameSimulation Sim(ScenarioRules rules)
    {
        var open = TestMaps.Empty();
        var bases = new List<MapBase>
        {
            new(BaseSide.Enemy, 1, "Враги 1", new[] { C(2, 0) }), new(BaseSide.Enemy, 2, "Враги 2", new[] { C(8, 0) }),
            new(BaseSide.Player, 1, "База 1", new[] { C(2, 8) }), new(BaseSide.Player, 2, "База 2", new[] { C(8, 8) }),
        };
        var map = new MapDefinition(open.Name, open.Grid, open.SpawnZones, open.PlayerBase) { Bases = bases, Routes = new[] { R1, R2 } };
        return new GameSimulation(map, new SimulationSettings { Scenario = rules });
    }

    private static ScenarioRules TowerDefense(params WaveDefinition[] waves) => new()
    {
        TowerDefense = true, EliminateAllEnemies = false, DefeatWhenSquadLost = false, Waves = waves,
        Bases = new[] { new BaseLives(1, 10), new BaseLives(2, 10) },
    };

    /// <summary>The demonstration wave of spec §4.5: 5 «Пехотинцев» every second on R1, 2 every half second on R2 after 3 s.</summary>
    private static WaveDefinition W1() => new("w1", "Волна 1", WaveStart.AtTime, 0, new[]
    {
        new SpawnGroupDefinition("g1", R1, Infantry, 5, IntervalSeconds: 1),
        new SpawnGroupDefinition("g2", R2, Infantry, 2, IntervalSeconds: 0.5, StartOffsetSeconds: 3),
    });

    private static List<SimulationEvent> Run(GameSimulation sim, int ticks)
    {
        var all = new List<SimulationEvent>();
        for (var i = 0; i < ticks; i++)
        {
            sim.Step();
            all.AddRange(sim.DrainEvents());
            var cells = sim.State.Units.Where(u => u.IsAlive).Select(u => u.Cell).ToList();
            Assert.Equal(cells.Count, cells.Distinct().Count()); // M7-57 also for spawns
        }

        return all;
    }

    [Fact]
    public void M7_17_a_wave_of_5_and_2_on_two_routes_counts_7_and_lets_in_exactly_7()
    {
        var wave = W1();
        Assert.Equal(7, wave.Total);
        var sim = Sim(TowerDefense(wave));
        var events = Run(sim, 30 * 20);
        var spawned = events.OfType<WaveEnemySpawned>().ToList();
        Assert.Equal(7, spawned.Count);
        Assert.Equal(5, spawned.Count(s => s.RouteId == "R1"));
        Assert.Equal(2, spawned.Count(s => s.RouteId == "R2"));
        Assert.Single(events.OfType<WaveStarted>());
    }

    [Fact]
    public void M7_18_spawn_times_follow_game_time_at_any_frame_rate()
    {
        List<double> Times(double frame)
        {
            var sim = Sim(TowerDefense(W1()));
            var times = new List<double>();
            for (var t = 0.0; t < 12; t += frame)
            {
                sim.Advance(frame);
                times.AddRange(sim.DrainEvents().OfType<WaveEnemySpawned>().Select(e => e.Tick * sim.Settings.TickDuration));
            }

            return times;
        }

        var at30 = Times(1.0 / 30);
        Assert.Equal(at30, Times(1.0 / 144));
        Assert.Equal(at30, Times(1.0 / 20));
        Assert.Equal(7, at30.Count);
    }

    [Fact]
    public void M7_19_the_pause_stops_the_timers_and_the_queues_and_nothing_spawns_twice()
    {
        var sim = Sim(TowerDefense(W1()));
        sim.Advance(1.0 / 30);
        sim.SetPaused(true);
        for (var i = 0; i < 300; i++)
        {
            sim.Advance(1.0 / 30);
        }

        Assert.Equal(1, sim.State.UnitCount); // only the first enemy of second 0
        Assert.Equal(6, sim.UpcomingEnemies);
        sim.SetPaused(false);
        var spawned = 0; // the first enemy's event is still in the queue: all seven are counted once below
        for (var i = 0; i < 30 * 20; i++)
        {
            sim.Advance(1.0 / 30);
            spawned += sim.DrainEvents().OfType<WaveEnemySpawned>().Count();
        }

        Assert.Equal(7, spawned);
        Assert.Equal(0, sim.UpcomingEnemies);
    }

    [Fact]
    public void M7_20_enemies_asked_for_at_the_same_time_wait_in_the_queue_of_the_entrance()
    {
        var crowd = new WaveDefinition("w", "Толпа", WaveStart.AtTime, 0, new[] { new SpawnGroupDefinition("g", R1, Infantry, 3, IntervalSeconds: 0) });
        var sim = Sim(TowerDefense(crowd));
        sim.Step();
        sim.DrainEvents();
        Assert.Equal(1, sim.State.UnitCount);
        Assert.Equal(2, sim.QueuedEnemies); // the others wait off the map, not on the same hex
        var events = Run(sim, 200);
        var ticks = events.OfType<WaveEnemySpawned>().Select(e => e.Tick).ToList();
        Assert.Equal(2, ticks.Count);
        Assert.True(ticks[0] < ticks[1]);
        Assert.Equal(0, sim.QueuedEnemies);
    }

    [Fact]
    public void M7_21_an_enemy_of_the_route_to_base_2_takes_a_life_of_base_2_only_and_leaves_once()
    {
        var wave = new WaveDefinition("w", "Одна", WaveStart.AtTime, 0, new[] { new SpawnGroupDefinition("g", R2, Infantry, 1) });
        var sim = Sim(TowerDefense(wave, new WaveDefinition("w2", "Вторая", WaveStart.AtTime, 60, new[] { new SpawnGroupDefinition("g", R1, Infantry, 1) })));
        var events = Run(sim, 30 * 10);
        var leak = Assert.Single(events.OfType<EnemyLeaked>());
        Assert.Equal((2, "R2", 9), (leak.Base, leak.RouteId, leak.LivesLeft));
        Assert.Equal(10, sim.BaseLives[1]);
        Assert.Equal(9, sim.BaseLives[2]);
        Assert.Null(sim.State.FindUnit(leak.Unit)); // off the map, not dead
        Assert.Empty(events.OfType<UnitDied>());
    }

    [Fact]
    public void M7_22_M7_23_no_victory_while_an_enemy_is_alive_or_to_come_victory_after_the_last_wave_defeat_when_the_base_falls()
    {
        var two = new WaveDefinition("w", "Две", WaveStart.AtTime, 0, new[] { new SpawnGroupDefinition("g", R1, Infantry, 2, IntervalSeconds: 3) });
        var sim = Sim(TowerDefense(two));
        sim.Step();
        var first = sim.State.Units.Single().Id;
        sim.State.ApplyDamage(first, 100000);
        Run(sim, 30);
        Assert.Equal(ScenarioPhase.Running, sim.ScenarioPhase); // the second enemy is still to come
        Run(sim, 60);
        var second = sim.State.Units.Single().Id;
        Assert.Equal(ScenarioPhase.Running, sim.ScenarioPhase); // it is alive
        sim.State.ApplyDamage(second, 100000);
        Run(sim, 2);
        Assert.Equal((ScenarioPhase.Victory, ScenarioEndReason.AllWavesCleared), (sim.ScenarioPhase, sim.ScenarioEndReason!.Value));

        var weak = Sim(TowerDefense(two) with { Bases = new[] { new BaseLives(1, 2), new BaseLives(2, 5) } });
        Run(weak, 30 * 20);
        Assert.Equal((ScenarioPhase.Defeat, ScenarioEndReason.BaseLost), (weak.ScenarioPhase, weak.ScenarioEndReason!.Value));
        Assert.Equal(0, weak.BaseLives[1]);
    }

    [Fact]
    public void Defeat_policies_are_chosen_explicitly_and_a_non_critical_base_may_fall()
    {
        var one = new WaveDefinition("w", "Одна", WaveStart.AtTime, 0, new[] { new SpawnGroupDefinition("g", R1, Infantry, 1), new SpawnGroupDefinition("h", R2, Infantry, 3, IntervalSeconds: 4) });
        var notCritical = Sim(TowerDefense(one) with { Bases = new[] { new BaseLives(1, 1, Critical: false), new BaseLives(2, 9) } });
        var events = Run(notCritical, 30 * 12);
        Assert.Contains(events.OfType<EnemyLeaked>(), l => l.Base == 1);
        Assert.NotEqual(ScenarioPhase.Defeat, notCritical.ScenarioPhase); // base 1 is not critical

        var shared = Sim(TowerDefense(one) with { Bases = new[] { new BaseLives(1, 2), new BaseLives(2, 2) }, Defeat = DefeatPolicy.SharedLives });
        Run(shared, 30 * 25);
        Assert.Equal(ScenarioPhase.Defeat, shared.ScenarioPhase); // 4 leaks empty the shared pool of 4
        Assert.Equal(0, shared.SharedLives);

        var all = Sim(TowerDefense(one) with { Bases = new[] { new BaseLives(1, 1), new BaseLives(2, 9) }, Defeat = DefeatPolicy.AllBases });
        Run(all, 30 * 25);
        Assert.NotEqual(ScenarioPhase.Defeat, all.ScenarioPhase); // base 2 still has lives
    }

    [Fact]
    public void A_unit_holding_the_base_hex_does_not_stop_the_leak_at_the_last_border()
    {
        var wave = new WaveDefinition("w", "Одна", WaveStart.AtTime, 0, new[] { new SpawnGroupDefinition("g", R1, Infantry, 1) });
        var sim = Sim(TowerDefense(wave));
        sim.Spawn(SpawnRequest.AtCell(C(2, 8), Side.Player, HexDirection.North));
        var events = Run(sim, 30 * 12);
        var leak = Assert.Single(events.OfType<EnemyLeaked>());
        Assert.Equal(1, leak.Base);
        Assert.Equal(9, sim.BaseLives[1]);
    }

    [Fact]
    public void After_previous_starts_the_next_wave_only_when_the_previous_one_is_resolved_and_waits_its_delay()
    {
        var first = new WaveDefinition("w1", "Первая", WaveStart.AtTime, 0, new[] { new SpawnGroupDefinition("g", R1, Infantry, 1) });
        var second = new WaveDefinition("w2", "Вторая", WaveStart.AfterPrevious, 2, new[] { new SpawnGroupDefinition("g", R2, Infantry, 1) });
        var sim = Sim(TowerDefense(first, second));
        var events = Run(sim, 30 * 30);
        var starts = events.OfType<WaveStarted>().ToList();
        var resolved = events.OfType<WaveResolved>().First(r => r.WaveId == "w1");
        Assert.Equal(2, starts.Count);
        Assert.Equal(resolved.Tick + 60, starts[1].Tick, 1.0); // 2 s after the first wave was resolved (its enemy leaked)
        Assert.Equal(ScenarioPhase.Victory, sim.ScenarioPhase); // both leaked, bases survive, nobody left
    }
}
