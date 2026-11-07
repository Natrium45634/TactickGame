using System.IO;
using System.Linq;
using HexTactics.Content.Kinds;
using HexTactics.Content.Registry;
using HexTactics.Content.Runtime;
using HexTactics.Core.Hex;
using HexTactics.Core.Roster;
using HexTactics.Core.Spawning;
using HexTactics.Editor.Units;
using HexTactics.Simulation;
using HexTactics.Tests.Support;
using Xunit;

namespace HexTactics.Editor.Tests;

/// <summary>The unit editor (spec part 4 §5; acceptance §12.2 p.10–11).</summary>
public class UnitEditorTests
{
    private static UnitEditorModel NewUnit(HexTactics.Content.Library.ContentLibrary library) =>
        new(library, library.Create(ContentTypeRegistry.Unit, "Тяжёлый"));

    private static HexTactics.Content.Schema.FieldSchema F(string key) => UnitData.Fields.Single(f => f.Key == key);

    [Fact]
    public void A_new_unit_starts_with_the_part_3_prototype_values()
    {
        var unit = NewUnit(ContentTestKit.Library(null)).Unit;
        Assert.Equal((100.0, 1.0, 3.0, 10.0, 1), (unit.MaxHealth, unit.MoveSpeed, unit.AttackRate, unit.AttackPower, unit.AttackRange));
    }

    [Fact]
    public void P10_a_saved_unit_with_175_HP_1_5_hex_per_second_2_attacks_per_second_plays_with_exactly_these_values()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var editor = NewUnit(library);
        editor.SetDescription("Медленный, но живучий");
        Assert.Null(editor.TrySetField(F("maxHealth"), "175"));
        Assert.Null(editor.TrySetField(F("moveSpeed"), "1,5"));
        Assert.Null(editor.TrySetField(F("attackRate"), "2"));
        Assert.Null(editor.TrySetField(F("attackPower"), "12.5"));
        Assert.Null(editor.AddSkill("core.skill.linear_strike"));
        Assert.True(editor.Save().Ok);

        var restarted = ContentTestKit.Library(dir.Path);
        var saved = restarted.Find(ContentTypeRegistry.Unit, editor.Item.Id)!;
        Assert.Equal("Медленный, но живучий", saved.Description);
        var game = RuntimeFactory.BuildGame(restarted, squad: new[] { saved.Id });
        Assert.Equal(saved.Id, game.Squad[0].Profile.DefinitionId);
        Assert.Equal("core.unit.operative", game.Squad[1].Profile.DefinitionId);

        var sim = new GameSimulation(game.Map, new SimulationSettings { DefaultStats = game.DefaultUnit.Stats, Skills = game.Skills, DefaultSkillSet = game.DefaultUnit.Skills },
            new RosterState(game.Squad.Select(s => (s.Name, (HexTactics.Core.Units.UnitProfile?)s.Profile))));
        var placed = sim.PlaceFromRoster(1, sim.State.Map.PlayerBase!.Cells[0]).Unit!.Value;
        var unit = sim.State.GetUnit(placed);
        Assert.Equal(175, unit.MaxHealth);
        Assert.Equal(175, unit.CurrentHealth);
        Assert.Equal(1.5, unit.SpeedHexesPerSecond);
        Assert.Equal(2, unit.Stats.AttackRatePerSecond);
        Assert.Equal(12.5, unit.Stats.AttackPower);
        Assert.Equal(saved.Id, unit.DefinitionId);
        Assert.Equal(new[] { "core.skill.linear_strike" }, unit.Skills);
    }

    [Theory]
    [InlineData("maxHealth", "-5", "не меньше")]
    [InlineData("maxHealth", "0", "не меньше")]
    [InlineData("moveSpeed", "-1", "не меньше")]
    [InlineData("moveSpeed", "0", "не меньше")]
    [InlineData("attackRate", "0", "не меньше")]
    [InlineData("attackPower", "-2", "не меньше")]
    [InlineData("moveSpeed", "быстро", "не число")]
    [InlineData("maxHealth", "12.5", "целое")]
    public void P11_invalid_values_are_refused_with_an_explanation_and_nothing_changes(string field, string input, string reason)
    {
        var editor = NewUnit(ContentTestKit.Library(null));
        var before = editor.Unit.ToJson().ToJsonString();
        var issue = editor.TrySetField(F(field), input);
        Assert.NotNull(issue);
        Assert.Contains(reason, issue!.Message);
        Assert.Contains("не изменено", issue.Message);
        Assert.Equal(before, editor.Unit.ToJson().ToJsonString());
        Assert.False(editor.History.CanUndo);
    }

    [Fact]
    public void P11_a_missing_skill_cannot_be_added_and_a_dangling_reference_blocks_saving()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var editor = NewUnit(library);
        Assert.Contains("не найден", editor.AddSkill("skill_nothere1"));
        editor.Unit.Skills.Add("skill_nothere1"); // e.g. a hand-edited file
        var result = editor.Save();
        Assert.False(result.Ok);
        Assert.Contains("skill_nothere1", result.Message);
        Assert.Empty(Directory.GetFiles(Path.Combine(dir.Path, "content"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Skills_are_references_added_from_the_catalog_removed_and_reordered_with_undo()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var copy = library.Duplicate(library.Find(ContentTypeRegistry.Skill, "core.skill.linear_strike")!, "Удар-2").Item!;
        var editor = NewUnit(library);
        Assert.Equal(2, editor.AvailableSkills().Count);
        Assert.Single(editor.AvailableSkills("удар-2"));
        Assert.Null(editor.AddSkill("core.skill.linear_strike"));
        Assert.Null(editor.AddSkill(copy.Id));
        Assert.Contains("уже есть", editor.AddSkill(copy.Id));
        Assert.Empty(editor.AvailableSkills());
        editor.MoveSkill(copy.Id, -1);
        Assert.Equal(new[] { copy.Id, "core.skill.linear_strike" }, editor.Unit.Skills);
        editor.RemoveSkill("core.skill.linear_strike");
        Assert.Equal(new[] { copy.Id }, editor.Unit.Skills);
        editor.Undo();
        editor.Undo();
        Assert.Equal(new[] { "core.skill.linear_strike", copy.Id }, editor.Unit.Skills);
        Assert.True(editor.Save().Ok);
        Assert.Equal(new[] { editor.Item.Id }, library.UsedBy(copy.Ref).Select(i => i.Id)); // reference, not a copy of the skill
    }

    [Fact]
    public void The_combat_preview_separates_attack_power_from_attack_rate()
    {
        var editor = NewUnit(ContentTestKit.Library(null));
        editor.TrySetField(F("attackPower"), "15");
        editor.TrySetField(F("attackRate"), "2");
        editor.AddSkill("core.skill.linear_strike");
        var preview = editor.CombatPreview();
        Assert.Contains("15 урона за удар (физический) × 2 удар/с ≈ 30 урона/с", preview[0]);
        Assert.Contains(preview, l => l.Contains("«Линейный удар»") && l.Contains("урон 20 врагам"));
    }

    [Fact]
    public void A_built_in_unit_is_saved_as_a_copy()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var editor = new UnitEditorModel(library, library.Find(ContentTypeRegistry.Unit, "core.unit.operative")!);
        editor.TrySetField(F("maxHealth"), "150");
        Assert.True(editor.Save().Ok);
        Assert.NotEqual("core.unit.operative", editor.Item.Id);
        Assert.Equal(100, ((UnitData)library.Find(ContentTypeRegistry.Unit, "core.unit.operative")!.Data).MaxHealth);
        Assert.False(editor.IsDirty);
    }

    [Fact]
    public void P36_a_tank_with_physical_plus_50_is_made_in_the_unit_editor_alone()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var editor = NewUnit(library);
        Assert.Null(editor.SetResistance("core.damage.physical", "50"));
        Assert.True(editor.Save().Ok);
        var saved = (UnitData)ContentTestKit.Library(dir.Path).Find(ContentTypeRegistry.Unit, editor.Item.Id)!.Data;
        Assert.Equal(50, saved.Resistances["core.damage.physical"]);
        Assert.Equal(50, editor.EffectiveResistance("core.damage.slashing"));
        Assert.Contains("Сильные стороны: ", editor.ResistanceSummary());
        Assert.Contains("режущий +50 %", editor.ResistanceSummary());
    }

    [Fact]
    public void P10_P12_resistance_input_is_checked_and_explained_by_the_formula()
    {
        var editor = NewUnit(ContentTestKit.Library(null));
        Assert.NotNull(editor.SetResistance("core.damage.piercing", "501"));
        Assert.NotNull(editor.SetResistance("core.damage.piercing", "-500,5"));
        Assert.NotNull(editor.SetResistance("core.damage.piercing", "много"));
        Assert.NotNull(editor.SetResistance("user_damage_none", "10"));
        Assert.Empty(editor.Unit.Resistances);
        Assert.Null(editor.SetResistance("core.damage.piercing", "-100"));
        Assert.Null(editor.SetResistance("global", "12,345"));
        Assert.Equal(12.35, editor.Unit.Resistances["global"]); // 0.01 of a point
        Assert.True(editor.Undo());
        Assert.False(editor.Unit.Resistances.ContainsKey("global"));
        Assert.Equal("−50 % = 150 % входящего урона", UnitEditorModel.Explain(-50));
        Assert.Equal("+75 % = 25 % входящего урона", UnitEditorModel.Explain(75));
        Assert.Equal("+300 % = 0 урона без дополнительных реакций (сверх 100 % не лечит)", UnitEditorModel.Explain(300));
        Assert.DoesNotContain("лечит", UnitEditorModel.Explain(100));
    }

    [Fact]
    public void The_resistance_table_lists_global_families_and_subtypes_and_hides_legacy_types()
    {
        var editor = NewUnit(ContentTestKit.Library(null));
        var rows = editor.ResistanceRows();
        Assert.Equal(new[] { "global", "core.damage.physical", "core.damage.blunt", "core.damage.piercing", "core.damage.slashing", "core.damage.magical", "core.damage.fire_example", "core.damage.true" }, rows.Select(r => r.Key));
        Assert.True(rows.Single(r => r.Key == "core.damage.true").Bypass);
        Assert.Equal(1, rows.Single(r => r.Key == "core.damage.piercing").Depth);
        Assert.Equal("Сопротивлений нет: весь урон проходит полностью.", editor.ResistanceSummary());
    }
}
