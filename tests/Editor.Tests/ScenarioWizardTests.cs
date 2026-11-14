using System;
using System.Linq;
using HexTactics.Content.Kinds;
using HexTactics.Content.Library;
using HexTactics.Content.Registry;
using HexTactics.Content.Runtime;
using HexTactics.Core.Hex;
using HexTactics.Editor.Scenarios;
using HexTactics.Tests.Support;
using Xunit;

namespace HexTactics.Editor.Tests;

/// <summary>The wizard path of the scenario editor and «Перейти и исправить» (spec part 7 §9.3; acceptance M7-52, M7-59, M7-60).</summary>
public class ScenarioWizardTests
{
    private static HexCoord C(int col, int row) => HexCoord.FromOffset(col, row);

    private static ScenarioEditorModel New(ContentLibrary library, string mode)
    {
        var model = new ScenarioEditorModel(library, library.Create(ContentTypeRegistry.Scenario, "Мастер"));
        Assert.Null(model.SetMode(mode));
        return model;
    }

    [Theory]
    [InlineData(ScenarioData.Battle, "type map forces goals check")]
    [InlineData(ScenarioData.TowerDefense, "type map forces waves goals check")]
    [InlineData(ScenarioData.Defense, "type map forces waves goals check")]
    [InlineData(ScenarioData.Exploration, "type map forces goals check")]
    public void The_steps_a_mode_does_not_need_are_hidden(string mode, string steps)
    {
        using var dir = new TempDirectory();
        var model = New(ContentTestKit.Library(dir.Path), mode);
        Assert.Equal(steps, string.Join(" ", model.WizardSteps().Select(s => s.Id)));
        Assert.True(model.IsNew); // a new scenario opens on the first step
    }

    [Fact]
    public void Every_issue_belongs_to_a_step_and_the_step_shows_its_errors()
    {
        using var dir = new TempDirectory();
        var tower = New(ContentTestKit.Library(dir.Path), ScenarioData.TowerDefense);
        var steps = tower.WizardSteps().ToDictionary(s => s.Id, s => s.Done);
        Assert.False(steps["waves"]); // «data.waves»: no wave with enemies yet
        Assert.False(steps["check"]);
        Assert.True(steps["type"] && steps["forces"] && steps["goals"]);
        Assert.Equal(("waves", "forces", "goals", "map", "waves", "type"), (ScenarioEditorModel.StepOf("data.waves"), ScenarioEditorModel.StepOf("enemies.2"),
            ScenarioEditorModel.StepOf("data.timeLimit"), ScenarioEditorModel.StepOf("data.exit"), ScenarioEditorModel.StepOf("waves[0].groups[1].route"), ScenarioEditorModel.StepOf("data.mode")));

        var battle = New(ContentTestKit.Library(dir.Path), ScenarioData.Battle);
        Assert.False(battle.WizardSteps().Single(s => s.Id == "forces").Done); // no enemy yet
        Assert.Null(battle.Click(C(6, 2)));
        Assert.True(battle.WizardSteps().All(s => s.Done));
        Assert.True(battle.Save().Ok);
        Assert.False(battle.IsNew);
    }

    [Fact]
    public void M7_59_go_to_and_fix_selects_the_broken_enemy_object_wave_or_area()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var model = New(library, ScenarioData.Battle);
        Assert.Null(model.Click(C(6, 2)));
        Assert.Null(model.Click(C(8, 3)));
        model.Deselect();
        model.Scenario.Enemies[1].Unit = "unit_gone"; // as after a deleted unit
        var issue = model.Validate().First(i => i.IsError && i.Path.StartsWith("enemies", StringComparison.Ordinal) && i.Message.Contains("unit_gone", StringComparison.Ordinal));
        Assert.Equal("check", ScenarioEditorModel.StepOf("references")); // a reference error names no place: no jump
        Assert.Equal("forces", model.GoTo(issue.Path));
        Assert.Equal(1, model.Selected);

        var defense = New(library, ScenarioData.Defense);
        defense.PlaceObjects = true;
        Assert.Null(defense.Click(C(3, 6)));
        defense.DeselectObject();
        Assert.Equal("forces", defense.GoTo("objects[0].health"));
        Assert.Equal((0, "objects"), (defense.SelectedObject, defense.BoardBrush));
        var second = defense.AddWave();
        defense.AddWave();
        Assert.Equal("waves", defense.GoTo("data.waves[0].groups[0].unit"));
        Assert.Equal(second, defense.SelectedWave);

        var trip = New(library, ScenarioData.Exploration);
        Assert.Equal("map", trip.GoTo(trip.Validate().First(i => i.IsError && i.Path.EndsWith("exit", StringComparison.Ordinal)).Path));
        Assert.Equal("exit", trip.BoardBrush);
    }

    [Fact]
    public void M7_52_a_unit_created_in_the_library_is_at_once_usable_for_enemies_squad_waves_and_groups()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var model = New(library, ScenarioData.Defense);
        var wave = model.AddWave();
        var group = model.Scenario.Waves[0].Groups[0].Id;

        var brute = library.Create(ContentTypeRegistry.Unit, "Громила");
        ((UnitData)brute.Data).MaxHealth = 300;
        Assert.True(library.Save(brute).Ok); // made in «Юниты» while the scenario editor stays open

        Assert.Null(model.SetSelectedUnit(brute.Id)); // the brush of the board
        Assert.Null(model.Click(C(9, 1)));
        Assert.Null(model.AddToSquad(brute.Id));
        Assert.Null(model.SetGroupUnit(wave, group, brute.Id));
        Assert.Equal((brute.Id, brute.Id, brute.Id), (model.Scenario.Enemies[0].Unit, model.Scenario.Squad[^1], model.Scenario.Waves[0].Groups[0].Unit));
        Assert.Contains("Громила", string.Join(" | ", model.Preview()), StringComparison.Ordinal);
        Assert.True(model.Save().Ok);
        var built = RuntimeFactory.BuildScenario(library, library.Find(model.Item.Ref)!);
        Assert.Equal(300, built.Scenario!.Placed[0].Profile.Stats.MaxHealth);
    }
}
