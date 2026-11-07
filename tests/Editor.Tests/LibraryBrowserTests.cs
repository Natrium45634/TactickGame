using System.IO;
using System.Linq;
using HexTactics.Content.Kinds;
using HexTactics.Content.Registry;
using HexTactics.Editor.History;
using HexTactics.Editor.Library;
using HexTactics.Tests.Support;
using Xunit;

namespace HexTactics.Editor.Tests;

/// <summary>The developer catalog: categories, lists, search, sort and undoable library operations (spec part 4 §2.2).</summary>
public class LibraryBrowserTests
{
    [Fact]
    public void Categories_come_from_the_registry_including_newly_registered_ones()
    {
        var registry = ContentTypeRegistry.CreateDefault();
        registry.Register(new ContentKind
        {
            Id = "status", Title = "Статусы", Singular = "статус", Folder = "statuses", Order = 50,
            Read = (o, i) => TileData.Read(o, i), CreateDefault = () => new TileData(),
        });
        var browser = new LibraryBrowser(ContentTestKit.Library(null, registry));
        var categories = browser.Categories();
        Assert.Equal(new[] { "Карты", "Юниты", "Навыки", "Гексы", "Статусы", "Правила защиты" }, categories.Select(c => c.Kind.Title));
        Assert.Equal(new[] { 1, 1, 1, 7, 0, 1 }, categories.Select(c => c.Count));
    }

    [Fact]
    public void Rows_show_name_id_summary_version_and_are_searched_and_sorted()
    {
        var browser = new LibraryBrowser(ContentTestKit.Library(null)) { Kind = ContentTypeRegistry.Tile };
        var rows = browser.Rows();
        Assert.Equal(7, rows.Count);
        Assert.All(rows, r => Assert.True(r.IsBuiltIn && r.SchemaVersion == 2 && r.Revision == 1 && r.Errors == 0));
        Assert.Equal(rows.Select(r => r.Name).OrderBy(n => n, System.StringComparer.CurrentCultureIgnoreCase), rows.Select(r => r.Name));

        browser.Search = "БАЗА";
        Assert.Equal(new[] { "База врага", "База игрока" }, browser.Rows().Select(r => r.Name));
        browser.Search = "core.tile.high";
        Assert.Equal("Высокий гекс", Assert.Single(browser.Rows()).Name);
        browser.Search = "препятствие";
        Assert.Equal("core.tile.obstacle", Assert.Single(browser.Rows()).Id); // name and category of one type

        browser.Search = string.Empty;
        browser.Sort = LibrarySort.Id;
        browser.Descending = true;
        Assert.Equal("core.tile.spawn", browser.Rows()[0].Id);
        var usedByMap = browser.Rows().Single(r => r.Id == "core.tile.ground").UsedByCount;
        Assert.Equal(1, usedByMap);
    }

    [Fact]
    public void Duplicate_rename_and_delete_are_undoable_and_redoable()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var history = new CommandHistory();
        var source = library.Find(ContentTypeRegistry.Tile, "core.tile.ground")!;
        HexTactics.Content.Library.ContentItem? copy = null;
        history.Execute(LibraryCommands.Duplicate(library, source, "Мой гекс", c => copy = c));
        Assert.Equal(8, library.Items(ContentTypeRegistry.Tile).Count);
        var file = Path.Combine(dir.Path, "content", "tiles", copy!.Id + ".json");
        Assert.True(File.Exists(file));

        history.Execute(LibraryCommands.Rename(library, library.Find(ContentTypeRegistry.Tile, copy.Id)!, "Кислотный гекс"));
        Assert.Equal("Кислотный гекс", library.Find(ContentTypeRegistry.Tile, copy.Id)!.Name);
        history.Undo();
        Assert.Equal("Мой гекс", library.Find(ContentTypeRegistry.Tile, copy.Id)!.Name);
        history.Redo();

        history.Execute(LibraryCommands.Delete(library, library.Find(ContentTypeRegistry.Tile, copy.Id)!));
        Assert.Null(library.Find(ContentTypeRegistry.Tile, copy.Id));
        Assert.False(File.Exists(file));
        history.Undo();
        Assert.Equal("Кислотный гекс", library.Find(ContentTypeRegistry.Tile, copy.Id)!.Name);

        history.Undo(); // rename
        history.Undo(); // duplicate → the copy is removed again
        Assert.Null(library.Find(ContentTypeRegistry.Tile, copy.Id));
        Assert.Equal(7, library.Items(ContentTypeRegistry.Tile).Count);
        history.Redo(); // duplicate again: the same object comes back
        Assert.Equal("Мой гекс", library.Find(ContentTypeRegistry.Tile, copy.Id)!.Name);
    }

    [Fact]
    public void Deleting_an_object_in_use_is_refused_and_the_history_is_unchanged()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var history = new CommandHistory();
        Assert.Throws<EditorCommandException>(() => history.Execute(LibraryCommands.Delete(library, library.Find(ContentTypeRegistry.Tile, "core.tile.ground")!)));
        Assert.False(history.CanUndo);
        Assert.NotNull(library.Find(ContentTypeRegistry.Tile, "core.tile.ground"));
    }
}
