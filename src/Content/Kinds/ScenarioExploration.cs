using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using HexTactics.Content.Model;
using HexTactics.Core.Hex;

namespace HexTactics.Content.Kinds;

/// <summary>A hex of an entry or exit area of an exploration: odd-q offset coordinates, as the hexes of the map.</summary>
public sealed record HexCellData(int Col, int Row)
{
    public HexCoord Cell => HexCoord.FromOffset(Col, Row);

    public JsonObject ToJson() => new() { ["col"] = Col, ["row"] = Row };

    public static HexCellData Read(JsonObject o, IssueList issues, string path) =>
        new(JsonFields.Integer(o, "col", 0, issues, path), JsonFields.Integer(o, "row", 0, issues, path));

    public override string ToString() => $"{Col},{Row}";
}

/// <summary>«Исследование» (spec part 7 §6): entry and exit areas, the exit rule, the key unit, the required members, the extra task.</summary>
public sealed partial class ScenarioData
{
    public const string Exploration = "exploration";

    /// <summary>Hexes per entry or exit area.</summary>
    public const int MaxArea = 64;

    public static IReadOnlyList<(string Value, string Label)> ExitRules { get; } = new[]
    {
        ("key", "ключевой юнит дошёл до выхода"),
        ("any", "любой выживший дошёл до выхода"),
        ("all", "все обязательные дошли до выхода"),
    };

    public static IReadOnlyList<(string Value, string Label)> ExtraGoals { get; } = new[]
    {
        ("none", "нет"),
        ("kill_marked", "победить отмеченных врагов"),
        ("clear_map", "зачистить карту"),
    };

    public bool IsExploration => Mode == Exploration;

    /// <summary>Entry area (<c>PlayerEntry</c>): the squad is deployed only here.</summary>
    public List<HexCellData> Entry { get; } = new();

    /// <summary>Exit area (<c>LevelExit</c>).</summary>
    public List<HexCellData> Exit { get; } = new();

    /// <summary>«key» (default), «any» or «all».</summary>
    public string ExitRule { get; set; } = "key";

    /// <summary>The key unit: its 1-based card of the squad (rule «key»).</summary>
    public int KeyUnit { get; set; } = 1;

    /// <summary>The required members: 1-based cards of the squad (rule «all»).</summary>
    public List<int> Required { get; } = new();

    /// <summary>«none» (default), «kill_marked» or «clear_map».</summary>
    public string ExtraGoal { get; set; } = "none";

    private void WriteExploration(JsonObject json)
    {
        if (Entry.Count > 0)
        {
            json["entry"] = new JsonArray(Entry.Select(c => (JsonNode)c.ToJson()).ToArray());
        }

        if (Exit.Count > 0)
        {
            json["exit"] = new JsonArray(Exit.Select(c => (JsonNode)c.ToJson()).ToArray());
        }

        if (ExitRule != "key")
        {
            json["exitRule"] = ExitRule;
        }

        if (KeyUnit != 1)
        {
            json["keyUnit"] = KeyUnit;
        }

        if (Required.Count > 0)
        {
            json["required"] = new JsonArray(Required.Select(r => (JsonNode)r).ToArray());
        }

        if (ExtraGoal != "none")
        {
            json["extraGoal"] = ExtraGoal;
        }
    }

    private void ReadExploration(JsonObject obj, IssueList issues)
    {
        void Cells(string key, List<HexCellData> into)
        {
            if (obj[key] is not JsonArray array)
            {
                return;
            }

            for (var i = 0; i < array.Count; i++)
            {
                if (array[i] is JsonObject c)
                {
                    into.Add(HexCellData.Read(c, issues, $"{key}[{i}]"));
                }
                else
                {
                    issues.Error($"{key}[{i}]", "гекс — объект { col, row }");
                }
            }
        }

        Cells("entry", Entry);
        Cells("exit", Exit);
        ExitRule = obj.ContainsKey("exitRule") ? JsonFields.Text(obj, "exitRule", "key", issues, string.Empty) : "key";
        KeyUnit = obj.ContainsKey("keyUnit") ? JsonFields.Integer(obj, "keyUnit", 1, issues, string.Empty) : 1;
        ExtraGoal = obj.ContainsKey("extraGoal") ? JsonFields.Text(obj, "extraGoal", "none", issues, string.Empty) : "none";
        if (obj["required"] is JsonArray required)
        {
            foreach (var node in required)
            {
                if (node is JsonValue v && v.TryGetValue<int>(out var slot))
                {
                    Required.Add(slot);
                }
                else
                {
                    issues.Error("required", "обязательный — номер карточки отряда (1, 2, …)");
                }
            }
        }
    }

    private void CopyExplorationTo(ScenarioData copy)
    {
        copy.Entry.AddRange(Entry);
        copy.Exit.AddRange(Exit);
        copy.ExitRule = ExitRule;
        copy.KeyUnit = KeyUnit;
        copy.Required.AddRange(Required);
        copy.ExtraGoal = ExtraGoal;
    }

    /// <summary>The checks of an exploration without the map (spec part 7 §6.2; M7-31, M7-35): an entry, an exit and a reachable task.</summary>
    private void ValidateExploration(ValidationContext context)
    {
        if (Entry.Count == 0)
        {
            context.Issues.Error("entry", "у исследования нет входа — отметьте гексы входа кистью «Вход»");
        }

        if (Exit.Count == 0)
        {
            context.Issues.Error("exit", "у исследования нет выхода — отметьте гексы выхода кистью «Выход»");
        }

        foreach (var (area, key, cells) in new[] { ("входа", "entry", Entry), ("выхода", "exit", Exit) })
        {
            if (cells.Count > MaxArea)
            {
                context.Issues.Error(key, $"гексов {area} не больше {MaxArea}");
            }

            foreach (var twice in cells.GroupBy(c => c).Where(g => g.Count() > 1))
            {
                context.Issues.Error(key, $"гекс {area} {twice.Key} отмечен дважды");
            }
        }

        foreach (var both in Entry.Intersect(Exit))
        {
            context.Issues.Error("exit", $"гекс {both} — и вход, и выход: выход должен быть в другом месте");
        }

        switch (ExitRule)
        {
            case "key":
                if (KeyUnit < 1 || KeyUnit > Squad.Count)
                {
                    context.Issues.Error("keyUnit", $"ключевой юнит — карточка {KeyUnit}, а в отряде {Squad.Count} — выберите ключевого");
                }

                break;
            case "all":
                if (Required.Count == 0)
                {
                    context.Issues.Error("required", "правило «все обязательные» без обязательных — отметьте их в отряде, иначе выход недостижим");
                }

                foreach (var r in Required.Where(r => r < 1 || r > Squad.Count))
                {
                    context.Issues.Error("required", $"обязательный — карточка {r}, а в отряде {Squad.Count}");
                }

                if (Required.Count != Required.Distinct().Count())
                {
                    context.Issues.Error("required", "обязательный юнит указан дважды");
                }

                break;
            case "any":
                break;
            default:
                context.Issues.Error("exitRule", $"неизвестное правило выхода «{ExitRule}»");
                break;
        }

        if (ExtraGoals.All(g => g.Value != ExtraGoal))
        {
            context.Issues.Error("extraGoal", $"неизвестная дополнительная задача «{ExtraGoal}»");
        }
        else if (ExtraGoal == "kill_marked" && !Enemies.Any(e => e.Target))
        {
            context.Issues.Error("extraGoal", "задача «победить отмеченных врагов» без отмеченных — отметьте врагов-целей или выберите другую задачу");
        }
        else if (ExtraGoal == "clear_map" && Enemies.Count == 0)
        {
            context.Issues.Warning("extraGoal", "задача «зачистить карту», а врагов на карте нет — она выполнена с начала");
        }
    }

    /// <summary>The cards of the squad as words: «карточка 1», «карточки 1, 3».</summary>
    private static string Cards(IEnumerable<int> cards)
    {
        var list = cards.ToList();
        return (list.Count == 1 ? "карточка " : "карточки ") + string.Join(", ", list.Select(c => c.ToString(CultureInfo.InvariantCulture)));
    }

    private string ExplorationGoals()
    {
        var win = ExitRule switch
        {
            "any" => "Победа: любой ваш юнит дошёл до выхода живым",
            "all" => $"Победа: все обязательные ({Cards(Required)}) дошли до выхода живыми",
            _ => $"Победа: ключевой юнит ({Cards(new[] { KeyUnit })}) дошёл до выхода живым",
        };
        win += ExtraGoal switch
        {
            "kill_marked" => $" и отмеченные враги ({Enemies.Count(e => e.Target)}) побеждены",
            "clear_map" => " и все враги на карте побеждены",
            _ => string.Empty,
        };
        var lose = new List<string>();
        if (ExitRule == "key")
        {
            lose.Add("ключевой юнит погиб");
        }
        else if (ExitRule == "all")
        {
            lose.Add("погиб обязательный юнит");
        }

        lose.Add("все ваши юниты погибли и в резерве никого");
        if (TimeLimit > 0)
        {
            lose.Add($"прошло {TimeLimit.ToString("0.##", CultureInfo.InvariantCulture)} с боя");
        }

        return win + ". Поражение: " + string.Join(" или ", lose) + ".";
    }
}
