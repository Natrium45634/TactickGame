using System;
using System.Collections.Generic;
using System.Globalization;
using HexTactics.Content.Library;
using HexTactics.Content.Model;
using HexTactics.Content.Registry;
using HexTactics.Editor.History;

namespace HexTactics.Editor.Library;

public enum LibrarySort
{
    Name = 0,
    Modified = 1,
    Id = 2,
}

/// <summary>One line of a library list (spec part 4 §2.2: name, type, description, id, date, version, errors).</summary>
public sealed record LibraryRow(
    ContentItem Item,
    string Name,
    string Id,
    string Summary,
    string Description,
    string Modified,
    int Revision,
    int SchemaVersion,
    bool IsBuiltIn,
    int Errors,
    int Warnings,
    int UsedByCount);

/// <summary>A category of the developer catalog with its object count.</summary>
public sealed record CatalogEntry(ContentKind Kind, int Count, int WithErrors);

/// <summary>
/// The list part of the developer catalog: one category, a search text and a sort order → the rows to show. Search looks
/// at the name, id, description and summary, ignoring case.
/// </summary>
public sealed class LibraryBrowser
{
    public LibraryBrowser(ContentLibrary library)
    {
        Library = library;
        Kind = library.Registry.Visible.Count > 0 ? library.Registry.Visible[0].Id : string.Empty;
    }

    public ContentLibrary Library { get; }

    public string Kind { get; set; }

    public string Search { get; set; } = string.Empty;

    public LibrarySort Sort { get; set; } = LibrarySort.Name;

    public bool Descending { get; set; }

    /// <summary>The categories of the catalog (registered and available), with counts.</summary>
    public IReadOnlyList<CatalogEntry> Categories()
    {
        var list = new List<CatalogEntry>();
        foreach (var kind in Library.Registry.Visible)
        {
            var items = Library.Items(kind.Id);
            var errors = 0;
            foreach (var i in items)
            {
                if (i.HasErrors)
                {
                    errors++;
                }
            }

            list.Add(new CatalogEntry(kind, items.Count, errors));
        }

        return list;
    }

    public IReadOnlyList<LibraryRow> Rows()
    {
        var rows = new List<LibraryRow>();
        var search = Search.Trim();
        foreach (var item in Library.Items(Kind))
        {
            var summary = item.Data.Summary();
            if (search.Length > 0 && !Matches(item, summary, search))
            {
                continue;
            }

            var errors = 0;
            var warnings = 0;
            foreach (var issue in item.Issues)
            {
                if (issue.IsError)
                {
                    errors++;
                }
                else
                {
                    warnings++;
                }
            }

            rows.Add(new LibraryRow(
                item,
                item.Name,
                item.Id,
                summary,
                item.Description,
                item.Modified?.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture) ?? "—",
                item.Revision,
                item.SchemaVersion,
                item.IsBuiltIn,
                errors,
                warnings,
                Library.UsedBy(item.Ref).Count));
        }

        Comparison<LibraryRow> compare = Sort switch
        {
            LibrarySort.Modified => (a, b) => Nullable.Compare(a.Item.Modified, b.Item.Modified),
            LibrarySort.Id => (a, b) => string.CompareOrdinal(a.Id, b.Id),
            _ => (a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase),
        };
        rows.Sort((a, b) =>
        {
            var c = compare(a, b);
            if (c == 0)
            {
                c = string.CompareOrdinal(a.Id, b.Id);
            }

            return Descending ? -c : c;
        });
        return rows;
    }

    private static bool Matches(ContentItem item, string summary, string search) =>
        item.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)
        || item.Id.Contains(search, StringComparison.OrdinalIgnoreCase)
        || item.Description.Contains(search, StringComparison.CurrentCultureIgnoreCase)
        || summary.Contains(search, StringComparison.CurrentCultureIgnoreCase);
}

/// <summary>
/// Undoable library operations of the catalog (spec part 4 §1 «отмена импорта и удаления»): each one changes the user
/// content folder and can be reverted with <see cref="CommandHistory"/>.
/// </summary>
public static class LibraryCommands
{
    /// <summary>Saves a copy; Undo deletes the copy again.</summary>
    public static IEditorCommand Duplicate(ContentLibrary library, ContentItem source, string name, Action<ContentItem>? created = null)
    {
        ContentItem? copy = null;
        DeletedContent? removed = null;
        return new DelegateCommand(
            $"Дублировать «{source.Name}»",
            () =>
            {
                if (removed is not null)
                {
                    Check(library.Restore(removed));
                    removed = null;
                    return;
                }

                var result = library.Duplicate(source, name);
                Check(result);
                copy = result.Item;
                created?.Invoke(copy!);
            },
            () =>
            {
                var (result, deleted) = library.Delete(copy!.Kind.Id, copy.Id);
                Check(result);
                removed = deleted;
            });
    }

    /// <summary>Changes the name (the id stays); Undo restores the old name.</summary>
    public static IEditorCommand Rename(ContentLibrary library, ContentItem item, string newName)
    {
        var oldName = item.Name;
        var kind = item.Kind.Id;
        var id = item.Id;
        return new DelegateCommand(
            $"Переименовать «{oldName}» → «{newName}»",
            () => Check(library.Rename(kind, id, newName)),
            () => Check(library.Rename(kind, id, oldName)));
    }

    /// <summary>Deletes (refused if other objects depend on it); Undo restores the file from the trash.</summary>
    public static IEditorCommand Delete(ContentLibrary library, ContentItem item)
    {
        DeletedContent? deleted = null;
        var kind = item.Kind.Id;
        var id = item.Id;
        return new DelegateCommand(
            $"Удалить «{item.Name}»",
            () =>
            {
                var (result, d) = library.Delete(kind, id);
                Check(result);
                deleted = d;
            },
            () => Check(library.Restore(deleted!)));
    }

    private static void Check(ContentOperationResult result)
    {
        if (!result.Ok)
        {
            throw new EditorCommandException(result.Message);
        }
    }
}
