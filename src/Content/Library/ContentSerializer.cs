using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using HexTactics.Content.Kinds;
using HexTactics.Content.Model;
using HexTactics.Content.Registry;

namespace HexTactics.Content.Library;

/// <summary>Result of reading one content file: the object (if it could be built) and every issue found.</summary>
public sealed record ContentReadResult(ContentItem? Item, IReadOnlyList<ContentIssue> Issues)
{
    /// <summary>The schema version the file had, if the object was migrated to the current one while reading.</summary>
    public int? MigratedFrom { get; init; }

    public bool HasErrors
    {
        get
        {
            foreach (var i in Issues)
            {
                if (i.IsError)
                {
                    return true;
                }
            }

            return false;
        }
    }
}

/// <summary>
/// Versioned JSON form of content objects (spec part 4 §8.2): UTF-8, a fixed field order, readable Cyrillic.
/// <code>
/// { "schemaVersion": 1, "id": "…", "kind": "skill", "name": "…", "description": "…", "revision": 1,
///   "modified": "…", "tags": [], "references": [{"kind": "skill", "id": "…"}], "data": { … } }
/// </code>
/// Reading never executes anything from the file: the data is checked against the registered kinds and effect types.
/// </summary>
public static class ContentSerializer
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };

    private static readonly JsonDocumentOptions ReadOptions = new() { MaxDepth = 32 };

    public static string Write(ContentItem item)
    {
        var references = new JsonArray();
        foreach (var r in item.References)
        {
            references.Add(new JsonObject { ["kind"] = r.Kind, ["id"] = r.Id });
        }

        var tags = new JsonArray();
        foreach (var t in item.Tags)
        {
            tags.Add(t);
        }

        var root = new JsonObject
        {
            ["schemaVersion"] = item.SchemaVersion,
            ["id"] = item.Id,
            ["kind"] = item.Kind.Id,
            ["name"] = item.Name,
            ["description"] = item.Description,
            ["revision"] = item.Revision,
        };
        if (item.Modified is { } modified)
        {
            root["modified"] = modified.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }

        root["tags"] = tags;
        root["references"] = references;
        root["data"] = item.Data.ToJson();
        return root.ToJsonString(WriteOptions) + "\n";
    }

    public static ContentReadResult Read(string json, ContentTypeRegistry registry, ContentOrigin origin, ContentLimits? limits = null, string? sourcePath = null)
    {
        limits ??= ContentLimits.Default;
        var issues = new IssueList();
        if (Encoding.UTF8.GetByteCount(json) > limits.MaxFileBytes)
        {
            issues.Error(string.Empty, $"файл больше {limits.MaxFileBytes / 1024} КБ — объект не загружен");
            return new ContentReadResult(null, issues.Items);
        }

        JsonNode? rootNode;
        try
        {
            rootNode = JsonNode.Parse(json, documentOptions: ReadOptions);
        }
        catch (JsonException e)
        {
            issues.Error(string.Empty, $"повреждённый JSON: {e.Message}");
            return new ContentReadResult(null, issues.Items);
        }

        if (rootNode is not JsonObject root)
        {
            issues.Error(string.Empty, "ожидается JSON-объект");
            return new ContentReadResult(null, issues.Items);
        }

        var schemaVersion = JsonFields.Integer(root, "schemaVersion", -1, issues, string.Empty);
        var id = JsonFields.Text(root, "id", string.Empty, issues, string.Empty);
        var kindId = JsonFields.Text(root, "kind", string.Empty, issues, string.Empty);
        if (!ContentIds.IsValid(id))
        {
            issues.Error("id", $"неверный идентификатор «{id}» (латиница, цифры, «.», «_», «-», 3–{ContentIds.MaxLength} символов)");
        }

        var kind = registry.Find(kindId);
        if (kind is null)
        {
            issues.Error("kind", $"неизвестный вид объекта «{kindId}»");
            return new ContentReadResult(null, issues.Items);
        }

        if (schemaVersion < 1)
        {
            issues.Error("schemaVersion", "нет версии схемы");
            return new ContentReadResult(null, issues.Items);
        }

        if (schemaVersion > kind.SchemaVersion)
        {
            issues.Error("schemaVersion", $"версия схемы {schemaVersion} новее поддерживаемой {kind.SchemaVersion}: объект оставлен без изменений");
            return new ContentReadResult(null, issues.Items);
        }

        if (root["data"] is not JsonObject data)
        {
            issues.Error("data", "нет содержимого data");
            return new ContentReadResult(null, issues.Items);
        }

        var version = schemaVersion;
        while (version < kind.SchemaVersion)
        {
            var migrated = kind.Migrate?.Invoke(version, data, issues);
            if (migrated is null)
            {
                issues.Error("schemaVersion", $"нет миграции {kind.Title.ToLowerInvariant()} со схемы {version}: объект оставлен без изменений");
                return new ContentReadResult(null, issues.Items);
            }

            data = migrated;
            version++;
        }

        if (!ContentIds.IsValid(id))
        {
            return new ContentReadResult(null, issues.Items);
        }

        var dataIssues = new IssueList();
        var typed = kind.Read(data, dataIssues);
        foreach (var i in dataIssues.Items)
        {
            issues.AddRange(new[] { i with { Path = JsonFields.Join("data", i.Path) } });
        }

        var item = new ContentItem(kind, id, typed, origin)
        {
            Name = JsonFields.Text(root, "name", string.Empty, issues, string.Empty),
            Description = JsonFields.Text(root, "description", string.Empty, issues, string.Empty),
            Revision = Math.Max(1, JsonFields.Integer(root, "revision", 1, issues, string.Empty)),
            SchemaVersion = kind.SchemaVersion,
            SourcePath = sourcePath,
        };
        var modified = JsonFields.Text(root, "modified", string.Empty, issues, string.Empty);
        if (modified.Length > 0 && DateTimeOffset.TryParse(modified, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var stamp))
        {
            item.Modified = stamp;
        }

        item.Tags.AddRange(JsonFields.TextList(root, "tags", issues, string.Empty));
        if (schemaVersion < kind.SchemaVersion)
        {
            issues.Warning("schemaVersion", $"объект обновлён со схемы {schemaVersion} до {kind.SchemaVersion}");
        }

        item.Issues = issues.Items;
        return new ContentReadResult(item, issues.Items) { MigratedFrom = schemaVersion < kind.SchemaVersion ? schemaVersion : null };
    }
}
