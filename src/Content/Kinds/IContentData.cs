using System;
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

    /// <summary>
    /// «Computational cost» of one skill (spec part 4 §8.3): the total number of effect applications of one use — every
    /// effect of every cell times its hits. Bounds the work of a single activation.
    /// </summary>
    public int MaxSkillHitEvents { get; init; } = 1024;

    /// <summary>Largest content package (<c>.hxp</c>) accepted for import.</summary>
    public long MaxPackageBytes { get; init; } = 32L * 1024 * 1024;

    /// <summary>Most files inside one package.</summary>
    public int MaxPackageEntries { get; init; } = 512;

    /// <summary>Most bytes all files of a package may unpack to (guards against «zip bombs»).</summary>
    public long MaxUnpackedBytes { get; init; } = 64L * 1024 * 1024;

    public static ContentLimits Default { get; } = new();

    /// <summary>Hard caps of the configurable limits: a settings file can lower limits, never raise them past these.</summary>
    public static ContentLimits HardCaps { get; } = new()
    {
        MaxFileBytes = 8 * 1024 * 1024, MaxMapSide = 64, MaxSkillCells = 128, MaxSkillRadius = 10, MaxEffectsPerCell = 16,
        MaxSkillsPerUnit = 16, MaxTextLength = 10000, MaxSkillHitEvents = 4096, MaxPackageBytes = 128L * 1024 * 1024,
        MaxPackageEntries = 2048, MaxUnpackedBytes = 256L * 1024 * 1024,
    };

    /// <summary>
    /// Limits with the values of a user settings file (<c>user://limits.json</c>, spec part 4 §8.3 «настраиваемые безопасные
    /// пределы»): known keys only, whole numbers from 1 up to <see cref="HardCaps"/>; anything else is reported and ignored.
    /// </summary>
    public static ContentLimits FromJson(string json, IssueList issues)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject;
        }
        catch (System.Text.Json.JsonException e)
        {
            issues.Error(string.Empty, $"limits.json: повреждённый JSON ({e.Message}) — действуют стандартные пределы");
            return Default;
        }

        if (root is null)
        {
            issues.Error(string.Empty, "limits.json: ожидается JSON-объект — действуют стандартные пределы");
            return Default;
        }

        var result = Default;
        foreach (var (key, node) in root)
        {
            long cap = key switch
            {
                "maxFileBytes" => HardCaps.MaxFileBytes,
                "maxMapSide" => HardCaps.MaxMapSide,
                "maxSkillCells" => HardCaps.MaxSkillCells,
                "maxSkillRadius" => HardCaps.MaxSkillRadius,
                "maxEffectsPerCell" => HardCaps.MaxEffectsPerCell,
                "maxSkillsPerUnit" => HardCaps.MaxSkillsPerUnit,
                "maxTextLength" => HardCaps.MaxTextLength,
                "maxSkillHitEvents" => HardCaps.MaxSkillHitEvents,
                "maxPackageBytes" => HardCaps.MaxPackageBytes,
                "maxPackageEntries" => HardCaps.MaxPackageEntries,
                "maxUnpackedBytes" => HardCaps.MaxUnpackedBytes,
                _ => -1,
            };
            if (cap < 0)
            {
                issues.Warning(key, $"limits.json: неизвестный предел «{key}» пропущен");
                continue;
            }

            if (node is not JsonValue value || !value.TryGetValue<long>(out var n) || n < 1 || n > cap)
            {
                issues.Warning(key, $"limits.json: «{key}» — целое от 1 до {cap}; оставлено стандартное значение");
                continue;
            }

            var i = (int)Math.Min(n, int.MaxValue);
            result = key switch
            {
                "maxFileBytes" => result with { MaxFileBytes = i },
                "maxMapSide" => result with { MaxMapSide = i },
                "maxSkillCells" => result with { MaxSkillCells = i },
                "maxSkillRadius" => result with { MaxSkillRadius = i },
                "maxEffectsPerCell" => result with { MaxEffectsPerCell = i },
                "maxSkillsPerUnit" => result with { MaxSkillsPerUnit = i },
                "maxTextLength" => result with { MaxTextLength = i },
                "maxSkillHitEvents" => result with { MaxSkillHitEvents = i },
                "maxPackageBytes" => result with { MaxPackageBytes = n },
                "maxPackageEntries" => result with { MaxPackageEntries = i },
                _ => result with { MaxUnpackedBytes = n },
            };
        }

        return result;
    }
}

/// <summary>What a data validator may use: the issue list, the effect and defense action registries and the limits.</summary>
public sealed class ValidationContext
{
    public ValidationContext(IssueList issues, EffectTypeRegistry effects, ContentLimits limits, DefenseActionRegistry? defenseActions = null)
    {
        Issues = issues;
        Effects = effects;
        Limits = limits;
        DefenseActions = defenseActions ?? DefenseActionRegistry.Default;
    }

    /// <summary>Actions available to defense rules (spec part 5 §9.4).</summary>
    public DefenseActionRegistry DefenseActions { get; }

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

    /// <summary>
    /// Points references to objects that were imported under a new id (spec part 4 §8.3 «импортировать копией с новым ID»,
    /// links inside the package are re-bound). Kinds without references do nothing.
    /// </summary>
    void RemapReferences(IReadOnlyDictionary<ContentRef, string> newIds)
    {
    }

    /// <summary>Checks the values (ranges, NaN/Infinity, limits, effect types); references are resolved by the library.</summary>
    void Validate(ValidationContext context);

    /// <summary>A short type/description line for library lists.</summary>
    string Summary();

    IContentData Clone();
}
