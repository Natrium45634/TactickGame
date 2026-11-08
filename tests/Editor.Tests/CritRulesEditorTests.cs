using System;
using System.Linq;
using HexTactics.Content.Kinds;
using HexTactics.Content.Registry;
using HexTactics.Core.Combat;
using HexTactics.Editor.Mechanics;
using HexTactics.Editor.Units;
using HexTactics.Tests.Support;
using Xunit;

namespace HexTactics.Editor.Tests;

/// <summary>«Правила боя» → direction and crits, and the sandbox with a side and a forced outcome (spec part 6 §9.5, §11).</summary>
public class CritRulesEditorTests
{
    [Fact]
    public void The_symmetric_preset_mirrors_flanks_values_are_checked_and_the_table_is_shown()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var rules = new CombatRulesetEditorModel(library, library.Create(ContentTypeRegistry.CombatRuleset, "Засада"));
        rules.SetSurpriseEnabled(true);
        Assert.Null(rules.SetSurpriseChance(1, "40", symmetric: true));
        Assert.Equal(new[] { 0.0, 40, 75, 100, 75, 40 }, rules.Ruleset.SurpriseChances);
        Assert.Null(rules.SetSurpriseChance(4, "60", symmetric: false));
        Assert.Equal(new[] { 0.0, 40, 75, 100, 60, 40 }, rules.Ruleset.SurpriseChances);
        Assert.NotNull(rules.SetSurpriseChance(3, "150", symmetric: true));
        Assert.NotNull(rules.SetValue("critMultiplier", "-2"));
        rules.SetCritEnabled(true);
        Assert.Null(rules.SetValue("superCritMultiplier", "3"));
        Assert.Contains(rules.Preview(), l => l.StartsWith("Внезапность: спереди 0 %, передний правый фланг 40 %", StringComparison.Ordinal));
        Assert.Contains("C 125 %: обычный 0 %, крит 75 %, сверхкрит 25 %", CombatRulesetEditorModel.CritTable());
        Assert.Contains(CombatRulesetEditorModel.CritTable(), l => l.StartsWith("C 250 %: обычный 0 %, крит 0 %, сверхкрит 100 % (избыток", StringComparison.Ordinal));
        Assert.True(rules.Save().Ok);
        Assert.True(rules.Undo()); // the last value back, saved state is kept on disk
    }

    [Fact]
    public void The_sandbox_shows_the_side_the_roll_and_a_forced_outcome_stage_by_stage()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var assassin = new UnitEditorModel(library, library.Create(ContentTypeRegistry.Unit, "Убийца"));
        Assert.Null(assassin.TrySetField(UnitData.Fields.Single(f => f.Key == "critChance"), "125"));
        Assert.True(assassin.Save().Ok);
        var setup = new SandboxSetup { DamageTypeId = DamageTypes.Blunt, Amount = 20, AttackerUnitId = assassin.Item.Id, Sector = 2, EnemiesAround = 1 };
        var forced = HitSandbox.Describe(HitSandbox.Run(library, setup with { ForceSurprise = true, ForceCrit = CritTier.SuperCrit }));
        Assert.Contains("Внезапность: задний правый фланг → 75 % → исход задан → да, ×1.5.", forced, StringComparison.Ordinal);
        Assert.Contains("Крит: шанс 125 п.п. (крит 75 %, сверхкрит 25 %) → исход задан → сверхкрит ×2.25.", forced, StringComparison.Ordinal);
        Assert.Contains("сила удара D0 = 67.5", forced, StringComparison.Ordinal);
        var rolled = HitSandbox.Run(library, setup);
        Assert.Equal(rolled.With.Result.Crit, HitSandbox.Run(library, setup).With.Result.Crit); // reproducible: the same key, the same roll
        Assert.NotEqual(CritTier.Normal, rolled.With.Result.Crit);                         // 125 %: a crit at least
        var legacy = HitSandbox.Describe(HitSandbox.Run(library, setup with { RulesetId = CombatRulesetData.Legacy }));
        Assert.DoesNotContain("Внезапность", legacy, StringComparison.Ordinal);
        Assert.Contains("сила удара D0 = 20", legacy, StringComparison.Ordinal);
    }
}
