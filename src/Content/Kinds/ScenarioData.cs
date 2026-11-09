using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using HexTactics.Content.Model;
using HexTactics.Content.Registry;
using HexTactics.Content.Schema;
using HexTactics.Core.Hex;

namespace HexTactics.Content.Kinds;

/// <summary>A registered scenario mode (spec part 7 §2.1 <c>ScenarioModeDefinition</c>): only modes with an executor are selectable.</summary>
public sealed record ScenarioMode(string Id, string Title, string Summary, bool Available, string AvailableFrom);

/// <summary>A unit already on the map when the scenario starts (spec part 7 §3.1 p.2): a library unit, a hex and a facing.</summary>
public sealed class PlacedUnitData
{
    public string Unit { get; set; } = string.Empty;

    public int Col { get; set; }

    public int Row { get; set; }

    public HexDirection Facing { get; set; } = HexDirection.South;

    public HexCoord Cell => HexCoord.FromOffset(Col, Row);

    public PlacedUnitData Copy() => new() { Unit = Unit, Col = Col, Row = Row, Facing = Facing };
}

/// <summary>
/// A scenario (spec part 7 §2.1 <c>ScenarioDefinition</c>): one object for every mode — the map, the mode, optional combat
/// rules, the player's squad, the enemies already on the map, the switches of automatic attacks and blocking, and goals. In this
/// edition the mode «Обычный бой» runs; the other modes are registered and become selectable with their executors (D-174).
/// </summary>
public sealed class ScenarioData : IContentData
{
    public const string Battle = "battle";

    public const int MaxSquad = 5;

    public const int MaxEnemies = 64;

    public static IReadOnlyList<ScenarioMode> Modes { get; } = new[]
    {
        new ScenarioMode(Battle, "Обычный бой", "Враги уже на карте; выставьте отряд и победите всех.", true, "v0.34.0"),
        new ScenarioMode("tower_defense", "Tower Defense", "Базы, красные маршруты и волны врагов.", false, "v0.36.0"),
        new ScenarioMode("defense", "Оборона", "Враги прибывают, своей базы нет; простой ИИ.", false, "v0.37.0"),
        new ScenarioMode("exploration", "Исследование", "От входа к выходу через бои.", false, "v0.38.0"),
    };

    public string Mode { get; set; } = Battle;

    public string Map { get; set; } = "core.map.prototype";

    /// <summary>Combat rules of the scenario; empty — the map's (the scenario is above the map, D-150).</summary>
    public string Ruleset { get; set; } = string.Empty;

    /// <summary>Units hit enemies in their base attack area on their own (spec part 7 §8; on for scenarios, D-170).</summary>
    public bool AutoAttacks { get; set; } = true;

    /// <summary>Blocking acts (spec part 7 §7.3: a scenario may switch it off for a free manoeuvre battle).</summary>
    public bool Blocking { get; set; } = true;

    /// <summary>The player's roster: library units, one card each, in order (1…5).</summary>
    public List<string> Squad { get; } = new() { "core.unit.operative", "core.unit.operative", "core.unit.operative" };

    public List<PlacedUnitData> Enemies { get; } = new();

    /// <summary>An additional defeat condition: game seconds of the battle; 0 — none (spec part 7 §3.2).</summary>
    public double TimeLimit { get; set; }

    /// <summary>«Подготовка» (spec part 7 §2.3, §3.4): the battle waits on a pause until «Начать»; off — it runs at once.</summary>
    public bool StartPaused { get; set; } = true;

    public JsonObject ToJson() => new()
    {
        ["mode"] = Mode, ["map"] = Map, ["ruleset"] = Ruleset, ["autoAttacks"] = AutoAttacks, ["blocking"] = Blocking,
        ["squad"] = new JsonArray(Squad.Select(s => (JsonNode)s).ToArray()),
        ["enemies"] = new JsonArray(Enemies.Select(e => (JsonNode)new JsonObject { ["unit"] = e.Unit, ["col"] = e.Col, ["row"] = e.Row, ["facing"] = e.Facing.ToString() }).ToArray()),
        ["timeLimit"] = TimeLimit, ["startPaused"] = StartPaused,
    };

    public static ScenarioData Read(JsonObject obj, IssueList issues)
    {
        var data = new ScenarioData
        {
            Mode = JsonFields.Text(obj, "mode", Battle, issues, string.Empty),
            Map = JsonFields.Text(obj, "map", "core.map.prototype", issues, string.Empty),
            Ruleset = JsonFields.Text(obj, "ruleset", string.Empty, issues, string.Empty),
            AutoAttacks = JsonFields.Flag(obj, "autoAttacks", true, issues, string.Empty),
            Blocking = JsonFields.Flag(obj, "blocking", true, issues, string.Empty),
            TimeLimit = JsonFields.Number(obj, "timeLimit", 0, issues, string.Empty),
            StartPaused = JsonFields.Flag(obj, "startPaused", true, issues, string.Empty),
        };
        if (obj["squad"] is JsonArray)
        {
            data.Squad.Clear();
            data.Squad.AddRange(JsonFields.TextList(obj, "squad", issues, string.Empty));
        }

        if (obj["enemies"] is JsonArray enemies)
        {
            foreach (var node in enemies)
            {
                if (node is not JsonObject e)
                {
                    issues.Error("enemies", "враг — объект { unit, col, row, facing }");
                    continue;
                }

                var placed = new PlacedUnitData
                {
                    Unit = JsonFields.Text(e, "unit", string.Empty, issues, "enemies"),
                    Col = (int)Math.Round(JsonFields.Number(e, "col", 0, issues, "enemies")),
                    Row = (int)Math.Round(JsonFields.Number(e, "row", 0, issues, "enemies")),
                };
                if (Enum.TryParse<HexDirection>(JsonFields.Text(e, "facing", "South", issues, "enemies"), out var facing))
                {
                    placed.Facing = facing;
                }
                else
                {
                    issues.Error("enemies", "направление взгляда врага — North, NorthEast, SouthEast, South, SouthWest или NorthWest");
                }

                data.Enemies.Add(placed);
            }
        }

        return data;
    }

    public IEnumerable<ContentRef> References()
    {
        if (Map.Length > 0)
        {
            yield return new ContentRef(ContentTypeRegistry.Map, Map);
        }

        if (Ruleset.Length > 0)
        {
            yield return new ContentRef(ContentTypeRegistry.CombatRuleset, Ruleset);
        }

        foreach (var unit in Squad.Concat(Enemies.Select(e => e.Unit)).Where(u => u.Length > 0).Distinct(StringComparer.Ordinal))
        {
            yield return new ContentRef(ContentTypeRegistry.Unit, unit);
        }
    }

    public void RemapReferences(IReadOnlyDictionary<ContentRef, string> newIds)
    {
        string Map1(string kind, string id) => newIds.TryGetValue(new ContentRef(kind, id), out var n) ? n : id;
        Map = Map1(ContentTypeRegistry.Map, Map);
        Ruleset = Ruleset.Length > 0 ? Map1(ContentTypeRegistry.CombatRuleset, Ruleset) : Ruleset;
        for (var i = 0; i < Squad.Count; i++)
        {
            Squad[i] = Map1(ContentTypeRegistry.Unit, Squad[i]);
        }

        foreach (var e in Enemies)
        {
            e.Unit = Map1(ContentTypeRegistry.Unit, e.Unit);
        }
    }

    public void Validate(ValidationContext context)
    {
        if (Modes.FirstOrDefault(m => m.Id == Mode) is not { } mode)
        {
            context.Issues.Error("mode", $"неизвестный режим сценария «{Mode}»");
        }
        else if (!mode.Available)
        {
            context.Issues.Error("mode", $"режим «{mode.Title}» появится в {mode.AvailableFrom} — запуск невозможен");
        }

        if (Map.Length == 0)
        {
            context.Issues.Error("map", "не выбрана карта сценария");
        }

        if (Squad.Count is < 1 or > MaxSquad)
        {
            context.Issues.Error("squad", $"в отряде игрока — от 1 до {MaxSquad} юнитов, сейчас {Squad.Count}");
        }

        if (Mode == Battle && Enemies.Count == 0)
        {
            context.Issues.Error("enemies", "в обычном бою нужен хотя бы один враг на карте — иначе нечего побеждать");
        }

        if (Enemies.Count > MaxEnemies)
        {
            context.Issues.Error("enemies", $"не больше {MaxEnemies} врагов на карте");
        }

        foreach (var group in Enemies.GroupBy(e => (e.Col, e.Row)).Where(g => g.Count() > 1))
        {
            context.Issues.Error("enemies", $"на гексе {group.Key.Col},{group.Key.Row} два врага — один гекс, один юнит");
        }

        if (Enemies.Any(e => e.Unit.Length == 0))
        {
            context.Issues.Error("enemies", "у врага не выбран юнит");
        }

        if (!double.IsFinite(TimeLimit) || TimeLimit < 0 || TimeLimit > 36000)
        {
            context.Issues.Error("timeLimit", $"ограничение времени — от 0 (нет) до 36000 с, сейчас {TimeLimit.ToString("0.##", CultureInfo.InvariantCulture)}");
        }
    }

    /// <summary>«Обычный бой · врагов 3 · отряд 3».</summary>
    public string Summary() =>
        $"{Modes.FirstOrDefault(m => m.Id == Mode)?.Title ?? Mode} · врагов {Enemies.Count} · отряд {Squad.Count}" +
        (TimeLimit > 0 ? $" · {TimeLimit.ToString("0.##", CultureInfo.InvariantCulture)} с" : string.Empty);

    public IContentData Clone()
    {
        var copy = new ScenarioData { Mode = Mode, Map = Map, Ruleset = Ruleset, AutoAttacks = AutoAttacks, Blocking = Blocking, TimeLimit = TimeLimit, StartPaused = StartPaused };
        copy.Squad.Clear();
        copy.Squad.AddRange(Squad);
        copy.Enemies.AddRange(Enemies.Select(e => e.Copy()));
        return copy;
    }

    /// <summary>
    /// The goals in plain words (spec part 7 §9.3 «итог понятным языком»): «Победа: все 3 врага побеждены. Поражение: все ваши
    /// юниты погибли и резерв пуст.».
    /// </summary>
    public string GoalsText()
    {
        var victory = $"Победа: все враги ({Enemies.Count}) побеждены.";
        var defeat = "Поражение: все ваши юниты погибли и в резерве никого" + (TimeLimit > 0 ? $" или прошло {TimeLimit.ToString("0.##", CultureInfo.InvariantCulture)} с боя." : ".");
        return victory + " " + defeat;
    }
}
