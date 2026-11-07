using System.Collections.Generic;
using System.Text.Json.Nodes;
using HexTactics.Content.Model;
using HexTactics.Content.Schema;

namespace HexTactics.Content.Kinds;

/// <summary>
/// A damage type of the library (spec part 5 §2, §9.1 <c>damage_type</c>): its family, calculation strategy, tags and look.
/// A family type (physical, magical, true or a custom family) names itself as its family; a subtype names its family.
/// The strategy is always explicit — a type is never treated as magical because of its name.
/// </summary>
public sealed class DamageTypeData : IContentData
{
    public const string ResistanceAffected = "resistance_affected";
    public const string TrueLike = "true_like";

    /// <summary>Id of the family type; empty = this type is a family itself.</summary>
    public string Family { get; set; } = string.Empty;

    public string Strategy { get; set; } = ResistanceAffected;

    public List<string> Tags { get; } = new();

    public string Color { get; set; } = "#B0B0B0";

    public string Icon { get; set; } = string.Empty;

    /// <summary>Kept for old content, not offered for new effects (spec part 5 §9.5: the legacy neutral types).</summary>
    public bool Legacy { get; set; }

    public static IReadOnlyList<FieldOption> Strategies { get; } = new[]
    {
        new FieldOption(ResistanceAffected, "сопротивляемый (учитывает R)"),
        new FieldOption(TrueLike, "как чистый (обходит численные R)"),
    };

    public JsonObject ToJson()
    {
        var tags = new JsonArray();
        foreach (var t in Tags)
        {
            tags.Add(t);
        }

        var obj = new JsonObject
        {
            ["family"] = Family,
            ["strategy"] = Strategy,
            ["tags"] = tags,
            ["color"] = Color,
            ["icon"] = Icon,
        };
        if (Legacy)
        {
            obj["legacy"] = true;
        }

        return obj;
    }

    public static DamageTypeData Read(JsonObject obj, IssueList issues)
    {
        var data = new DamageTypeData
        {
            Family = JsonFields.Text(obj, "family", string.Empty, issues, string.Empty),
            Strategy = JsonFields.Text(obj, "strategy", ResistanceAffected, issues, string.Empty),
            Color = JsonFields.Text(obj, "color", "#B0B0B0", issues, string.Empty),
            Icon = JsonFields.Text(obj, "icon", string.Empty, issues, string.Empty),
            Legacy = JsonFields.Flag(obj, "legacy", false, issues, string.Empty),
        };
        data.Tags.AddRange(JsonFields.TextList(obj, "tags", issues, string.Empty));
        return data;
    }

    public IEnumerable<ContentRef> References()
    {
        if (Family.Length > 0)
        {
            yield return new ContentRef("damage_type", Family);
        }
    }

    public void Validate(ValidationContext context)
    {
        if (Strategy is not (ResistanceAffected or TrueLike))
        {
            context.Issues.Error("strategy", $"неизвестная стратегия расчёта «{Strategy}» (поддерживаются {ResistanceAffected}, {TrueLike})");
        }

        if (Family.Length > 0 && !Model.ContentIds.IsValid(Family))
        {
            context.Issues.Error("family", $"неверный ID семейства «{Family}»");
        }

        if (!FieldSchema.IsColor(Color))
        {
            context.Issues.Error("color", "ожидается цвет #RRGGBB");
        }

        if (Icon.Length > 4)
        {
            context.Issues.Error("icon", "значок — до 4 символов");
        }

        if (Tags.Count > 16)
        {
            context.Issues.Error("tags", "не больше 16 тегов");
        }
    }

    public string Summary() => (Family.Length == 0 ? "семейство" : "подтип") + (Strategy == TrueLike ? " · обходит R" : string.Empty) + (Legacy ? " · старый контент" : string.Empty);

    public IContentData Clone()
    {
        var copy = new DamageTypeData { Family = Family, Strategy = Strategy, Color = Color, Icon = Icon, Legacy = Legacy };
        copy.Tags.AddRange(Tags);
        return copy;
    }
}
