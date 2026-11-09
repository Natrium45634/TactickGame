using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Content.Kinds;
using HexTactics.Content.Library;
using HexTactics.Content.Model;
using HexTactics.Content.Registry;
using HexTactics.Content.Runtime;
using HexTactics.Content.Transfer;
using HexTactics.Core.Hex;
using HexTactics.Core.Units;
using HexTactics.Tests.Support;
using Xunit;

namespace HexTactics.Content.Tests;

/// <summary>The scenario definition (spec part 7 §2.1, §3, §10; acceptance M7-51, M7-54, M7-59).</summary>
public class ScenarioContentTests
{
    private const string TrainingId = "core.scenario.training";

    private static ContentItem Scenario(ContentLibrary library, string name, params (int Col, int Row)[] enemies)
    {
        var item = library.Create(ContentTypeRegistry.Scenario, name);
        var data = (ScenarioData)item.Data;
        foreach (var (col, row) in enemies)
        {
            data.Enemies.Add(new PlacedUnitData { Unit = RuntimeFactory.DefaultUnitId, Col = col, Row = row });
        }

        return item;
    }

    [Fact]
    public void The_built_in_training_battle_is_valid_and_builds_three_enemies_on_the_map_and_a_squad_of_three()
    {
        var library = ContentTestKit.Library(null);
        var item = library.Find(ContentTypeRegistry.Scenario, TrainingId)!;
        Assert.Empty(RuntimeFactory.CheckScenario(library, item));
        var content = RuntimeFactory.BuildScenario(library, item);
        var scenario = content.Scenario!;
        Assert.Equal(3, scenario.Placed.Count);
        Assert.All(scenario.Placed, p => Assert.Equal(Side.Enemy, p.Side));
        Assert.Equal(HexCoord.FromOffset(6, 2), scenario.Placed[0].Cell);
        Assert.Equal(HexDirection.South, scenario.Placed[0].Facing);
        Assert.Equal(3, content.Squad.Count);
        Assert.True(content.AutoAttacks && content.Blocking && scenario.StartPaused);
        Assert.Equal(0, scenario.Rules.TimeLimitSeconds);
        Assert.Equal("Победа: все враги (3) побеждены. Поражение: все ваши юниты погибли и в резерве никого.", scenario.GoalsText);
    }

    [Fact]
    public void M7_51_scenarios_is_one_new_category_and_holds_all_four_modes_the_ones_without_executor_are_refused()
    {
        var registry = ContentTypeRegistry.CreateDefault();
        Assert.Single(registry.All, k => k.Title == "Сценарии");
        Assert.Equal(new[] { "battle", "tower_defense", "defense", "exploration" }, ScenarioData.Modes.Select(m => m.Id));
        Assert.Equal(new[] { "battle", "tower_defense" }, ScenarioData.Modes.Where(m => m.Available).Select(m => m.Id)); // v0.36.0

        var library = ContentTestKit.Library(null);
        var defense = Scenario(library, "Оборона", (6, 2));
        ((ScenarioData)defense.Data).Mode = "defense";
        Assert.Contains(library.Validate(defense), i => i.IsError && i.Message.Contains("появится в v0.37.0", StringComparison.Ordinal));
    }

    [Fact]
    public void A_user_scenario_keeps_every_field_after_save_and_reload()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var item = Scenario(library, "Засада", (6, 2), (8, 3));
        var data = (ScenarioData)item.Data;
        data.Enemies[1].Facing = HexDirection.NorthWest;
        data.Ruleset = CombatRulesetData.Standard;
        data.AutoAttacks = false;
        data.Blocking = false;
        data.StartPaused = false;
        data.TimeLimit = 90;
        data.Squad.Add(RuntimeFactory.DefaultUnitId);
        Assert.True(library.Save(item).Ok);

        var loaded = (ScenarioData)ContentTestKit.Library(dir.Path).Find(item.Ref)!.Data;
        Assert.Equal(data.ToJson().ToJsonString(), loaded.ToJson().ToJsonString());
        Assert.Equal(HexDirection.NorthWest, loaded.Enemies[1].Facing);
        Assert.Equal(4, loaded.Squad.Count);
        Assert.Equal("Обычный бой · врагов 2 · отряд 4 · 90 с", loaded.Summary());
        Assert.Contains("или прошло 90 с боя", loaded.GoalsText(), StringComparison.Ordinal);
    }

    [Fact]
    public void M7_59_the_checks_say_what_to_fix_and_a_broken_scenario_never_starts()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        string Errors(ContentItem item) => string.Join(" | ", RuntimeFactory.CheckScenario(library, item).Select(i => i.Message));

        var rock = Scenario(library, "Скала", (5, 2)); // an obstacle of the prototype map
        Assert.Contains("стоит на 5,2 — там нет проходимого гекса карты", Errors(rock), StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => RuntimeFactory.BuildScenario(library, rock));

        var ghost = Scenario(library, "Призрак", (6, 2));
        ((ScenarioData)ghost.Data).Enemies[0].Unit = "unit_gone";
        Assert.Contains("юнит «unit_gone» не найден", Errors(ghost), StringComparison.Ordinal);

        var twice = Scenario(library, "Тесно", (6, 2), (6, 2));
        Assert.Contains("на гексе 6,2 два врага", Errors(twice), StringComparison.Ordinal);

        var empty = Scenario(library, "Пусто");
        Assert.Contains("хотя бы один враг", Errors(empty), StringComparison.Ordinal);

        var crowd = Scenario(library, "Толпа", (6, 2));
        ((ScenarioData)crowd.Data).Squad.AddRange(Enumerable.Repeat(RuntimeFactory.DefaultUnitId, 3));
        Assert.Contains("от 1 до 5 юнитов, сейчас 6", Errors(crowd), StringComparison.Ordinal);

        var time = Scenario(library, "Вечность", (6, 2));
        ((ScenarioData)time.Data).TimeLimit = -5;
        Assert.Contains("ограничение времени", Errors(time), StringComparison.Ordinal);

        var lost = Scenario(library, "Без карты", (6, 2));
        ((ScenarioData)lost.Data).Map = "map_gone";
        Assert.Contains("карта «map_gone» не найдена", Errors(lost), StringComparison.Ordinal);

        // A map without deploy hexes: the squad has nowhere to come out.
        var field = library.Create(ContentTypeRegistry.Map, "Поле");
        var map = (MapData)field.Data;
        map.Tiles.Clear();
        for (var c = 0; c < 4; c++)
        {
            map.Tiles.Add(new MapTileData { Col = c, Row = 0 });
        }

        Assert.True(library.Save(field).Ok);
        var open = Scenario(library, "В поле", (2, 0));
        ((ScenarioData)open.Data).Map = field.Id;
        Assert.Contains("нет гексов выставления", Errors(open), StringComparison.Ordinal);

        Assert.True(library.Save(rock).Ok); // a draft with a misplaced enemy is kept, it only cannot be started
    }

    [Fact]
    public void References_cover_the_map_rules_and_every_unit_and_are_remapped_on_import_copy()
    {
        var library = ContentTestKit.Library(null);
        var item = Scenario(library, "Ссылки", (6, 2));
        var data = (ScenarioData)item.Data;
        data.Ruleset = CombatRulesetData.Standard;
        data.Enemies[0].Unit = "unit_wolf";
        data.Squad[1] = "unit_wolf";
        Assert.Equal(new[] { "map:core.map.prototype", "combat_ruleset:" + CombatRulesetData.Standard, "unit:core.unit.operative", "unit:unit_wolf" },
            data.References().Select(r => $"{r.Kind}:{r.Id}"));

        data.RemapReferences(new Dictionary<ContentRef, string>
        {
            [new ContentRef(ContentTypeRegistry.Unit, "unit_wolf")] = "unit_wolf_2",
            [new ContentRef(ContentTypeRegistry.Map, "core.map.prototype")] = "map_copy",
        });
        Assert.Equal(("map_copy", "unit_wolf_2", "unit_wolf_2", RuntimeFactory.DefaultUnitId), (data.Map, data.Enemies[0].Unit, data.Squad[1], data.Squad[0]));
    }

    [Fact]
    public void M7_54_a_package_of_a_battle_carries_its_map_and_custom_units_and_keeps_the_enemies_in_order()
    {
        using var source = new TempDirectory();
        using var target = new TempDirectory();
        var library = ContentTestKit.Library(source.Path);
        var mapItem = library.Find(ContentTypeRegistry.Map, RuntimeFactory.DefaultMapId)!.CopyAs(library.NewId(ContentTypeRegistry.Map), ContentOrigin.User, "Арена сценария");
        mapItem.Revision = 0;
        Assert.True(library.Save(mapItem).Ok);
        var brute = library.Create(ContentTypeRegistry.Unit, "Громила");
        ((UnitData)brute.Data).MaxHealth = 300;
        Assert.True(library.Save(brute).Ok);
        var item = Scenario(library, "Громилы", (6, 2), (8, 3), (9, 5));
        var data = (ScenarioData)item.Data;
        data.Map = mapItem.Id;
        data.Enemies[1].Unit = brute.Id;
        data.Enemies[2].Facing = HexDirection.SouthWest;
        Assert.True(library.Save(item).Ok);

        var bytes = ContentExporter.ExportPackage(library, new[] { library.Find(item.Ref)! }, "Громилы", string.Empty, "0.34.0", DateTimeOffset.UnixEpoch);
        var clean = ContentTestKit.Library(target.Path);
        var preview = ContentImporter.Inspect(clean, "brutes.hxp", bytes);
        Assert.Contains(preview.Entries, e => e.Item.Ref == mapItem.Ref);
        Assert.Contains(preview.Entries, e => e.Item.Ref == brute.Ref);
        Assert.True(ContentImporter.Install(clean, preview).Ok);

        var imported = ContentTestKit.Library(target.Path);
        var back = imported.Find(item.Ref)!;
        Assert.Empty(RuntimeFactory.CheckScenario(imported, back));
        var placed = RuntimeFactory.BuildScenario(imported, back).Scenario!.Placed;
        Assert.Equal(new[] { (6, 2), (8, 3), (9, 5) }, placed.Select(p => (p.Cell.ToOffset().Col, p.Cell.ToOffset().Row)));
        Assert.Equal(300, placed[1].Profile.Stats.MaxHealth);
        Assert.Equal(HexDirection.SouthWest, placed[2].Facing);
    }
}
