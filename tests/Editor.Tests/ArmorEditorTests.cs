using System;
using System.Linq;
using HexTactics.Content.Kinds;
using HexTactics.Content.Library;
using HexTactics.Content.Registry;
using HexTactics.Content.Runtime;
using HexTactics.Content.Schema;
using HexTactics.Core.Combat;
using HexTactics.Editor.Library;
using HexTactics.Editor.Maps;
using HexTactics.Editor.Mechanics;
using HexTactics.Editor.Units;
using HexTactics.Tests.Support;
using Xunit;

namespace HexTactics.Editor.Tests;

/// <summary>Defense A and combat rules in the developer mode (spec part 6 §3, §9.4, §9.5, §11).</summary>
public class ArmorEditorTests
{
    [Fact]
    public void A_is_entered_in_the_same_row_as_R_checked_and_layered_with_its_source()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var unit = new UnitEditorModel(library, library.Create(ContentTypeRegistry.Unit, "Латник"));
        Assert.Null(unit.SetArmor(DamageTypes.Physical, "100"));
        Assert.Null(unit.SetArmor(UnitData.GlobalResistance, "12,5"));
        Assert.Equal(112.5, unit.EffectiveArmor(DamageTypes.Slashing));
        Assert.Equal(12.5, unit.EffectiveArmor(DamageTypes.Magical));
        Assert.Equal(0, unit.EffectiveArmor(DamageTypes.True));
        var row = unit.ResistanceRows().Single(r => r.Key == DamageTypes.Physical);
        Assert.Equal((0.0, 100.0, true, "своё"), (row.Value, row.Armor, row.ArmorOwn, row.ArmorSource));

        Assert.NotNull(unit.SetArmor(DamageTypes.Physical, "-5"));        // points, never negative
        Assert.NotNull(unit.SetArmor(DamageTypes.Physical, "25 %"));      // not a percentage: R is for that
        Assert.NotNull(unit.SetArmor(DamageTypes.True, "50"));            // true damage bypasses A
        Assert.NotNull(unit.SetArmor(DamageTypes.Physical, "2000000"));
        Assert.Equal(100, unit.Unit.Armor[DamageTypes.Physical]);         // refused input changes nothing

        Assert.Contains("A физический 100", unit.Sections().Resistances, StringComparison.Ordinal);
        Assert.True(unit.Undo());
        Assert.Equal(100, unit.EffectiveArmor(DamageTypes.Slashing));

        // A set gives A; the unit's own value replaces it per key, «↺» brings the set's back.
        var profile = new ResistanceProfileEditorModel(library, library.Create(ContentTypeRegistry.ResistanceProfile, "Латы"));
        Assert.Null(profile.SetArmor(DamageTypes.Physical, "40"));
        Assert.Null(profile.SetArmor(DamageTypes.Magical, "20"));
        Assert.Equal(40, profile.ArmorTotal(DamageTypes.Piercing));
        Assert.Contains(profile.Preview(), l => l.StartsWith("Защита A: физический 40", StringComparison.Ordinal));
        Assert.True(profile.Save().Ok);
        Assert.Null(unit.AddProfile(profile.Item.Id));
        Assert.Equal(100, unit.EffectiveArmor(DamageTypes.Slashing)); // own 100 over the set's 40
        Assert.Equal(20, unit.EffectiveArmor(DamageTypes.Magical));
        unit.ResetArmor(DamageTypes.Physical);
        Assert.Equal(40, unit.EffectiveArmor(DamageTypes.Slashing));
        Assert.True(unit.ResistanceRows().Single(r => r.Key == DamageTypes.Physical).ArmorInherited);
    }

    [Fact]
    public void Copying_the_defense_and_saving_it_as_a_profile_take_R_and_A_together()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var source = new UnitEditorModel(library, library.Create(ContentTypeRegistry.Unit, "Образец"));
        Assert.Null(source.SetResistance(DamageTypes.Blunt, "-25"));
        Assert.Null(source.SetArmor(DamageTypes.Physical, "60"));
        Assert.True(source.Save().Ok);
        var copy = new UnitEditorModel(library, library.Create(ContentTypeRegistry.Unit, "Копия"));
        Assert.Null(copy.CopyResistancesFrom(source.Item.Id));
        Assert.Equal((-25.0, 60.0), (copy.EffectiveResistance(DamageTypes.Blunt), copy.EffectiveArmor(DamageTypes.Blunt)));
        var saved = source.SaveAsProfile("Защита образца");
        Assert.True(saved.Ok);
        Assert.Equal(60, ((ResistanceProfileData)saved.Item!.Data).Armor[DamageTypes.Physical]);
    }

    [Fact]
    public void The_combat_rules_editor_checks_K_explains_it_and_its_draft_drives_the_sandbox()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var rules = new CombatRulesetEditorModel(library, library.Create(ContentTypeRegistry.CombatRuleset, "Правила арены"));
        Assert.NotNull(rules.SetDefenseK("0"));
        Assert.NotNull(rules.SetDefenseK("abc"));
        Assert.Null(rules.SetDefenseK("50"));
        Assert.Contains(rules.Preview(), l => l.Contains("A 100 → ×0.333", StringComparison.Ordinal));
        Assert.NotNull(rules.SetFamilyK(DamageTypes.True, "100"));      // bypasses A
        Assert.NotNull(rules.SetFamilyK(DamageTypes.Slashing, "100"));  // a subtype
        Assert.Contains(rules.FamilyChoices(), f => f.Value == DamageTypes.Magical);
        Assert.Null(rules.SetFamilyK(DamageTypes.Magical, "200"));
        Assert.DoesNotContain(rules.FamilyChoices(), f => f.Value == DamageTypes.Magical);
        Assert.Equal((50.0, 200.0), (rules.Rules.DefenseK, rules.Rules.KFor(RuntimeFactory.BuildDamageTypes(library)["core.damage.fire_example"])));

        var unit = new UnitEditorModel(library, library.Create(ContentTypeRegistry.Unit, "Латник"));
        Assert.Null(unit.SetArmor(UnitData.GlobalResistance, "50"));
        Assert.True(unit.Save().Ok);
        var setup = new SandboxSetup { TargetUnitId = unit.Item.Id, DamageTypeId = DamageTypes.Slashing, Amount = 60, EnemiesAround = 1 };
        var hit = HitSandbox.Run(library, setup with { RulesetId = rules.Item.Id }, rulesetDraft: rules.Item); // unsaved K = 50
        Assert.Equal(30, hit.With.Result.HpLoss, 9);
        var text = HitSandbox.Describe(hit);
        Assert.Contains("Защита: A = 50, K = 50 → ×0.5 → 30.", text, StringComparison.Ordinal);
        var standard = HitSandbox.Run(library, setup); // the standard set: K = 100
        Assert.Equal(40, standard.With.Result.HpLoss, 9);

        rules.RemoveFamilyK(DamageTypes.Magical);
        Assert.Empty(rules.Ruleset.DefenseKByFamily);
        Assert.True(rules.Undo());
        Assert.Equal(200, rules.Ruleset.DefenseKByFamily[DamageTypes.Magical]);
        Assert.True(rules.Save().Ok);
    }

    [Fact]
    public void P6_18_the_sandbox_shows_both_penetrations_on_their_own_stages()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var unit = new UnitEditorModel(library, library.Create(ContentTypeRegistry.Unit, "Латник"));
        Assert.Null(unit.SetResistance(DamageTypes.Physical, "50"));
        Assert.Null(unit.SetArmor(DamageTypes.Physical, "100"));
        Assert.True(unit.Save().Ok);
        var text = HitSandbox.Describe(HitSandbox.Run(library, new SandboxSetup
        {
            TargetUnitId = unit.Item.Id, DamageTypeId = DamageTypes.Piercing, Amount = 100, Penetration = 30, ArmorPenetration = 20, ArmorPenetrationPercent = 50, EnemiesAround = 1,
        }));
        Assert.Contains("R после пробития = 20 % → 80", text, StringComparison.Ordinal);
        Assert.Contains("Защита: A = 100, после пробития A = 40, K = 100 → ×0.714 → 57.14.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Защита:", HitSandbox.Describe(HitSandbox.Run(library, new SandboxSetup { DamageTypeId = DamageTypes.Piercing, Amount = 100 })), StringComparison.Ordinal); // no A — no stage
    }

    [Fact]
    public void A_map_chooses_its_rules_and_a_new_element_is_offered_for_a_temporary_A()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var map = new MapEditorModel(library, library.Create(ContentTypeRegistry.Map, "Арена"));
        Assert.Equal(CombatRulesetData.Standard, map.Map.EffectiveRuleset);
        Assert.Contains(map.Rulesets(), r => r.Id == CombatRulesetData.Legacy);
        Assert.NotNull(map.SetRuleset("user.ruleset.nothing"));
        Assert.Null(map.SetRuleset(CombatRulesetData.Legacy));
        Assert.Equal(CombatRulesetData.Legacy, map.Map.CombatRuleset);

        // P6-01 for A: a new type is offered in the «Защита A против» field of buffs/debuffs at once.
        var fire = new DamageTypeEditorModel(library, DamageTypeEditorModel.CreateFrom(library, DamageTypeEditorModel.BaseMagical, "Огонь"));
        Assert.True(fire.Save().Ok);
        var armorOf = library.Registry.Effects.Find("debuff")!.FindField("armorOf")!;
        Assert.Contains(ReferenceCatalog.Options(library, armorOf.RefKind!, armorOf.RefFilter, null), o => o.Value == fire.Item.Id);
        Assert.DoesNotContain(ReferenceCatalog.Options(library, armorOf.RefKind!, armorOf.RefFilter, null), o => o.Value == DamageTypes.True);
        Assert.Contains(new UnitEditorModel(library, library.Create(ContentTypeRegistry.Unit, "Юнит")).ResistanceRows(), r => r.Key == fire.Item.Id);
    }
}
