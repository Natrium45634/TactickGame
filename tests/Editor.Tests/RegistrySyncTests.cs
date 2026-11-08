using System;
using System.IO;
using System.Linq;
using HexTactics.Content.Kinds;
using HexTactics.Content.Library;
using HexTactics.Content.Registry;
using HexTactics.Content.Schema;
using HexTactics.Content.Transfer;
using HexTactics.Core.Combat;
using HexTactics.Core.Effects;
using HexTactics.Editor.History;
using HexTactics.Editor.Library;
using HexTactics.Editor.Defense;
using HexTactics.Editor.Mechanics;
using HexTactics.Editor.Skills;
using HexTactics.Editor.Tiles;
using HexTactics.Editor.Units;
using HexTactics.Tests.Support;
using Xunit;

namespace HexTactics.Editor.Tests;

/// <summary>One registry of definitions and its synchronisation (spec part 6 §2; acceptance §12.1 P6-01…P6-08).</summary>
public class RegistrySyncTests
{
    private static ContentItem Fire(ContentLibrary library, string name = "Огонь")
    {
        var fire = new DamageTypeEditorModel(library, DamageTypeEditorModel.CreateFrom(library, DamageTypeEditorModel.BaseMagical, name));
        fire.SetElement(true);
        Assert.True(fire.Save().Ok);
        return fire.Item;
    }

    private static FieldSchema EffectField(ContentLibrary library, string type, string key) => library.Registry.Effects.Find(type)!.FindField(key)!;

    [Fact]
    public void P6_01_a_new_element_is_offered_at_once_in_every_compatible_selector()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var fire = Fire(library);
        bool Offered(FieldSchema field) => ReferenceCatalog.Options(library, field.RefKind!, field.RefFilter, null).Any(o => o.Value == fire.Id);

        Assert.True(Offered(EffectField(library, "damage", "damageType")));                          // skills
        Assert.True(Offered(UnitData.Fields.Single(f => f.Key == "attackDamageType")));              // normal attack
        Assert.True(Offered(library.Registry.Effects.Find("periodic")!.FindField("damageType", EffectContext.Tile)!)); // tiles
        Assert.True(Offered(EffectField(library, "debuff", "resistanceOf")));                       // temporary R
        Assert.True(Offered(DefenseRuleData.Fields.Single(f => f.Key == "damageTypeId")));          // reactions
        Assert.True(Offered(library.Registry.DefenseActions.Find("redirect_as_damage")!.FindField("responseDamageType")!));
        Assert.Contains(new UnitEditorModel(library, library.Create(ContentTypeRegistry.Unit, "Юнит")).ResistanceRows(), r => r.Key == fire.Id); // R table
        var option = ReferenceCatalog.Options(library, ContentTypeRegistry.DamageType, ReferenceFilter.Any, null).Single(o => o.Value == fire.Id);
        Assert.Equal(("Магический", "Огонь · элемент (свой)"), (option.Group, option.Label)); // grouped under its family, labelled
    }

    [Fact]
    public void P6_02_typed_filters_keep_types_out_of_fields_that_cannot_take_them()
    {
        var library = ContentTestKit.Library(null);
        var families = ReferenceCatalog.Options(library, ContentTypeRegistry.DamageType, ReferenceFilter.Family, null);
        Assert.All(families, o => Assert.Equal(string.Empty, ((DamageTypeData)library.Find(ContentTypeRegistry.DamageType, o.Value)!.Data).Family));
        Assert.DoesNotContain(ReferenceCatalog.Options(library, ContentTypeRegistry.DamageType, ReferenceFilter.ResistanceAffected, null), o => o.Value == DamageTypes.True);
        Assert.DoesNotContain(ReferenceCatalog.Options(library, ContentTypeRegistry.DamageType, ReferenceFilter.Subtype, null), o => o.Value == DamageTypes.Physical);
        Assert.DoesNotContain(ReferenceCatalog.Options(library, ContentTypeRegistry.Skill, ReferenceFilter.Any, null), o => o.Value.StartsWith("core.damage", StringComparison.Ordinal));
        Assert.DoesNotContain(ReferenceCatalog.Options(library, ContentTypeRegistry.DamageType, ReferenceFilter.Any, null), o => o.Value == DamageTypes.Untyped); // legacy hidden
        Assert.Contains(ReferenceCatalog.Options(library, ContentTypeRegistry.DamageType, ReferenceFilter.Any, DamageTypes.Untyped), o => o.Value == DamageTypes.Untyped); // unless chosen
        Assert.Equal(ReferenceCatalog.CreateValue, ReferenceCatalog.Options(library, ContentTypeRegistry.DamageType, ReferenceFilter.Any, null, allowCreate: true).Last().Value);
        Assert.DoesNotContain(ReferenceCatalog.Options(library, ContentTypeRegistry.Skill, ReferenceFilter.Any, null, allowCreate: true), o => o.IsCreate);
    }

    [Fact]
    public void P6_03_a_unit_without_its_own_fire_value_inherits_the_magical_resistance()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var fire = Fire(library);
        var unit = new UnitEditorModel(library, library.Create(ContentTypeRegistry.Unit, "Маг"));
        Assert.Null(unit.SetResistance(DamageTypes.Magical, "40"));
        Assert.Equal(40, unit.EffectiveResistance(fire.Id));
        Assert.Null(unit.SetResistance(fire.Id, "-60"));
        Assert.Equal((-20.0, 40.0), (unit.EffectiveResistance(fire.Id), unit.EffectiveResistance(DamageTypes.Magical)));
    }

    /// <summary>A skill, a surface, a rule and a unit all using «Огонь» (attack type, own R, rule condition).</summary>
    private static (ContentItem Skill, ContentItem Tile, ContentItem Rule, ContentItem Unit) UseEverywhere(ContentLibrary library, ContentItem fire)
    {
        var skill = library.Duplicate(library.Find(ContentTypeRegistry.Skill, "core.skill.linear_strike")!, "Огненный удар").Item!;
        foreach (var effect in ((SkillData)skill.Data).Cells.SelectMany(c => c.Effects))
        {
            effect.Set("damageType", fire.Id);
        }

        Assert.True(library.Save(skill).Ok);
        var tile = new TileEditorModel(library, library.Create(ContentTypeRegistry.Tile, "Огненная поверхность"));
        Assert.Null(tile.TrySetField(TileData.Fields.Single(f => f.Key == "category"), TileData.Special));
        Assert.Null(tile.AddEffect(EffectTypes.Periodic));
        Assert.Null(tile.SetEffectField(0, "damageType", fire.Id));
        Assert.True(tile.Save().Ok);
        var rule = new DefenseRuleEditorModel(library, library.Create(ContentTypeRegistry.DefenseRule, "Огненная регенерация"));
        Assert.Null(rule.ApplyTemplate("complex"));
        Assert.Null(rule.TrySetField(DefenseRuleData.Fields.Single(f => f.Key == "damageTypeId"), fire.Id));
        Assert.True(rule.Save().Ok, string.Join("; ", rule.Validate()));
        var unit = new UnitEditorModel(library, library.Create(ContentTypeRegistry.Unit, "Саламандра"));
        Assert.Null(unit.TrySetField(UnitData.Fields.Single(f => f.Key == "attackDamageType"), fire.Id));
        Assert.Null(unit.SetResistance(fire.Id, "300"));
        Assert.Null(unit.AddRule(rule.Item.Id));
        Assert.True(unit.Save().Ok);
        return (skill, tile.Item, rule.Item, unit.Item);
    }

    [Fact]
    public void P6_04_renaming_the_type_keeps_the_links_of_skill_tile_rule_and_unit_editors()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var fire = Fire(library);
        var (skill, tile, rule, unit) = UseEverywhere(library, fire);
        Assert.True(library.Rename(ContentTypeRegistry.DamageType, fire.Id, "Пламя").Ok);

        var reopened = ContentTestKit.Library(dir.Path); // links are ids: they survive a restart as well
        foreach (var lib in new[] { library, reopened })
        {
            Assert.Contains(new SkillEditorModel(lib, lib.Find(skill.Ref)!).Preview(), l => l.Contains("пламя", StringComparison.Ordinal));
            Assert.Contains(new TileEditorModel(lib, lib.Find(tile.Ref)!).Preview(), l => l.Contains("пламя", StringComparison.Ordinal));
            Assert.Contains("«Пламя»", new DefenseRuleEditorModel(lib, lib.Find(rule.Ref)!).Summary(), StringComparison.Ordinal);
            var u = new UnitEditorModel(lib, lib.Find(unit.Ref)!);
            Assert.Equal(("Пламя", 300.0), (u.ResistanceRows().Single(r => r.Key == fire.Id).Label, u.EffectiveResistance(fire.Id)));
            Assert.All(new[] { skill, tile, rule, unit }, i => Assert.DoesNotContain(lib.Validate(lib.Find(i.Ref)!), x => x.IsError));
        }
    }

    [Fact]
    public void P6_06_a_package_with_the_element_restores_every_link_and_the_same_hits_on_a_clean_installation()
    {
        using var source = new TempDirectory();
        using var target = new TempDirectory();
        var library = ContentTestKit.Library(source.Path);
        var fire = Fire(library);
        var (skill, tile, _, unit) = UseEverywhere(library, fire);
        string Hits(ContentLibrary lib) => string.Join(" || ", new[]
        {
            new SandboxSetup { TargetUnitId = unit.Id, DamageTypeId = fire.Id, Amount = 40, HealthPercent = 60, EnemiesAround = 2 },
            new SandboxSetup { TargetUnitId = unit.Id, DamageTypeId = DamageTypes.Magical, Amount = 40 },
        }.Select(s => HitSandbox.Describe(HitSandbox.Run(lib, s))));
        var before = Hits(library);
        Assert.Contains("лечение +10", before, StringComparison.Ordinal);

        var items = new[] { skill, tile, unit }.Select(i => library.Find(i.Ref)!).ToArray();
        var bytes = ContentExporter.ExportPackage(library, items, "Огонь", string.Empty, "0.26.0", DateTimeOffset.UnixEpoch);
        var clean = ContentTestKit.Library(target.Path);
        var preview = ContentImporter.Inspect(clean, "fire.hxp", bytes);
        Assert.False(preview.Rejected, preview.Summary);
        Assert.Equal(5, preview.Entries.Count); // the type, the rule and the three chosen objects
        Assert.True(ContentImporter.Install(clean, preview).Ok);
        var imported = ContentTestKit.Library(target.Path);
        Assert.Equal(before, Hits(imported));
        var type = imported.Find(fire.Ref)!;
        Assert.Equal(new[] { DamageTypeData.Element }, ((DamageTypeData)type.Data).Classification);
        Assert.Contains(ReferenceCatalog.Options(imported, ContentTypeRegistry.DamageType, ReferenceFilter.Any, null), o => o.Value == fire.Id && o.Group == "Магический");
        Assert.Equal(fire.Id, ((UnitData)imported.Find(unit.Ref)!.Data).AttackDamageType);
    }

    [Fact]
    public void P6_08_an_unknown_effect_handler_is_an_error_and_never_shown_as_a_working_rule()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var json = "{\"schemaVersion\":1,\"id\":\"user.skill.aura\",\"kind\":\"skill\",\"name\":\"Аура\",\"revision\":1," +
                   "\"data\":{\"anchor\":\"caster\",\"rotationMode\":\"facing\",\"baseEffects\":[],\"cells\":[{\"q\":0,\"r\":-1," +
                   "\"effects\":[{\"type\":\"burn_aura\",\"amount\":5}]}]}}";
        var read = ContentSerializer.Read(json, library.Registry, ContentOrigin.User);
        var issues = read.Item is null ? read.Issues : read.Issues.Concat(library.Validate(read.Item)).ToList();
        Assert.Contains(issues, i => i.IsError && i.Message.Contains("burn_aura", StringComparison.Ordinal));
        if (read.Item is not null)
        {
            Assert.False(library.Save(read.Item).Ok); // not saved, so never offered or executed
        }
    }

    [Fact]
    public void P6_05_deleting_a_used_type_lists_its_users_and_either_replaces_references_or_archives()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var fire = Fire(library);
        var unit = library.Create(ContentTypeRegistry.Unit, "Поджигатель");
        ((UnitData)unit.Data).AttackDamageType = fire.Id;
        ((UnitData)unit.Data).Resistances[fire.Id] = 50;
        Assert.True(library.Save(unit).Ok);

        Assert.False(library.Delete(ContentTypeRegistry.DamageType, fire.Id).Result.Ok);
        Assert.Equal("юниты: Поджигатель", LibraryCommands.DependencyReport(library, library.Find(fire.Ref)!));
        var replacements = LibraryCommands.Replacements(library, library.Find(fire.Ref)!);
        Assert.Equal(DamageTypes.Magical, replacements[0].Id); // the own family first
        Assert.DoesNotContain(replacements, r => r.Id == DamageTypes.True); // another strategy is not a compatible replacement

        // Archive: references keep working, selectors stop offering it, a warning tells why.
        var history = new CommandHistory();
        history.Execute(LibraryCommands.Archive(library, library.Find(fire.Ref)!, true));
        Assert.True(library.Find(fire.Ref)!.Archived);
        Assert.DoesNotContain(ReferenceCatalog.Options(library, ContentTypeRegistry.DamageType, ReferenceFilter.Any, null), o => o.Value == fire.Id);
        Assert.Contains(library.Validate(library.Find(unit.Ref)!), i => !i.IsError && i.Message.Contains("в архиве", StringComparison.Ordinal));
        Assert.True(history.Undo());
        Assert.False(library.Find(fire.Ref)!.Archived);

        // Replace and delete: one undo step brings back the type and the unit's old references.
        history.Execute(LibraryCommands.ReplaceAndDelete(library, library.Find(fire.Ref)!, DamageTypes.Magical));
        Assert.Null(library.Find(fire.Ref));
        var moved = (UnitData)library.Find(unit.Ref)!.Data;
        Assert.Equal((DamageTypes.Magical, 50.0), (moved.AttackDamageType, moved.Resistances[DamageTypes.Magical]));
        Assert.True(history.Undo());
        Assert.NotNull(library.Find(fire.Ref));
        Assert.Equal(fire.Id, ((UnitData)library.Find(unit.Ref)!.Data).AttackDamageType);
    }

    [Fact]
    public void P6_07_every_change_of_the_library_is_announced_and_an_open_editor_keeps_its_unsaved_edits()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var unit = new UnitEditorModel(library, library.Create(ContentTypeRegistry.Unit, "Открытый"));
        Assert.Null(unit.TrySetField(UnitData.Fields.Single(f => f.Key == "maxHealth"), "150"));
        var changes = 0;
        library.Changed += () => changes++;
        var fire = Fire(library);
        Assert.Equal(1, changes);
        Assert.Contains(unit.ResistanceRows(), r => r.Key == fire.Id); // the open editor sees the new type
        Assert.Equal(150, unit.Unit.MaxHealth);                         // and its edit is still there
        Assert.True(library.Rename(ContentTypeRegistry.DamageType, fire.Id, "Пламя").Ok);
        Assert.Equal(2, changes);
        Assert.Equal("Пламя", unit.ResistanceRows().Single(r => r.Key == fire.Id).Label);
    }

    [Fact]
    public void Unknown_fields_of_a_newer_file_are_kept_on_save_and_never_executed()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var json = "{\"schemaVersion\":1,\"id\":\"user.damage.fire\",\"kind\":\"damage_type\",\"name\":\"Огонь\",\"revision\":1,\"workshopId\":\"w-42\"," +
                   "\"data\":{\"family\":\"core.damage.magical\",\"strategy\":\"resistance_affected\",\"tags\":[],\"color\":\"#EF6A3A\",\"icon\":\"\",\"auraRadius\":3}}";
        var read = ContentSerializer.Read(json, library.Registry, ContentOrigin.User);
        Assert.Contains(read.Issues, i => !i.IsError && i.Path == "data.auraRadius");
        var item = read.Item!;
        Assert.True(library.Save(item).Ok);
        var text = File.ReadAllText(library.User!.PathOf("damage_types", "user.damage.fire"));
        Assert.Contains("\"auraRadius\": 3", text, StringComparison.Ordinal);
        Assert.Contains("\"workshopId\": \"w-42\"", text, StringComparison.Ordinal);
        Assert.Equal(DamageStrategy.ResistanceAffected, HexTactics.Content.Runtime.RuntimeFactory.DamageTypeOf(item).Strategy); // nothing new executes
    }

    [Fact]
    public void Replacing_references_rolls_back_when_one_user_cannot_be_saved()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var fire = Fire(library);
        var a = library.Create(ContentTypeRegistry.Unit, "А");
        ((UnitData)a.Data).AttackDamageType = fire.Id;
        Assert.True(library.Save(a).Ok);
        Assert.False(library.ReplaceReferences(fire.Ref, "user.damage.nothing").Ok); // the replacement must exist
        Assert.False(library.ReplaceReferences(fire.Ref, fire.Id).Ok);
        Assert.Equal(fire.Id, ((UnitData)library.Find(a.Ref)!.Data).AttackDamageType);
    }
}
