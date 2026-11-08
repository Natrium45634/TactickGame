using System;
using System.IO;
using System.Linq;
using HexTactics.Content.Kinds;
using HexTactics.Content.Library;
using HexTactics.Content.Registry;
using HexTactics.Content.Runtime;
using HexTactics.Core.Combat;
using HexTactics.Tests.Support;
using Xunit;

namespace HexTactics.Content.Tests;

/// <summary>Surprise and crit settings as combat rules data, their migrations and unit data (spec part 6 §6, §7, §10.3; P6-45, P6-46).</summary>
public class CritRulesContentTests
{
    [Fact]
    public void Built_in_standard_rules_turn_surprise_and_crits_on_the_compatible_ones_keep_them_off()
    {
        var library = ContentTestKit.Library(null);
        var standard = RuntimeFactory.BuildCombatRules(library, CombatRulesetData.Standard);
        Assert.True(standard.SurpriseEnabled && standard.CritEnabled);
        Assert.Equal(new[] { 0.0, 25, 75, 100, 75, 25 }, standard.SurpriseChance);
        Assert.Equal((1.5, 1.5, 2.25), (standard.SurpriseMultiplier, standard.CritMultiplier, standard.SuperCritMultiplier));
        var legacy = RuntimeFactory.BuildCombatRules(library, CombatRulesetData.Legacy);
        Assert.False(legacy.SurpriseEnabled || legacy.CritEnabled);
        Assert.False(((CombatRulesetData)library.Create(ContentTypeRegistry.CombatRuleset, "Новые").Data).CritEnabled); // explicit opt-in
    }

    [Fact]
    public void P6_46_old_rules_and_maps_migrate_without_silent_bonuses()
    {
        using var dir = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(dir.Path, "content", "combat_rulesets"));
        Directory.CreateDirectory(Path.Combine(dir.Path, "content", "maps"));
        File.WriteAllText(Path.Combine(dir.Path, "content", "combat_rulesets", "rules_v27.json"),
            "{\"schemaVersion\":1,\"id\":\"rules_v27\",\"kind\":\"combat_ruleset\",\"name\":\"Старые\",\"revision\":1,\"data\":{\"defenseK\":50,\"defenseKByFamily\":{}}}");
        var library0 = ContentTestKit.Library(dir.Path);
        var map = library0.Duplicate(library0.Find(ContentTypeRegistry.Map, RuntimeFactory.DefaultMapId)!, "Арена").Item!;
        var mapFile = Path.Combine(dir.Path, "content", "maps", map.Id + ".json");
        var text = File.ReadAllText(mapFile).Replace("\"schemaVersion\": 2", "\"schemaVersion\": 1").Replace("\"layout\"", "\"combatRuleset\": \"core.ruleset.standard\",\n    \"layout\"");
        File.WriteAllText(mapFile, text); // a map saved by v0.27–v0.29 with the standard rules

        var library = ContentTestKit.Library(dir.Path);
        var old = (CombatRulesetData)library.Find(ContentTypeRegistry.CombatRuleset, "rules_v27")!.Data;
        Assert.Equal((50.0, false, false), (old.DefenseK, old.SurpriseEnabled, old.CritEnabled));
        Assert.Equal(CombatRulesetData.Legacy, ((MapData)library.Find(map.Ref)!.Data).CombatRuleset);
        Assert.False(RuntimeFactory.BuildGame(library, library.Find(map.Ref)!).CombatRules.CritEnabled);
    }

    [Fact]
    public void P6_45_edited_sides_and_multipliers_reach_the_game_after_a_reload()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var rules = library.Create(ContentTypeRegistry.CombatRuleset, "Засада");
        var data = (CombatRulesetData)rules.Data;
        (data.SurpriseEnabled, data.SurpriseChances, data.SurpriseMultiplier) = (true, new double[] { 10, 20, 30, 40, 50, 60 }, 2);
        (data.CritEnabled, data.BaseCritChance, data.CritMultiplier, data.SuperCritMultiplier) = (true, 5, 1.75, 3);
        Assert.True(library.Save(rules).Ok);
        var map = library.Duplicate(library.Find(ContentTypeRegistry.Map, RuntimeFactory.DefaultMapId)!, "Засада").Item!;
        ((MapData)map.Data).CombatRuleset = rules.Id;
        Assert.True(library.Save(map).Ok);

        var reloaded = ContentTestKit.Library(dir.Path);
        var game = RuntimeFactory.BuildGame(reloaded, reloaded.Find(map.Ref)!).CombatRules;
        Assert.Equal(new[] { 10.0, 20, 30, 40, 50, 60 }, game.SurpriseChance);
        Assert.Equal((2.0, 5.0, 1.75, 3.0), (game.SurpriseMultiplier, game.BaseCritChance, game.CritMultiplier, game.SuperCritMultiplier));

        data.SurpriseChances = new double[] { 10, 20, 30 };
        data.CritMultiplier = -1;
        var issues = library.Validate(rules);
        Assert.Contains(issues, i => i.IsError && i.Path == "data.surpriseChances");
        Assert.Contains(issues, i => i.IsError && i.Path == "data.critMultiplier");
    }

    [Fact]
    public void Critical_chance_of_units_and_passives_reaches_the_runtime_and_old_units_get_zero()
    {
        using var dir = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(dir.Path, "content", "units"));
        File.WriteAllText(Path.Combine(dir.Path, "content", "units", "unit_v29.json"),
            "{\"schemaVersion\":7,\"id\":\"unit_v29\",\"kind\":\"unit\",\"name\":\"Боец v0.29\",\"revision\":1,\"data\":{\"maxHealth\":100}}");
        var library = ContentTestKit.Library(dir.Path);
        Assert.Equal(0, ((UnitData)library.Find(ContentTypeRegistry.Unit, "unit_v29")!.Data).CritChance);

        var item = library.Create(ContentTypeRegistry.Unit, "Убийца");
        var unit = (UnitData)item.Data;
        unit.CritChance = 90;
        unit.Passives.Add(new PassiveData { Id = "p1", Name = "Глаз", Operation = PassiveOperations.CritChanceDelta, Value = 35 });
        unit.Passives.Add(new PassiveData { Id = "p2", Name = "Камень", Operation = PassiveOperations.CritImmunity });
        Assert.True(library.Save(item).Ok);
        var stats = RuntimeFactory.BuildUnit(library.Find(item.Ref)!, library).Stats;
        Assert.Equal((125.0, true, false), (stats.CritChance, stats.CritImmune, stats.SurpriseImmune));
    }
}
