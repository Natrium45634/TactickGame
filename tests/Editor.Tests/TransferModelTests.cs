using System;
using System.IO;
using System.Linq;
using HexTactics.Content.Kinds;
using HexTactics.Content.Library;
using HexTactics.Content.Registry;
using HexTactics.Content.Transfer;
using HexTactics.Editor.Transfer;
using HexTactics.Tests.Support;
using Xunit;

namespace HexTactics.Editor.Tests;

/// <summary>The models behind the export and import dialogs (spec part 4 §8.3).</summary>
public class TransferModelTests
{
    private static (ContentItem Skill, ContentItem Unit) UnitWithSkill(ContentLibrary library)
    {
        var skill = library.Create(ContentTypeRegistry.Skill, "Выпад");
        ((SkillData)skill.Data).Cells.Add(new SkillCellData { Q = 0, R = -1 });
        ((SkillData)skill.Data).BaseEffects.Add(library.Registry.Effects.Find("damage")!.CreateDefault());
        Assert.True(library.Save(skill).Ok);
        var unit = library.Create(ContentTypeRegistry.Unit, "Фехтовальщик");
        ((UnitData)unit.Data).Skills.Add(skill.Id);
        Assert.True(library.Save(unit).Ok);
        return (library.Find(skill.Ref)!, library.Find(unit.Ref)!);
    }

    [Fact]
    public void Dependencies_of_a_chosen_object_are_included_and_cannot_be_unticked()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var (skill, unit) = UnitWithSkill(library);
        var plan = new ExportPlan(library, unit);
        Assert.False(plan.Single.SelfContained);
        Assert.Equal(new[] { skill.Ref, unit.Ref }, plan.PackageItems.Select(i => i.Ref));
        Assert.True(plan.IsRequired(skill.Ref));
        Assert.NotNull(plan.Toggle(skill.Ref, false));
        Assert.True(plan.IsIncluded(skill.Ref));
        Assert.Null(plan.Toggle(unit.Ref, false));
        Assert.Empty(plan.PackageItems);
        Assert.Null(plan.Toggle(skill.Ref, true));
        Assert.Equal(new[] { skill.Ref }, plan.PackageItems.Select(i => i.Ref));
    }

    [Fact]
    public void Exported_files_land_in_the_export_folder_and_a_bare_name_imports_from_there()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var (_, unit) = UnitWithSkill(library);
        var plan = new ExportPlan(library, unit) { PackageName = "Фехтование" };
        var package = plan.WritePackage("0.20.0", DateTimeOffset.UnixEpoch.AddYears(57));
        Assert.Equal(Path.Combine(dir.Path, "exports", "Фехтование.hxp"), package);
        var single = plan.WriteObject();
        Assert.StartsWith(Path.Combine(dir.Path, "exports"), single, StringComparison.Ordinal);

        var session = new ImportSession(library);
        var preview = session.Inspect("Фехтование.hxp");
        Assert.False(preview.Rejected, preview.Summary);
        Assert.All(preview.Entries, e => Assert.Equal(ImportStatus.Same, e.Status));
        Assert.Contains("уже есть, без изменений", ImportSession.Describe(preview.Entries[0]), StringComparison.Ordinal);
        Assert.NotNull(session.SetResolution(0, ImportResolution.Replace)); // not offered for an identical object
        Assert.Null(session.SetResolution(1, ImportResolution.Copy));
        var result = session.Install();
        Assert.True(result.Ok, result.Message);
        Assert.Equal(2, library.Items(ContentTypeRegistry.Unit).Count(u => !u.IsBuiltIn));

        Assert.True(session.Inspect("нет-такого.hxp").Rejected);
    }
}
