using System.Collections.Generic;
using System.IO;
using System.Linq;
using HexTactics.Content.Kinds;
using HexTactics.Content.Library;
using HexTactics.Content.Registry;
using HexTactics.Content.Runtime;
using HexTactics.Core.Hex;
using HexTactics.Core.Orders;
using HexTactics.Core.Roster;
using HexTactics.Core.Skills;
using HexTactics.Core.Spawning;
using HexTactics.Core.Units;
using HexTactics.Editor.Skills;
using HexTactics.Editor.Units;
using HexTactics.Simulation;
using HexTactics.Tests.Support;
using Xunit;

namespace HexTactics.Editor.Tests;

/// <summary>The skill editor (spec part 4 §6; acceptance §12.2 p.12–15, 18–19).</summary>
public class SkillEditorTests
{
    private static readonly HexCoord Front = new(0, -1);
    private static readonly HexCoord Front3 = new(0, -3);

    private static HexCoord C(int col, int row) => HexCoord.FromOffset(col, row);

    /// <summary>Builds «Тройной рубящий удар» through the editor operations, as a user would.</summary>
    private static SkillEditorModel TripleSlash(ContentLibrary library)
    {
        var editor = new SkillEditorModel(library, library.Create(ContentTypeRegistry.Skill, "Тройной рубящий удар"));
        Assert.Null(editor.AddEffect(null, "damage"));
        Assert.Null(editor.SetEffectField(null, 0, "formula", "attack"));
        Assert.Null(editor.SetEffectField(null, 0, "multiplier", "1,5"));
        Assert.Null(editor.SetEffectField(null, 0, "hits", "3"));
        Assert.Null(editor.SetEffectField(null, 0, "interval", "0.2"));
        Assert.Equal(SkillCellClick.Added, editor.Click(Front));
        Assert.Equal(SkillCellClick.Added, editor.Click(Front3));
        Assert.Null(editor.SetEffectField(Front3, 0, "multiplier", "0.75")); // override in this cell only
        return editor;
    }

    [Fact]
    public void P12_P13_per_cell_percentages_of_attack_power_are_previewed_with_the_numbers()
    {
        var editor = TripleSlash(ContentTestKit.Library(null));
        var preview = editor.Preview();
        Assert.Contains("3 × 150 % силы атаки 10 = 15 = 45, каждые 0.2 с", preview[0]);
        Assert.Contains("3 × 75 % силы атаки 10 = 7.5 = 22.5", preview[1]);
        Assert.Contains("с изменениями", preview[1]);
        Assert.True(editor.IsOverridden(Front3, 0, "multiplier"));
        Assert.False(editor.IsOverridden(Front3, 0, "hits"));
        editor.PreviewAttackPower = 20;
        Assert.Contains("= 30 = 90", editor.Preview()[0]);
        Assert.DoesNotContain(editor.Validate(), i => i.IsError);
    }

    [Fact]
    public void P12_P13_the_saved_skill_deals_45_and_22_5_in_the_simulation()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var editor = TripleSlash(library);
        Assert.True(editor.Save().Ok);
        var runtime = RuntimeFactory.BuildSkill(library.Find(ContentTypeRegistry.Skill, editor.Item.Id)!, library.Registry.Effects);
        var sim = new GameSimulation(HexTactics.Tests.Shared.TestMaps.Empty(), new SimulationSettings { Skills = new Dictionary<string, SkillDefinition> { [runtime.Id] = runtime }, DefaultSkillSet = new[] { runtime.Id } });
        var caster = sim.Spawn(SpawnRequest.AtCell(C(5, 8), Side.Player, HexDirection.North)).Unit!.Value;
        var near = sim.Spawn(SpawnRequest.AtCell(C(5, 7), Side.Enemy, HexDirection.South)).Unit!.Value;
        var far = sim.Spawn(SpawnRequest.AtCell(C(5, 5), Side.Enemy, HexDirection.South)).Unit!.Value;
        sim.UseSkill(caster, runtime.Id, HexDirection.North);
        for (var i = 0; i < 30; i++)
        {
            sim.Step();
        }

        Assert.Equal(55, sim.State.GetUnit(near).CurrentHealth, 9);
        Assert.Equal(77.5, sim.State.GetUnit(far).CurrentHealth, 9);
    }

    [Fact]
    public void P14_turning_the_preview_unit_rotates_the_drawing_and_clicks_are_stored_relative_to_the_facing()
    {
        var editor = TripleSlash(ContentTestKit.Library(null));
        foreach (var d in HexDirections.All)
        {
            editor.Facing = d;
            Assert.Equal(HexCoord.DirectionOffset(d), editor.ToDrawn(Front));
            Assert.Equal(Front, editor.ToStored(HexCoord.DirectionOffset(d)));
        }

        editor.Facing = HexDirection.SouthEast;
        var drawnSide = new HexCoord(1, -2).RotateFor(HexDirection.SouthEast);
        Assert.Equal(SkillCellClick.Added, editor.Click(drawnSide));
        Assert.NotNull(editor.CellAt(new HexCoord(1, -2))); // stored for a caster looking North
        Assert.Equal(3, editor.Skill.Cells.Count);
    }

    [Fact]
    public void P15_a_cell_colour_and_a_fixed_damage_survive_a_restart_and_the_colour_never_changes_the_result()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var editor = TripleSlash(library);
        editor.SetCellOwnEffects(Front, true);
        Assert.Null(editor.SetEffectField(Front, 0, "formula", "fixed"));
        Assert.Null(editor.SetEffectField(Front, 0, "amount", "12"));
        editor.SetCellColor(Front, "#C04040", 0.5);
        Assert.True(editor.Save().Ok);

        var restarted = ContentTestKit.Library(dir.Path);
        var skill = (SkillData)restarted.Find(ContentTypeRegistry.Skill, editor.Item.Id)!.Data;
        var cell = skill.FindCell(0, -1)!;
        Assert.Equal(("#C04040", 0.5), (cell.Color, cell.Alpha));
        Assert.Equal(12.0, cell.Effects[0].Get("amount"));
        Assert.Equal(0.75, skill.FindCell(0, -3)!.Overrides[0]["multiplier"]);

        var colourless = (SkillData)skill.Clone();
        colourless.Cells[0].Color = null;
        colourless.Cells[0].Alpha = null;
        var a = RuntimeFactory.BuildSkill(restarted.Find(ContentTypeRegistry.Skill, editor.Item.Id)!, restarted.Registry.Effects);
        var b = RuntimeFactory.BuildSkill(new ContentItem(restarted.Registry.Find("skill")!, "skill_nocolour", colourless, ContentOrigin.User) { Name = "x" }, restarted.Registry.Effects);
        Assert.Equal(a.Cells.Select(c => (c.Offset, string.Join(";", c.Effects.Select(e => string.Join(",", e.Params.OrderBy(p => p.Key)))))), b.Cells.Select(c => (c.Offset, string.Join(";", c.Effects.Select(e => string.Join(",", e.Params.OrderBy(p => p.Key)))))));
    }

    [Fact]
    public void Invalid_effect_values_are_refused_and_an_override_equal_to_the_base_is_dropped()
    {
        var editor = TripleSlash(ContentTestKit.Library(null));
        Assert.Contains("не меньше", editor.SetEffectField(null, 0, "hits", "0")!.Message);
        Assert.Contains("не число", editor.SetEffectField(null, 0, "interval", "часто")!.Message);
        Assert.Contains("не меньше", editor.SetEffectField(Front3, 0, "multiplier", "-1")!.Message);
        Assert.Null(editor.SetEffectField(Front3, 0, "multiplier", "1.5")); // equal to the base value
        Assert.False(editor.IsOverridden(Front3, 0, "multiplier"));
        Assert.Contains("нельзя", editor.AddEffect(null, "teleport"));
    }

    [Fact]
    public void Erase_mode_and_remove_take_cells_out_and_undo_brings_them_back()
    {
        var editor = TripleSlash(ContentTestKit.Library(null));
        editor.EraseMode = true;
        Assert.Equal(SkillCellClick.Removed, editor.Click(Front3));
        Assert.Single(editor.Skill.Cells);
        editor.EraseMode = false;
        editor.RemoveCell(Front);
        Assert.Empty(editor.Skill.Cells);
        editor.Undo();
        editor.Undo();
        Assert.Equal(2, editor.Skill.Cells.Count);
        Assert.True(editor.IsOverridden(Front3, 0, "multiplier"));
    }

    [Fact]
    public void P19_the_part_3_linear_strike_is_ordinary_data_viewable_and_copyable_in_the_editor()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var editor = new SkillEditorModel(library, library.Find(ContentTypeRegistry.Skill, "core.skill.linear_strike")!);
        Assert.Equal(new[] { Front, new HexCoord(0, -2) }, editor.Skill.Cells.Select(c => c.Offset));
        Assert.All(editor.Preview(), l => Assert.Contains("урон 20 врагам", l));
        editor.SetName("Линейный удар+");
        Assert.True(editor.Save().Ok);
        Assert.NotEqual(SkillCatalog.LinearStrikeId, editor.Item.Id);
        var original = RuntimeFactory.BuildSkill(library.Find(ContentTypeRegistry.Skill, SkillCatalog.LinearStrikeId)!, library.Registry.Effects);
        Assert.Equal(SkillCatalog.LinearStrike().Area(C(5, 5), HexDirection.SouthWest), original.Area(C(5, 5), HexDirection.SouthWest));
    }

    [Fact]
    public void P18_a_custom_skill_of_a_custom_unit_planned_at_a_waypoint_strikes_with_the_units_attack_power_and_goes_on()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var skillEditor = TripleSlash(library);
        Assert.True(skillEditor.Save().Ok);
        var unitEditor = new UnitEditorModel(library, library.Create(ContentTypeRegistry.Unit, "Рубака"));
        unitEditor.TrySetField(UnitData.Fields.Single(f => f.Key == "attackPower"), "20");
        Assert.Null(unitEditor.AddSkill(skillEditor.Item.Id));
        Assert.True(unitEditor.Save().Ok);

        var game = RuntimeFactory.BuildGame(library, squad: new[] { unitEditor.Item.Id });
        var sim = new GameSimulation(game.Map, new SimulationSettings { DefaultStats = game.DefaultUnit.Stats, Skills = game.Skills, DefaultSkillSet = game.DefaultUnit.Skills },
            new RosterState(game.Squad.Select(s => (s.Name, (UnitProfile?)s.Profile))));
        var unit = sim.PlaceFromRoster(1, C(1, 5)).Unit!.Value;
        sim.SetFacing(unit, HexDirection.North);
        var enemy = sim.Spawn(SpawnRequest.AtCell(C(3, 4), Side.Enemy, HexDirection.South)).Unit!.Value;
        var order = new IndividualOrder(unit, new[] { C(3, 5), C(4, 5) }, HexDirection.North, null, new Dictionary<int, WaypointSkillSpec> { [0] = new(skillEditor.Item.Id, HexDirection.North) });
        Assert.True(sim.ApplyOrder(order).Success);
        for (var i = 0; i < 400; i++)
        {
            sim.Step();
        }

        Assert.Equal(10, sim.State.GetUnit(enemy).CurrentHealth, 9); // 3 × 150 % × 20 = 90
        Assert.Equal(C(4, 5), sim.State.GetUnit(unit).Cell);
    }
}
