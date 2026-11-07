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

/// <summary>Resistance sets as library data and their layering on units (spec part 5 §6.4, §9.1, §9.5).</summary>
public class ProfileContentTests
{
    [Fact]
    public void A_set_round_trips_and_refuses_values_outside_500_and_unknown_keys()
    {
        var library = ContentTestKit.Library(null);
        var item = library.Create(ContentTypeRegistry.ResistanceProfile, "Каменная кожа");
        var data = (ResistanceProfileData)item.Data;
        data.Parent = "core.profile.armored";
        data.Values[DamageTypes.Blunt] = -40;
        data.Values[DamageTypes.True] = 50;
        var issues = library.Validate(item);
        Assert.DoesNotContain(issues, i => i.IsError);
        Assert.Contains(issues, i => !i.IsError && i.Path == "data.values.core.damage.true"); // true damage ignores R
        Assert.Contains(new ContentRef("resistance_profile", "core.profile.armored"), item.References);

        var again = (ResistanceProfileData)ContentSerializer.Read(ContentSerializer.Write(item), library.Registry, ContentOrigin.User).Item!.Data;
        Assert.Equal(("core.profile.armored", -40.0), (again.Parent, again.Values[DamageTypes.Blunt]));

        data.Values[DamageTypes.Piercing] = 501;
        data.Values["user_damage_nothing"] = 5;
        issues = library.Validate(item);
        Assert.Contains(issues, i => i.IsError && i.Path == "data.values.core.damage.piercing");
        Assert.Contains(issues, i => i.IsError && i.Message.Contains("user_damage_nothing", StringComparison.Ordinal));
    }

    [Fact]
    public void A_schema_3_unit_keeps_its_numbers_as_own_values_and_gets_no_set()
    {
        using var dir = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(dir.Path, "content", "units"));
        var v22 = "{\"schemaVersion\":3,\"id\":\"unit_v22\",\"kind\":\"unit\",\"name\":\"Боец v0.22\",\"revision\":1,\"data\":{\"maxHealth\":100,\"resistances\":{\"core.damage.slashing\":40},\"defenseRules\":[],\"shields\":[]}}";
        File.WriteAllText(Path.Combine(dir.Path, "content", "units", "unit_v22.json"), v22);
        var library = ContentTestKit.Library(dir.Path);
        var unit = library.Find(ContentTypeRegistry.Unit, "unit_v22")!;
        Assert.Empty(((UnitData)unit.Data).ResistanceProfiles);
        Assert.Equal(40, RuntimeFactory.BuildUnit(unit, library).Stats.Resistances[DamageTypes.Slashing]);
        Assert.Contains("\"schemaVersion\": 4", File.ReadAllText(Path.Combine(dir.Path, "content", "units", "unit_v22.json")), StringComparison.Ordinal);
        Assert.Equal(v22, File.ReadAllText(Directory.GetFiles(Path.Combine(dir.Path, "backups"), "unit_v22.json", SearchOption.AllDirectories).Single()));
    }

    [Fact]
    public void A_unit_package_carries_its_sets_with_their_parents_and_fights_the_same_after_import()
    {
        using var source = new TempDirectory();
        using var target = new TempDirectory();
        var library = ContentTestKit.Library(source.Path);
        var basis = library.Create(ContentTypeRegistry.ResistanceProfile, "Основа");
        ((ResistanceProfileData)basis.Data).Values[DamageTypes.Physical] = 40;
        Assert.True(library.Save(basis).Ok);
        var heavy = library.Create(ContentTypeRegistry.ResistanceProfile, "Тяжёлый");
        ((ResistanceProfileData)heavy.Data).Parent = basis.Id;
        ((ResistanceProfileData)heavy.Data).Values[DamageTypes.Piercing] = -100;
        Assert.True(library.Save(heavy).Ok);
        var unit = library.Create(ContentTypeRegistry.Unit, "Латник");
        ((UnitData)unit.Data).ResistanceProfiles.Add(heavy.Id);
        ((UnitData)unit.Data).ResistanceProfiles.Add("core.profile.weak_piercing");
        ((UnitData)unit.Data).Resistances[DamageTypes.Blunt] = 10;
        Assert.True(library.Save(unit).Ok);

        var deps = ContentExporter.Dependencies(library, new[] { library.Find(unit.Ref)! }).Select(i => i.Id).ToList();
        Assert.Contains(heavy.Id, deps);
        Assert.Contains(basis.Id, deps);
        Assert.Contains(new ContentRef("resistance_profile", "core.profile.weak_piercing"), ContentExporter.BuiltInDependencies(library, new[] { library.Find(unit.Ref)! }));

        var bytes = ContentExporter.ExportPackage(library, new[] { library.Find(unit.Ref)! }, "Латник", string.Empty, "0.23.0", DateTimeOffset.UnixEpoch);
        var clean = ContentTestKit.Library(target.Path);
        var preview = ContentImporter.Inspect(clean, "plate.hxp", bytes);
        Assert.False(preview.Rejected, preview.Summary);
        Assert.True(ContentImporter.Install(clean, preview).Ok);

        ResistanceSet Of(ContentLibrary lib) => RuntimeFactory.BuildUnit(lib.Find(unit.Ref)!, lib).Stats.Resistances;
        var mine = Of(library);
        var theirs = Of(ContentTestKit.Library(target.Path));
        Assert.Equal(mine.Values.OrderBy(v => v.Key), theirs.Values.OrderBy(v => v.Key));
        Assert.Equal((40.0, -50.0, 10.0), (mine[DamageTypes.Physical], mine[DamageTypes.Piercing], mine[DamageTypes.Blunt])); // the later set wins per key
    }
}
