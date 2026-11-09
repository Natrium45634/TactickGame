using System;
using System.Linq;
using HexTactics.Content.Kinds;
using HexTactics.Content.Library;
using HexTactics.Content.Registry;
using HexTactics.Content.Runtime;
using HexTactics.Core.Hex;
using HexTactics.Core.Orders;
using HexTactics.Core.Roster;
using HexTactics.Core.Scenarios;
using HexTactics.Core.Units;
using HexTactics.Editor.Scenarios;
using HexTactics.Simulation;
using HexTactics.Tests.Support;
using Xunit;

namespace HexTactics.Editor.Tests;

/// <summary>The scenario editor and the start of a scenario battle (spec part 7 §3, §9.3; acceptance M7-01…M7-04, M7-60).</summary>
public class ScenarioEditorTests
{
    private static HexCoord C(int col, int row) => HexCoord.FromOffset(col, row);

    /// <summary>Real time in small frames, as the client does (the clock limits a single long frame).</summary>
    private static void Play(GameSimulation sim, double seconds)
    {
        for (var t = 0.0; t < seconds; t += 1.0 / 30)
        {
            sim.Advance(1.0 / 30);
        }
    }

    private static ScenarioEditorModel NewScenario(ContentLibrary library, string name = "Засада") =>
        new(library, library.Create(ContentTypeRegistry.Scenario, name));

    [Fact]
    public void Clicks_place_select_turn_and_remove_enemies_and_every_step_is_undoable()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var model = NewScenario(library);
        Assert.Contains(model.Validate(), i => i.IsError && i.Message.Contains("хотя бы один враг", StringComparison.Ordinal));

        Assert.Null(model.Click(C(6, 2)));
        Assert.Equal((6, 2, RuntimeFactory.DefaultUnitId, HexDirection.South), (model.Scenario.Enemies[0].Col, model.Scenario.Enemies[0].Row, model.Scenario.Enemies[0].Unit, model.Scenario.Enemies[0].Facing));
        Assert.Equal(0, model.Selected);                                                    // a new enemy is selected at once
        Assert.Contains("нет проходимого гекса", model.Click(C(5, 2)), StringComparison.Ordinal); // an obstacle
        Assert.Contains("нет проходимого гекса", model.Click(C(20, 2)), StringComparison.Ordinal); // outside the map

        model.Turn(1);
        Assert.Equal(HexDirection.SouthWest, model.Scenario.Enemies[0].Facing);
        model.Deselect();
        model.Turn(-1); // with no selection the brush turns: the next enemies look south-east
        Assert.Null(model.Click(C(8, 3)));
        Assert.Equal(HexDirection.SouthEast, model.Scenario.Enemies[1].Facing);
        Assert.Null(model.Click(C(6, 2)));
        Assert.Equal(0, model.Selected); // a click on an enemy selects it instead of placing a second one
        Assert.Equal(2, model.Scenario.Enemies.Count);

        Assert.True(model.RemoveEnemy(C(8, 3)));
        Assert.Single(model.Scenario.Enemies);
        Assert.True(model.Undo());
        Assert.Equal(2, model.Scenario.Enemies.Count);
        Assert.DoesNotContain(model.Validate(), i => i.IsError);
        Assert.True(model.Save().Ok);
    }

    [Fact]
    public void Squad_mode_rules_and_time_refuse_bad_values_and_the_preview_says_the_goals_in_plain_words()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var model = NewScenario(library);
        Assert.Null(model.Click(C(6, 2)));
        Assert.Null(model.AddToSquad(RuntimeFactory.DefaultUnitId));
        Assert.Null(model.AddToSquad(RuntimeFactory.DefaultUnitId));
        Assert.Contains("не больше 5", model.AddToSquad(RuntimeFactory.DefaultUnitId), StringComparison.Ordinal);
        Assert.Contains("не найден", model.AddToSquad("unit_gone"), StringComparison.Ordinal);
        for (var i = 0; i < 4; i++)
        {
            Assert.Null(model.RemoveFromSquad(0));
        }

        Assert.Contains("хотя бы один", model.RemoveFromSquad(0), StringComparison.Ordinal);
        Assert.Contains("появится в v0.37.0", model.SetMode("defense"), StringComparison.Ordinal);
        Assert.Equal(ScenarioData.Battle, model.Scenario.Mode);
        Assert.NotNull(model.SetRuleset("ruleset_gone"));
        Assert.Null(model.SetRuleset(CombatRulesetData.Standard));
        Assert.NotNull(model.SetTimeLimit("долго"));
        Assert.NotNull(model.SetTimeLimit("-1"));
        Assert.Null(model.SetTimeLimit("120"));
        model.SetFlag("blocking", false);
        model.SetFlag("startPaused", false);

        var preview = string.Join(" | ", model.Preview());
        Assert.Contains("Враги на карте: 1 × «Оперативник»", preview, StringComparison.Ordinal);
        Assert.Contains("Отряд игрока (нижняя панель): 1. Оперативник", preview, StringComparison.Ordinal);
        Assert.Contains("Бой идёт сразу", preview, StringComparison.Ordinal);
        Assert.Contains("блок выключен", preview, StringComparison.Ordinal);
        Assert.Contains("Поражение: все ваши юниты погибли и в резерве никого или прошло 120 с боя.", preview, StringComparison.Ordinal);
    }

    [Fact]
    public void A_map_change_leaves_misplaced_enemies_as_errors_until_they_are_removed_in_one_step()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var small = library.Create(ContentTypeRegistry.Map, "Островок");
        var data = (MapData)small.Data;
        data.Tiles.Clear();
        data.Tiles.Add(new MapTileData { Col = 0, Row = 0, Tile = "core.tile.deploy" });
        data.Tiles.Add(new MapTileData { Col = 1, Row = 0 });
        Assert.True(library.Save(small).Ok);

        var model = NewScenario(library);
        Assert.Null(model.Click(C(1, 0)));
        Assert.Null(model.Click(C(6, 2)));
        Assert.Null(model.SetMap(small.Id));
        Assert.Contains(model.Validate(), i => i.IsError && i.Message.Contains("стоит на 6,2", StringComparison.Ordinal));
        Assert.Contains(model.Validate(), i => !i.IsError && i.Message.Contains("гексов выставления 1, а в отряде 3", StringComparison.Ordinal));
        Assert.Equal(1, model.RemoveMisplaced());
        Assert.DoesNotContain(model.Validate(), i => i.IsError);
        Assert.True(model.Undo());
        Assert.Equal(2, model.Scenario.Enemies.Count);
    }

    [Fact]
    public void M7_01_a_scenario_made_in_the_editor_starts_on_a_preparation_pause_with_its_enemies_and_the_squad_moves_with_orders()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var map = library.Find(ContentTypeRegistry.Map, RuntimeFactory.DefaultMapId)!.CopyAs(library.NewId(ContentTypeRegistry.Map), ContentOrigin.User, "Новая карта");
        map.Revision = 0;
        Assert.True(library.Save(map).Ok);
        var model = NewScenario(library, "Три врага");
        Assert.Null(model.SetMap(map.Id));
        foreach (var cell in new[] { C(6, 2), C(8, 3), C(9, 5) })
        {
            Assert.Null(model.Click(cell));
        }

        Assert.True(model.Save().Ok);
        var sim = MatchFactory.Start(RuntimeFactory.BuildScenario(library, library.Find(model.Item.Ref)!));
        Assert.True(sim.IsPaused);
        Assert.Equal((ScenarioPhase.Preparing, 3, 3), (sim.ScenarioPhase, sim.EnemiesLeft, sim.State.Roster.Entries.Count));

        // On the pause: deploy the first card on a deploy hex and give it its route and final facing at once.
        var unit = sim.PlaceFromRoster(1, C(1, 5)).Unit!.Value;
        Assert.True(sim.ApplyOrder(new IndividualOrder(unit, new[] { C(3, 5) }, HexDirection.NorthEast)).Success);
        Play(sim, 5);
        Assert.Equal(C(1, 5), sim.State.GetUnit(unit).Cell); // nothing moves before «Начать бой»
        Assert.Equal(ScenarioPhase.Preparing, sim.ScenarioPhase);

        sim.SetPaused(false);
        Play(sim, 4);
        Assert.Equal(ScenarioPhase.Running, sim.ScenarioPhase);
        Assert.Equal(C(3, 5), sim.State.GetUnit(unit).Cell);
        Assert.Equal(HexDirection.NorthEast, sim.State.GetUnit(unit).Facing);
    }

    [Fact]
    public void M7_02_M7_04_the_training_battle_ends_with_victory_only_after_the_last_enemy_and_with_defeat_when_no_card_is_left()
    {
        var library = ContentTestKit.Library(null);
        var training = library.Find(ContentTypeRegistry.Scenario, "core.scenario.training")!;

        var won = MatchFactory.Start(RuntimeFactory.BuildScenario(library, training));
        won.SetPaused(false);
        var enemies = won.State.Units.Where(u => u.Side == Side.Enemy).Select(u => u.Id).ToList();
        foreach (var e in enemies.Take(2))
        {
            won.State.ApplyDamage(e, 100000);
        }

        won.Step();
        Assert.Equal((ScenarioPhase.Running, 1), (won.ScenarioPhase, won.EnemiesLeft)); // not before the last one
        won.State.ApplyDamage(enemies[2], 100000);
        won.Step();
        Assert.Equal(ScenarioPhase.Victory, won.ScenarioPhase);
        Assert.True(won.IsPaused);

        var lost = MatchFactory.Start(RuntimeFactory.BuildScenario(library, training));
        lost.SetPaused(false);
        Play(lost, 3);
        Assert.Equal(ScenarioPhase.Running, lost.ScenarioPhase); // M7-03: nobody deployed yet, three cards wait
        foreach (var (entry, cell) in new[] { (1, C(1, 5)), (2, C(0, 6)) })
        {
            var u = lost.PlaceFromRoster(entry, cell).Unit!.Value;
            Assert.True(lost.SetFacing(u, HexDirection.North).Success);
        }

        Play(lost, 0.2);
        foreach (var p in lost.State.Units.Where(u => u.Side == Side.Player).ToList())
        {
            lost.State.ApplyDamage(p.Id, 100000);
        }

        Play(lost, 0.2);
        Assert.Equal(ScenarioPhase.Running, lost.ScenarioPhase); // the third card is still in the roster
        var last = lost.PlaceFromRoster(3, C(1, 5)).Unit!.Value;
        Play(lost, 0.2);
        lost.State.ApplyDamage(last, 100000);
        Play(lost, 0.2);
        Assert.Equal((ScenarioPhase.Defeat, ScenarioEndReason.SquadLost), (lost.ScenarioPhase, lost.ScenarioEndReason!.Value));
        Assert.DoesNotContain(lost.State.Roster.Entries, e => e.State is RosterEntryState.AvailableInRoster or RosterEntryState.PlacementPreview);
    }
}
