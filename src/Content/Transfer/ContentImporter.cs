using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HexTactics.Content.Library;
using HexTactics.Content.Model;
using HexTactics.Content.Storage;

namespace HexTactics.Content.Transfer;

/// <summary>What an imported object is compared to the library.</summary>
public enum ImportStatus
{
    /// <summary>No object with this <c>kind + id</c> yet.</summary>
    New = 0,

    /// <summary>The library already has exactly this object (same data, name and description).</summary>
    Same = 1,

    /// <summary>The library has a different object with the same <c>kind + id</c> (spec part 4 §8.3).</summary>
    Conflict = 2,

    /// <summary>Errors (values, unknown effect type, missing dependency…): cannot be installed.</summary>
    Invalid = 3,
}

/// <summary>What to do with one imported object (spec part 4 §8.3: never replace user objects silently).</summary>
public enum ImportResolution
{
    Install = 0,
    Skip = 1,

    /// <summary>«Заменить после резервной копии».</summary>
    Replace = 2,

    /// <summary>«Импортировать копией с новым ID»; links inside the package are re-bound to the copy.</summary>
    Copy = 3,
}

/// <summary>One object of an import preview.</summary>
public sealed class ImportEntry
{
    internal ImportEntry(ContentItem item, string source, int? migratedFrom)
    {
        Item = item;
        Source = source;
        MigratedFrom = migratedFrom;
    }

    /// <summary>The object as read (and migrated to the current schema) from the file.</summary>
    public ContentItem Item { get; }

    /// <summary>The file name or the path inside the package.</summary>
    public string Source { get; }

    public int? MigratedFrom { get; }

    public ImportStatus Status { get; internal set; }

    /// <summary>The library object with the same <c>kind + id</c>, if any.</summary>
    public ContentItem? Existing { get; internal set; }

    public List<ContentIssue> Issues { get; } = new();

    public ImportResolution Resolution { get; set; }

    /// <summary>The choices the user has for this object.</summary>
    public IReadOnlyList<ImportResolution> Options => Status switch
    {
        ImportStatus.New => new[] { ImportResolution.Install, ImportResolution.Skip },
        ImportStatus.Same => new[] { ImportResolution.Skip, ImportResolution.Copy },
        ImportStatus.Conflict when Existing is { IsBuiltIn: true } => new[] { ImportResolution.Skip, ImportResolution.Copy },
        ImportStatus.Conflict => new[] { ImportResolution.Skip, ImportResolution.Replace, ImportResolution.Copy },
        _ => new[] { ImportResolution.Skip },
    };

    public bool Writes => Status != ImportStatus.Invalid && Resolution is ImportResolution.Install or ImportResolution.Replace or ImportResolution.Copy;
}

/// <summary>
/// The preview shown before an import (spec part 4 §8.3): objects, versions, dependencies, how many are new / the same /
/// conflicting / invalid, and file-level problems. With file-level errors nothing can be installed.
/// </summary>
public sealed class ImportPreview
{
    internal ImportPreview(string fileName, byte[] bytes, bool isPackage)
    {
        FileName = fileName;
        Bytes = bytes;
        IsPackage = isPackage;
    }

    public string FileName { get; }

    public bool IsPackage { get; }

    public PackageManifest? Manifest { get; internal set; }

    /// <summary>Problems of the file itself (damaged archive, dangerous path, checksum, size…).</summary>
    public List<ContentIssue> Problems { get; } = new();

    public List<ImportEntry> Entries { get; } = new();

    internal byte[] Bytes { get; }

    public bool Rejected => Problems.Any(p => p.IsError);

    public bool CanInstall => !Rejected && Entries.Any(e => e.Writes);

    public int Count(ImportStatus status) => Entries.Count(e => e.Status == status);

    /// <summary>«4 объекта: новых 2, без изменений 1, конфликтов 1, с ошибками 0».</summary>
    public string Summary =>
        Rejected
            ? $"файл отклонён: {string.Join("; ", Problems.Where(p => p.IsError).Select(p => p.Message))}"
            : $"{Entries.Count} объект(ов): новых {Count(ImportStatus.New)}, без изменений {Count(ImportStatus.Same)}, конфликтов {Count(ImportStatus.Conflict)}, с ошибками {Count(ImportStatus.Invalid)}";
}

/// <summary>Result of an installation.</summary>
public sealed record ImportResult(bool Ok, IReadOnlyList<ContentItem> Installed, IReadOnlyList<ContentIssue> Issues)
{
    public string Message => string.Join("; ", Issues.Select(i => i.Message));
}

/// <summary>
/// Import of content (spec part 4 §8.3): a single object (<c>.json</c>) or a package (<c>.hxp</c>, ZIP). Everything is
/// checked before anything is written — size limits, archive paths, allowed files (only JSON objects: no executables,
/// libraries, scripts or links), the manifest and checksums, the schema version, values, effect types and links. Objects
/// whose <c>kind + id</c> exist are offered «Пропустить / Заменить после резервной копии / Импортировать копией с новым ID»
/// and are skipped by default. The installation is a transaction: either every chosen object is saved, or the storage is
/// left as it was.
/// </summary>
public static class ContentImporter
{
    private const int MaxPathLength = 200;

    public static ImportPreview InspectFile(ContentLibrary library, string path)
    {
        var name = Path.GetFileName(path);
        var isPackage = IsPackageName(name);
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            var missing = new ImportPreview(name, Array.Empty<byte>(), isPackage);
            missing.Problems.Add(ContentIssue.Error(string.Empty, $"файл «{path}» не найден"));
            return missing;
        }

        var limit = isPackage ? library.Limits.MaxPackageBytes : library.Limits.MaxFileBytes;
        if (info.Length > limit)
        {
            var big = new ImportPreview(name, Array.Empty<byte>(), isPackage);
            big.Problems.Add(ContentIssue.Error(string.Empty, $"файл слишком большой: {info.Length / 1024} КБ (предел {limit / 1024} КБ)"));
            return big;
        }

        return Inspect(library, name, File.ReadAllBytes(path));
    }

    public static ImportPreview Inspect(ContentLibrary library, string fileName, byte[] bytes)
    {
        var isPackage = IsPackageName(fileName) || (bytes.Length >= 4 && bytes[0] == 'P' && bytes[1] == 'K' && bytes[2] == 3 && bytes[3] == 4);
        var preview = new ImportPreview(fileName, bytes, isPackage);
        var limit = isPackage ? library.Limits.MaxPackageBytes : library.Limits.MaxFileBytes;
        if (bytes.LongLength > limit)
        {
            preview.Problems.Add(ContentIssue.Error(string.Empty, $"файл слишком большой: {bytes.LongLength / 1024} КБ (предел {limit / 1024} КБ)"));
            return preview;
        }

        if (isPackage)
        {
            ReadPackage(library, preview);
        }
        else if (Decode(bytes, fileName, preview.Problems) is { } text)
        {
            var read = ContentSerializer.Read(text, library.Registry, ContentOrigin.User, library.Limits, fileName);
            if (read.Item is null)
            {
                preview.Problems.AddRange(read.Issues);
            }
            else
            {
                var entry = new ImportEntry(read.Item, fileName, read.MigratedFrom);
                entry.Issues.AddRange(read.Issues);
                preview.Entries.Add(entry);
            }
        }

        if (!preview.Rejected)
        {
            Classify(library, preview);
        }

        return preview;
    }

    /// <summary>
    /// Installs the entries whose resolution writes (install / replace with backup / copy with a new id). All of them are
    /// validated together first; then they are written one by one, and if anything fails every written file is removed and
    /// every replaced one restored from its backup. <paramref name="beforeWrite"/> is a test hook called before each write.
    /// </summary>
    public static ImportResult Install(ContentLibrary library, ImportPreview preview, Action<ContentItem>? beforeWrite = null)
    {
        if (library.User is not { } user)
        {
            return Fail("нет пользовательского хранилища");
        }

        if (preview.Rejected)
        {
            return Fail("файл отклонён — ничего не установлено: " + preview.Summary);
        }

        var chosen = preview.Entries.Where(e => e.Writes).ToList();
        foreach (var e in chosen)
        {
            if (!e.Options.Contains(e.Resolution))
            {
                return Fail($"«{e.Item.Name}»: действие «{Word(e.Resolution)}» недопустимо — ничего не установлено");
            }
        }

        if (chosen.Count == 0)
        {
            return Fail("ничего не выбрано для установки");
        }

        // New ids for copies; links inside the package that point to a copied object follow it.
        var taken = new HashSet<string>(preview.Entries.Select(e => e.Item.Id), StringComparer.Ordinal);
        var newIds = new Dictionary<ContentRef, string>();
        foreach (var e in chosen.Where(e => e.Resolution == ImportResolution.Copy))
        {
            string id;
            do
            {
                id = library.NewId(e.Item.Kind.Id);
            }
            while (!taken.Add(id));
            newIds[e.Item.Ref] = id;
        }

        var finals = new List<(ImportEntry Entry, ContentItem Item)>();
        foreach (var e in chosen)
        {
            var copy = e.Resolution == ImportResolution.Copy
                ? e.Item.CopyAs(newIds[e.Item.Ref], ContentOrigin.User, $"{e.Item.Name} (импорт)")
                : e.Item.CopyAs(e.Item.Id, ContentOrigin.User, e.Item.Name);
            copy.Revision = e.Item.Revision;
            copy.Modified = e.Item.Modified;
            copy.Data.RemapReferences(newIds);
            finals.Add((e, copy));
        }

        var overlay = library.Overlay(finals.Select(f => f.Item));
        var errors = new List<ContentIssue>();
        foreach (var (entry, item) in finals)
        {
            foreach (var issue in overlay.Validate(item).Where(i => i.IsError))
            {
                errors.Add(issue with { Message = $"«{item.Name}» ({item.Kind.Singular}): {issue.Message}" });
            }
        }

        if (errors.Count > 0)
        {
            errors.Insert(0, ContentIssue.Error(string.Empty, "проверка выбранного набора не прошла — ничего не установлено"));
            return new ImportResult(false, Array.Empty<ContentItem>(), errors);
        }

        var created = new List<ContentItem>();
        var replaced = new List<(ContentItem Item, string Backup)>();
        try
        {
            foreach (var (entry, item) in finals)
            {
                beforeWrite?.Invoke(item);
                var folder = item.Kind.Folder;
                if (user.Exists(folder, item.Id))
                {
                    var backup = user.Backup(folder, item.Id, "import") ?? throw new ContentSaveException($"не удалось сделать резервную копию «{item.Name}»");
                    replaced.Add((item, backup));
                }
                else
                {
                    created.Add(item);
                }

                item.SchemaVersion = item.Kind.SchemaVersion;
                user.SaveAtomic(folder, item.Id, ContentSerializer.Write(item), written =>
                    ContentSerializer.Read(written, library.Registry, ContentOrigin.User, library.Limits) is { Item: not null, HasErrors: false });
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            foreach (var item in created)
            {
                TryUndo(() => user.DeleteSaved(item.Kind.Folder, item.Id));
            }

            foreach (var (item, backup) in replaced)
            {
                TryUndo(() => user.Restore(item.Kind.Folder, item.Id, backup));
            }

            user.Log($"import of {preview.FileName} rolled back: {e.Message}");
            library.Reload();
            return Fail($"установка отменена: {e.Message}; хранилище возвращено в исходное состояние");
        }

        var stamp = user.NowStamp();
        var record = new JsonObject
        {
            ["file"] = preview.FileName,
            ["installed"] = new JsonArray(finals.Select(f => (JsonNode)new JsonObject
            {
                ["kind"] = f.Item.Kind.Id,
                ["id"] = f.Item.Id,
                ["name"] = f.Item.Name,
                ["from"] = f.Entry.Item.Id,
                ["action"] = Word(f.Entry.Resolution),
            }).ToArray()),
        };
        try
        {
            var baseName = $"{stamp}-{ContentExporter.SafeFileName(Path.GetFileNameWithoutExtension(preview.FileName))}";
            if (preview.IsPackage)
            {
                user.WritePack(baseName + ContentExporter.PackageExtension, preview.Bytes);
            }

            user.WritePack(baseName + ".installed.json", new UTF8Encoding(false).GetBytes(record.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })));
        }
        catch (ContentSaveException)
        {
            // The record of the installation is informative; the objects are already saved.
        }

        user.Log($"imported {preview.FileName}: " + string.Join(", ", finals.Select(f => $"{f.Item.Kind.Id}:{f.Item.Id} ({Word(f.Entry.Resolution)})")));
        library.Reload();
        var installed = finals.Select(f => library.Find(f.Item.Kind.Id, f.Item.Id) ?? f.Item).ToList();
        return new ImportResult(true, installed, Array.Empty<ContentIssue>());
    }

    public static string Word(ImportResolution resolution) => resolution switch
    {
        ImportResolution.Install => "установить",
        ImportResolution.Replace => "заменить (с резервной копией)",
        ImportResolution.Copy => "копией с новым ID",
        _ => "пропустить",
    };

    public static string Word(ImportStatus status) => status switch
    {
        ImportStatus.New => "новый",
        ImportStatus.Same => "уже есть, без изменений",
        ImportStatus.Conflict => "конфликт ID",
        _ => "ошибки",
    };

    private static ImportResult Fail(string message) => new(false, Array.Empty<ContentItem>(), new[] { ContentIssue.Error(string.Empty, message) });

    private static void TryUndo(Action action)
    {
        try
        {
            action();
        }
        catch (IOException)
        {
            // Best effort: the remaining files are listed in the log.
        }
    }

    private static bool IsPackageName(string name) =>
        name.EndsWith(ContentExporter.PackageExtension, StringComparison.OrdinalIgnoreCase) || name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    private static string? Decode(byte[] bytes, string where, List<ContentIssue> problems)
    {
        try
        {
            var text = new UTF8Encoding(false, true).GetString(bytes);
            return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
        }
        catch (DecoderFallbackException)
        {
            problems.Add(ContentIssue.Error(where, $"«{where}»: текст не в кодировке UTF-8"));
            return null;
        }
    }

    /// <summary>A path inside a package: relative, no «..», no drive or backslash, a small set of characters.</summary>
    public static bool IsSafePath(string path)
    {
        if (path.Length == 0 || path.Length > MaxPathLength || path[0] == '/' || path.Contains('\\') || path.Contains(':'))
        {
            return false;
        }

        foreach (var c in path)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '/'))
            {
                return false;
            }
        }

        foreach (var part in path.TrimEnd('/').Split('/'))
        {
            if (part.Length == 0 || part is "." or "..")
            {
                return false;
            }
        }

        return true;
    }

    private static void ReadPackage(ContentLibrary library, ImportPreview preview)
    {
        var problems = preview.Problems;
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(new MemoryStream(preview.Bytes, writable: false), ZipArchiveMode.Read);
        }
        catch (InvalidDataException e)
        {
            problems.Add(ContentIssue.Error(string.Empty, $"повреждённый архив пакета: {e.Message}"));
            return;
        }

        using (zip)
        {
            if (zip.Entries.Count > library.Limits.MaxPackageEntries)
            {
                problems.Add(ContentIssue.Error(string.Empty, $"в пакете {zip.Entries.Count} файлов (предел {library.Limits.MaxPackageEntries})"));
                return;
            }

            long total = 0;
            var files = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
            foreach (var entry in zip.Entries)
            {
                var path = entry.FullName;
                if (!IsSafePath(path))
                {
                    problems.Add(ContentIssue.Error(path, $"опасный путь в архиве: «{path}»"));
                    continue;
                }

                if (path.EndsWith('/'))
                {
                    continue; // a folder entry
                }

                if (!path.EndsWith(".json", StringComparison.Ordinal))
                {
                    problems.Add(ContentIssue.Error(path, $"недопустимый файл «{path}»: в пакете бывают только JSON-объекты и manifest.json (исполняемые файлы, библиотеки и скрипты запрещены)"));
                    continue;
                }

                total += entry.Length;
                if (entry.Length > library.Limits.MaxFileBytes)
                {
                    problems.Add(ContentIssue.Error(path, $"«{path}» больше {library.Limits.MaxFileBytes / 1024} КБ"));
                }
                else if (entry.CompressedLength > 0 && entry.Length > 64 * 1024 && entry.Length / entry.CompressedLength > 200)
                {
                    problems.Add(ContentIssue.Error(path, $"«{path}»: подозрительно сильное сжатие — пакет отклонён"));
                }

                if (!files.TryAdd(path, entry))
                {
                    problems.Add(ContentIssue.Error(path, $"файл «{path}» повторяется в архиве"));
                }
            }

            if (total > library.Limits.MaxUnpackedBytes)
            {
                problems.Add(ContentIssue.Error(string.Empty, $"пакет распаковывается в {total / 1024} КБ (предел {library.Limits.MaxUnpackedBytes / 1024} КБ)"));
            }

            if (problems.Any(p => p.IsError))
            {
                return;
            }

            if (!files.TryGetValue(PackageManifest.FileName, out var manifestEntry))
            {
                problems.Add(ContentIssue.Error(string.Empty, "в пакете нет manifest.json"));
                return;
            }

            var manifestIssues = new IssueList();
            var manifestBytes = ReadEntry(manifestEntry, library.Limits.MaxFileBytes, problems);
            var manifest = manifestBytes is not null && Decode(manifestBytes, PackageManifest.FileName, problems) is { } manifestText ? PackageManifest.Read(manifestText, manifestIssues) : null;
            problems.AddRange(manifestIssues.Items.Where(i => i.IsError));
            if (manifest is null)
            {
                return;
            }

            preview.Manifest = manifest;
            var listed = new HashSet<string>(StringComparer.Ordinal) { PackageManifest.FileName };
            var refs = new HashSet<ContentRef>();
            foreach (var o in manifest.Objects)
            {
                var kind = library.Registry.Find(o.Kind);
                if (kind is null)
                {
                    problems.Add(ContentIssue.Error(o.Path, $"неизвестный вид объекта «{o.Kind}» ({o.Id})"));
                    continue;
                }

                if (!ContentIds.IsValid(o.Id) || o.Path != $"content/{kind.Folder}/{o.Id}.json")
                {
                    problems.Add(ContentIssue.Error(o.Path, $"путь «{o.Path}» не соответствует объекту {o.Kind}:{o.Id}"));
                    continue;
                }

                if (!refs.Add(new ContentRef(o.Kind, o.Id)))
                {
                    problems.Add(ContentIssue.Error(o.Path, $"объект {o.Kind}:{o.Id} указан дважды"));
                    continue;
                }

                listed.Add(o.Path);
                if (!files.TryGetValue(o.Path, out var entry))
                {
                    problems.Add(ContentIssue.Error(o.Path, $"в архиве нет файла «{o.Path}», указанного в manifest.json"));
                    continue;
                }

                if (ReadEntry(entry, library.Limits.MaxFileBytes, problems) is not { } bytes)
                {
                    continue;
                }

                if (ContentExporter.Sha256(bytes) != o.Sha256)
                {
                    problems.Add(ContentIssue.Error(o.Path, $"«{o.Path}»: контрольная сумма не совпадает с manifest.json — файл повреждён или изменён"));
                    continue;
                }

                if (Decode(bytes, o.Path, problems) is not { } text)
                {
                    continue;
                }

                var read = ContentSerializer.Read(text, library.Registry, ContentOrigin.User, library.Limits, o.Path);
                if (read.Item is null)
                {
                    problems.AddRange(read.Issues.Where(i => i.IsError).Select(i => i with { Message = $"«{o.Path}»: {i.Message}" }));
                    continue;
                }

                if (read.Item.Id != o.Id || read.Item.Kind != kind)
                {
                    problems.Add(ContentIssue.Error(o.Path, $"«{o.Path}»: внутри объект {read.Item.Kind.Id}:{read.Item.Id}, а не {o.Kind}:{o.Id}"));
                    continue;
                }

                var importEntry = new ImportEntry(read.Item, o.Path, read.MigratedFrom);
                importEntry.Issues.AddRange(read.Issues);
                preview.Entries.Add(importEntry);
            }

            foreach (var path in files.Keys)
            {
                if (!listed.Contains(path))
                {
                    problems.Add(ContentIssue.Error(path, $"файл «{path}» не указан в manifest.json"));
                }
            }

            foreach (var dep in manifest.BuiltInDependencies)
            {
                if (library.Find(dep) is not { IsBuiltIn: true })
                {
                    problems.Add(ContentIssue.Warning(dep.ToString(), $"пакету нужен встроенный объект {dep}, которого нет в этой версии игры"));
                }
            }
        }
    }

    /// <summary>Reads an entry, never more than <paramref name="limit"/> bytes whatever the archive headers claim.</summary>
    private static byte[]? ReadEntry(ZipArchiveEntry entry, int limit, List<ContentIssue> problems)
    {
        try
        {
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (buffer.Length + read > limit)
                {
                    problems.Add(ContentIssue.Error(entry.FullName, $"«{entry.FullName}» распаковывается больше чем в {limit / 1024} КБ — пакет отклонён"));
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
        catch (InvalidDataException e)
        {
            problems.Add(ContentIssue.Error(entry.FullName, $"«{entry.FullName}» повреждён: {e.Message}"));
            return null;
        }
    }

    /// <summary>New / same / conflict / invalid for every entry, with links checked against the library plus the file.</summary>
    private static void Classify(ContentLibrary library, ImportPreview preview)
    {
        var overlay = library.Overlay(preview.Entries.Select(e => e.Item));
        var inFile = preview.Entries.ToDictionary(e => e.Item.Ref);
        foreach (var e in preview.Entries)
        {
            var item = e.Item;
            e.Existing = library.Find(item.Ref);
            if (ContentIds.IsBuiltIn(item.Id) && e.Existing is null)
            {
                e.Issues.Add(ContentIssue.Error("id", $"ID «{item.Id}» зарезервирован для встроенного контента игры"));
            }

            foreach (var issue in overlay.Validate(item))
            {
                if (issue.IsError && issue.Path == "references" && issue.Message.StartsWith("ссылка на отсутствующий", StringComparison.Ordinal))
                {
                    e.Issues.Add(issue with
                    {
                        Message = preview.IsPackage
                            ? $"{issue.Message} — его нет ни в пакете, ни в библиотеке"
                            : $"файл не самодостаточен: {issue.Message} — его нет в библиотеке; экспортируйте объект пакетом (.hxp) с зависимостями",
                    });
                }
                else
                {
                    e.Issues.Add(issue);
                }
            }

            e.Status = e.Issues.Any(i => i.IsError) ? ImportStatus.Invalid
                : e.Existing is null ? ImportStatus.New
                : SameContent(e.Existing, item) ? ImportStatus.Same
                : ImportStatus.Conflict;
        }

        // An object that needs another object of the file which cannot be installed (and is not in the library) cannot be
        // installed either.
        bool changed;
        do
        {
            changed = false;
            foreach (var e in preview.Entries.Where(e => e.Status != ImportStatus.Invalid))
            {
                foreach (var r in e.Item.References)
                {
                    if (inFile.TryGetValue(r, out var dep) && dep.Status == ImportStatus.Invalid && library.Find(r) is null)
                    {
                        e.Issues.Add(ContentIssue.Error("references", $"нужен «{dep.Item.Name}» ({dep.Item.Kind.Singular}), который нельзя установить из-за ошибок"));
                        e.Status = ImportStatus.Invalid;
                        changed = true;
                        break;
                    }
                }
            }
        }
        while (changed);

        foreach (var e in preview.Entries)
        {
            e.Resolution = e.Status == ImportStatus.New ? ImportResolution.Install : ImportResolution.Skip;
        }
    }

    private static bool SameContent(ContentItem a, ContentItem b) =>
        a.Name == b.Name && a.Description == b.Description && a.Data.ToJson().ToJsonString() == b.Data.ToJson().ToJsonString();
}
