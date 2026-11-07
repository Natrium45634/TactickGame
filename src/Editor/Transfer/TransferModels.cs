using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HexTactics.Content.Library;
using HexTactics.Content.Model;
using HexTactics.Content.Transfer;

namespace HexTactics.Editor.Transfer;

/// <summary>
/// What the export dialog shows (spec part 4 §8.3): the selected object as a single <c>.json</c> (with a warning if it is
/// not self-contained) or a package <c>.hxp</c> of chosen user objects — every dependency of a chosen object is included
/// automatically and cannot be unticked while something needs it.
/// </summary>
public sealed class ExportPlan
{
    private readonly ContentLibrary _library;
    private readonly List<ContentRef> _roots = new(); // in the order chosen: the package lists objects deterministically

    public ExportPlan(ContentLibrary library, ContentItem selected)
    {
        _library = library;
        Selected = selected;
        Single = ContentExporter.ExportObject(library, selected);
        if (!selected.IsBuiltIn)
        {
            _roots.Add(selected.Ref);
        }

        PackageName = selected.Name;
    }

    public ContentItem Selected { get; }

    public ObjectExport Single { get; }

    public string PackageName { get; set; }

    /// <summary>User objects that can go into a package, in catalog order (category, then name).</summary>
    public IReadOnlyList<ContentItem> Candidates =>
        _library.Registry.Visible.SelectMany(k => _library.Items(k.Id)).Where(i => !i.IsBuiltIn).ToList();

    /// <summary>The objects the package will contain (chosen ones and their dependencies, dependencies first).</summary>
    public IReadOnlyList<ContentItem> PackageItems =>
        ContentExporter.Dependencies(_library, _roots.Select(r => _library.Find(r)).OfType<ContentItem>());

    public bool IsIncluded(ContentRef item) => PackageItems.Any(i => i.Ref == item);

    /// <summary>Included only because a chosen object needs it (cannot be unticked).</summary>
    public bool IsRequired(ContentRef item) =>
        ContentExporter.Dependencies(_library, _roots.Where(r => r != item).Select(r => _library.Find(r)).OfType<ContentItem>()).Any(i => i.Ref == item);

    /// <summary>Ticks/unticks an object; returns a refusal if a chosen object still needs it.</summary>
    public string? Toggle(ContentRef item, bool on)
    {
        if (on)
        {
            if (!_roots.Contains(item))
            {
                _roots.Add(item);
            }

            return null;
        }

        if (IsRequired(item))
        {
            var needers = _roots.Select(r => _library.Find(r)).OfType<ContentItem>()
                .Where(r => r.Ref != item && ContentExporter.Dependencies(_library, new[] { r }).Any(d => d.Ref == item))
                .Select(r => $"«{r.Name}»");
            return $"«{_library.Find(item)?.Name}» нужен для {string.Join(", ", needers)} — он останется в пакете";
        }

        _roots.Remove(item);
        return null;
    }

    /// <summary>Writes the single object into the export folder; returns the file path.</summary>
    public string WriteObject()
    {
        var user = _library.User ?? throw new InvalidOperationException("No user storage.");
        var name = ContentExporter.SafeFileName(Selected.Name) + "_" + Selected.Id + ContentExporter.ObjectExtension;
        var path = user.WriteExport(name, new UTF8Encoding(false).GetBytes(Single.Json));
        user.Log($"exported {Selected.Ref} → {path}");
        return path;
    }

    /// <summary>Writes the package into the export folder; returns the file path.</summary>
    public string WritePackage(string gameVersion, DateTimeOffset created)
    {
        var user = _library.User ?? throw new InvalidOperationException("No user storage.");
        var items = PackageItems;
        if (items.Count == 0)
        {
            throw new InvalidOperationException("Nothing to export.");
        }

        var roots = _roots.Select(r => _library.Find(r)).OfType<ContentItem>().ToList();
        var bytes = ContentExporter.ExportPackage(_library, roots, PackageName.Trim().Length == 0 ? Selected.Name : PackageName.Trim(), string.Join(", ", items.Select(i => i.Name)), gameVersion, created);
        var path = user.WriteExport(ContentExporter.SafeFileName(PackageName.Trim().Length == 0 ? Selected.Name : PackageName) + ContentExporter.PackageExtension, bytes);
        user.Log($"exported package {path}: {string.Join(", ", items.Select(i => i.Ref))}");
        return path;
    }
}

/// <summary>The import dialog's state: a file, its preview, the chosen action per object, the result.</summary>
public sealed class ImportSession
{
    private readonly ContentLibrary _library;

    public ImportSession(ContentLibrary library) => _library = library;

    public ImportPreview? Preview { get; private set; }

    /// <summary>
    /// Opens a file for preview. A bare file name is looked up in the export folder of this installation, otherwise the
    /// path is used as given.
    /// </summary>
    public ImportPreview Inspect(string path)
    {
        var trimmed = path.Trim().Trim('"');
        if (_library.User is { } user && trimmed.Length > 0 && Path.GetFileName(trimmed) == trimmed)
        {
            trimmed = Path.Combine(user.ExportsDir, trimmed);
        }

        Preview = ContentImporter.InspectFile(_library, trimmed);
        return Preview;
    }

    public string? SetResolution(int index, ImportResolution resolution)
    {
        if (Preview is null || index < 0 || index >= Preview.Entries.Count)
        {
            return "нет такого объекта";
        }

        var entry = Preview.Entries[index];
        if (!entry.Options.Contains(resolution))
        {
            return $"«{entry.Item.Name}»: «{ContentImporter.Word(resolution)}» недопустимо";
        }

        entry.Resolution = resolution;
        return null;
    }

    public ImportResult Install() =>
        Preview is null ? new ImportResult(false, Array.Empty<ContentItem>(), new[] { ContentIssue.Error(string.Empty, "файл не выбран") }) : ContentImporter.Install(_library, Preview);

    /// <summary>One line per object: «навык «Тройной удар» · skill_1a2b3c4d · ревизия 2 · конфликт ID».</summary>
    public static string Describe(ImportEntry e)
    {
        var line = $"{e.Item.Kind.Singular} «{e.Item.Name}» · {e.Item.Id} · ревизия {e.Item.Revision} · {ContentImporter.Word(e.Status)}";
        if (e.MigratedFrom is { } from)
        {
            line += $" · схема {from} → {e.Item.Kind.SchemaVersion}";
        }

        if (e.Status == ImportStatus.Conflict && e.Existing is { } existing)
        {
            line += existing.IsBuiltIn ? " (встроенный объект игры)" : $" (у вас: «{existing.Name}», ревизия {existing.Revision})";
        }

        return line;
    }
}
