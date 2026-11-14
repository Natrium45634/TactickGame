using System;
using System.Linq;
using System.Text.Json.Nodes;
using HexTactics.Content.Kinds;
using HexTactics.Content.Library;
using HexTactics.Content.Model;
using HexTactics.Content.Registry;
using HexTactics.Content.Runtime;
using HexTactics.Content.Transfer;
using HexTactics.Core.Hex;
using HexTactics.Core.Scenarios;
using HexTactics.Tests.Support;
using Xunit;

namespace HexTactics.Content.Tests;

/// <summary>The exploration scenario data (spec part 7 §6, §10; acceptance M7-31, M7-35, M7-54).</summary>
public class ExplorationContentTests
{
    /// <summary>On the prototype map: entry on its deploy hexes (1,5) and (0,6), exit (10,1)–(11,1) behind the wall, a guard.</summary>
    private static ContentItem Trip(ContentLibrary library)
    {
        var item = library.Create(ContentTypeRegistry.Scenario, "Вылазка");
        var s = (ScenarioData)item.Data;
        s.Mode = ScenarioData.Exploration;
        s.Entry.AddRange(new[] { new HexCellData(1, 5), new HexCellData(0, 6) });
        s.Exit.AddRange(new[] { new HexCellData(10, 1), new HexCellData(11, 1) });
        s.Enemies.Add(new PlacedUnitData { Unit = RuntimeFactory.DefaultUnitId, Col = 6, Row = 4, Ai = new AiData { Preset = "guard", GuardRadius = 1 } });
        return item;
    }

    private static string Errors(ContentLibrary library, ContentItem item) => string.Join(" | ", RuntimeFactory.CheckScenario(library, item).Select(i => i.Message));

    [Fact]
    public void An_exploration_round_trips_says_its_goals_and_builds_its_rules()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var item = Trip(library);
        var s = (ScenarioData)item.Data;
        s.ExitRule = "all";
        s.Required.AddRange(new[] { 1, 3 });
        s.ExtraGoal = "kill_marked";
        s.Enemies[0].Target = true;
        Assert.True(library.Save(item).Ok);

        var back = (ScenarioData)ContentTestKit.Library(dir.Path).Find(item.Ref)!.Data;
        Assert.Equal(s.ToJson().ToJsonString(), back.ToJson().ToJsonString());
        Assert.Equal((2, 2, "all", "kill_marked", true), (back.Entry.Count, back.Exit.Count, back.ExitRule, back.ExtraGoal, back.Enemies[0].Target));
        Assert.Equal(new[] { 1, 3 }, back.Required);
        Assert.Equal("Победа: все обязательные (карточки 1, 3) дошли до выхода живыми и отмеченные враги (1) побеждены. Поражение: погиб обязательный юнит или все ваши юниты погибли и в резерве никого.", back.GoalsText());
        Assert.Equal("Исследование · врагов 1 · вход 2 · выход 2 · отряд 3", back.Summary());

        Assert.Empty(RuntimeFactory.CheckScenario(library, item));
        var runtime = RuntimeFactory.BuildScenario(library, item).Scenario!;
        var rules = runtime.Rules;
        Assert.True(rules.Exploration && !rules.EliminateAllEnemies);
        Assert.Equal((ExitRule.AllRequired, ExtraGoal.KillMarked), (rules.ExitRule, rules.ExtraGoal));
        Assert.Equal(new[] { HexCoord.FromOffset(1, 5), HexCoord.FromOffset(0, 6) }, rules.EntryCells);
        Assert.Equal(new[] { 1, 3 }, rules.RequiredEntries);
        Assert.True(Assert.Single(runtime.Placed).Target);
    }

    [Fact]
    public void An_exploration_needs_no_deploy_hexes_of_the_map_its_entry_is_enough()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var field = library.Create(ContentTypeRegistry.Map, "Тропа");
        var map = (MapData)field.Data;
        map.Tiles.Clear();
        for (var c = 0; c < 6; c++)
        {
            map.Tiles.Add(new MapTileData { Col = c, Row = 0 });
        }

        Assert.True(library.Save(field).Ok);
        var item = library.Create(ContentTypeRegistry.Scenario, "По тропе");
        var s = (ScenarioData)item.Data;
        s.Mode = ScenarioData.Exploration;
        s.Map = field.Id;
        s.Entry.Add(new HexCellData(0, 0));
        s.Exit.Add(new HexCellData(5, 0));
        Assert.Empty(RuntimeFactory.CheckScenario(library, item)); // no deploy hexes, no enemies: still a valid exploration (M7-34)
    }

    [Fact]
    public void M7_31_without_an_entry_or_an_exit_the_exploration_is_an_error_and_never_starts()
    {
        var library = ContentTestKit.Library(null);
        var item = Trip(library);
        var s = (ScenarioData)item.Data;
        s.Entry.Clear();
        s.Exit.Clear();
        var errors = Errors(library, item);
        Assert.Contains("нет входа", errors, StringComparison.Ordinal);
        Assert.Contains("нет выхода", errors, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => RuntimeFactory.BuildScenario(library, item));
    }

    [Fact]
    public void M7_35_tasks_without_a_possible_end_are_errors_before_the_test()
    {
        var library = ContentTestKit.Library(null);
        var item = Trip(library);
        var s = (ScenarioData)item.Data;
        s.ExitRule = "all";
        s.ExtraGoal = "kill_marked";
        s.KeyUnit = 9;
        var errors = Errors(library, item);
        Assert.Contains("без обязательных", errors, StringComparison.Ordinal);
        Assert.Contains("без отмеченных", errors, StringComparison.Ordinal);
        Assert.DoesNotContain("ключевой юнит", errors, StringComparison.Ordinal); // the key matters only with the rule «key»

        s.ExitRule = "key";
        s.ExtraGoal = "none";
        Assert.Contains("ключевой юнит — карточка 9, а в отряде 3", Errors(library, item), StringComparison.Ordinal);
        s.KeyUnit = 2;

        s.Exit.Clear();
        s.Exit.Add(new HexCellData(8, 3)); // a ground hex closed by the walls of the prototype
        Assert.Contains("выход недостижим от входа", Errors(library, item), StringComparison.Ordinal);

        s.Exit.Clear();
        s.Exit.Add(new HexCellData(1, 5));
        s.Entry.Add(new HexCellData(5, 3));
        s.Enemies[0].Col = 0;
        s.Enemies[0].Row = 6;
        errors = Errors(library, item);
        Assert.Contains("гекс 1,5 — и вход, и выход", errors, StringComparison.Ordinal);
        Assert.Contains("гекс входа 5,3 — там нет проходимого гекса", errors, StringComparison.Ordinal);
        Assert.Contains("враг стоит на гексе входа 0,6", errors, StringComparison.Ordinal);
    }

    [Fact]
    public void A_scenario_of_v0_37_without_the_exploration_keys_reads_and_writes_as_before()
    {
        var old = JsonNode.Parse("""
            { "mode": "battle", "map": "core.map.prototype", "ruleset": "", "autoAttacks": true, "blocking": true,
              "squad": ["core.unit.operative"], "enemies": [ { "unit": "core.unit.operative", "col": 6, "row": 2, "facing": "South" } ],
              "timeLimit": 0, "startPaused": true }
            """)!.AsObject();
        var issues = new IssueList();
        var data = ScenarioData.Read(old, issues);
        Assert.Empty(issues.Items);
        Assert.Equal(("key", 1, "none", 0, 0), (data.ExitRule, data.KeyUnit, data.ExtraGoal, data.Entry.Count, data.Exit.Count));
        Assert.Equal(old.ToJsonString(), data.ToJson().ToJsonString()); // no new keys written
    }

    [Fact]
    public void M7_54_a_package_of_an_exploration_keeps_its_entry_exit_rule_and_targets()
    {
        using var source = new TempDirectory();
        using var target = new TempDirectory();
        var library = ContentTestKit.Library(source.Path);
        var item = Trip(library);
        var s = (ScenarioData)item.Data;
        s.ExtraGoal = "kill_marked";
        s.Enemies[0].Target = true;
        s.KeyUnit = 2;
        Assert.True(library.Save(item).Ok);

        var bytes = ContentExporter.ExportPackage(library, new[] { library.Find(item.Ref)! }, "Вылазка", string.Empty, "0.38.0", DateTimeOffset.UnixEpoch);
        var clean = ContentTestKit.Library(target.Path);
        Assert.True(ContentImporter.Install(clean, ContentImporter.Inspect(clean, "trip.hxp", bytes)).Ok);
        var back = (ScenarioData)ContentTestKit.Library(target.Path).Find(item.Ref)!.Data;
        Assert.Equal(s.ToJson().ToJsonString(), back.ToJson().ToJsonString());
        Assert.Empty(RuntimeFactory.CheckScenario(ContentTestKit.Library(target.Path), ContentTestKit.Library(target.Path).Find(item.Ref)!));
    }
}
