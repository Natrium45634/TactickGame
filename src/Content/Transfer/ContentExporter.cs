using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HexTactics.Content.Library;
using HexTactics.Content.Model;

namespace HexTactics.Content.Transfer;

/// <summary>One object listed in a package manifest.</summary>
public sealed record PackageObject(string Kind, string Id, string Name, int Revision, int SchemaVersion, string Path, string Sha256, long Bytes);

/// <summary>
/// <c>manifest.json</c> of a content package (spec part 4 §8.3): what is inside, versions, checksums and the built-in
/// objects of the game the package relies on.
/// </summary>
public sealed record PackageManifest(
    string Name,
    string Description,
    string GameVersion,
    DateTimeOffset Created,
    IReadOnlyList<PackageObject> Objects,
    IReadOnlyList<ContentRef> BuiltInDependencies)
{
    public const string Format = "hextactics-package";
    public const int FormatVersion = 1;
    public const string FileName = "manifest.json";

    public JsonObject ToJson()
    {
        var objects = new JsonArray();
        foreach (var o in Objects)
        {
            objects.Add(new JsonObject
            {
                ["kind"] = o.Kind,
                ["id"] = o.Id,
                ["name"] = o.Name,
                ["revision"] = o.Revision,
                ["schemaVersion"] = o.SchemaVersion,
                ["path"] = o.Path,
                ["sha256"] = o.Sha256,
                ["bytes"] = o.Bytes,
            });
        }

        var deps = new JsonArray();
        foreach (var d in BuiltInDependencies)
        {
            deps.Add(new JsonObject { ["kind"] = d.Kind, ["id"] = d.Id });
        }

        return new JsonObject
        {
            ["format"] = Format,
            ["formatVersion"] = FormatVersion,
            ["name"] = Name,
            ["description"] = Description,
            ["gameVersion"] = GameVersion,
            ["created"] = Created.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            ["objects"] = objects,
            ["builtInDependencies"] = deps,
        };
    }

    /// <summary>Reads and checks a manifest; null (with errors) if it is not a valid manifest of a supported format.</summary>
    public static PackageManifest? Read(string json, IssueList issues)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException e)
        {
            issues.Error(FileName, $"повреждённый manifest.json: {e.Message}");
            return null;
        }

        if (root is null || JsonFields.Text(root, "format", string.Empty, issues, FileName) != Format)
        {
            issues.Error(FileName, "это не пакет HexTactics (нет format = hextactics-package)");
            return null;
        }

        var version = JsonFields.Integer(root, "formatVersion", 0, issues, FileName);
        if (version < 1 || version > FormatVersion)
        {
            issues.Error(FileName, $"версия формата пакета {version} не поддерживается (поддерживается {FormatVersion})");
            return null;
        }

        var objects = new List<PackageObject>();
        if (JsonFields.Array(root, "objects", issues, FileName) is { } list)
        {
            foreach (var node in list)
            {
                if (node is not JsonObject o)
                {
                    issues.Error(FileName, "objects: ожидаются объекты");
                    return null;
                }

                objects.Add(new PackageObject(
                    JsonFields.Text(o, "kind", string.Empty, issues, FileName),
                    JsonFields.Text(o, "id", string.Empty, issues, FileName),
                    JsonFields.Text(o, "name", string.Empty, issues, FileName),
                    JsonFields.Integer(o, "revision", 1, issues, FileName),
                    JsonFields.Integer(o, "schemaVersion", 1, issues, FileName),
                    JsonFields.Text(o, "path", string.Empty, issues, FileName),
                    JsonFields.Text(o, "sha256", string.Empty, issues, FileName).ToLowerInvariant(),
                    JsonFields.Integer(o, "bytes", 0, issues, FileName)));
            }
        }

        var deps = new List<ContentRef>();
        if (root["builtInDependencies"] is JsonArray depNodes)
        {
            foreach (var node in depNodes)
            {
                if (node is JsonObject d)
                {
                    deps.Add(new ContentRef(JsonFields.Text(d, "kind", string.Empty, issues, FileName), JsonFields.Text(d, "id", string.Empty, issues, FileName)));
                }
            }
        }

        var created = DateTimeOffset.TryParse(JsonFields.Text(root, "created", string.Empty, issues, FileName), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var stamp) ? stamp : DateTimeOffset.MinValue;
        return new PackageManifest(
            JsonFields.Text(root, "name", string.Empty, issues, FileName),
            JsonFields.Text(root, "description", string.Empty, issues, FileName),
            JsonFields.Text(root, "gameVersion", string.Empty, issues, FileName),
            created,
            objects,
            deps);
    }
}

/// <summary>A single exported object and the user objects it needs that the file does not contain.</summary>
public sealed record ObjectExport(string Json, IReadOnlyList<ContentItem> NotIncluded)
{
    /// <summary>True if the file can be imported anywhere on its own (it needs only built-in objects of the game).</summary>
    public bool SelfContained => NotIncluded.Count == 0;
}

/// <summary>
/// Export of content (spec part 4 §8.3): one object as UTF-8 JSON, or a package — a ZIP container <c>.hxp</c> with
/// <c>manifest.json</c> and the objects with every user object they depend on, collected automatically. Objects are written
/// with the same serializer as the library files, so every field (per-cell effects, colours, hit intervals, links,
/// metadata) survives the round trip.
/// </summary>
public static class ContentExporter
{
    public const string ObjectExtension = ".json";
    public const string PackageExtension = ".hxp";

    public static ObjectExport ExportObject(ContentLibrary library, ContentItem item)
    {
        var needed = Dependencies(library, new[] { item }).Where(d => d.Ref != item.Ref).ToList();
        return new ObjectExport(ContentSerializer.Write(item), needed);
    }

    /// <summary>
    /// The user objects of <paramref name="roots"/> and everything they need (transitively), dependencies first. Built-in
    /// objects are part of every installation of the game and are not copied.
    /// </summary>
    public static IReadOnlyList<ContentItem> Dependencies(ContentLibrary library, IEnumerable<ContentItem> roots)
    {
        var result = new List<ContentItem>();
        var seen = new HashSet<ContentRef>();
        foreach (var root in roots)
        {
            Visit(root);
        }

        return result;

        void Visit(ContentItem item)
        {
            if (!seen.Add(item.Ref))
            {
                return;
            }

            foreach (var r in item.References)
            {
                if (library.Find(r) is { } next)
                {
                    Visit(next);
                }
            }

            if (!item.IsBuiltIn)
            {
                result.Add(item);
            }
        }
    }

    /// <summary>Built-in objects the given objects refer to (listed in the manifest as requirements).</summary>
    public static IReadOnlyList<ContentRef> BuiltInDependencies(ContentLibrary library, IEnumerable<ContentItem> items)
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        var list = new List<ContentRef>();
        foreach (var item in items)
        {
            foreach (var r in item.References)
            {
                if (library.Find(r) is { IsBuiltIn: true } && set.Add(r.ToString()))
                {
                    list.Add(r);
                }
            }
        }

        return list;
    }

    /// <summary>A package of <paramref name="roots"/> with their dependencies (ZIP bytes of a <c>.hxp</c> file).</summary>
    public static byte[] ExportPackage(ContentLibrary library, IEnumerable<ContentItem> roots, string name, string description, string gameVersion, DateTimeOffset created)
    {
        var items = Dependencies(library, roots);
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var objects = new List<PackageObject>();
            foreach (var item in items)
            {
                var bytes = new UTF8Encoding(false).GetBytes(ContentSerializer.Write(item));
                var path = $"content/{item.Kind.Folder}/{ContentIds.FileName(item.Id)}";
                Add(zip, path, bytes, created);
                objects.Add(new PackageObject(item.Kind.Id, item.Id, item.Name, item.Revision, item.SchemaVersion, path, Sha256(bytes), bytes.Length));
            }

            var manifest = new PackageManifest(name, description, gameVersion, created, objects, BuiltInDependencies(library, items));
            Add(zip, PackageManifest.FileName, new UTF8Encoding(false).GetBytes(manifest.ToJson().ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n"), created);
        }

        return stream.ToArray();
    }

    public static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>A safe file name for an export (letters, digits, «-», «_»; the rest becomes «_»).</summary>
    public static string SafeFileName(string name)
    {
        var builder = new StringBuilder();
        foreach (var c in name.Trim())
        {
            builder.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        }

        var text = builder.ToString().Trim('_');
        return text.Length == 0 ? "export" : text.Length > 60 ? text[..60] : text;
    }

    private static void Add(ZipArchive zip, string path, byte[] bytes, DateTimeOffset created)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        entry.LastWriteTime = created.Year < 1980 ? new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero) : created;
        using var s = entry.Open();
        s.Write(bytes, 0, bytes.Length);
    }
}
