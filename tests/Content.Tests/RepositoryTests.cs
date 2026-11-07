using System.IO;
using System.Linq;
using HexTactics.Content.Kinds;
using HexTactics.Content.Library;
using HexTactics.Content.Registry;
using HexTactics.Content.Storage;
using HexTactics.Tests.Support;
using Xunit;

namespace HexTactics.Content.Tests;

/// <summary>Saving, reloading after a restart, atomic writes, drafts and deletion (spec part 4 §8.1, §12.3 p.24).</summary>
public class RepositoryTests
{
    [Fact]
    public void A_saved_object_is_found_by_a_new_library_after_a_restart()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var unit = library.Create(ContentTypeRegistry.Unit, "Тяжёлый");
        ((UnitData)unit.Data).MaxHealth = 175;
        ((UnitData)unit.Data).MoveSpeed = 1.5;
        ((UnitData)unit.Data).Skills.Add("core.skill.linear_strike");
        var saved = library.Save(unit);
        Assert.True(saved.Ok, saved.Message);

        var path = Path.Combine(dir.Path, "content", "units", unit.Id + ".json");
        Assert.True(File.Exists(path)); // file name = stable id, not the user name
        var restarted = ContentTestKit.Library(dir.Path);
        var again = restarted.Find(ContentTypeRegistry.Unit, unit.Id)!;
        Assert.Equal("Тяжёлый", again.Name);
        Assert.Equal(ContentOrigin.User, again.Origin);
        Assert.Equal(175, ((UnitData)again.Data).MaxHealth);
        Assert.Equal(1.5, ((UnitData)again.Data).MoveSpeed);
        Assert.Equal(1, again.Revision);
        Assert.NotNull(again.Modified);
    }

    [Fact]
    public void Invalid_objects_and_built_in_ids_are_never_written()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var unit = library.Create(ContentTypeRegistry.Unit, "Плохой");
        ((UnitData)unit.Data).MaxHealth = -10;
        Assert.False(library.Save(unit).Ok);
        Assert.Empty(Directory.GetFiles(Path.Combine(dir.Path, "content"), "*", SearchOption.AllDirectories));

        var builtIn = library.Find(ContentTypeRegistry.Unit, "core.unit.operative")!;
        var result = library.Save(builtIn.Clone());
        Assert.False(result.Ok);
        Assert.Contains("встроенный", result.Message);
    }

    [Fact]
    public void A_failed_atomic_write_leaves_the_previous_file_intact()
    {
        using var dir = new TempDirectory();
        var repository = new ContentRepository(dir.Path);
        repository.SaveAtomic("units", "unit_abc123", "first");
        Assert.Throws<ContentSaveException>(() => repository.SaveAtomic("units", "unit_abc123", "second", _ => false));
        Assert.Equal("first", File.ReadAllText(repository.PathOf("units", "unit_abc123")));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(repository.PathOf("units", "unit_abc123"))!, "*.tmp"));
    }

    [Fact]
    public void Rename_keeps_the_id_and_bumps_the_revision_duplicate_gets_a_new_id()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var copy = library.Duplicate(library.Find(ContentTypeRegistry.Skill, "core.skill.linear_strike")!).Item!;
        Assert.NotEqual("core.skill.linear_strike", copy.Id);
        Assert.Equal("Линейный удар (копия)", copy.Name);
        Assert.Equal(1, copy.Revision);

        var renamed = library.Rename(ContentTypeRegistry.Skill, copy.Id, "Мой удар").Item!;
        Assert.Equal(copy.Id, renamed.Id);
        Assert.Equal("Мой удар", renamed.Name);
        Assert.Equal(2, renamed.Revision);
    }

    [Fact]
    public void Used_objects_cannot_be_deleted_silently_deleted_ones_can_be_restored()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var skill = library.Duplicate(library.Find(ContentTypeRegistry.Skill, "core.skill.linear_strike")!, "Удар").Item!;
        var unit = library.Create(ContentTypeRegistry.Unit, "Боец");
        ((UnitData)unit.Data).Skills.Add(skill.Id);
        Assert.True(library.Save(unit).Ok);

        var (refused, none) = library.Delete(ContentTypeRegistry.Skill, skill.Id);
        Assert.False(refused.Ok);
        Assert.Null(none);
        Assert.Contains("Боец", refused.Message);
        Assert.Equal(new[] { unit.Id }, library.UsedBy(skill.Ref).Select(i => i.Id));

        var (ok, deleted) = library.Delete(ContentTypeRegistry.Unit, unit.Id);
        Assert.True(ok.Ok);
        Assert.Null(library.Find(ContentTypeRegistry.Unit, unit.Id));
        Assert.False(File.Exists(Path.Combine(dir.Path, "content", "units", unit.Id + ".json")));
        Assert.True(File.Exists(deleted!.TrashPath));

        Assert.True(library.Restore(deleted).Ok);
        Assert.Equal("Боец", library.Find(ContentTypeRegistry.Unit, unit.Id)!.Name);
        Assert.False(library.Delete(ContentTypeRegistry.Unit, "core.unit.operative").Result.Ok);
    }

    [Fact]
    public void Broken_user_files_are_reported_and_left_untouched()
    {
        using var dir = new TempDirectory();
        var units = Path.Combine(dir.Path, "content", "units");
        Directory.CreateDirectory(units);
        File.WriteAllText(Path.Combine(units, "unit_broken1.json"), "{ broken");
        File.WriteAllText(Path.Combine(units, "core.unit.fake.json"), File.ReadAllText(Path.Combine(ContentTestKit.BuiltInRoot, "units", "core.unit.operative.json")));
        var library = ContentTestKit.Library(dir.Path);
        Assert.Equal(2, library.Problems.Count);
        Assert.Contains(library.Problems, p => p.Issues.Any(i => i.Message.Contains("повреждённый JSON")));
        Assert.Contains(library.Problems, p => p.Issues.Any(i => i.Message.Contains("зарезервирован")));
        Assert.Equal("{ broken", File.ReadAllText(Path.Combine(units, "unit_broken1.json")));
    }

    [Fact]
    public void Drafts_are_kept_apart_from_saved_objects()
    {
        using var dir = new TempDirectory();
        var repository = new ContentRepository(dir.Path);
        repository.SaveAtomic("maps", "map_aaaa1111", "saved");
        repository.SaveDraft("maps", "map_aaaa1111", "draft");
        Assert.Equal("saved", File.ReadAllText(repository.PathOf("maps", "map_aaaa1111")));
        Assert.Equal("draft", repository.ReadDraft("maps", "map_aaaa1111"));
        Assert.Equal("map_aaaa1111", Assert.Single(repository.ListDrafts()).Id);
        repository.DeleteDraft("maps", "map_aaaa1111");
        Assert.Empty(repository.ListDrafts());
        Assert.Null(repository.ReadDraft("maps", "map_aaaa1111"));
    }

    [Fact]
    public void Backups_copy_the_saved_file_and_the_log_records_operations()
    {
        using var dir = new TempDirectory();
        var repository = new ContentRepository(dir.Path);
        repository.SaveAtomic("tiles", "tile_aaaa1111", "v1");
        var backup = repository.Backup("tiles", "tile_aaaa1111", "test")!;
        Assert.Equal("v1", File.ReadAllText(backup));
        Assert.Contains("backup tiles/tile_aaaa1111", File.ReadAllText(Path.Combine(dir.Path, "logs", "content.log")));
    }
}
