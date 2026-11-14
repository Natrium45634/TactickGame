using System;
using System.Linq;
using HexTactics.Content.Kinds;
using HexTactics.Content.Library;
using HexTactics.Content.Registry;
using HexTactics.Content.Runtime;
using HexTactics.Core.Hex;
using HexTactics.Core.Orders;
using HexTactics.Core.Scenarios;
using HexTactics.Core.Spawning;
using HexTactics.Core.Units;
using HexTactics.Editor.Scenarios;
using HexTactics.Simulation;
using HexTactics.Tests.Support;
using Xunit;

namespace HexTactics.Editor.Tests;

/// <summary>«Исследование» made in the scenario editor and played (spec part 7 §6.3; acceptance M7-31…M7-34, M7-60).</summary>
public class ExplorationEditorTests
{
    private static HexCoord C(int col, int row) => HexCoord.FromOffset(col, row);

    private static void Play(GameSimulation sim, double seconds)
    {
        for (var t = 0.0; t < seconds && !sim.IsScenarioOver; t += 1.0 / 30)
        {
            sim.Advance(1.0 / 30);
        }
    }

    private static ScenarioEditorModel Exploration(ContentLibrary library)
    {
        var model = new ScenarioEditorModel(library, library.Create(ContentTypeRegistry.Scenario, "Вылазка"));
        Assert.Null(model.SetMode(ScenarioData.Exploration));
        return model;
    }

    [Fact]
    public void The_entry_starts_on_the_deploy_hexes_and_the_brushes_paint_and_unpaint_the_areas()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var model = Exploration(library);
        Assert.Equal(new[] { "0,4", "1,4", "0,5", "1,5", "0,6" }, model.Scenario.Entry.Select(c => c.ToString())); // the map's deploy hexes
        Assert.Contains(model.Validate(), i => i.IsError && i.Message.Contains("нет выхода", StringComparison.Ordinal));
        Assert.Contains(model.ExplorationSteps(), s => s.Step.StartsWith("Выход", StringComparison.Ordinal) && !s.Done);

        Assert.Null(model.SetBrush("exit"));
        Assert.Null(model.Click(C(10, 1)));
        Assert.Null(model.Click(C(11, 1)));
        Assert.Null(model.Click(C(11, 1))); // a second click takes the hex back
        Assert.Contains("нет проходимого гекса", model.Click(C(5, 3)), StringComparison.Ordinal);
        Assert.Contains("уже вход", model.Click(C(0, 4)), StringComparison.Ordinal);
        Assert.Equal(new[] { "10,1" }, model.Scenario.Exit.Select(c => c.ToString()));

        Assert.Null(model.SetBrush("entry"));
        Assert.Null(model.Click(C(0, 4)));
        Assert.Contains("уже выход", model.Click(C(10, 1)), StringComparison.Ordinal);
        Assert.Equal(4, model.Scenario.Entry.Count);
        Assert.True(model.Undo());
        Assert.Equal(5, model.Scenario.Entry.Count);

        Assert.Null(model.SetBrush("enemies"));
        Assert.Contains("вход отряда", model.Click(C(1, 5)), StringComparison.Ordinal); // no enemy on the entry
        Assert.All(model.ExplorationSteps().Where(s => !s.Step.StartsWith("Враги", StringComparison.Ordinal)), s => Assert.True(s.Done));
        Assert.Contains(model.Preview(), l => l.Contains("путь от входа до выхода —", StringComparison.Ordinal));
        Assert.DoesNotContain(model.Validate(), i => i.IsError);
    }

    [Fact]
    public void Removing_a_squad_card_moves_the_key_and_the_required_cards_with_it()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var model = Exploration(library);
        Assert.Null(model.SetKeyUnit(3));
        Assert.NotNull(model.SetKeyUnit(4));
        Assert.Null(model.SetExitRule("all"));
        Assert.Equal(new[] { 1, 2, 3 }, model.Scenario.Required); // everyone first, then narrowed
        Assert.Null(model.SetRequired(2, false));
        Assert.Null(model.RemoveFromSquad(0));
        Assert.Equal(2, model.Scenario.KeyUnit);
        Assert.Equal(new[] { 2 }, model.Scenario.Required);
        Assert.True(model.Undo());
        Assert.Equal(3, model.Scenario.KeyUnit);
        Assert.Equal(new[] { 1, 3 }, model.Scenario.Required);
    }

    [Fact]
    public void M7_60_an_exploration_is_made_in_the_editor_and_won_by_the_required_members_after_the_marked_enemy()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var model = Exploration(library);
        Assert.Null(model.SetBrush("exit"));
        Assert.Null(model.Click(C(10, 1)));
        Assert.Null(model.Click(C(11, 1)));
        Assert.Null(model.SetExitRule("all"));
        Assert.Null(model.SetRequired(2, false));
        Assert.Null(model.SetExtraGoal("kill_marked"));
        Assert.Contains(model.Validate(), i => i.IsError && i.Message.Contains("без отмеченных", StringComparison.Ordinal));
        Assert.Null(model.SetBrush("enemies"));
        Assert.Null(model.Click(C(9, 8)));
        Assert.Null(model.SetEnemyTarget(true));
        Assert.Contains(model.Preview(), l => l.Contains("Отмеченные враги: 9,8", StringComparison.Ordinal));
        Assert.DoesNotContain(model.Validate(), i => i.IsError);
        Assert.True(model.Save().Ok);

        var sim = MatchFactory.Start(RuntimeFactory.BuildScenario(library, library.Find(model.Item.Ref)!));
        Assert.False(sim.PlaceFromRoster(1, C(3, 3)).Success); // only the entry
        var first = sim.PlaceFromRoster(1, C(1, 5)).Unit!.Value;
        var third = sim.PlaceFromRoster(3, C(0, 6)).Unit!.Value;
        Assert.True(sim.SetFacing(first, HexDirection.North).Success);
        Assert.True(sim.SetFacing(third, HexDirection.North).Success);
        Assert.True(sim.ApplyOrder(new IndividualOrder(first, new[] { C(10, 1) }, HexDirection.North)).Success);
        Assert.True(sim.ApplyOrder(new IndividualOrder(third, new[] { C(11, 1) }, HexDirection.North)).Success);
        sim.SetPaused(false);
        Play(sim, 40);
        Assert.Equal(ScenarioPhase.Running, sim.ScenarioPhase); // both at the exit, the marked enemy still stands
        Assert.Equal((true, 1), (sim.Exploration!.ExitDone, sim.Exploration.MarkedLeft));
        sim.State.ApplyDamage(sim.State.Units.Single(u => u.Side == Side.Enemy).Id, 100000);
        Play(sim, 0.2);
        Assert.Equal((ScenarioPhase.Victory, ScenarioEndReason.ExitReached), (sim.ScenarioPhase, sim.ScenarioEndReason!.Value));
    }
}
