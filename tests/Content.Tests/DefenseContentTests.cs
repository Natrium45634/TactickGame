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
using HexTactics.Core.Hex;
using HexTactics.Core.Spawning;
using HexTactics.Core.Units;
using HexTactics.Simulation;
using HexTactics.Tests.Support;
using Xunit;

namespace HexTactics.Content.Tests;

/// <summary>Defense rules and shields as library data (spec part 5 §6.3, §8.3, §9.1, §9.3, §9.5).</summary>
public class DefenseContentTests
{
    private const string FireRuleId = "core.rule.fire_convert_25_reflect_75";
    private const string FireType = "core.damage.fire_example";

    /// <summary>The example of spec part 5 §9.3, verbatim (envelope schema 2 → our defense_rule schema 1).</summary>
    private const string SpecExample = """
        {
          "schemaVersion": 1,
          "id": "user.rule.fire_convert_25_reflect_75",
          "kind": "defense_rule",
          "name": "Огненная регенерация и круговое отражение",
          "revision": 1,
          "description": "Работает при R к огню не ниже 300%",
          "references": [
            {"kind": "damage_type", "id": "core.damage.fire_example"}
          ],
          "data": {
            "trigger": "on_hit_contact",
            "damageTypeId": "core.damage.fire_example",
            "minimumEffectiveResistancePercent": 300,
            "triggerOnImmuneContact": false,
            "priority": 100,
            "cooldownSeconds": 0,
            "actions": [
              {"effect": "convert_to_healing", "fraction": 0.25, "recipient": "self"},
              {
                "effect": "redirect_as_damage",
                "fraction": 0.75,
                "targeting": "adjacent_hex_ring",
                "radiusHexes": 1,
                "includeCenter": false,
                "targetFilter": "enemies_of_defender",
                "targetDamageMode": "full_per_target",
                "responseDamageType": "same_as_incoming",
                "allowReactiveChain": false
              }
            ]
          }
        }
        """;

    private static DefenseRuleData Rule(ContentItem item) => (DefenseRuleData)item.Data;

    [Fact]
    public void The_contract_of_section_9_3_loads_validates_and_round_trips()
    {
        var library = ContentTestKit.Library(null);
        var read = ContentSerializer.Read(SpecExample, library.Registry, ContentOrigin.User);
        Assert.DoesNotContain(read.Issues, i => i.IsError);
        var item = read.Item!;
        Assert.DoesNotContain(library.Validate(item), i => i.IsError);
        var rule = Rule(item);
        Assert.Equal(300, rule.MinEffectiveResistance);
        Assert.Equal(2, rule.Actions.Count);
        Assert.Contains(new ContentRef("damage_type", FireType), item.References);

        var again = ContentSerializer.Read(ContentSerializer.Write(item), library.Registry, ContentOrigin.User).Item!;
        Assert.Equal(ContentSerializer.Write(item), ContentSerializer.Write(again));

        var runtime = rule.ToRule(item.Id, item.Name, library.Registry.DefenseActions);
        Assert.Equal((DefenseTrigger.OnHit, FireType, 300.0), (runtime.Trigger, runtime.DamageType, runtime.MinEffectiveResistance!.Value));
        Assert.Equal(DefenseActionKind.ConvertToHealing, runtime.Actions[0].Kind);
        var reflect = runtime.Actions[1];
        Assert.Equal((DefenseActionKind.RedirectAsDamage, 0.75, ReactionTargeting.Area, 1, false, ReactionFilter.EnemiesOfDefender, false, (string?)null),
            (reflect.Kind, reflect.Fraction, reflect.Targeting, reflect.Radius, reflect.IncludeCenter, reflect.Filter, reflect.Split, reflect.ResponseDamageType));
    }

    [Fact]
    public void Reactive_chains_wrong_triggers_unknown_actions_and_over_100_percent_are_refused()
    {
        var library = ContentTestKit.Library(null);
        var chain = ContentSerializer.Read(SpecExample.Replace("\"allowReactiveChain\": false", "\"allowReactiveChain\": true", StringComparison.Ordinal), library.Registry, ContentOrigin.User).Item!;
        Assert.Contains(library.Validate(chain), i => i.IsError && i.Message.Contains("allowReactiveChain", StringComparison.Ordinal));

        var item = library.Create(ContentTypeRegistry.DefenseRule, "Жадное правило");
        var rule = Rule(item);
        Assert.DoesNotContain(library.Validate(item), i => i.IsError); // the default: absorb 25 % of any hit
        var heal = library.Registry.DefenseActions.Find("convert_to_healing")!.CreateDefault();
        heal.Set("fraction", 0.8);
        rule.Actions.Add(heal);
        Assert.Contains(library.Validate(item), i => i.IsError && i.Message.Contains("105 %", StringComparison.Ordinal));

        rule.Actions.RemoveAt(1);
        rule.Actions.Add(library.Registry.DefenseActions.Find("retaliate")!.CreateDefault());
        Assert.Contains(library.Validate(item), i => i.IsError && i.Path == "data.actions[1]" && i.Message.Contains("потеряно HP", StringComparison.Ordinal));

        rule.Actions.RemoveAt(1);
        rule.Actions.Add(new EffectData("explode_everything"));
        Assert.Contains(library.Validate(item), i => i.IsError && i.Message.Contains("explode_everything", StringComparison.Ordinal));

        rule.Actions.Clear();
        Assert.Contains(library.Validate(item), i => i.IsError && i.Path == "data.actions");
        rule.Actions.Add(library.Registry.DefenseActions.Find("negate")!.CreateDefault());
        rule.Actions[0].Set("fraction", 1.5);
        Assert.Contains(library.Validate(item), i => i.IsError && i.Path == "data.actions[0].fraction");
        rule.Actions[0].Set("fraction", 0.5);
        rule.MinEffectiveResistance = 900;
        Assert.Contains(library.Validate(item), i => i.IsError && i.Path == "data.minimumEffectiveResistancePercent");
    }

    [Fact]
    public void Immunity_with_a_conversion_warns_unless_the_contact_flag_is_set()
    {
        var library = ContentTestKit.Library(null);
        var item = library.Create(ContentTypeRegistry.DefenseRule, "Иммунитет и лечение");
        var rule = Rule(item);
        rule.Actions.Insert(0, library.Registry.DefenseActions.Find("immunity")!.CreateDefault());
        Assert.Contains(library.Validate(item), i => !i.IsError && i.Path == "data.triggerOnImmuneContact");
        rule.TriggerOnImmuneContact = true;
        Assert.DoesNotContain(library.Validate(item), i => i.Path == "data.triggerOnImmuneContact");
    }

    [Fact]
    public void Old_units_get_no_rules_and_no_shields_and_a_unit_with_both_round_trips()
    {
        using var dir = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(dir.Path, "content", "units"));
        var v21 = "{\"schemaVersion\":2,\"id\":\"unit_v21\",\"kind\":\"unit\",\"name\":\"Боец v0.21\",\"revision\":1,\"data\":{\"maxHealth\":100,\"attackDamageType\":\"core.damage.physical\",\"resistances\":{\"core.damage.physical\":50}}}";
        File.WriteAllText(Path.Combine(dir.Path, "content", "units", "unit_v21.json"), v21);
        var library = ContentTestKit.Library(dir.Path);
        var old = (UnitData)library.Find(ContentTypeRegistry.Unit, "unit_v21")!.Data;
        Assert.Empty(old.DefenseRules);
        Assert.Empty(old.Shields);
        Assert.Equal(50, old.Resistances["core.damage.physical"]);
        Assert.Contains("\"schemaVersion\": 3", File.ReadAllText(Path.Combine(dir.Path, "content", "units", "unit_v21.json")), StringComparison.Ordinal);
        Assert.Equal(v21, File.ReadAllText(Directory.GetFiles(Path.Combine(dir.Path, "backups"), "unit_v21.json", SearchOption.AllDirectories).Single()));

        var unit = library.Create(ContentTypeRegistry.Unit, "Страж");
        var data = (UnitData)unit.Data;
        data.DefenseRules.Add(FireRuleId);
        data.Shields.Add(new ShieldData { Name = "Барьер", Capacity = 30, DamageType = "core.damage.physical", Order = 1 });
        Assert.DoesNotContain(library.Validate(unit), i => i.IsError);
        Assert.Contains(new ContentRef("defense_rule", FireRuleId), unit.References);
        var again = (UnitData)ContentSerializer.Read(ContentSerializer.Write(unit), library.Registry, ContentOrigin.User).Item!.Data;
        Assert.Equal(new[] { FireRuleId }, again.DefenseRules);
        Assert.Equal(("Барьер", 30.0, "core.damage.physical", 1), (again.Shields[0].Name, again.Shields[0].Capacity, again.Shields[0].DamageType, again.Shields[0].Order));

        data.Shields.Add(new ShieldData { Name = "Барьер", Capacity = -5 });
        data.DefenseRules.Add("user_rule_missing");
        var issues = library.Validate(unit);
        Assert.Contains(issues, i => i.IsError && i.Path == "data.shields[1].capacity");
        Assert.Contains(issues, i => i.IsError && i.Path == "data.shields[1].name");
        Assert.Contains(issues, i => i.IsError && i.Message.Contains("user_rule_missing", StringComparison.Ordinal));
    }

    [Fact]
    public void P18_the_built_in_example_rule_on_a_library_unit_heals_10_and_reflects_30_in_the_simulation()
    {
        var library = ContentTestKit.Library(null);
        var guard = library.Create(ContentTypeRegistry.Unit, "Огненный страж");
        var data = (UnitData)guard.Data;
        data.Resistances[FireType] = 300;
        data.DefenseRules.Add(FireRuleId);
        var game = RuntimeFactory.BuildGame(library, library.Find(ContentTypeRegistry.Map, RuntimeFactory.DefaultMapId)!, extraUnits: new[] { guard });
        var sim = new GameSimulation(HexTactics.Tests.Shared.TestMaps.Empty(), new SimulationSettings { Skills = game.Skills, DamageTypes = game.DamageTypes });
        var defender = sim.Spawn(SpawnRequest.AtCell(HexCoord.FromOffset(5, 5), Side.Player, HexDirection.North) with { Profile = game.Units[guard.Id].ToProfile() }).Unit!.Value;
        var enemy = sim.Spawn(SpawnRequest.AtCell(HexCoord.FromOffset(6, 5), Side.Enemy, HexDirection.North)).Unit!.Value;
        sim.State.ApplyDamage(defender, 40);
        var result = sim.Damage.Resolve(sim.Damage.Create(null, defender, FireType, 40, DamageOrigin.Skill), sim.Tick);
        Assert.Equal((0.0, 10.0), (result.HpLoss, result.Healed));
        Assert.Equal(70, sim.State.GetUnit(defender).CurrentHealth, 9);
        Assert.Equal(70, sim.State.GetUnit(enemy).CurrentHealth, 9);
    }

    [Fact]
    public void A_rule_with_errors_or_a_missing_rule_never_reaches_the_battle()
    {
        var library = ContentTestKit.Library(null);
        var unit = library.Create(ContentTypeRegistry.Unit, "Без правил");
        ((UnitData)unit.Data).DefenseRules.Add("user_rule_missing");
        var issues = new IssueList();
        var template = RuntimeFactory.BuildUnit(unit, library, issues);
        Assert.Empty(template.Stats.DefenseRules);
        Assert.Contains(issues.Items, i => i.IsError && i.Message.Contains("user_rule_missing", StringComparison.Ordinal));
    }

    [Fact]
    public void P46_a_unit_package_carries_its_rule_and_the_fire_type_and_fights_the_same_after_import()
    {
        using var source = new TempDirectory();
        using var target = new TempDirectory();
        var library = ContentTestKit.Library(source.Path);
        var fire = library.Create(ContentTypeRegistry.DamageType, "Плазма");
        ((DamageTypeData)fire.Data).Family = "core.damage.magical";
        Assert.True(library.Save(fire).Ok);
        var rule = library.Create(ContentTypeRegistry.DefenseRule, "Плазменное зеркало");
        var r = Rule(rule);
        r.DamageType = fire.Id;
        r.Actions.Clear();
        var mirror = library.Registry.DefenseActions.Find("redirect_as_damage")!.CreateDefault();
        mirror.Set("fraction", 0.5);
        r.Actions.Add(mirror);
        Assert.True(library.Save(rule).Ok, string.Join("; ", library.Validate(rule)));
        var unit = library.Create(ContentTypeRegistry.Unit, "Зеркальщик");
        ((UnitData)unit.Data).DefenseRules.Add(rule.Id);
        ((UnitData)unit.Data).Shields.Add(new ShieldData { Name = "Щит", Capacity = 10 });
        Assert.True(library.Save(unit).Ok);

        Assert.Equal(new[] { fire.Id, rule.Id, unit.Id }.OrderBy(x => x), ContentExporter.Dependencies(library, new[] { library.Find(unit.Ref)! }).Select(i => i.Id).OrderBy(x => x));
        var bytes = ContentExporter.ExportPackage(library, new[] { library.Find(unit.Ref)! }, "Зеркальщик", string.Empty, "0.22.0", DateTimeOffset.UnixEpoch);
        var clean = ContentTestKit.Library(target.Path);
        var preview = ContentImporter.Inspect(clean, "mirror.hxp", bytes);
        Assert.False(preview.Rejected, preview.Summary);
        Assert.True(ContentImporter.Install(clean, preview).Ok);

        double Fight(ContentLibrary lib)
        {
            var template = RuntimeFactory.BuildUnit(lib.Find(unit.Ref)!, lib);
            var sim = new GameSimulation(HexTactics.Tests.Shared.TestMaps.Empty(), new SimulationSettings { DamageTypes = RuntimeFactory.BuildDamageTypes(lib) });
            var me = sim.Spawn(SpawnRequest.AtCell(HexCoord.FromOffset(5, 5), Side.Player, HexDirection.North) with { Profile = template.ToProfile() }).Unit!.Value;
            var foe = sim.Spawn(SpawnRequest.AtCell(HexCoord.FromOffset(6, 5), Side.Enemy, HexDirection.North)).Unit!.Value;
            sim.Damage.Resolve(sim.Damage.Create(foe, me, fire.Id, 40, DamageOrigin.Skill), sim.Tick);
            return sim.State.GetUnit(me).CurrentHealth * 1000 + sim.State.GetUnit(foe).CurrentHealth;
        }

        Assert.Equal(Fight(library), Fight(ContentTestKit.Library(target.Path)));
        Assert.Equal(90 * 1000 + 80, Fight(library)); // 20 reflected; 20 left: shield 10, health 10
    }
}
