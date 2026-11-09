using System;
using System.Linq;
using HexTactics.Content.Kinds;
using HexTactics.Content.Library;
using HexTactics.Content.Model;
using HexTactics.Content.Registry;
using HexTactics.Content.Runtime;
using HexTactics.Content.Transfer;
using HexTactics.Core.Scenarios;
using HexTactics.Tests.Support;
using Xunit;

namespace HexTactics.Content.Tests;

/// <summary>Tower Defense scenarios: waves, groups, base lives (spec part 7 §4.5, §4.6, §10; acceptance M7-17, M7-24, M7-25, M7-54).</summary>
public class TowerDefenseContentTests
{
    /// <summary>
    /// A 6×7 field: enemy bases 1 (1,0) and 2 (4,0), player bases 1 (1,6) and 2 (4,6), a deploy hex (0,3); routes «route1» and
    /// «route2» straight down their columns.
    /// </summary>
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

        foreach (var (id, col, number) in new[] { ("route1", 1, 1), ("route2", 4, 2) })
        {
            var route = new MapRouteData { Id = id, Name = id == "route1" ? "Левая" : "Правая", Source = number, Target = number };
            route.Cells.AddRange(Enumerable.Range(0, 7).Select(r => (col, r)));
            map.Routes.Add(route);
        }

        Assert.True(library.Save(item).Ok);
        return library.Find(item.Ref)!;
    }

    private static ContentItem Defense(ContentLibrary library, ContentItem map)
    {
        var item = library.Create(ContentTypeRegistry.Scenario, "Две колонны: оборона");
        var s = (ScenarioData)item.Data;
        s.Mode = ScenarioData.TowerDefense;
        s.Map = map.Id;
        var wave = new WaveData { Id = "wave1", Name = "Первая", Start = WaveData.AtTime };
        wave.Groups.Add(new SpawnGroupData { Id = "group1", Route = "route1", Unit = RuntimeFactory.DefaultUnitId, Count = 5, Interval = 1 });
        wave.Groups.Add(new SpawnGroupData { Id = "group2", Route = "route2", Unit = RuntimeFactory.DefaultUnitId, Count = 2, Interval = 0.5, Offset = 3 });
        s.Waves.Add(wave);
        s.BaseLives.Add(new BaseLivesData { Base = 2, Lives = 5, Critical = false });
        return item;
    }

    [Fact]
    public void Waves_groups_lives_and_the_defeat_policy_survive_save_and_reload_and_a_battle_writes_none_of_them()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var item = Defense(library, Field(library));
        ((ScenarioData)item.Data).Defeat = "shared_lives";
        Assert.True(library.Save(item).Ok);
        var back = (ScenarioData)ContentTestKit.Library(dir.Path).Find(item.Ref)!.Data;
        Assert.Equal(((ScenarioData)item.Data).ToJson().ToJsonString(), back.ToJson().ToJsonString());
        Assert.Equal((7, 7, "shared_lives"), (back.Waves[0].Total, back.WaveEnemies, back.Defeat));
        Assert.Equal("Tower Defense · волн 1 · врагов 7 · отряд 3", back.Summary());
        Assert.Contains("Поражение: общий запас жизней баз исчерпан", back.GoalsText(), StringComparison.Ordinal);

        var battle = (ScenarioData)library.Find(ContentTypeRegistry.Scenario, "core.scenario.training")!.Data;
        Assert.False(battle.ToJson().ContainsKey("waves") || battle.ToJson().ContainsKey("bases") || battle.ToJson().ContainsKey("defeat"));
    }

    [Fact]
    public void The_checks_name_the_wave_and_the_group_and_a_missing_or_broken_route_blocks_the_start()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var map = Field(library);
        var item = Defense(library, map);
        Assert.Empty(RuntimeFactory.CheckScenario(library, item));

        var s = (ScenarioData)item.Data;
        s.Waves[0].Groups[1].Count = 0;
        Assert.Contains(library.Validate(item), i => !i.IsError && i.Message.Contains("группа 2: 0 врагов", StringComparison.Ordinal));
        s.Waves[0].Groups[1].Count = 2;

        // M7-24: the route of a group is deleted on the map — the scenario cannot start and says which group needs it.
        var mapData = (MapData)map.Data;
        mapData.Routes.RemoveAll(r => r.Id == "route2");
        Assert.True(library.Save(map).Ok);
        var errors = RuntimeFactory.CheckScenario(library, item).Select(i => i.Message).ToList();
        Assert.Contains(errors, m => m.Contains("волна «Первая», группа 2: маршрута «route2» нет на карте «Две колонны»", StringComparison.Ordinal));
        Assert.Throws<InvalidOperationException>(() => RuntimeFactory.BuildScenario(library, item));

        // A hex taken away under the other route: the same, with the step and the hex.
        mapData.Tiles.RemoveAll(t => t.Col == 1 && t.Row == 3);
        Assert.True(library.Save(map).Ok);
        Assert.Contains(RuntimeFactory.CheckScenario(library, item), i => i.Message.Contains("маршрут «Левая» требует проверки — шаг 3: гекса (1,3) нет на карте", StringComparison.Ordinal));

        var empty = library.Create(ContentTypeRegistry.Scenario, "Пустая оборона");
        ((ScenarioData)empty.Data).Mode = ScenarioData.TowerDefense;
        Assert.Contains(library.Validate(empty), i => i.IsError && i.Message.Contains("хотя бы одна включённая волна с врагами", StringComparison.Ordinal));
    }

    [Fact]
    public void The_runtime_rules_carry_every_enabled_wave_and_the_lives_of_each_player_base()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var item = Defense(library, Field(library));
        var s = (ScenarioData)item.Data;
        s.Waves.Add(new WaveData { Id = "wave2", Name = "Выключенная", Enabled = false });
        var content = RuntimeFactory.BuildScenario(library, item);
        var rules = content.Scenario!.Rules;
        Assert.True(rules.TowerDefense);
        var wave = Assert.Single(rules.Waves);
        Assert.Equal((WaveStart.AtTime, 7), (wave.Start, wave.Total));
        Assert.Equal(new[] { "route1", "route2" }, wave.Groups.Select(g => g.Route.Id));
        Assert.Equal(3, wave.Groups[1].StartOffsetSeconds);
        Assert.Equal(new[] { (1, BaseLivesData.DefaultLives, true), (2, 5, false) }, rules.Bases.Select(b => (b.Base, b.Lives, b.Critical)));
        Assert.Equal(DefeatPolicy.AnyCriticalBase, rules.Defeat);
    }

    [Fact]
    public void M7_54_a_package_of_a_defense_scenario_carries_its_map_with_the_routes_and_the_units_of_the_waves()
    {
        using var source = new TempDirectory();
        using var target = new TempDirectory();
        var library = ContentTestKit.Library(source.Path);
        var map = Field(library);
        var runner = library.Create(ContentTypeRegistry.Unit, "Бегун");
        ((UnitData)runner.Data).MoveSpeed = 2;
        Assert.True(library.Save(runner).Ok);
        var item = Defense(library, map);
        ((ScenarioData)item.Data).Waves[0].Groups[1].Unit = runner.Id;
        Assert.True(library.Save(item).Ok);

        var bytes = ContentExporter.ExportPackage(library, new[] { library.Find(item.Ref)! }, "Оборона", string.Empty, "0.36.0", DateTimeOffset.UnixEpoch);
        var clean = ContentTestKit.Library(target.Path);
        var preview = ContentImporter.Inspect(clean, "defense.hxp", bytes);
        Assert.Contains(preview.Entries, e => e.Item.Ref == map.Ref);
        Assert.Contains(preview.Entries, e => e.Item.Ref == runner.Ref);
        Assert.True(ContentImporter.Install(clean, preview).Ok);
        var imported = ContentTestKit.Library(target.Path);
        var back = imported.Find(item.Ref)!;
        Assert.Empty(RuntimeFactory.CheckScenario(imported, back));
        var wave = RuntimeFactory.BuildScenario(imported, back).Scenario!.Rules.Waves[0];
        Assert.Equal((7, "route2", 2.0), (wave.Total, wave.Groups[1].Route.Id, wave.Groups[1].Profile.Stats.MoveSpeedHexPerSecond));
    }
}
