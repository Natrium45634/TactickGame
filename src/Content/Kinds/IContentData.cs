using System.Collections.Generic;
using System.Text.Json.Nodes;
using HexTactics.Content.Model;
using HexTactics.Content.Registry;

namespace HexTactics.Content.Kinds;

/// <summary>Safety limits of user content (spec part 4 §8.3: a wrong import must not hang the application).</summary>
public sealed record ContentLimits
{
    public int MaxFileBytes { get; init; } = 2 * 1024 * 1024;

    public int MaxMapSide { get; init; } = 32;

    public int MaxSkillCells { get; init; } = 64;

    /// <summary>Largest distance of a skill cell from the caster.</summary>
    public int MaxSkillRadius { get; init; } = 6;

    public int MaxEffectsPerCell { get; init; } = 8;

    public int MaxSkillsPerUnit { get; init; } = 8;

    public int MaxTextLength { get; init; } = 2000;

    public static ContentLimits Default { get; } = new();
}

/// <summary>What a data validator may use: the issue list, the effect type registry and the limits.</summary>
public sealed class ValidationContext
{
    public ValidationContext(IssueList issues, EffectTypeRegistry effects, ContentLimits limits)
    {
        Issues = issues;
        Effects = effects;
        Limits = limits;
    }

    public IssueList Issues { get; }

    public EffectTypeRegistry Effects { get; }

    public ContentLimits Limits { get; }
}

/// <summary>
/// The typed <c>data</c> part of a content object (spec part 4 §8.2). One class per kind; the envelope (id, name,
/// revision …) lives in <see cref="Library.ContentItem"/>.
/// </summary>
public interface IContentData
{
    JsonObject ToJson();

    /// <summary>Objects this one depends on, by <c>kind + id</c>.</summary>
    IEnumerable<ContentRef> References();

    /// <summary>Checks the values (ranges, NaN/Infinity, limits, effect types); references are resolved by the library.</summary>
    void Validate(ValidationContext context);

    /// <summary>A short type/description line for library lists.</summary>
    string Summary();

    IContentData Clone();
}
