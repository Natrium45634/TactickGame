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

/// <summary>Personal passives as unit data and the one modifier engine (spec part 6 §4; P6-26, P6-30, P6-31).</summary>
public class PassiveContentTests
{
    private static PassiveData P(string id, string op, double value, string stat = "attackPower", string type = "", string key = "global") =>
        new() { Id = id, Name = $"Пассив {id}", Operation = op, Value = value, Stat = stat, DamageType = type, Key = key };

    [Fact]
    public void Passives_round_trip_are_checked_and_an_unknown_operation_is_an_error_not_a_silent_effect()
    {
        var library = ContentTestKit.Library(null);
        var item = library.Create(ContentTypeRegistry.Unit, "Гидромант");
        var unit = (UnitData)item.Data;
        unit.Passives.Add(P("p1", PassiveOperations.OutgoingDamagePercent, 50, type: "core.damage.fire_example"));
        unit.Passives[0].HealthBelowPercent = 50;
        unit.Passives.Add(P("p2", PassiveOperations.GrantDefenseRule, 0));
        unit.Passives[1].Rule = "core.rule.fire_convert_25_reflect_75";
        Assert.DoesNotContain(library.Validate(item), i => i.IsError);
        Assert.Contains(new ContentRef(ContentTypeRegistry.DefenseRule, "core.rule.fire_convert_25_reflect_75"), item.References);
        Assert.Contains(new ContentRef(ContentTypeRegistry.DamageType, "core.damage.fire_example"), item.References);

        var again = (UnitData)ContentSerializer.Read(ContentSerializer.Write(item), library.Registry, ContentOrigin.User).Item!.Data;
        Assert.Equal((PassiveOperations.OutgoingDamagePercent, 50.0, 50.0, "core.damage.fire_example"), (again.Passives[0].Operation, again.Passives[0].Value, again.Passives[0].HealthBelowPercent, again.Passives[0].DamageType));
        Assert.Equal("core.rule.fire_convert_25_reflect_75", again.Passives[1].Rule);

        unit.Passives.Add(P("p3", "summon_dragon", 1));
        unit.Passives.Add(P("p1", PassiveOperations.StatAddPercent, 5000));
        var issues = library.Validate(item);
        Assert.Contains(issues, i => i.IsError && i.Message.Contains("summon_dragon", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.IsError && i.Path == "data.passives[3].value");
        Assert.Contains(issues, i => i.IsError && i.Message.Contains("два пассива с ID", StringComparison.Ordinal));
        Assert.Equal(new[] { "p1", "p2" }, PassiveEffects.Active(unit, library).Select(x => x.Passive.Id).Take(2)); // the unknown one never acts
    }

    [Fact]
    public void A_schema_5_unit_gets_no_passives_and_the_same_numbers()
    {
        using var dir = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(dir.Path, "content", "units"));
        var v27 = "{\"schemaVersion\":5,\"id\":\"unit_v27\",\"kind\":\"unit\",\"name\":\"Боец v0.27\",\"revision\":1,\"data\":{\"maxHealth\":100,\"attackPower\":12," +
                  "\"resistances\":{},\"armor\":{\"global\":50},\"resistanceProfiles\":[],\"defenseRules\":[],\"shields\":[]}}";
        File.WriteAllText(Path.Combine(dir.Path, "content", "units", "unit_v27.json"), v27);
        var library = ContentTestKit.Library(dir.Path);
        var item = library.Find(ContentTypeRegistry.Unit, "unit_v27")!;
        Assert.Empty(((UnitData)item.Data).Passives);
        var stats = RuntimeFactory.BuildUnit(item, library).Stats;
        Assert.Equal((12.0, 50.0), (stats.AttackPower, stats.Armor[ArmorSet.Global]));
        Assert.Empty(stats.DamageModifiers);
        Assert.Contains("\"passives\": []", File.ReadAllText(Path.Combine(dir.Path, "content", "units", "unit_v27.json")), StringComparison.Ordinal);
    }

    [Fact]
    public void P6_26_characteristics_change_by_percent_of_the_base_then_flat_and_each_source_is_listed()
    {
        var library = ContentTestKit.Library(null);
        var unit = new UnitData { AttackPower = 10, MaxHealth = 100 };
        unit.Passives.Add(P("p1", PassiveOperations.StatAddPercent, 10, "attackPower"));   // «Матёрый»: +10 % атаки
        unit.Passives.Add(P("p2", PassiveOperations.StatAddPercent, -25, "maxHealth"));    // «Раненый»: −25 % здоровья
        unit.Passives.Add(P("p3", PassiveOperations.StatAddFlat, 5, "attackPower"));
        var attack = PassiveEffects.Stat(unit, library, "attackPower");
        Assert.Equal((10.0, 16.0), (attack.Base, attack.Final)); // 10 × 1.1 + 5
        Assert.Equal(new[] { "+10 %", "+5" }, attack.Sources.Select(s => s.Change));
        Assert.Equal(75, PassiveEffects.Stat(unit, library, "maxHealth").Final);
        unit.Passives.Add(P("p4", PassiveOperations.StatAddPercent, -100, "moveSpeed"));
        Assert.Equal(HexTactics.Core.Effects.Stats.MinMoveSpeed, PassiveEffects.Stat(unit, library, "moveSpeed").Final); // the safe minimum
    }

    [Fact]
    public void R_and_A_passives_change_their_layer_once_and_granted_rules_join_the_unit()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var item = library.Create(ContentTypeRegistry.Unit, "Страж");
        var unit = (UnitData)item.Data;
        unit.Resistances[DamageTypes.Piercing] = 10;
        unit.Armor[DamageTypes.Magical] = 100;
        unit.Passives.Add(P("p1", PassiveOperations.ResistanceDeltaPP, 25, key: DamageTypes.Piercing));
        unit.Passives.Add(P("p2", PassiveOperations.DefenseDeltaPercent, 20, key: DamageTypes.Magical));
        unit.Passives.Add(P("p3", PassiveOperations.DefenseDeltaFlat, 30, key: DamageTypes.Magical));
        unit.Passives.Add(P("p4", PassiveOperations.GrantDefenseRule, 0));
        unit.Passives[3].Rule = "core.rule.fire_convert_25_reflect_75";
        unit.Passives.Add(P("p5", PassiveOperations.IncomingDamagePercent, -25, type: DamageTypes.Physical));
        Assert.True(library.Save(item).Ok);

        var stats = RuntimeFactory.BuildUnit(library.Find(item.Ref)!, library).Stats;
        Assert.Equal(35, stats.Resistances[DamageTypes.Piercing]);
        Assert.Equal(150, stats.Armor[DamageTypes.Magical]); // 100 × 1.2 + 30
        Assert.Equal("core.rule.fire_convert_25_reflect_75", stats.DefenseRules.Single().Id);
        var modifier = stats.DamageModifiers.Single();
        Assert.Equal((DamageModifierKind.Incoming, DamageTypes.Physical, -25.0, "incoming"), (modifier.Kind, modifier.DamageTypeId, modifier.Percent, modifier.Group));
        Assert.False(library.Delete(ContentTypeRegistry.DefenseRule, "core.rule.fire_convert_25_reflect_75").Result.Ok); // used, built-in anyway
    }

    [Fact]
    public void P6_30_P6_31_a_passive_belongs_to_one_unit_and_a_battle_never_writes_back_to_the_definition()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var strong = library.Create(ContentTypeRegistry.Unit, "Сильный");
        ((UnitData)strong.Data).Passives.Add(P("p1", PassiveOperations.StatAddPercent, 100, "attackPower"));
        Assert.True(library.Save(strong).Ok);
        var plain = library.Create(ContentTypeRegistry.Unit, "Обычный");
        Assert.True(library.Save(plain).Ok);
        Assert.Equal((20.0, 10.0), (RuntimeFactory.BuildUnit(library.Find(strong.Ref)!, library).Stats.AttackPower, RuntimeFactory.BuildUnit(library.Find(plain.Ref)!, library).Stats.AttackPower));

        var before = ContentSerializer.Write(library.Find(strong.Ref)!);
        var game = RuntimeFactory.BuildGame(library, library.Find(ContentTypeRegistry.Map, RuntimeFactory.DefaultMapId)!, squad: new[] { strong.Id });
        var sim = new HexTactics.Simulation.GameSimulation(game.Map, new HexTactics.Simulation.SimulationSettings { DamageTypes = game.DamageTypes });
        var a = sim.Spawn(HexTactics.Core.Spawning.SpawnRequest.AtCell(HexTactics.Core.Hex.HexCoord.FromOffset(3, 5), HexTactics.Core.Units.Side.Player, HexTactics.Core.Hex.HexDirection.North) with { Profile = game.Units[strong.Id].ToProfile() }).Unit!.Value;
        sim.State.ApplyDamage(a, 30);
        sim.Advance(2);
        Assert.Equal(before, ContentSerializer.Write(library.Find(strong.Ref)!)); // current HP, statuses, position: never in the library
        Assert.Equal(before, ContentSerializer.Write(ContentTestKit.Library(dir.Path).Find(strong.Ref)!));
    }

    [Fact]
    public void A_package_of_a_unit_with_passives_carries_the_rule_and_the_type_they_refer_to()
    {
        using var source = new TempDirectory();
        using var target = new TempDirectory();
        var library = ContentTestKit.Library(source.Path);
        var rule = library.Duplicate(library.Find(ContentTypeRegistry.DefenseRule, "core.rule.fire_convert_25_reflect_75")!, "Своя регенерация").Item!;
        var unit = library.Create(ContentTypeRegistry.Unit, "Саламандра");
        ((UnitData)unit.Data).Passives.Add(P("p1", PassiveOperations.GrantDefenseRule, 0));
        ((UnitData)unit.Data).Passives[0].Rule = rule.Id;
        ((UnitData)unit.Data).Passives.Add(P("p2", PassiveOperations.OutgoingDamagePercent, 50, type: "core.damage.fire_example"));
        Assert.True(library.Save(unit).Ok);
        var bytes = ContentExporter.ExportPackage(library, new[] { library.Find(unit.Ref)! }, "Саламандра", string.Empty, "0.28.0", DateTimeOffset.UnixEpoch);
        var clean = ContentTestKit.Library(target.Path);
        var preview = ContentImporter.Inspect(clean, "unit.hxp", bytes);
        Assert.False(preview.Rejected, preview.Summary);
        Assert.Contains(preview.Entries, e => e.Item.Ref == rule.Ref);
        Assert.True(ContentImporter.Install(clean, preview).Ok);
        var stats = RuntimeFactory.BuildUnit(ContentTestKit.Library(target.Path).Find(unit.Ref)!, ContentTestKit.Library(target.Path)).Stats;
        Assert.Equal(rule.Id, stats.DefenseRules.Single().Id);
        Assert.Equal(50, stats.DamageModifiers.Single().Percent);
    }
}
