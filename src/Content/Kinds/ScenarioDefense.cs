using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using HexTactics.Content.Model;
using HexTactics.Core.Hex;

namespace HexTactics.Content.Kinds;

/// <summary>
/// The AI of an enemy in the data (spec part 7 §5.2): one of five presets and its few numbers. «none» — the unit stands (or
/// walks its route) and hits with its automatic base attack.
/// </summary>
public sealed class AiData
{
    public static IReadOnlyList<(string Value, string Label)> Presets { get; } = new[]
    {
        ("none", "стоит и бьёт автоатакой"),
        ("attack_units", "атаковать юнитов"),
        ("attack_objective", "к объекту"),
        ("units_then_objective", "сначала юниты, потом объект"),
        ("objective_then_units", "сначала объект, потом юниты"),
        ("guard", "охранник"),
    };

    public static IReadOnlyList<(string Value, string Label)> Priorities { get; } = new[]
    {
        ("nearest", "ближайший"),
        ("lowest_health", "меньше здоровья"),
    };

    public string Preset { get; set; } = "none";

    /// <summary>Hexes; 0 — the whole map.</summary>
    public int DetectRange { get; set; }

    public string Priority { get; set; } = "nearest";

    public double Reevaluate { get; set; } = 1;

    public int GuardRadius { get; set; } = 3;

    public bool SwitchToBlocker { get; set; } = true;

    public bool IsNone => Preset == "none";

    public AiData Copy() => new() { Preset = Preset, DetectRange = DetectRange, Priority = Priority, Reevaluate = Reevaluate, GuardRadius = GuardRadius, SwitchToBlocker = SwitchToBlocker };

    public JsonObject ToJson() => new()
    {
        ["preset"] = Preset, ["detectRange"] = DetectRange, ["priority"] = Priority, ["reevaluate"] = Reevaluate,
        ["guardRadius"] = GuardRadius, ["switchToBlocker"] = SwitchToBlocker,
    };

    public static AiData Read(JsonObject o, IssueList issues, string path) => new()
    {
        Preset = JsonFields.Text(o, "preset", "none", issues, path),
        DetectRange = JsonFields.Integer(o, "detectRange", 0, issues, path),
        Priority = JsonFields.Text(o, "priority", "nearest", issues, path),
        Reevaluate = JsonFields.Number(o, "reevaluate", 1, issues, path),
        GuardRadius = JsonFields.Integer(o, "guardRadius", 3, issues, path),
        SwitchToBlocker = JsonFields.Flag(o, "switchToBlocker", true, issues, path),
    };

    /// <summary>Problems of the numbers, for the checks of the scenario.</summary>
    public IEnumerable<string> Problems()
    {
        if (Presets.All(p => p.Value != Preset))
        {
            yield return $"неизвестный пресет ИИ «{Preset}»";
        }

        if (Priorities.All(p => p.Value != Priority))
        {
            yield return $"неизвестный приоритет цели «{Priority}»";
        }

        if (DetectRange is < 0 or > 99 || GuardRadius is < 0 or > 99)
        {
            yield return "дальность обнаружения и радиус охраны — от 0 до 99 гексов";
        }

        if (!double.IsFinite(Reevaluate) || Reevaluate < 0.1 || Reevaluate > 60)
        {
            yield return "пересмотр цели — от 0,1 до 60 с";
        }
    }

    /// <summary>«охранник, радиус 3» — short words for lists.</summary>
    public string Short() => Preset switch
    {
        "guard" => $"охранник, радиус {GuardRadius}",
        _ => Presets.FirstOrDefault(p => p.Value == Preset).Label ?? Preset,
    };
}

/// <summary>A defended mission object of a scenario (spec part 7 §5.1): a hex, health and whether its loss is a defeat.</summary>
public sealed class MissionObjectData
{
    public string Id { get; set; } = "object1";

    public string Name { get; set; } = "Объект";

    public int Col { get; set; }

    public int Row { get; set; }

    public double Health { get; set; } = 500;

    public bool Critical { get; set; } = true;

    public HexCoord Cell => HexCoord.FromOffset(Col, Row);

    public MissionObjectData Copy() => new() { Id = Id, Name = Name, Col = Col, Row = Row, Health = Health, Critical = Critical };

    public JsonObject ToJson() => new() { ["id"] = Id, ["name"] = Name, ["col"] = Col, ["row"] = Row, ["health"] = Health, ["critical"] = Critical };

    public static MissionObjectData Read(JsonObject o, IssueList issues, string path) => new()
    {
        Id = JsonFields.Text(o, "id", "object1", issues, path),
        Name = JsonFields.Text(o, "name", "Объект", issues, path),
        Col = JsonFields.Integer(o, "col", 0, issues, path),
        Row = JsonFields.Integer(o, "row", 0, issues, path),
        Health = JsonFields.Number(o, "health", 500, issues, path),
        Critical = JsonFields.Flag(o, "critical", true, issues, path),
    };

    public string Describe() => $"«{Name}» на {Col},{Row}: {Health.ToString("0.##", CultureInfo.InvariantCulture)} HP" + (Critical ? ", критический" : string.Empty);
}
