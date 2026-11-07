using System;
using System.Collections.Generic;
using HexTactics.Content.Kinds;
using HexTactics.Content.Model;
using HexTactics.Content.Registry;

namespace HexTactics.Content.Library;

/// <summary>Where an object comes from.</summary>
public enum ContentOrigin
{
    /// <summary>Shipped with the game (read-only; can be duplicated).</summary>
    BuiltIn = 0,

    /// <summary>Created by the user, stored in the user content folder.</summary>
    User = 1,
}

/// <summary>
/// One content object (spec part 4 §8.2): the common envelope — stable id, kind, schema version, name, description,
/// revision — and its typed <see cref="Data"/>. References are derived from the data, so they never go stale.
/// </summary>
public sealed class ContentItem
{
    public ContentItem(ContentKind kind, string id, IContentData data, ContentOrigin origin)
    {
        Kind = kind;
        Id = id;
        Data = data;
        Origin = origin;
        SchemaVersion = kind.SchemaVersion;
    }

    public ContentKind Kind { get; }

    /// <summary>Stable id: never changes on rename.</summary>
    public string Id { get; }

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>Incremented on every save.</summary>
    public int Revision { get; set; } = 1;

    public int SchemaVersion { get; set; }

    /// <summary>Time of the last save (UTC).</summary>
    public DateTimeOffset? Modified { get; set; }

    public List<string> Tags { get; } = new();

    public IContentData Data { get; set; }

    public ContentOrigin Origin { get; }

    public bool IsBuiltIn => Origin == ContentOrigin.BuiltIn;

    /// <summary>File the object was read from, if any (for messages).</summary>
    public string? SourcePath { get; set; }

    /// <summary>Issues found when the object was read or last validated.</summary>
    public IReadOnlyList<ContentIssue> Issues { get; set; } = Array.Empty<ContentIssue>();

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

    public ContentRef Ref => new(Kind.Id, Id);

    public IEnumerable<ContentRef> References => Data.References();

    /// <summary>A deep copy (same id and origin) — editors work on a copy and save it back.</summary>
    public ContentItem Clone() => CopyAs(Id, Origin, Name);

    /// <summary>A deep copy under another id / origin / name.</summary>
    public ContentItem CopyAs(string id, ContentOrigin origin, string name)
    {
        var copy = new ContentItem(Kind, id, Data.Clone(), origin)
        {
            Name = name,
            Description = Description,
            Revision = Revision,
            SchemaVersion = SchemaVersion,
            Modified = Modified,
            SourcePath = origin == Origin && id == Id ? SourcePath : null,
            Issues = Issues,
        };
        copy.Tags.AddRange(Tags);
        return copy;
    }

    public override string ToString() => $"{Kind.Id}:{Id} «{Name}» r{Revision}";
}
