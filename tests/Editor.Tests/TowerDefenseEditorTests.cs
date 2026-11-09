using System;
using System.Linq;
using HexTactics.Content.Kinds;
using HexTactics.Content.Library;
using HexTactics.Content.Registry;
using HexTactics.Content.Runtime;
using HexTactics.Core.Scenarios;
using HexTactics.Editor.Scenarios;
using HexTactics.Simulation;
using HexTactics.Tests.Support;
using Xunit;

namespace HexTactics.Editor.Tests;

/// <summary>Waves and bases in the scenario editor and a Tower Defense battle (spec part 7 §4.5–4.7; M7-17, M7-21, M7-23, M7-25).</summary>
public class TowerDefenseEditorTests
{
    /// <summary>6×7: enemy bases 1 (1,0) and 2 (4,0), player bases 1 (1,6) and 2 (4,6), a deploy hex (0,3), «Левая» and «Правая».</summary>
    private static ContentItem Field(ContentLibrary library)
    {
        var item = library.Create(ContentTypeRegistry.Map, "Две колонны");
        var map = (MapData)item.Data;
        map.Columns = 6;
        map.Rows = 7;
        map.Tiles.Clear();
        for (var c = 0; c < 6; c++)
        {
            for (var r = 0; r < 7; r++)
            {
                var tile = (c, r) switch
                {
                    (1, 0) or (4, 0) => "core.tile.enemy_base",
                    (1, 6) or (4, 6) => "core.tile.player_base",
                    (0, 3) => "core.tile.deploy",
                    _ => "core.tile.ground",
                };
                map.Tiles.Add(new MapTileData { Col = c, Row = r, Tile = tile, Base = c == 4 && r is 0 or 6 ? 2 : null });
            }
        }

        foreach (var (id, name, col, number) in new[] { ("route1", "Левая", 1, 1), ("route2", "Правая", 4, 2) })
        {
            var route = new MapRouteData { Id = id, Name = name, Source = number, Target = number };
            route.Cells.AddRange(Enumerable.Range(0, 7).Select(r => (col, r)));
            map.Routes.Add(route);
        }

        Assert.True(library.Save(item).Ok);
        return library.Find(item.Ref)!;
    }

    private static ScenarioEditorModel Defense(ContentLibrary library)
    {
        var map = Field(library);
        var model = new ScenarioEditorModel(library, library.Create(ContentTypeRegistry.Scenario, "Оборона колонн"));
        Assert.Null(model.SetMap(map.Id));
        Assert.Null(model.SetMode(ScenarioData.TowerDefense));
        return model;
    }

    [Fact]
    public void M7_25_a_wave_is_duplicated_disabled_and_restored_with_its_routes_units_and_sums()
    {
        using var dir = new TempDirectory();
        var model = Defense(ContentTestKit.Library(dir.Path));
        var first = model.AddWave();
        Assert.Equal(5, model.Scenario.WaveEnemies);
        Assert.Equal("route1", model.Scenario.Waves[0].Groups[0].Route);
        var g2 = model.AddGroup(first)!;
        Assert.Null(model.SetGroupRoute(first, g2, "route2"));
        Assert.Null(model.SetGroupCount(first, g2, "2"));
        Assert.Null(model.SetGroupTime(first, g2, "interval", "0,5"));
        Assert.Null(model.SetGroupTime(first, g2, "offset", "3"));
        Assert.Equal(7, model.Scenario.Waves[0].Total); // M7-17: the sum is computed, never typed

        var copy = model.DuplicateWave(first)!;
        Assert.Equal((2, 14), (model.Scenario.Waves.Count, model.Scenario.WaveEnemies));
        Assert.Equal(new[] { ("route1", 10), ("route2", 4) }, model.RouteTotals());
        Assert.Equal(model.Scenario.Waves[0].Groups.Select(g => (g.Route, g.Unit)), model.Scenario.Waves[1].Groups.Select(g => (g.Route, g.Unit)));

        model.SetWaveEnabled(copy, false);
        Assert.Equal(7, model.Scenario.WaveEnemies);
        Assert.True(model.Undo()); // restored
        Assert.Equal(14, model.Scenario.WaveEnemies);
        model.MoveWave(copy, -1);
        Assert.Equal(copy, model.Scenario.Waves[0].Id);
        Assert.DoesNotContain(model.Validate(), i => i.IsError);
        Assert.True(model.Save().Ok);
    }

    [Fact]
    public void Groups_and_bases_refuse_bad_values_and_the_preview_tells_the_schedule()
    {
        using var dir = new TempDirectory();
        var model = Defense(ContentTestKit.Library(dir.Path));
        var wave = model.AddWave();
        var group = model.Scenario.Waves[0].Groups[0].Id;
        Assert.Contains("нарисуйте его в редакторе карт", model.SetGroupRoute(wave, group, "route9"), StringComparison.Ordinal);
        Assert.NotNull(model.SetGroupCount(wave, group, "-1"));
        Assert.NotNull(model.SetGroupTime(wave, group, "firstDelay", "скоро"));
        Assert.NotNull(model.SetWaveDelay(wave, "-2"));
        Assert.Null(model.SetWaveStart(wave, WaveData.AtTime));
        Assert.Null(model.SetWaveDelay(wave, "4"));

        Assert.Equal(new[] { (1, "База игрока", 20, true), (2, "База игрока 2", 20, true) }, model.PlayerBases());
        Assert.NotNull(model.SetBaseLives(2, "0"));
        Assert.Null(model.SetBaseLives(2, "3"));
        model.SetBaseCritical(2, false);
        model.SetDefeat("all_bases");
        Assert.Equal((3, false, "all_bases"), (model.PlayerBases()[1].Lives, model.PlayerBases()[1].Critical, model.Scenario.Defeat));

        var preview = string.Join(" | ", model.Preview());
        Assert.Contains("Волн: 1 · врагов по волнам: 5 · «Левая» — 5.", preview, StringComparison.Ordinal);
        Assert.Contains("1. «Волна 1» — на 4 с боя: 5 × «Оперативник» по «Левая» с 0 с каждые 1 с · всего 5", preview, StringComparison.Ordinal);
        Assert.Contains("«База игрока 2» — 3 жизн.", preview, StringComparison.Ordinal);
        Assert.Contains("Поражение: все базы игрока потеряли все жизни", preview, StringComparison.Ordinal);
    }

    [Fact]
    public void M7_17_M7_21_M7_23_a_defense_made_in_the_editor_lets_in_its_enemies_counts_leaks_per_base_and_is_won_or_lost_by_the_lives()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var model = Defense(library);
        var wave = model.AddWave();
        var right = model.AddGroup(wave)!;
        model.SetGroupRoute(wave, right, "route2");
        model.SetGroupCount(wave, right, "2");
        model.SetWaveStart(wave, WaveData.AtTime);
        Assert.Null(model.SetBaseLives(1, "10"));
        Assert.Null(model.SetBaseLives(2, "10"));
        Assert.True(model.Save().Ok);

        var sim = MatchFactory.Start(RuntimeFactory.BuildScenario(library, library.Find(model.Item.Ref)!));
        Assert.Equal((ScenarioPhase.Preparing, 1, 7), (sim.ScenarioPhase, sim.WaveCount, sim.UpcomingEnemies));
        sim.SetPaused(false);
        var spawned = 0;
        var leaks = new System.Collections.Generic.List<EnemyLeaked>();
        for (var i = 0; i < 30 * 30 && !sim.IsScenarioOver; i++)
        {
            sim.Advance(1.0 / 30);
            var events = sim.DrainEvents();
            spawned += events.OfType<WaveEnemySpawned>().Count();
            leaks.AddRange(events.OfType<EnemyLeaked>());
        }

        Assert.Equal(7, spawned);
        Assert.Equal(5, leaks.Count(l => l.Base == 1));
        Assert.Equal(2, leaks.Count(l => l.Base == 2));
        Assert.Equal((5, 8), (sim.BaseLives[1], sim.BaseLives[2])); // each base lost only its own lives
        Assert.Equal((ScenarioPhase.Victory, ScenarioEndReason.AllWavesCleared), (sim.ScenarioPhase, sim.ScenarioEndReason!.Value));

        Assert.Null(model.SetBaseLives(1, "3"));
        Assert.True(model.Save().Ok);
        var lost = MatchFactory.Start(RuntimeFactory.BuildScenario(library, library.Find(model.Item.Ref)!));
        lost.SetPaused(false);
        for (var i = 0; i < 30 * 30 && !lost.IsScenarioOver; i++)
        {
            lost.Advance(1.0 / 30);
        }

        Assert.Equal((ScenarioPhase.Defeat, ScenarioEndReason.BaseLost), (lost.ScenarioPhase, lost.ScenarioEndReason!.Value));
    }
}
