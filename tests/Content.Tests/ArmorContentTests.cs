using System;
using System.IO;
using System.Linq;
using HexTactics.Content.Kinds;
using HexTactics.Content.Library;
using HexTactics.Content.Model;
using HexTactics.Content.Registry;
using HexTactics.Content.Runtime;
using HexTactics.Content.Transfer;
using HexTactics.Core.Combat;
using HexTactics.Tests.Support;
using Xunit;

namespace HexTactics.Content.Tests;

/// <summary>Defense A and combat rules as library data (spec part 6 §3, §9.5, §10.3; P6-14, P6-16).</summary>
public class ArmorContentTests
{
    [Fact]
    public void A_schema_4_unit_reaches_schema_5_with_zero_A_a_backup_and_the_same_numbers()
    {
        using var dir = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(dir.Path, "content", "units"));
        var v26 = "{\"schemaVersion\":4,\"id\":\"unit_v26\",\"kind\":\"unit\",\"name\":\"Боец v0.26\",\"revision\":2,\"data\":{\"maxHealth\":100,\"attackDamageType\":\"core.damage.slashing\"," +
                  "\"resistances\":{\"core.damage.physical\":50},\"resistanceProfiles\":[],\"defenseRules\":[],\"shields\":[]}}";
        File.WriteAllText(Path.Combine(dir.Path, "content", "units", "unit_v26.json"), v26);
        var library = ContentTestKit.Library(dir.Path);
        var item = library.Find(ContentTypeRegistry.Unit, "unit_v26")!;
        var data = (UnitData)item.Data;
        Assert.Empty(data.Armor);
        Assert.Equal((0.0, 0.0), (data.AttackArmorPenetration, data.AttackArmorPenetrationPercent));
        var stats = RuntimeFactory.BuildUnit(item, library).Stats;
        Assert.Empty(stats.Armor.Values);
        Assert.Equal(50, stats.Resistances[DamageTypes.Physical]);
        var text = File.ReadAllText(Path.Combine(dir.Path, "content", "units", "unit_v26.json"));
        Assert.Contains("\"schemaVersion\": 5", text, StringComparison.Ordinal);
        Assert.Contains("\"armor\": {}", text, StringComparison.Ordinal);
        Assert.Equal(v26, File.ReadAllText(Directory.GetFiles(Path.Combine(dir.Path, "backups"), "unit_v26.json", SearchOption.AllDirectories).Single()));

        // Loading again does not migrate twice (idempotent: no second backup).
        ContentTestKit.Library(dir.Path);
        Assert.Single(Directory.GetFiles(Path.Combine(dir.Path, "backups"), "unit_v26.json", SearchOption.AllDirectories));
    }

    [Fact]
    public void A_schema_1_profile_gets_an_empty_A_table()
    {
        using var dir = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(dir.Path, "content", "resistance_profiles"));
        var v23 = "{\"schemaVersion\":1,\"id\":\"profile_v23\",\"kind\":\"resistance_profile\",\"name\":\"Старый набор\",\"revision\":1,\"data\":{\"parent\":\"\",\"values\":{\"core.damage.blunt\":-25}}}";
        File.WriteAllText(Path.Combine(dir.Path, "content", "resistance_profiles", "profile_v23.json"), v23);
        var library = ContentTestKit.Library(dir.Path);
        var data = (ResistanceProfileData)library.Find(ContentTypeRegistry.ResistanceProfile, "profile_v23")!.Data;
        Assert.Equal(-25, data.Values[DamageTypes.Blunt]);
        Assert.Empty(data.Armor);
        Assert.Contains("\"schemaVersion\": 2", File.ReadAllText(Path.Combine(dir.Path, "content", "resistance_profiles", "profile_v23.json")), StringComparison.Ordinal);
    }

    [Fact]
    public void A_values_are_checked_points_round_trip_and_true_damage_keys_only_warn()
    {
        var library = ContentTestKit.Library(null);
        var item = library.Create(ContentTypeRegistry.Unit, "Латник");
        var unit = (UnitData)item.Data;
        unit.Armor[UnitData.GlobalResistance] = 10;
        unit.Armor[DamageTypes.Physical] = 100;
        unit.AttackArmorPenetration = 25;
        unit.AttackArmorPenetrationPercent = 50;
        Assert.DoesNotContain(library.Validate(item), i => i.IsError);
        var again = (UnitData)ContentSerializer.Read(ContentSerializer.Write(item), library.Registry, ContentOrigin.User).Item!.Data;
        Assert.Equal((10.0, 100.0, 25.0, 50.0), (again.Armor[UnitData.GlobalResistance], again.Armor[DamageTypes.Physical], again.AttackArmorPenetration, again.AttackArmorPenetrationPercent));
        Assert.Contains(new ContentRef(ContentTypeRegistry.DamageType, DamageTypes.Physical), item.References);

        unit.Armor[DamageTypes.True] = 50;
        Assert.Contains(library.Validate(item), i => !i.IsError && i.Path == "data.armor.core.damage.true"); // A does not act on true damage
        unit.Armor[DamageTypes.Slashing] = -1;
        unit.Armor[DamageTypes.Blunt] = double.NaN;
        unit.Armor[DamageTypes.Piercing] = 2_000_000;
        unit.AttackArmorPenetrationPercent = 150;
        var issues = library.Validate(item);
        Assert.Contains(issues, i => i.IsError && i.Path.EndsWith("armor.core.damage.slashing", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.IsError && i.Path.EndsWith("armor.core.damage.blunt", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.IsError && i.Path.EndsWith("armor.core.damage.piercing", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.IsError && i.Path.EndsWith("attackArmorPenetrationPercent", StringComparison.Ordinal));
    }

    [Fact]
    public void P6_14_profiles_and_own_values_layer_A_like_R_and_reach_the_runtime_once()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var profile = library.Create(ContentTypeRegistry.ResistanceProfile, "Латы");
        ((ResistanceProfileData)profile.Data).Armor[UnitData.GlobalResistance] = 10;
        ((ResistanceProfileData)profile.Data).Armor[DamageTypes.Magical] = 50;
        Assert.True(library.Save(profile).Ok);
        var item = library.Create(ContentTypeRegistry.Unit, "Маг в латах");
        var unit = (UnitData)item.Data;
        unit.ResistanceProfiles.Add(profile.Id);
        unit.Armor[DamageTypes.Magical] = 20; // own value replaces the set's per key
        unit.Armor["core.damage.fire_example"] = 30;
        Assert.True(library.Save(item).Ok);

        var armor = RuntimeFactory.BuildUnit(library.Find(item.Ref)!, library).Stats.Armor;
        Assert.Equal((10.0, 20.0, 30.0), (armor[ArmorSet.Global], armor[DamageTypes.Magical], armor["core.damage.fire_example"]));
        var fire = RuntimeFactory.BuildDamageTypes(library)["core.damage.fire_example"];
        Assert.Equal(60, ArmorMath.Raw(armor, Array.Empty<HexTactics.Core.Effects.StatusEffect>(), fire));
    }

    [Fact]
    public void Built_in_rulesets_old_maps_play_by_the_compatible_one_and_new_maps_by_the_standard_one()
    {
        var library = ContentTestKit.Library(null);
        Assert.NotNull(library.Find(ContentTypeRegistry.CombatRuleset, CombatRulesetData.Standard));
        Assert.NotNull(library.Find(ContentTypeRegistry.CombatRuleset, CombatRulesetData.Legacy));
        var prototype = (MapData)library.Find(ContentTypeRegistry.Map, RuntimeFactory.DefaultMapId)!.Data;
        Assert.Equal((string.Empty, CombatRulesetData.Legacy), (prototype.CombatRuleset, prototype.EffectiveRuleset));
        Assert.Equal(CombatRulesetData.Standard, ((MapData)library.Create(ContentTypeRegistry.Map, "Новая").Data).CombatRuleset);
        var game = RuntimeFactory.BuildGame(library, library.Find(ContentTypeRegistry.Map, RuntimeFactory.DefaultMapId)!);
        Assert.Equal((CombatRulesetData.Legacy, 100.0), (game.CombatRulesetId, game.CombatRules.DefenseK));
    }

    [Fact]
    public void A_map_plays_with_its_own_ruleset_and_a_missing_one_is_an_error_of_the_map()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var rules = library.Create(ContentTypeRegistry.CombatRuleset, "Тяжёлая броня");
        ((CombatRulesetData)rules.Data).DefenseK = 50;
        ((CombatRulesetData)rules.Data).DefenseKByFamily[DamageTypes.Magical] = 200;
        Assert.True(library.Save(rules).Ok);
        var map = library.Duplicate(library.Find(ContentTypeRegistry.Map, RuntimeFactory.DefaultMapId)!, "Арена").Item!;
        ((MapData)map.Data).CombatRuleset = rules.Id;
        Assert.True(library.Save(map).Ok);
        var game = RuntimeFactory.BuildGame(library, library.Find(map.Ref)!);
        Assert.Equal((50.0, 200.0), (game.CombatRules.DefenseK, game.CombatRules.DefenseKByFamily[DamageTypes.Magical]));
        Assert.Equal(rules.Id, ((MapData)ContentTestKit.Library(dir.Path).Find(map.Ref)!.Data).CombatRuleset); // after a restart

        Assert.False(library.Delete(ContentTypeRegistry.CombatRuleset, rules.Id).Result.Ok); // used by the map
        ((MapData)map.Data).CombatRuleset = "user.ruleset.nothing";
        Assert.Contains(library.Validate(map), i => i.IsError && i.Message.Contains("user.ruleset.nothing", StringComparison.Ordinal));
    }

    [Fact]
    public void A_ruleset_refuses_K_out_of_range_and_K_for_a_subtype_or_a_bypassing_family()
    {
        var library = ContentTestKit.Library(null);
        var item = library.Create(ContentTypeRegistry.CombatRuleset, "Правила");
        var data = (CombatRulesetData)item.Data;
        Assert.DoesNotContain(library.Validate(item), i => i.IsError);
        foreach (var bad in new[] { 0.0, -5, double.NaN, 2_000_000 })
        {
            data.DefenseK = bad;
            Assert.Contains(library.Validate(item), i => i.IsError && i.Path == "data.defenseK");
        }

        data.DefenseK = 100;
        data.DefenseKByFamily[DamageTypes.Slashing] = 50;
        data.DefenseKByFamily[DamageTypes.True] = 50;
        var issues = library.Validate(item);
        Assert.Contains(issues, i => i.IsError && i.Path == "data.defenseKByFamily.core.damage.slashing");
        Assert.Contains(issues, i => i.IsError && i.Path == "data.defenseKByFamily.core.damage.true");
        var again = (CombatRulesetData)ContentSerializer.Read(ContentSerializer.Write(item), library.Registry, ContentOrigin.User).Item!.Data;
        Assert.Equal(50, again.DefenseKByFamily[DamageTypes.Slashing]);
    }

    [Fact]
    public void Buffs_and_damage_effects_carry_A_fields_and_their_references()
    {
        var library = ContentTestKit.Library(null);
        var skill = library.Create(ContentTypeRegistry.Skill, "Ржавчина");
        var data = (SkillData)skill.Data;
        data.BaseEffects.Add(new EffectData("debuff", new System.Collections.Generic.Dictionary<string, object>
        {
            ["stat"] = HexTactics.Core.Effects.Stats.Armor, ["armorOf"] = DamageTypes.Physical, ["delta"] = 50.0, ["duration"] = 3.0, ["targets"] = "enemies",
        }));
        data.BaseEffects.Add(new EffectData("damage", new System.Collections.Generic.Dictionary<string, object>
        {
            ["amount"] = 20.0, ["damageType"] = DamageTypes.Blunt, ["armorPenetration"] = 30.0, ["armorPenetrationPercent"] = 120.0,
        }));
        var issues = library.Validate(skill);
        Assert.Contains(issues, i => i.IsError && i.Path.Contains("armorPenetrationPercent", StringComparison.Ordinal)); // 0…100 %
        Assert.Contains(new ContentRef(ContentTypeRegistry.DamageType, DamageTypes.Physical), skill.References);
        data.BaseEffects[1].Set("armorPenetrationPercent", 40.0);
        Assert.DoesNotContain(library.Validate(skill), i => i.IsError);
        var spec = data.BaseEffects[1].ToSpec(library.Registry.Effects);
        Assert.Equal((30.0, 40.0), (spec.Number("armorPenetration"), spec.Number("armorPenetrationPercent")));
    }

    [Fact]
    public void A_package_of_a_map_with_its_ruleset_and_an_armored_unit_restores_the_same_combat_on_a_clean_installation()
    {
        using var source = new TempDirectory();
        using var target = new TempDirectory();
        var library = ContentTestKit.Library(source.Path);
        var rules = library.Create(ContentTypeRegistry.CombatRuleset, "Правила арены");
        ((CombatRulesetData)rules.Data).DefenseK = 80;
        Assert.True(library.Save(rules).Ok);
        var profile = library.Create(ContentTypeRegistry.ResistanceProfile, "Латы");
        ((ResistanceProfileData)profile.Data).Armor[DamageTypes.Physical] = 40;
        Assert.True(library.Save(profile).Ok);
        var unit = library.Create(ContentTypeRegistry.Unit, "Латник");
        ((UnitData)unit.Data).ResistanceProfiles.Add(profile.Id);
        ((UnitData)unit.Data).Armor[UnitData.GlobalResistance] = 40;
        Assert.True(library.Save(unit).Ok);
        var map = library.Duplicate(library.Find(ContentTypeRegistry.Map, RuntimeFactory.DefaultMapId)!, "Арена").Item!;
        ((MapData)map.Data).CombatRuleset = rules.Id;
        Assert.True(library.Save(map).Ok);

        var bytes = ContentExporter.ExportPackage(library, new[] { library.Find(map.Ref)!, library.Find(unit.Ref)! }, "Арена", string.Empty, "0.27.0", DateTimeOffset.UnixEpoch);
        var clean = ContentTestKit.Library(target.Path);
        var preview = ContentImporter.Inspect(clean, "arena.hxp", bytes);
        Assert.False(preview.Rejected, preview.Summary);
        Assert.True(ContentImporter.Install(clean, preview).Ok);
        var imported = ContentTestKit.Library(target.Path);
        var game = RuntimeFactory.BuildGame(imported, imported.Find(map.Ref)!);
        Assert.Equal(80, game.CombatRules.DefenseK);
        var armor = RuntimeFactory.BuildUnit(imported.Find(unit.Ref)!, imported).Stats.Armor;
        Assert.Equal((40.0, 40.0), (armor[ArmorSet.Global], armor[DamageTypes.Physical]));
    }
}
