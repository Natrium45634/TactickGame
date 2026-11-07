using System.Linq;
using HexTactics.Content.Kinds;
using HexTactics.Content.Library;
using HexTactics.Content.Registry;
using HexTactics.Editor.Defense;
using HexTactics.Editor.Units;
using HexTactics.Tests.Support;
using Xunit;

namespace HexTactics.Editor.Tests;

/// <summary>The defense rule constructor and the «Особые защиты и реакции» of the unit editor (spec part 5 §6.3, §6.4).</summary>
public class DefenseRuleEditorTests
{
    private const string FireRuleId = "core.rule.fire_convert_25_reflect_75";

    private static DefenseRuleEditorModel NewRule(ContentLibrary library) =>
        new(library, library.Create(ContentTypeRegistry.DefenseRule, "Новое правило"));

    private static HexTactics.Content.Schema.FieldSchema F(string key) => DefenseRuleData.Fields.Single(f => f.Key == key);

    [Fact]
    public void The_built_in_fire_rule_reads_as_the_spec_summary()
    {
        var library = ContentTestKit.Library(null);
        var rule = (DefenseRuleData)library.Find(ContentTypeRegistry.DefenseRule, FireRuleId)!.Data;
        Assert.Equal(
            "При попадании урона «Огонь (пример)» и сопротивлении ≥ 300 %: вылечить себя на 25 % силы удара; 75 % силы удара отразить каждому врагу на шести соседних гексах. Ответные удары не отражаются.",
            DefenseRuleSummary.Describe(rule, library));
    }

    [Fact]
    public void Every_template_builds_a_valid_rule_with_only_its_fields()
    {
        var library = ContentTestKit.Library(null);
        foreach (var template in DefenseRuleTemplates.All)
        {
            var editor = NewRule(library);
            Assert.Null(editor.ApplyTemplate(template.Id));
            Assert.Equal("Новое правило", editor.Item.Name);
            Assert.All(editor.Rule.Actions, a => Assert.Equal(editor.Rule.Trigger, library.Registry.DefenseActions.Find(a.Type)!.Trigger));
            Assert.DoesNotContain(editor.Validate(), i => i.IsError);
            Assert.DoesNotContain("✗", editor.Summary(), System.StringComparison.Ordinal);
        }

        var complex = NewRule(library);
        complex.ApplyTemplate("complex");
        Assert.Equal(1.0, complex.ConversionTotal(), 9);
        Assert.True(complex.Undo());
        Assert.Equal("negate", complex.Rule.Actions.Single().Type); // one undo step back to the default
    }

    [Fact]
    public void Actions_follow_the_trigger_and_bad_values_are_refused_unchanged()
    {
        var library = ContentTestKit.Library(null);
        var editor = NewRule(library);
        Assert.DoesNotContain(editor.AvailableActions, a => a.Effect == "retaliate");
        Assert.NotNull(editor.AddAction("retaliate"));
        Assert.Null(editor.TrySetField(F("trigger"), DefenseActionRegistry.OnHealthLoss));
        Assert.Contains(editor.Validate(), i => i.IsError && i.Path == "data.actions[0]"); // negate does not belong to «потеряно HP»
        Assert.Null(editor.AddAction("retaliate"));
        editor.RemoveAction(0);
        Assert.DoesNotContain(editor.Validate(), i => i.IsError);

        Assert.Null(editor.SetActionField(0, "fraction", "75%"));
        Assert.Equal(0.75, (double)editor.Rule.Actions[0].Get("fraction")!, 9);
        Assert.Contains("500 %", editor.SetActionField(0, "fraction", "600%")!.Message, System.StringComparison.Ordinal);
        Assert.NotNull(editor.SetActionField(0, "radiusHexes", "9"));
        Assert.NotNull(editor.SetActionField(0, "targeting", "everyone"));
        Assert.Equal(0.75, (double)editor.Rule.Actions[0].Get("fraction")!, 9);
        Assert.Null(editor.SetActionField(0, "targeting", DefenseActionRegistry.AreaAround));
        Assert.Contains("каждому врагу на шести соседних гексах", editor.Summary(), System.StringComparison.Ordinal);
        Assert.Null(editor.SetActionField(0, "targetDamageMode", "split_between_targets"));
        Assert.Contains("поровну между врагами", editor.Summary(), System.StringComparison.Ordinal);
    }

    [Fact]
    public void Conditions_appear_only_when_switched_on()
    {
        var library = ContentTestKit.Library(null);
        var editor = NewRule(library);
        var threshold = F("minimumEffectiveResistancePercent");
        Assert.False(threshold.VisibleWhen!(editor.Rule));
        Assert.Null(editor.TrySetField(F("useResistanceThreshold"), true));
        Assert.True(threshold.VisibleWhen!(editor.Rule));
        Assert.Null(editor.TrySetField(threshold, "250"));
        Assert.Contains("сопротивлении ≥ 250 %", editor.Summary(), System.StringComparison.Ordinal);
        Assert.NotNull(editor.TrySetField(threshold, "700"));
        Assert.Null(editor.TrySetField(F("useResistanceThreshold"), false));
        Assert.Null(editor.Rule.MinEffectiveResistance);
        Assert.Null(editor.TrySetField(F("damageTypeId"), "core.damage.physical"));
        Assert.StartsWith("При попадании урона «Физический» (и его подтипов): поглотить 25 % силы удара.", editor.Summary(), System.StringComparison.Ordinal);
    }

    [Fact]
    public void A_unit_attaches_rules_by_reference_and_edits_its_shields()
    {
        var library = ContentTestKit.Library(null);
        var unit = new UnitEditorModel(library, library.Create(ContentTypeRegistry.Unit, "Страж"));
        Assert.Contains(unit.AvailableRules("огн"), r => r.Id == FireRuleId);
        Assert.Null(unit.AddRule(FireRuleId));
        Assert.NotNull(unit.AddRule(FireRuleId));
        Assert.NotNull(unit.AddRule("user_rule_missing"));
        Assert.DoesNotContain(unit.AvailableRules(), r => r.Id == FireRuleId);
        Assert.Contains("отразить каждому врагу", unit.RuleSummaries().Single().Summary, System.StringComparison.Ordinal);

        Assert.Null(unit.AddShield());
        Assert.Null(unit.AddShield());
        Assert.Equal(new[] { "Щит", "Щит 2" }, unit.Unit.Shields.Select(s => s.Name));
        Assert.Null(unit.SetShield(0, "capacity", "35,5"));
        Assert.NotNull(unit.SetShield(0, "capacity", "-1"));
        Assert.NotNull(unit.SetShield(1, "name", "Щит"));
        Assert.NotNull(unit.SetShield(0, "damageTypeId", "user_damage_none"));
        Assert.Null(unit.SetShield(0, "damageTypeId", "core.damage.magical"));
        Assert.Equal((35.5, "core.damage.magical"), (unit.Unit.Shields[0].Capacity, unit.Unit.Shields[0].DamageType));
        unit.RemoveShield(1);
        unit.RemoveRule(FireRuleId);
        Assert.Empty(unit.Unit.DefenseRules);
        Assert.True(unit.Undo());
        Assert.Equal(new[] { FireRuleId }, unit.Unit.DefenseRules);
        Assert.DoesNotContain(unit.Validate(), i => i.IsError);
    }

    [Fact]
    public void Heal_block_and_resistance_debuffs_read_correctly_in_previews()
    {
        var library = ContentTestKit.Library(null);
        var block = library.Registry.Effects.Find("debuff")!.CreateDefault();
        block.Set("stat", "healingReceived");
        block.Set("percent", 1.0);
        Assert.Contains(library.Registry.Effects.Find("debuff")!.FindField("stat")!.Options, o => o.Value == "healingReceived");
        Assert.StartsWith("−100 % получаемого лечения", HexTactics.Editor.Skills.EffectPreview.Describe(block, 10, 100, library), System.StringComparison.Ordinal);

        var armour = library.Registry.Effects.Find("buff")!.CreateDefault();
        armour.Set("stat", "resistance");
        armour.Set("delta", 20.0);
        Assert.StartsWith("+20 п.п. сопротивления", HexTactics.Editor.Skills.EffectPreview.Describe(armour, 10, 100, library), System.StringComparison.Ordinal);
    }
}
