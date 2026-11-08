using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using HexTactics.Content.Kinds;
using HexTactics.Content.Library;
using HexTactics.Content.Model;
using HexTactics.Content.Registry;
using HexTactics.Content.Runtime;
using HexTactics.Content.Schema;
using HexTactics.Content.Transfer;
using HexTactics.Core.Combat;
using HexTactics.Editor.Defense;
using HexTactics.Editor.Mechanics;
using HexTactics.Editor.Units;
using HexTactics.Tests.Support;
using Xunit;

namespace HexTactics.Editor.Tests;

/// <summary>
/// End-to-end acceptance of spec part 5 (§11.1 p.3, 7; §11.5 p.40–44, 47): every kind of part 5 is made in the editors, saved,
/// exported, imported on a clean installation and gives the same hits; broken imports leave the library untouched.
/// </summary>
public class Part5EndToEndTests
{
    private static readonly DateTimeOffset Created = DateTimeOffset.UnixEpoch;

    /// <summary>Plasma (magical subtype), a set with a parent, a rule with a plasma response, a typed skill and tile, a unit with all.</summary>
    private static (ContentItem Plasma, ContentItem Base, ContentItem Set, ContentItem Rule, ContentItem Unit) MakeEverything(ContentLibrary library)
    {
        var plasma = new DamageTypeEditorModel(library, DamageTypeEditorModel.CreateFrom(library, DamageTypeEditorModel.BaseMagical, "Плазменный"));
        Assert.Null(plasma.SetTags("plasma"));
        Assert.True(plasma.Save().Ok);

        var basis = new ResistanceProfileEditorModel(library, library.Create(ContentTypeRegistry.ResistanceProfile, "Основа"));
        Assert.Null(basis.SetValue(DamageTypes.Physical, "40"));
        Assert.True(basis.Save().Ok);
        var set = new ResistanceProfileEditorModel(library, library.Create(ContentTypeRegistry.ResistanceProfile, "Плазменная броня"));
        Assert.Null(set.SetParent(basis.Item.Id));
        Assert.Null(set.SetValue(plasma.Item.Id, "300"));
        Assert.True(set.Save().Ok);

        var rule = new DefenseRuleEditorModel(library, library.Create(ContentTypeRegistry.DefenseRule, "Плазменная регенерация"));
        Assert.Null(rule.ApplyTemplate("complex"));
        Assert.Null(rule.TrySetField(DefenseRuleData.Fields.Single(f => f.Key == "damageTypeId"), plasma.Item.Id));
        Assert.Null(rule.SetActionField(1, "responseDamageType", plasma.Item.Id));
        Assert.True(rule.Save().Ok, string.Join("; ", rule.Validate()));

        var unit = new UnitEditorModel(library, library.Create(ContentTypeRegistry.Unit, "Плазмоид"));
        Assert.Null(unit.AddProfile(set.Item.Id));
        Assert.Null(unit.SetResistance(DamageTypes.Blunt, "-25"));
        Assert.Null(unit.AddRule(rule.Item.Id));
        Assert.Null(unit.AddShield());
        Assert.Null(unit.SetShield(0, "capacity", "15"));
        Assert.Null(unit.SetShield(0, "damageTypeId", DamageTypes.Physical));
        Assert.Null(unit.TrySetField(UnitData.Fields.Single(f => f.Key == "attackDamageType"), plasma.Item.Id));
        Assert.True(unit.Save().Ok);
        return (plasma.Item, basis.Item, set.Item, rule.Item, unit.Item);
    }

    /// <summary>Several hits on the unit in the sandbox — the fingerprint of its whole defense.</summary>
    private static string Fingerprint(ContentLibrary library, string unitId, string plasmaId) => string.Join(" || ", new[]
    {
        new SandboxSetup { TargetUnitId = unitId, DamageTypeId = plasmaId, Amount = 40, HealthPercent = 60, EnemiesAround = 3 },
        new SandboxSetup { TargetUnitId = unitId, DamageTypeId = DamageTypes.Slashing, Amount = 40 },
        new SandboxSetup { TargetUnitId = unitId, DamageTypeId = DamageTypes.Blunt, Amount = 30 },
        new SandboxSetup { TargetUnitId = unitId, DamageTypeId = DamageTypes.True, Amount = 25 },
        new SandboxSetup { TargetUnitId = unitId, DamageTypeId = plasmaId, Amount = 40, Penetration = 100 },
    }.Select(s => HitSandbox.Describe(HitSandbox.Run(library, s))));

    [Fact]
    public void P41_P43_everything_of_part_5_survives_restart_export_and_import_with_identical_hits()
    {
        using var source = new TempDirectory();
        using var target = new TempDirectory();
        var library = ContentTestKit.Library(source.Path);
        var (plasma, _, _, rule, unit) = MakeEverything(library);
        var before = Fingerprint(library, unit.Id, plasma.Id);
        Assert.Contains("лечение +10", before, StringComparison.Ordinal);       // plasma 40 at R 300 % with the rule
        Assert.Contains("R после пробития = 200 %", before, StringComparison.Ordinal); // penetration 100 → below the 300 % threshold

        // Restart (p.43): a fresh library over the same folder gives the same hits.
        Assert.Equal(before, Fingerprint(ContentTestKit.Library(source.Path), unit.Id, plasma.Id));

        // Package of the unit (p.41): the closure carries the type, both sets, the rule; the order of rules and sets stays.
        var bytes = ContentExporter.ExportPackage(library, new[] { library.Find(unit.Ref)! }, "Плазмоид", string.Empty, "0.25.0", Created);
        var clean = ContentTestKit.Library(target.Path);
        var preview = ContentImporter.Inspect(clean, "plasma.hxp", bytes);
        Assert.False(preview.Rejected, preview.Summary);
        Assert.Equal(5, preview.Entries.Count); // type, base set, set, rule, unit
        Assert.True(ContentImporter.Install(clean, preview).Ok);
        var imported = ContentTestKit.Library(target.Path);
        Assert.Equal(before, Fingerprint(imported, unit.Id, plasma.Id));
        var importedUnit = (UnitData)imported.Find(unit.Ref)!.Data;
        Assert.Equal(((UnitData)unit.Data).ResistanceProfiles, importedUnit.ResistanceProfiles);
        Assert.Equal(new[] { rule.Id }, importedUnit.DefenseRules);
        Assert.Equal(ContentSerializer.Write(library.Find(rule.Ref)!), ContentSerializer.Write(imported.Find(rule.Ref)!));
    }

    [Fact]
    public void P42_an_unknown_handler_or_a_missing_dependency_is_refused_without_touching_the_library()
    {
        using var source = new TempDirectory();
        using var target = new TempDirectory();
        var library = ContentTestKit.Library(source.Path);
        var (plasma, _, _, rule, unit) = MakeEverything(library);

        // A rule file with an action no handler exists for.
        var json = JsonNode.Parse(ContentSerializer.Write(library.Find(rule.Ref)!))!.AsObject();
        json["data"]!["actions"]![0]!["effect"] = "run_my_script";
        var clean = ContentTestKit.Library(target.Path);
        var snapshot = Directory.GetFiles(target.Path, "*", SearchOption.AllDirectories).OrderBy(f => f).ToList();
        var bad = ContentImporter.Inspect(clean, "rule.json", System.Text.Encoding.UTF8.GetBytes(json.ToJsonString()));
        Assert.False(bad.CanInstall);
        Assert.Contains(bad.Entries.SelectMany(e => e.Issues).Concat(bad.Problems), i => i.IsError && i.Message.Contains("run_my_script", StringComparison.Ordinal));

        // The unit alone, without its type, sets and rule: a missing dependency, not a guess.
        var lone = ContentImporter.Inspect(clean, "unit.json", System.Text.Encoding.UTF8.GetBytes(ContentSerializer.Write(library.Find(unit.Ref)!)));
        Assert.False(lone.CanInstall);
        Assert.Contains(lone.Entries.SelectMany(e => e.Issues).Concat(lone.Problems), i => i.IsError && i.Message.Contains(plasma.Id, StringComparison.Ordinal));
        Assert.Equal(snapshot, Directory.GetFiles(target.Path, "*", SearchOption.AllDirectories).OrderBy(f => f).ToList());
    }

    [Fact]
    public void P3_P7_a_renamed_user_type_keeps_its_id_and_links_and_built_in_types_are_protected()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var (plasma, _, set, rule, unit) = MakeEverything(library);
        Assert.True(library.Rename(ContentTypeRegistry.DamageType, plasma.Id, "Плазма (новое имя)").Ok);
        Assert.Equal("Плазма (новое имя)", library.Find(plasma.Ref)!.Name);
        Assert.All(new[] { set, rule, unit }, i => Assert.DoesNotContain(library.Validate(library.Find(i.Ref)!), x => x.IsError));
        Assert.Equal(plasma.Id, ((UnitData)library.Find(unit.Ref)!.Data).AttackDamageType);

        // A built-in type cannot be changed in place or deleted: the editor saves a copy, the library refuses deletion.
        var pure = new DamageTypeEditorModel(library, library.Find(ContentTypeRegistry.DamageType, DamageTypes.True)!.Clone());
        Assert.Null(pure.SetStrategy(DamageTypeData.ResistanceAffected));
        Assert.True(pure.Save().Ok);
        Assert.NotEqual(DamageTypes.True, pure.Item.Id);
        Assert.Equal(DamageTypeData.TrueLike, ((DamageTypeData)library.Find(ContentTypeRegistry.DamageType, DamageTypes.True)!.Data).Strategy);
        Assert.False(library.Delete(ContentTypeRegistry.DamageType, DamageTypes.True).Result.Ok);
        Assert.False(library.Delete(ContentTypeRegistry.DamageType, plasma.Id).Result.Ok); // used by the unit, the set and the rule
    }

    [Fact]
    public void P40_a_rule_changed_in_the_library_reaches_units_only_when_a_match_is_built_again()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var (_, _, _, rule, unit) = MakeEverything(library);
        var running = RuntimeFactory.BuildUnit(library.Find(unit.Ref)!, library).Stats;
        var edit = new DefenseRuleEditorModel(library, library.Find(rule.Ref)!.Clone());
        Assert.Null(edit.TrySetField(DefenseRuleData.Fields.Single(f => f.Key == "minimumEffectiveResistancePercent"), "100"));
        Assert.True(edit.Save().Ok);
        Assert.Equal(300, running.DefenseRules.Single().MinEffectiveResistance); // the running match keeps its rules
        Assert.Equal(100, RuntimeFactory.BuildUnit(library.Find(unit.Ref)!, library).Stats.DefenseRules.Single().MinEffectiveResistance);
    }

    [Fact]
    public void P44_a_part_3_unit_of_schema_1_reaches_the_current_schema_with_its_old_numbers()
    {
        using var dir = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(dir.Path, "content", "units"));
        var v17 = "{\"schemaVersion\":1,\"id\":\"unit_v17\",\"kind\":\"unit\",\"name\":\"Боец v0.17\",\"revision\":3,\"data\":{\"maxHealth\":120,\"moveSpeed\":1,\"attackRate\":3,\"attackPower\":10,\"attackRange\":1,\"skills\":[],\"side\":\"player\",\"color\":\"#4D80CC\",\"scale\":1}}";
        File.WriteAllText(Path.Combine(dir.Path, "content", "units", "unit_v17.json"), v17);
        var library = ContentTestKit.Library(dir.Path);
        var item = library.Find(ContentTypeRegistry.Unit, "unit_v17")!;
        var data = (UnitData)item.Data;
        Assert.Equal((Migrations.Untyped, 0, 0, 0), (data.AttackDamageType, data.ResistanceProfiles.Count, data.DefenseRules.Count, data.Shields.Count));
        Assert.Empty(data.Resistances);
        Assert.Empty(data.Armor); // spec part 6 §3.5: A = 0 after the migration, no hidden armor
        Assert.Equal((0.0, 0.0), (data.AttackArmorPenetration, data.AttackArmorPenetrationPercent));
        Assert.Equal(3, item.Revision);
        Assert.Contains("\"schemaVersion\": 5", File.ReadAllText(Path.Combine(dir.Path, "content", "units", "unit_v17.json")), StringComparison.Ordinal);
        Assert.Equal(v17, File.ReadAllText(Directory.GetFiles(Path.Combine(dir.Path, "backups"), "unit_v17.json", SearchOption.AllDirectories).Single()));
        var hit = HitSandbox.Run(library, new SandboxSetup { TargetUnitId = item.Id, DamageTypeId = DamageTypes.Slashing, Amount = 20 });
        Assert.Equal(20, hit.With.Result.HpLoss, 9); // an unprotected old unit takes the old numbers
    }

    [Fact]
    public void P47_every_registered_defense_action_works_through_registry_validator_editor_file_and_simulation()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        foreach (var descriptor in library.Registry.DefenseActions.All)
        {
            // Registry → editor: a rule of the action's trigger with the action at its defaults, every field set once from the UI.
            var editor = new DefenseRuleEditorModel(library, library.Create(ContentTypeRegistry.DefenseRule, "Проверка " + descriptor.Label));
            Assert.Null(editor.TrySetField(DefenseRuleData.Fields.Single(f => f.Key == "trigger"), descriptor.Trigger));
            editor.Rule.Actions.Clear();
            Assert.Null(editor.AddAction(descriptor.Effect));
            foreach (var field in descriptor.Fields.Where(f => f.Default is not null))
            {
                var input = field.Kind is FieldKind.Number or FieldKind.Integer ? Convert.ToString(field.Default, System.Globalization.CultureInfo.InvariantCulture) : field.Default;
                Assert.Null(editor.SetActionField(0, field.Key, input));
            }

            // Validator, save, file round trip.
            Assert.DoesNotContain(editor.Validate(), i => i.IsError);
            Assert.True(editor.Save().Ok, descriptor.Effect);
            var reread = ContentSerializer.Read(File.ReadAllText(library.User!.PathOf("defense_rules", editor.Item.Id)), library.Registry, ContentOrigin.User).Item!;
            Assert.Equal(ContentSerializer.Write(editor.Item), ContentSerializer.Write(reread));

            // Simulation: the runtime rule has the action's kind, and the summary names it.
            var runtime = ((DefenseRuleData)reread.Data).ToRule(reread.Id, reread.Name, library.Registry.DefenseActions);
            Assert.Equal(descriptor.Kind, runtime.Actions.Single().Kind);
            Assert.DoesNotContain("✗", DefenseRuleSummary.Describe((DefenseRuleData)reread.Data, library), StringComparison.Ordinal);
        }
    }
}
