using System;
using System.Linq;
using HexTactics.Content.Kinds;
using HexTactics.Content.Library;
using HexTactics.Content.Registry;
using HexTactics.Content.Runtime;
using HexTactics.Core.Scenarios;
using HexTactics.Core.Units;
using HexTactics.Editor.Scenarios;
using HexTactics.Simulation;
using HexTactics.Tests.Support;
using Xunit;

namespace HexTactics.Editor.Tests;

/// <summary>«Оборона» from the data to the battle (spec part 7 §3.3, §5; acceptance M7-05, M7-26…M7-28, M7-35).</summary>
public class DefenseScenarioTests
{
    /// <summary>8×6 ground with an enemy base 1 at (4,0), a deploy hex (0,5) and no player base at all.</summary>
    private static ContentItem Yard(ContentLibrary library)
    {
        var item = library.Create(ContentTypeRegistry.Map, "Двор");
        var map = (MapData)item.Data;
        map.Columns = 8;
        map.Rows = 6;
        map.Tiles.Clear();
        for (var c = 0; c < 8; c++)
        {
            for (var r = 0; r < 6; r++)
            {
                map.Tiles.Add(new MapTileData { Col = c, Row = r, Tile = (c, r) switch { (4, 0) => "core.tile.enemy_base", (0, 5) => "core.tile.deploy", _ => "core.tile.ground" } });
            }
        }

        Assert.True(library.Save(item).Ok);
        return library.Find(item.Ref)!;
    }

    private static ContentItem Siege(ContentLibrary library, bool critical, double survive)
    {
        var item = library.Create(ContentTypeRegistry.Scenario, "Генератор");
        var s = (ScenarioData)item.Data;
        s.Mode = ScenarioData.Defense;
        s.Map = Yard(library).Id;
        s.SurviveSeconds = survive;
        s.SquadLossDefeat = false;
        s.Objects.Add(new MissionObjectData { Id = "gen", Name = "Генератор", Col = 4, Row = 4, Health = 60, Critical = critical });
        var wave = new WaveData { Id = "wave1", Name = "Натиск", Start = WaveData.AtTime };
        wave.Groups.Add(new SpawnGroupData { Id = "group1", Route = string.Empty, Source = "base:1", Unit = RuntimeFactory.DefaultUnitId, Count = 2, Interval = 1, Ai = new AiData { Preset = "attack_objective" } });
        s.Waves.Add(wave);
        return item;
    }

    private static GameSimulation Play(ContentLibrary library, ContentItem scenario, int seconds)
    {
        var sim = MatchFactory.Start(RuntimeFactory.BuildScenario(library, scenario));
        sim.SetPaused(false);
        for (var i = 0; i < seconds * 30 && !sim.IsScenarioOver; i++)
        {
            sim.Advance(1.0 / 30);
        }

        return sim;
    }

    [Fact]
    public void The_defense_data_round_trips_and_its_goals_are_told_in_words()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var item = Siege(library, critical: true, survive: 60);
        ((ScenarioData)item.Data).ObjectsPolicy = "any";
        ((ScenarioData)item.Data).Enemies.Add(new PlacedUnitData { Unit = RuntimeFactory.DefaultUnitId, Col = 6, Row = 2, Ai = new AiData { Preset = "guard", GuardRadius = 2 } });
        Assert.True(library.Save(item).Ok);
        var back = (ScenarioData)ContentTestKit.Library(dir.Path).Find(item.Ref)!.Data;
        Assert.Equal(((ScenarioData)item.Data).ToJson().ToJsonString(), back.ToJson().ToJsonString());
        Assert.Equal(("base:1", "attack_objective", "guard", 2), (back.Waves[0].Groups[0].Source, back.Waves[0].Groups[0].Ai.Preset, back.Enemies[0].Ai.Preset, back.Enemies[0].Ai.GuardRadius));
        Assert.Equal("Победа: продержаться 60 с боя. Поражение: уничтожены все критические объекты («Генератор»).", back.GoalsText());
        Assert.Equal("Оборона · волн 1 · врагов 2 · объектов 1 · отряд 3", back.Summary());
    }

    [Fact]
    public void M7_35_a_defense_without_an_end_or_with_a_missing_source_or_a_misplaced_object_cannot_start()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var item = Siege(library, critical: true, survive: 0);
        Assert.Empty(RuntimeFactory.CheckScenario(library, item)); // the waves are finite: repelling them is the end

        var s = (ScenarioData)item.Data;
        s.Waves.Clear();
        Assert.Contains(library.Validate(item), i => i.IsError && i.Message.Contains("оборона без окончания", StringComparison.Ordinal));

        s.SurviveSeconds = 30;
        var wave = new WaveData { Id = "wave1", Name = "Натиск" };
        wave.Groups.Add(new SpawnGroupData { Id = "group1", Source = "base:7", Unit = RuntimeFactory.DefaultUnitId, Count = 1 });
        s.Waves.Add(wave);
        s.Objects[0].Col = 40;
        var errors = RuntimeFactory.CheckScenario(library, item).Select(i => i.Message).ToList();
        Assert.Contains(errors, m => m.Contains("источника «base:7» нет на карте «Двор»", StringComparison.Ordinal));
        Assert.Contains(errors, m => m.Contains("объект «Генератор» на 40,4 — там нет проходимого гекса", StringComparison.Ordinal));
    }

    [Fact]
    public void M7_26_M7_28_the_enemies_go_for_the_object_and_only_a_critical_object_loses_the_battle_without_any_player_base()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var lost = Play(library, Siege(library, critical: true, survive: 40), 40);
        Assert.Equal((ScenarioPhase.Defeat, ScenarioEndReason.ObjectLost), (lost.ScenarioPhase, lost.ScenarioEndReason!.Value));

        var held = Play(library, Siege(library, critical: false, survive: 40), 41);
        Assert.Equal((ScenarioPhase.Victory, ScenarioEndReason.Survived), (held.ScenarioPhase, held.ScenarioEndReason!.Value));
        Assert.DoesNotContain(held.State.Units, u => u.Role == UnitRole.Objective); // it fell, but it was not critical
    }

    [Fact]
    public void M7_60_a_defense_is_made_in_the_editor_with_objects_sources_ai_and_a_guard_without_typing_data()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var model = new ScenarioEditorModel(library, library.Create(ContentTypeRegistry.Scenario, "Оборона двора"));
        Assert.Null(model.SetMap(Yard(library).Id));
        Assert.Null(model.SetMode(ScenarioData.Defense));
        Assert.Contains(model.Validate(), i => i.IsError && i.Message.Contains("оборона без окончания", StringComparison.Ordinal));

        model.PlaceObjects = true;
        Assert.Null(model.Click(HexTactics.Core.Hex.HexCoord.FromOffset(4, 4)));
        Assert.Null(model.SetObjectName("Генератор"));
        Assert.Null(model.SetObjectHealth("80"));
        Assert.NotNull(model.SetObjectHealth("0"));
        Assert.Equal(("Генератор", 80.0, true), (model.Scenario.Objects[0].Name, model.Scenario.Objects[0].Health, model.Scenario.Objects[0].Critical));

        model.PlaceObjects = false;
        Assert.Null(model.Click(HexTactics.Core.Hex.HexCoord.FromOffset(6, 2)));
        Assert.Contains("стоит объект миссии", model.Click(HexTactics.Core.Hex.HexCoord.FromOffset(4, 4)), StringComparison.Ordinal);
        Assert.Null(model.SetEnemyAi("guard"));
        Assert.Null(model.SetAiNumber(null, null, "guardRadius", "2"));
        Assert.Equal(("guard", 2), (model.Scenario.Enemies[0].Ai.Preset, model.Scenario.Enemies[0].Ai.GuardRadius));

        Assert.Equal(new[] { ("base:1", "База врага 1") }, model.Sources());
        var wave = model.AddWave();
        var group = model.Scenario.Waves[0].Groups[0];
        Assert.Equal(("", "base:1", "attack_objective"), (group.Route, group.Source, group.Ai.Preset)); // a defense group: source + AI
        Assert.Null(model.SetGroupAi(wave, group.Id, "units_then_objective"));
        Assert.NotNull(model.SetGroupSource(wave, group.Id, "zone:9"));
        Assert.Null(model.SetSurvive("45"));
        var preview = string.Join(" | ", model.Preview());
        Assert.Contains("из «База врага 1» (сначала юниты, потом объект)", preview, StringComparison.Ordinal);
        Assert.Contains($"«База врага 1» — {group.Count}.", preview, StringComparison.Ordinal); // the per-source total names the source
        Assert.DoesNotContain("нет на карте", preview, StringComparison.Ordinal);
        Assert.DoesNotContain(model.Validate(), i => i.IsError);
        Assert.True(model.Save().Ok);

        var sim = MatchFactory.Start(RuntimeFactory.BuildScenario(library, library.Find(model.Item.Ref)!));
        Assert.Single(sim.Objects);
        Assert.Equal(AiPreset.Guard, sim.State.Units.Single(u => u.Side == Side.Enemy).Ai!.Profile.Preset);
    }

    [Fact]
    public void M7_05_a_guard_placed_in_an_ordinary_battle_stays_on_its_post_while_the_player_is_outside_its_radius()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var item = library.Create(ContentTypeRegistry.Scenario, "Пост");
        var s = (ScenarioData)item.Data;
        s.Enemies.Add(new PlacedUnitData { Unit = RuntimeFactory.DefaultUnitId, Col = 9, Row = 1, Ai = new AiData { Preset = "guard", GuardRadius = 2 } });
        var sim = MatchFactory.Start(RuntimeFactory.BuildScenario(library, item));
        var guard = sim.State.Units.Single(u => u.Side == Side.Enemy);
        Assert.Equal(AiPreset.Guard, guard.Ai!.Profile.Preset);
        var mine = sim.PlaceFromRoster(1, HexTactics.Core.Hex.HexCoord.FromOffset(1, 5)).Unit!.Value;
        Assert.True(sim.SetFacing(mine, HexTactics.Core.Hex.HexDirection.North).Success);
        sim.SetPaused(false);
        for (var i = 0; i < 300; i++)
        {
            sim.Advance(1.0 / 30);
        }

        Assert.Equal(HexTactics.Core.Hex.HexCoord.FromOffset(9, 1), sim.State.GetUnit(guard.Id).Cell); // no chase across the map
    }
}
