using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using HexTactics.Content.Model;
using HexTactics.Content.Registry;
using HexTactics.Content.Schema;
using HexTactics.Core.Combat;
using HexTactics.Core.Hex;

namespace HexTactics.Content.Kinds;

/// <summary>
/// A reusable automatic base attack (spec part 7 §8.2 <c>BasicAttackDefinition</c>): its own area of hexes relative to the
/// unit (axial offsets for a unit facing north, like skill areas), how it chooses targets, how strong a hit is (a share of the
/// unit's Attack Power) and of which type. Units refer to it by id; changing a skill never changes it (M7-43).
/// </summary>
public sealed class BasicAttackData : IContentData
{
    /// <summary>Area hexes are kept within this distance of the unit (the editor's board).</summary>
    public const int MaxRadius = 4;

    public const int MaxCells = 36;

    public const string Nearest = "nearest";
    public const string LowestHealth = "lowest_hp";
    public const string PatternOrder = "pattern_order";

    public static IReadOnlyList<(string Value, string Label)> Priorities { get; } = new[]
    {
        (Nearest, "ближайший враг"), (LowestHealth, "враг с наименьшим здоровьем"), (PatternOrder, "по порядку клеток области"),
    };

    /// <summary>Area cells in the order the author added them (the order is the priority of «по порядку клеток»).</summary>
    public List<HexCoord> Cells { get; } = new() { new HexCoord(0, -1) };

    public string TargetPriority { get; set; } = Nearest;

    public int MaxTargets { get; set; } = 1;

    public double PowerPercent { get; set; } = 100;

    /// <summary>Own damage type of the hit; empty — the unit's normal attack type.</summary>
    public string DamageType { get; set; } = string.Empty;

    public bool CanCrit { get; set; } = true;

    public bool CanSurprise { get; set; } = true;

    public bool WhileMoving { get; set; } = true;

    public JsonObject ToJson() => new()
    {
        ["cells"] = new JsonArray(Cells.Select(c => (JsonNode)new JsonObject { ["q"] = c.Q, ["r"] = c.R }).ToArray()),
        ["targetPriority"] = TargetPriority, ["maxTargets"] = MaxTargets, ["powerPercent"] = PowerPercent, ["damageType"] = DamageType,
        ["canCrit"] = CanCrit, ["canSurprise"] = CanSurprise, ["whileMoving"] = WhileMoving,
    };

    public static BasicAttackData Read(JsonObject obj, IssueList issues)
    {
        var data = new BasicAttackData
        {
            TargetPriority = JsonFields.Text(obj, "targetPriority", Nearest, issues, string.Empty),
            MaxTargets = (int)Math.Round(JsonFields.Number(obj, "maxTargets", 1, issues, string.Empty)),
            PowerPercent = JsonFields.Number(obj, "powerPercent", 100, issues, string.Empty),
            DamageType = JsonFields.Text(obj, "damageType", string.Empty, issues, string.Empty),
            CanCrit = JsonFields.Flag(obj, "canCrit", true, issues, string.Empty),
            CanSurprise = JsonFields.Flag(obj, "canSurprise", true, issues, string.Empty),
            WhileMoving = JsonFields.Flag(obj, "whileMoving", true, issues, string.Empty),
        };
        if (obj["cells"] is JsonArray cells)
        {
            data.Cells.Clear();
            foreach (var node in cells)
            {
                if (node is JsonObject c && c["q"] is JsonValue q && q.TryGetValue<int>(out var qi) && c["r"] is JsonValue r && r.TryGetValue<int>(out var ri))
                {
                    data.Cells.Add(new HexCoord(qi, ri));
                }
                else
                {
                    issues.Error("cells", "клетка области — объект { \"q\": целое, \"r\": целое }");
                }
            }
        }

        return data;
    }

    public IEnumerable<ContentRef> References() =>
        DamageType.Length > 0 ? new[] { new ContentRef(ContentTypeRegistry.DamageType, DamageType) } : Array.Empty<ContentRef>();

    public void RemapReferences(IReadOnlyDictionary<ContentRef, string> newIds)
    {
        if (DamageType.Length > 0 && newIds.TryGetValue(new ContentRef(ContentTypeRegistry.DamageType, DamageType), out var id))
        {
            DamageType = id;
        }
    }

    public void Validate(ValidationContext context)
    {
        if (Cells.Count == 0)
        {
            context.Issues.Error("cells", "в области базовой атаки нет ни одного гекса — отметьте хотя бы один");
        }
        else if (Cells.Count > MaxCells)
        {
            context.Issues.Error("cells", $"не больше {MaxCells} гексов в области");
        }

        if (Cells.Any(c => c == default))
        {
            context.Issues.Error("cells", "собственный гекс юнита не может быть частью области");
        }

        if (Cells.Any(c => c.DistanceTo(default) > MaxRadius))
        {
            context.Issues.Error("cells", $"гексы области — не дальше {MaxRadius} от юнита");
        }

        if (Cells.Distinct().Count() != Cells.Count)
        {
            context.Issues.Error("cells", "гекс указан в области дважды");
        }

        if (Priorities.All(p => p.Value != TargetPriority))
        {
            context.Issues.Error("targetPriority", $"неизвестный порядок выбора цели «{TargetPriority}»");
        }

        if (MaxTargets is < 1 or > 6)
        {
            context.Issues.Error("maxTargets", $"целей за удар — от 1 до 6, сейчас {MaxTargets}");
        }

        if (!double.IsFinite(PowerPercent) || PowerPercent < 0 || PowerPercent > 1000)
        {
            context.Issues.Error("powerPercent", $"сила удара — от 0 до 1000 % силы атаки, сейчас {PowerPercent.ToString("0.##", CultureInfo.InvariantCulture)}");
        }
    }

    /// <summary>«2 гекса · ближайший враг · 100 % атаки».</summary>
    public string Summary() =>
        $"{Cells.Count} {(Cells.Count == 1 ? "гекс" : Cells.Count < 5 ? "гекса" : "гексов")} · {Priorities.FirstOrDefault(p => p.Value == TargetPriority).Label ?? TargetPriority}" +
        (MaxTargets > 1 ? $" · до {MaxTargets} целей" : string.Empty) + $" · {PowerPercent.ToString("0.##", CultureInfo.InvariantCulture)} % атаки";

    public IContentData Clone()
    {
        var copy = new BasicAttackData
        {
            TargetPriority = TargetPriority, MaxTargets = MaxTargets, PowerPercent = PowerPercent, DamageType = DamageType,
            CanCrit = CanCrit, CanSurprise = CanSurprise, WhileMoving = WhileMoving,
        };
        copy.Cells.Clear();
        copy.Cells.AddRange(Cells);
        return copy;
    }

    /// <summary>The runtime definition (spec part 7 §8.2); the id and name come from the content item.</summary>
    public BasicAttackDefinition ToDefinition(string id, string name) => new()
    {
        Id = id, Name = name, Cells = Cells.ToArray(),
        Priority = TargetPriority switch { LowestHealth => Core.Combat.TargetPriority.LowestHealth, PatternOrder => Core.Combat.TargetPriority.PatternOrder, _ => Core.Combat.TargetPriority.Nearest },
        MaxTargets = Math.Clamp(MaxTargets, 1, 6), PowerPercent = Math.Max(0, PowerPercent), DamageTypeId = DamageType.Length > 0 ? DamageType : null,
        CanCrit = CanCrit, CanSurprise = CanSurprise, WhileMoving = WhileMoving,
    };
}
