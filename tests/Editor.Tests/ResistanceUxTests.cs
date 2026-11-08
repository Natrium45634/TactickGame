using System.Collections.Generic;
using System.Linq;
using HexTactics.Content.Kinds;
using HexTactics.Content.Library;
using HexTactics.Content.Registry;
using HexTactics.Core.Combat;
using HexTactics.Editor.Mechanics;
using HexTactics.Editor.Units;
using HexTactics.Tests.Support;
using Xunit;

namespace HexTactics.Editor.Tests;

/// <summary>The «Сопротивления» section without overload (spec part 5 §7.1–§7.5; acceptance §11.5).</summary>
public class ResistanceUxTests
{
    private static readonly IReadOnlySet<string> None = new HashSet<string>();

    private static UnitEditorModel NewUnit(ContentLibrary library, string name = "Танк") => new(library, library.Create(ContentTypeRegistry.Unit, name));

    private static string[] Keys(ResistanceView view) => view.Rows.Select(r => r.Row.Key).ToArray();

    [Fact]
    public void A_new_unit_shows_only_the_short_group_table_with_zeros()
    {
        var unit = NewUnit(ContentTestKit.Library(null));
        var view = unit.ResistanceView(string.Empty, false, None);
        Assert.Equal(new[] { "global", DamageTypes.Physical, DamageTypes.Magical, DamageTypes.True }, Keys(view));
        Assert.All(view.Rows, r => Assert.Equal(0, r.Row.Value));
        Assert.True(view.Rows.Single(r => r.Row.Key == DamageTypes.Physical).HasChildren);
        Assert.Equal(4, view.Hidden); // piercing, slashing, blunt, fire example
        Assert.Equal("Сопротивлений нет: весь урон проходит полностью.", unit.ResistanceSummary());
        Assert.Equal("всё 0 %", unit.Sections().Resistances);
    }

    [Fact]
    public void A_family_opens_its_subtypes_and_set_values_are_always_visible()
    {
        var unit = NewUnit(ContentTestKit.Library(null));
        var open = unit.ResistanceView(string.Empty, false, new HashSet<string> { DamageTypes.Physical });
        Assert.Equal(new[] { "global", DamageTypes.Physical, DamageTypes.Blunt, DamageTypes.Piercing, DamageTypes.Slashing, DamageTypes.Magical, DamageTypes.True }, Keys(open));

        Assert.Null(unit.SetResistance(DamageTypes.Piercing, "-50"));
        Assert.Contains(DamageTypes.Piercing, Keys(unit.ResistanceView(string.Empty, false, None))); // non-zero: shown without opening
        Assert.Equal(8, unit.ResistanceView(string.Empty, true, None).Rows.Count); // «Показать все»: every row except legacy types
    }

    [Fact]
    public void Custom_types_appear_only_through_search_or_show_all()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var psi = DamageTypeEditorModel.CreateFrom(library, DamageTypeEditorModel.BaseFamily, "Психический");
        Assert.True(library.Save(psi).Ok);
        var plasma = new DamageTypeEditorModel(library, DamageTypeEditorModel.CreateFrom(library, DamageTypeEditorModel.BaseMagical, "Плазма"));
        Assert.Null(plasma.SetTags("plasma"));
        Assert.True(plasma.Save().Ok);

        var unit = NewUnit(library);
        var view = unit.ResistanceView(string.Empty, false, None);
        Assert.DoesNotContain(psi.Id, Keys(view));
        Assert.DoesNotContain(plasma.Item.Id, Keys(view));
        Assert.Equal(6, view.Hidden);

        var byTag = unit.ResistanceView("plasma", false, None);
        Assert.Contains(plasma.Item.Id, Keys(byTag));
        Assert.True(byTag.Rows.Single(r => r.Row.Key == DamageTypes.Magical).Expanded); // the family opens around the match
        Assert.DoesNotContain(DamageTypes.Physical, Keys(byTag));
        Assert.Contains(psi.Id, Keys(unit.ResistanceView("псих", false, None)));
        Assert.Contains(psi.Id, Keys(unit.ResistanceView(string.Empty, true, None)));
        var byFamily = unit.ResistanceView("физ", false, None);
        Assert.Contains(DamageTypes.Piercing, Keys(byFamily)); // a family name finds its subtypes
    }

    [Fact]
    public void Sections_and_the_summary_tell_the_essentials_in_one_line_each()
    {
        var library = ContentTestKit.Library(null);
        var unit = NewUnit(library);
        Assert.Null(unit.SetResistance(DamageTypes.Physical, "70"));
        Assert.Null(unit.SetResistance(DamageTypes.Blunt, "-120"));
        Assert.Null(unit.AddRule("core.rule.fire_convert_25_reflect_75"));
        Assert.Null(unit.AddShield());
        var s = unit.Sections();
        Assert.Equal("100 HP · 1 гекс/с · 3 удар/с", s.Main);
        Assert.Equal("атака 10, физический · навыков: 0", s.Attack);
        Assert.StartsWith("физический +70 %, дробящий −120 %", s.Resistances, System.StringComparison.Ordinal);
        Assert.EndsWith("реакций: 2", s.Resistances, System.StringComparison.Ordinal);
        Assert.Contains("слабости: дробящий −50 %", unit.ResistanceSummary(), System.StringComparison.Ordinal);
        Assert.EndsWith("реакции: 1, щиты: 1.", unit.ResistanceSummary(), System.StringComparison.Ordinal);

        var cards = unit.DefenseCards();
        Assert.Equal("огонь (пример) · лечение 25 % + отражение 75 % · R ≥ 300 %", cards[0].Short);
        Assert.Equal(("Щит «Щит»", "20 HP · любой урон"), (cards[1].Title, cards[1].Short));
    }

    [Fact]
    public void P39_add_rule_offers_templates_and_saved_rules_and_a_copy_replaces_the_shared_rule_only_for_this_unit()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var unit = NewUnit(library, "Страж");
        var choices = unit.AddRuleChoices();
        Assert.Equal(11, choices.Count(c => c.Value.StartsWith(UnitEditorModel.TemplatePrefix, System.StringComparison.Ordinal)));
        Assert.Contains(choices, c => c.Value == "core.rule.fire_convert_25_reflect_75");
        Assert.Single(unit.AddRuleChoices("вампир"));

        // A template becomes a new rule for this unit (saved in the rule editor, then attached).
        var fromTemplate = unit.NewRuleFromTemplate("immunity")!;
        Assert.Equal("Страж: Иммунитет", fromTemplate.Name);
        Assert.Equal(0, fromTemplate.Revision);
        Assert.True(library.Save(fromTemplate).Ok);
        Assert.Null(unit.AddRule(fromTemplate.Id));

        // «Изменить копию»: the shared rule stays, this unit refers to its copy in the same place.
        Assert.Null(unit.AddRule("core.rule.fire_convert_25_reflect_75"));
        var copy = unit.CopyOfRule("core.rule.fire_convert_25_reflect_75")!;
        Assert.NotEqual("core.rule.fire_convert_25_reflect_75", copy.Id);
        Assert.Equal("Огненная регенерация и круговое отражение (пример) (Страж)", copy.Name);
        ((DefenseRuleData)copy.Data).MinEffectiveResistance = 200;
        Assert.True(library.Save(copy).Ok);
        unit.ReplaceRule("core.rule.fire_convert_25_reflect_75", copy.Id);
        Assert.Equal(new[] { fromTemplate.Id, copy.Id }, unit.Unit.DefenseRules);
        Assert.Equal(300, ((DefenseRuleData)library.Find(ContentTypeRegistry.DefenseRule, "core.rule.fire_convert_25_reflect_75")!.Data).MinEffectiveResistance);
        Assert.True(unit.Undo());
        Assert.Contains("core.rule.fire_convert_25_reflect_75", unit.Unit.DefenseRules);
    }

    [Fact]
    public void Protection_is_copied_from_another_unit_in_one_undo_step()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var source = NewUnit(library, "Образец");
        Assert.Null(source.AddProfile("core.profile.armored"));
        Assert.Null(source.SetResistance(DamageTypes.Blunt, "-25"));
        Assert.True(source.Save().Ok);

        var unit = NewUnit(library, "Копия");
        Assert.Null(unit.SetResistance(DamageTypes.Magical, "40"));
        Assert.Null(unit.CopyResistancesFrom(source.Item.Id));
        Assert.Equal(new[] { "core.profile.armored" }, unit.Unit.ResistanceProfiles);
        Assert.Equal(25, unit.EffectiveResistance(DamageTypes.Blunt));
        Assert.Equal(0, unit.EffectiveResistance(DamageTypes.Magical));
        Assert.True(unit.Undo());
        Assert.Equal(40, unit.EffectiveResistance(DamageTypes.Magical));
        Assert.NotNull(unit.CopyResistancesFrom("unit_nobody"));
    }
}
