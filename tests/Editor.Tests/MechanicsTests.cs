using System.Linq;
using HexTactics.Content.Kinds;
using HexTactics.Content.Library;
using HexTactics.Content.Registry;
using HexTactics.Content.Runtime;
using HexTactics.Core.Combat;
using HexTactics.Editor.Defense;
using HexTactics.Editor.Mechanics;
using HexTactics.Editor.Units;
using HexTactics.Tests.Support;
using Xunit;

namespace HexTactics.Editor.Tests;

/// <summary>«Механики»: damage type constructor, resistance sets, the hit sandbox (spec part 5 §6.1–§6.5; acceptance §11.5).</summary>
public class MechanicsTests
{
    private const string FireRuleId = "core.rule.fire_convert_25_reflect_75";
    private const string Fire = "core.damage.fire_example";

    private static ContentItem Guard(ContentLibrary library, double fireR)
    {
        var unit = library.Create(ContentTypeRegistry.Unit, "Огненный страж");
        ((UnitData)unit.Data).Resistances[Fire] = fireR;
        ((UnitData)unit.Data).DefenseRules.Add(FireRuleId);
        return unit;
    }

    // ------------------------------------------------------------------ sandbox (§6.5)

    [Fact]
    public void P38_the_sandbox_shows_the_fire_case_stage_by_stage_and_compares_with_and_without_the_rule()
    {
        var library = ContentTestKit.Library(null);
        var before = library.Version;
        var rule = library.Find(ContentTypeRegistry.DefenseRule, FireRuleId)!;
        var report = HitSandbox.Run(library, new SandboxSetup { DamageTypeId = Fire, Amount = 40, HealthPercent = 60, EnemiesAround = 3, AlliesAround = 1 }, rule, Guard(library, 300));
        Assert.Empty(report.Problems);
        var with = report.With;
        Assert.Equal((60.0, 70.0), (with.TargetBefore, with.TargetAfter));
        Assert.Equal(3, with.Others.Count(o => o.Before - o.After == 30)); // attacker + two enemies on the ring
        Assert.Equal(100, with.Others.Single(o => o.Who == "союзник 1").After);
        var text = HitSandbox.Describe(report);
        Assert.Contains("1. До защиты: сила удара D0 = 40 (огонь (пример))", text, System.StringComparison.Ordinal);
        Assert.Contains("израсходовано 40: лечение +10", text, System.StringComparison.Ordinal);
        Assert.Contains("R после пробития = 300 %", text, System.StringComparison.Ordinal);
        Assert.Contains("«Огненная регенерация и круговое отражение (пример)» (цель): сработало", text, System.StringComparison.Ordinal);
        Assert.Equal((60.0, 60.0), (report.Without!.TargetBefore, report.Without.TargetAfter)); // R 300 % alone: 0 damage, no heal
        Assert.Contains("Без правила: HP цели 60 → 60, другим — 0", report.Comparison, System.StringComparison.Ordinal);
        Assert.Equal(before, library.Version); // nothing in the library changed
    }

    [Fact]
    public void The_sandbox_explains_why_a_rule_did_not_act()
    {
        var library = ContentTestKit.Library(null);
        var rule = library.Find(ContentTypeRegistry.DefenseRule, FireRuleId)!;
        var low = HitSandbox.Run(library, new SandboxSetup { DamageTypeId = Fire, Amount = 40, Penetration = 50 }, rule, Guard(library, 300));
        Assert.Contains(low.With.Reasons, r => r.Contains("R после пробития = 250 %, порог 300 % не достигнут", System.StringComparison.Ordinal));
        var physical = HitSandbox.Run(library, new SandboxSetup { DamageTypeId = DamageTypes.Slashing, Amount = 40 }, rule, Guard(library, 300));
        Assert.Contains(physical.With.Reasons, r => r.Contains("другой тип урона", System.StringComparison.Ordinal));
        var pure = library.Create(ContentTypeRegistry.DefenseRule, "Порог к чистому");
        ((DefenseRuleData)pure.Data).DamageType = DefenseRuleData.AnyType;
        ((DefenseRuleData)pure.Data).MinEffectiveResistance = 50;
        var t = HitSandbox.Run(library, new SandboxSetup { DamageTypeId = DamageTypes.True, Amount = 10 }, pure);
        Assert.Contains(t.With.Reasons, r => r.Contains("нет численного R", System.StringComparison.Ordinal));
        Assert.Contains("тип обходит численные R", HitSandbox.Describe(t), System.StringComparison.Ordinal);
    }

    [Fact]
    public void An_unsaved_damage_type_can_be_hit_in_the_sandbox()
    {
        var library = ContentTestKit.Library(null);
        var plasma = DamageTypeEditorModel.CreateFrom(library, DamageTypeEditorModel.BaseMagical, "Плазма");
        var target = library.Create(ContentTypeRegistry.Unit, "Маг");
        ((UnitData)target.Data).Resistances["core.damage.magical"] = 50;
        var report = HitSandbox.Run(library, new SandboxSetup { DamageTypeId = plasma.Id, Amount = 40 }, unitDraft: target, typeDraft: plasma);
        Assert.Empty(report.Problems);
        Assert.Equal(20, report.With.Result.HpLoss, 9); // the magical family resistance applies to the new subtype
    }

    // ------------------------------------------------------------------ damage type constructor (§6.2)

    [Fact]
    public void P35_a_new_type_from_each_base_has_an_explicit_family_and_strategy_and_appears_in_every_list()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var plasma = new DamageTypeEditorModel(library, DamageTypeEditorModel.CreateFrom(library, DamageTypeEditorModel.BaseMagical, "Плазма"));
        Assert.Equal(("core.damage.magical", DamageTypeData.ResistanceAffected), (plasma.Type.Family, plasma.Type.Strategy));
        Assert.NotNull(plasma.SetStrategy(DamageTypeData.TrueLike)); // a subtype keeps its family's strategy
        Assert.Null(plasma.SetTags("plasma, energy"));
        Assert.True(plasma.Save().Ok);
        Assert.StartsWith("damage_type_", plasma.Item.Id, System.StringComparison.Ordinal); // user namespace, never core.*

        var pure = new DamageTypeEditorModel(library, DamageTypeEditorModel.CreateFrom(library, DamageTypeEditorModel.BaseTrue, "Распад"));
        Assert.Equal(DamageTypeData.TrueLike, pure.Type.Strategy);
        var family = new DamageTypeEditorModel(library, DamageTypeEditorModel.CreateFrom(library, DamageTypeEditorModel.BaseFamily, "Психический"));
        Assert.Null(family.SetStrategy(DamageTypeData.ResistanceAffected));
        Assert.True(family.Save().Ok);

        // The new type is offered by the registries' lists: unit resistances, the rule filter, effect fields.
        var unit = new UnitEditorModel(library, library.Create(ContentTypeRegistry.Unit, "Тест"));
        Assert.Contains(unit.ResistanceRows(), r => r.Key == plasma.Item.Id && r.Depth == 1);
        Assert.Contains(unit.ResistanceRows(), r => r.Key == family.Item.Id && r.Depth == 0);
        Assert.Contains(plasma.Preview(), l => l.Contains("Подтип семейства «Магический»", System.StringComparison.Ordinal));
        Assert.Contains(plasma.Preview(), l => l.Contains("Пока нигде не используется", System.StringComparison.Ordinal));
        Assert.Contains(RuntimeFactory.BuildDamageTypes(library), t => t.Key == plasma.Item.Id && t.Value.FamilyId == "core.damage.magical");
    }

    [Fact]
    public void A_family_with_subtypes_stays_a_family_and_a_strategy_change_warns_about_users()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var family = new DamageTypeEditorModel(library, DamageTypeEditorModel.CreateFrom(library, DamageTypeEditorModel.BaseFamily, "Психический"));
        Assert.True(family.Save().Ok);
        var sub = new DamageTypeEditorModel(library, DamageTypeEditorModel.CreateFrom(library, DamageTypeEditorModel.BasePhysical, "Внушение"));
        Assert.Null(sub.SetFamily(family.Item.Id));
        Assert.True(sub.Save().Ok);
        Assert.NotNull(family.SetFamily("core.damage.magical"));
        Assert.NotNull(sub.SetFamily(sub.Item.Id));
        Assert.NotNull(sub.SetFamily("core.damage.piercing")); // a subtype is not a family

        // A family with subtypes keeps its strategy (the subtypes would silently change their calculation).
        var reopened = new DamageTypeEditorModel(library, library.Find(family.Item.Ref)!.Clone());
        Assert.Contains("подтип", reopened.SetStrategy(DamageTypeData.TrueLike), System.StringComparison.Ordinal);

        // A family used by a unit may change its strategy, with a warning about the users.
        var lone = new DamageTypeEditorModel(library, DamageTypeEditorModel.CreateFrom(library, DamageTypeEditorModel.BaseFamily, "Звук"));
        Assert.True(lone.Save().Ok);
        var unit = library.Create(ContentTypeRegistry.Unit, "Глушитель");
        ((UnitData)unit.Data).Resistances[lone.Item.Id] = 30;
        Assert.True(library.Save(unit).Ok);
        var changed = new DamageTypeEditorModel(library, library.Find(lone.Item.Ref)!.Clone());
        Assert.Null(changed.SetStrategy(DamageTypeData.TrueLike));
        Assert.Contains(changed.Preview(), l => l.StartsWith("⚠ Смена стратегии изменит расчёт у 1", System.StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ resistance sets (§6.4)

    [Fact]
    public void P37_sets_apply_by_reference_own_values_override_and_reset_and_the_battle_uses_the_layers()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var editor = new UnitEditorModel(library, library.Create(ContentTypeRegistry.Unit, "Танк"));
        Assert.Contains(editor.AvailableProfiles(), p => p.Id == "core.profile.armored" && p.Summary == "физический +50 %");
        Assert.Null(editor.AddProfile("core.profile.armored"));
        Assert.Null(editor.AddProfile("core.profile.weak_piercing"));
        Assert.NotNull(editor.AddProfile("core.profile.armored"));
        var physical = editor.ResistanceRows().Single(r => r.Key == DamageTypes.Physical);
        Assert.Equal((50.0, true, "из набора «Бронированный»"), (physical.Value, physical.Inherited, physical.Source));
        Assert.Equal(0, editor.EffectiveResistance(DamageTypes.Piercing)); // +50 family −50 subtype

        Assert.Null(editor.SetResistance(DamageTypes.Physical, "0")); // an explicit 0 over the set
        Assert.Equal((0.0, true), (editor.ResistanceRows().Single(r => r.Key == DamageTypes.Physical).Value, editor.ResistanceRows().Single(r => r.Key == DamageTypes.Physical).Own));
        Assert.Equal(0, RuntimeFactory.BuildUnit(editor.Item, library).Stats.Resistances[DamageTypes.Physical]);
        editor.ResetResistance(DamageTypes.Physical);
        Assert.Equal(50, editor.EffectiveResistance(DamageTypes.Slashing));
        Assert.Equal(50, RuntimeFactory.BuildUnit(editor.Item, library).Stats.Resistances[DamageTypes.Physical]);
        Assert.Equal(-50, RuntimeFactory.BuildUnit(editor.Item, library).Stats.Resistances[DamageTypes.Piercing]);

        var saved = editor.SaveAsProfile("Танк: защита");
        Assert.True(saved.Ok);
        Assert.Equal(2, ((ResistanceProfileData)saved.Item!.Data).Values.Count);
        Assert.Equal(0, editor.Item.Revision); // the unit itself stays an unsaved draft
        Assert.Equal(new[] { "core.profile.armored", "core.profile.weak_piercing" }, editor.Unit.ResistanceProfiles);
    }

    [Fact]
    public void A_set_extends_another_without_cycles_and_reports_its_users()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var heavy = new ResistanceProfileEditorModel(library, library.Create(ContentTypeRegistry.ResistanceProfile, "Тяжёлая броня"));
        Assert.Null(heavy.SetParent("core.profile.armored"));
        Assert.Null(heavy.SetValue(DamageTypes.Blunt, "-25"));
        Assert.Equal(50, heavy.Total(DamageTypes.Slashing));
        Assert.Equal(25, heavy.Total(DamageTypes.Blunt));
        Assert.True(heavy.Rows().Single(r => r.Key == DamageTypes.Physical).Inherited);
        Assert.NotNull(heavy.SetValue(DamageTypes.Blunt, "700"));
        Assert.True(heavy.Save().Ok);

        var top = new ResistanceProfileEditorModel(library, library.Create(ContentTypeRegistry.ResistanceProfile, "Сверхтяжёлая"));
        Assert.Null(top.SetParent(heavy.Item.Id));
        Assert.True(top.Save().Ok);
        var again = new ResistanceProfileEditorModel(library, library.Find(heavy.Item.Ref)!.Clone());
        Assert.DoesNotContain(again.Parents(), p => p.Value == top.Item.Id);
        Assert.NotNull(again.SetParent(top.Item.Id)); // heavy → top → heavy would be a cycle
        Assert.Contains(again.Users(), u => u.Id == top.Item.Id);

        // A hand-made cycle in files is still caught by the library.
        ((ResistanceProfileData)again.Item.Data).Parent = top.Item.Id;
        Assert.Contains(library.Validate(again.Item), i => i.IsError && i.Message.Contains("цикл", System.StringComparison.Ordinal));
        Assert.Equal(50, ResistanceLayers.Of(library, top.Item.Id)[DamageTypes.Physical]); // resolution stops safely
    }

    [Fact]
    public void Rule_templates_still_build_through_the_mechanics_group()
    {
        var library = ContentTestKit.Library(null);
        Assert.All(new[] { ContentTypeRegistry.DamageType, ContentTypeRegistry.DefenseRule, ContentTypeRegistry.ResistanceProfile },
            k => Assert.Equal(ContentTypeRegistry.MechanicsGroup, library.Registry.Find(k)!.Group));
        Assert.Equal(11, DefenseRuleTemplates.All.Count);
    }
}
