using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using HexTactics.Content.Model;

namespace HexTactics.Content.Kinds;

/// <summary>
/// A group of a wave (spec part 7 §4.5): <see cref="Count"/> enemies of one library unit along one route of the scenario's map;
/// the k-th appears at wave start + offset + first delay + k × interval (game seconds).
/// </summary>
public sealed class SpawnGroupData
{
    public string Id { get; set; } = "group1";

    /// <summary>Id of a route of the scenario's map (a stable map route id).</summary>
    public string Route { get; set; } = string.Empty;

    /// <summary>Library unit of the enemies.</summary>
    public string Unit { get; set; } = string.Empty;

    public int Count { get; set; } = 5;

    public double FirstDelay { get; set; }

    public double Interval { get; set; } = 1;

    public double Offset { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Defense (spec part 7 §5.1): «base:N» (an enemy base) or «zone:N» (a spawn zone) when the group has no route.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>The AI of the group's enemies (spec part 7 §5.2); route walkers keep «none».</summary>
    public AiData Ai { get; set; } = new();

    public SpawnGroupData Copy() => new() { Id = Id, Route = Route, Unit = Unit, Count = Count, FirstDelay = FirstDelay, Interval = Interval, Offset = Offset, Enabled = Enabled, Source = Source, Ai = Ai.Copy() };

    public JsonObject ToJson()
    {
        var json = new JsonObject
        {
            ["id"] = Id, ["route"] = Route, ["unit"] = Unit, ["count"] = Count, ["firstDelay"] = FirstDelay, ["interval"] = Interval,
            ["offset"] = Offset, ["enabled"] = Enabled,
        };
        if (Source.Length > 0)
        {
            json["source"] = Source;
        }

        if (!Ai.IsNone)
        {
            json["ai"] = Ai.ToJson();
        }

        return json;
    }

    public static SpawnGroupData Read(JsonObject o, IssueList issues, string path) => new()
    {
        Id = JsonFields.Text(o, "id", "group1", issues, path),
        Route = JsonFields.Text(o, "route", string.Empty, issues, path),
        Unit = JsonFields.Text(o, "unit", string.Empty, issues, path),
        Count = JsonFields.Integer(o, "count", 0, issues, path),
        FirstDelay = JsonFields.Number(o, "firstDelay", 0, issues, path),
        Interval = JsonFields.Number(o, "interval", 1, issues, path),
        Offset = JsonFields.Number(o, "offset", 0, issues, path),
        Enabled = JsonFields.Flag(o, "enabled", true, issues, path),
        Source = o.ContainsKey("source") ? JsonFields.Text(o, "source", string.Empty, issues, path) : string.Empty,
        Ai = o["ai"] is JsonObject ai ? AiData.Read(ai, issues, path) : new AiData(),
    };
}

/// <summary>A wave of a Tower Defense scenario: its start rule, delay and groups (several routes at once).</summary>
public sealed class WaveData
{
    public const string AfterPrevious = "after_previous";
    public const string AtTime = "at_time";
    public const string AfterPreviousStart = "after_previous_start";

    public static IReadOnlyList<(string Value, string Label)> Starts { get; } = new[]
    {
        (AfterPrevious, "после окончания предыдущей"),
        (AtTime, "в секунду боя"),
        (AfterPreviousStart, "после начала предыдущей"),
    };

    public string Id { get; set; } = "wave1";

    public string Name { get; set; } = "Волна 1";

    public string Start { get; set; } = AfterPrevious;

    /// <summary>Seconds: the delay after the previous wave (or its start), or the second of the battle for <see cref="AtTime"/>.</summary>
    public double Delay { get; set; }

    public bool Enabled { get; set; } = true;

    public List<SpawnGroupData> Groups { get; } = new();

    /// <summary>Enemies of the wave — the sum of its enabled groups, never stored twice (spec part 7 §4.5).</summary>
    public int Total => Groups.Where(g => g.Enabled).Sum(g => Math.Max(0, g.Count));

    public WaveData Copy()
    {
        var copy = new WaveData { Id = Id, Name = Name, Start = Start, Delay = Delay, Enabled = Enabled };
        copy.Groups.AddRange(Groups.Select(g => g.Copy()));
        return copy;
    }

    public JsonObject ToJson() => new()
    {
        ["id"] = Id, ["name"] = Name, ["start"] = Start, ["delay"] = Delay, ["enabled"] = Enabled,
        ["groups"] = new JsonArray(Groups.Select(g => (JsonNode)g.ToJson()).ToArray()),
    };

    public static WaveData Read(JsonObject o, IssueList issues, string path)
    {
        var wave = new WaveData
        {
            Id = JsonFields.Text(o, "id", "wave1", issues, path),
            Name = JsonFields.Text(o, "name", string.Empty, issues, path),
            Start = JsonFields.Text(o, "start", AfterPrevious, issues, path),
            Delay = JsonFields.Number(o, "delay", 0, issues, path),
            Enabled = JsonFields.Flag(o, "enabled", true, issues, path),
        };
        if (o["groups"] is JsonArray groups)
        {
            for (var i = 0; i < groups.Count; i++)
            {
                if (groups[i] is JsonObject g)
                {
                    wave.Groups.Add(SpawnGroupData.Read(g, issues, $"{path}.groups[{i}]"));
                }
                else
                {
                    issues.Error($"{path}.groups[{i}]", "группа волны — объект");
                }
            }
        }

        return wave;
    }

    /// <summary>«сразу», «через 5 с после окончания предыдущей», «на 30-й секунде боя».</summary>
    public string StartText(bool first)
    {
        var s = Delay.ToString("0.##", CultureInfo.InvariantCulture);
        return Start switch
        {
            AtTime => $"на {s} с боя",
            AfterPreviousStart => first ? $"через {s} с после начала боя" : $"через {s} с после начала предыдущей",
            _ => first ? (Delay > 0 ? $"через {s} с после начала боя" : "сразу") : (Delay > 0 ? $"через {s} с после окончания предыдущей" : "после окончания предыдущей"),
        };
    }
}

/// <summary>The lives of a player base in a Tower Defense scenario (spec part 7 §4.6).</summary>
public sealed class BaseLivesData
{
    /// <summary>PROTOTYPE_DEFAULT: lives of a base the author did not set.</summary>
    public const int DefaultLives = 20;

    public int Base { get; set; } = 1;

    public int Lives { get; set; } = DefaultLives;

    public bool Critical { get; set; } = true;

    public BaseLivesData Copy() => new() { Base = Base, Lives = Lives, Critical = Critical };
}
