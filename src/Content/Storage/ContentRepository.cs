using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using HexTactics.Content.Model;

namespace HexTactics.Content.Storage;

/// <summary>Saving failed; the previously saved file is unchanged.</summary>
public sealed class ContentSaveException : Exception
{
    public ContentSaveException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>A draft of an unsaved editor change found on disk.</summary>
public sealed record DraftInfo(string Folder, string Id, string Path, DateTimeOffset Saved);

/// <summary>
/// The user content folder (spec part 4 §8.1), by default Godot's <c>user://</c>:
/// <code>
/// content/&lt;folder&gt;/&lt;id&gt;.json   saved objects (file name = stable id)
/// drafts/&lt;folder&gt;/&lt;id&gt;.json    restorable drafts of the editors
/// backups/…                     copies before replacement / migration, deleted objects (restorable)
/// logs/content.log              import / migration / validation log
/// packs/                        installed content packs
/// </code>
/// Every write is atomic: a temporary file next to the target → read back and checked → renamed over the target. On any
/// failure the previously saved object stays intact. Releases of the game never touch this folder.
/// </summary>
public sealed class ContentRepository
{
    private readonly Func<DateTimeOffset> _clock;

    public ContentRepository(string root, Func<DateTimeOffset>? clock = null)
    {
        Root = Path.GetFullPath(root);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public string Root { get; }

    public string ContentDir => Path.Combine(Root, "content");

    public string DraftsDir => Path.Combine(Root, "drafts");

    public string BackupsDir => Path.Combine(Root, "backups");

    public string LogsDir => Path.Combine(Root, "logs");

    public string PacksDir => Path.Combine(Root, "packs");

    /// <summary>Where exported objects and packages are written (spec part 4 §8.3).</summary>
    public string ExportsDir => Path.Combine(Root, "exports");

    public void EnsureLayout()
    {
        foreach (var dir in new[] { ContentDir, DraftsDir, BackupsDir, LogsDir, PacksDir, ExportsDir })
        {
            Directory.CreateDirectory(dir);
        }
    }

    public string PathOf(string folder, string id) => Path.Combine(ContentDir, folder, ContentIds.FileName(id));

    public bool Exists(string folder, string id) => File.Exists(PathOf(folder, id));

    /// <summary>All saved objects of a folder (path, text), in file name order.</summary>
    public IReadOnlyList<(string Path, string Text)> ReadAll(string folder)
    {
        var dir = Path.Combine(ContentDir, folder);
        var result = new List<(string, string)>();
        if (!Directory.Exists(dir))
        {
            return result;
        }

        var files = Directory.GetFiles(dir, "*.json");
        Array.Sort(files, StringComparer.Ordinal);
        foreach (var file in files)
        {
            result.Add((file, File.ReadAllText(file, Encoding.UTF8)));
        }

        return result;
    }

    /// <summary>
    /// Atomically writes <paramref name="text"/> as the object <paramref name="id"/>. <paramref name="verify"/> receives
    /// the text read back from the temporary file and must return true to commit (syntax/semantic check).
    /// </summary>
    public string SaveAtomic(string folder, string id, string text, Func<string, bool>? verify = null) =>
        WriteAtomic(PathOf(folder, id), text, verify);

    public void SaveDraft(string folder, string id, string text) =>
        WriteAtomic(Path.Combine(DraftsDir, folder, ContentIds.FileName(id)), text, null);

    public string? ReadDraft(string folder, string id)
    {
        var path = Path.Combine(DraftsDir, folder, ContentIds.FileName(id));
        return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
    }

    public void DeleteDraft(string folder, string id)
    {
        var path = Path.Combine(DraftsDir, folder, ContentIds.FileName(id));
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public IReadOnlyList<DraftInfo> ListDrafts()
    {
        var list = new List<DraftInfo>();
        if (!Directory.Exists(DraftsDir))
        {
            return list;
        }

        foreach (var dir in Directory.GetDirectories(DraftsDir))
        {
            foreach (var file in Directory.GetFiles(dir, "*.json"))
            {
                list.Add(new DraftInfo(Path.GetFileName(dir), Path.GetFileNameWithoutExtension(file), file, File.GetLastWriteTimeUtc(file)));
            }
        }

        list.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return list;
    }

    /// <summary>Copies a saved object into <c>backups/&lt;time&gt;/&lt;folder&gt;/</c>; returns the copy's path (null if nothing to copy).</summary>
    public string? Backup(string folder, string id, string reason)
    {
        var source = PathOf(folder, id);
        if (!File.Exists(source))
        {
            return null;
        }

        var target = Path.Combine(BackupsDir, Stamp() + "-" + reason, folder, ContentIds.FileName(id));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target, overwrite: true);
        Log($"backup {folder}/{id} → {Path.GetRelativePath(Root, target)} ({reason})");
        return target;
    }

    /// <summary>Moves a saved object to <c>backups/deleted</c> (restorable with <see cref="Restore"/>); returns the new path.</summary>
    public string MoveToTrash(string folder, string id)
    {
        var source = PathOf(folder, id);
        var target = Path.Combine(BackupsDir, "deleted", folder, $"{id}.{Stamp()}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(source, target, overwrite: true);
        Log($"deleted {folder}/{id} → {Path.GetRelativePath(Root, target)}");
        return target;
    }

    /// <summary>Puts a file from the trash (or a backup) back as the saved object.</summary>
    public void Restore(string folder, string id, string fromPath)
    {
        var target = PathOf(folder, id);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(fromPath, target, overwrite: true);
        Log($"restored {folder}/{id} from {Path.GetRelativePath(Root, fromPath)}");
    }

    /// <summary>Removes a saved object file (rollback of an import that created it); false if there was none.</summary>
    public bool DeleteSaved(string folder, string id)
    {
        var path = PathOf(folder, id);
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    /// <summary>Writes an exported file (object or package) into <see cref="ExportsDir"/>; returns its full path.</summary>
    public string WriteExport(string fileName, byte[] bytes) => WriteBytes(ExportsDir, fileName, bytes);

    /// <summary>Keeps a copy of an installed package or its install record in <see cref="PacksDir"/>.</summary>
    public string WritePack(string fileName, byte[] bytes) => WriteBytes(PacksDir, fileName, bytes);

    public string NowStamp() => Stamp();

    public void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(LogsDir);
            File.AppendAllText(Path.Combine(LogsDir, "content.log"), $"{_clock().UtcDateTime:yyyy-MM-dd HH:mm:ss} {message}\n", Encoding.UTF8);
        }
        catch (IOException)
        {
            // The log is best effort; it must never break saving.
        }
    }

    private static string WriteBytes(string dir, string fileName, byte[] bytes)
    {
        if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || fileName.Contains("..", StringComparison.Ordinal))
        {
            throw new ContentSaveException($"недопустимое имя файла «{fileName}»");
        }

        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, fileName);
        var temp = target + ".tmp";
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, target, overwrite: true);
            return target;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ContentSaveException($"не удалось записать файл: {e.Message}", e);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private string Stamp() => _clock().UtcDateTime.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);

    private static string WriteAtomic(string target, string text, Func<string, bool>? verify)
    {
        var dir = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, "." + Path.GetFileName(target) + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var bytes = new UTF8Encoding(false).GetBytes(text);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }

            var written = File.ReadAllText(temp, Encoding.UTF8);
            if (written != text || (verify is not null && !verify(written)))
            {
                throw new ContentSaveException("проверка записанного файла не прошла — сохранённый объект не изменён");
            }

            File.Move(temp, target, overwrite: true);
            return target;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ContentSaveException($"не удалось записать файл: {e.Message}", e);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}
