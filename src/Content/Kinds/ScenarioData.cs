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

    /// <summary>Its simple AI (spec part 7 §3.3: «Охранник» in a battle; any preset in a defense); «none» — stands and hits.</summary>
    public AiData Ai { get; set; } = new();

    /// <summary>Exploration: a target of the extra task «defeat the marked enemies» (spec part 7 §6.1).</summary>
    public bool Target { get; set; }

    public PlacedUnitData Copy() => new() { Unit = Unit, Col = Col, Row = Row, Facing = Facing, Ai = Ai.Copy(), Target = Target };
}

/// <summary>
/// A scenario (spec part 7 §2.1 <c>ScenarioDefinition</c>): one object for every mode — the map, the mode, optional combat
/// rules, the player's squad, the enemies already on the map, the switches of automatic attacks and blocking, and goals. In this
/// edition the mode «Обычный бой» runs; the other modes are registered and become selectable with their executors (D-174).
/// </summary>
public sealed partial class ScenarioData : IContentData
{
    public const string Battle = "battle";

    public const string TowerDefense = "tower_defense";

    public const string Defense = "defense";

    public const int MaxObjects = 16;

    public const int MaxWaves = 50;

    public const int MaxGroups = 20;

    public static IReadOnlyList<(string Value, string Label)> DefeatPolicies { get; } = new[]
    {
        ("any_critical", "любая критическая база без жизней"),
        ("all_bases", "все базы без жизней"),
        ("shared_lives", "общий запас жизней баз исчерпан"),
    };

    public const int MaxSquad = 5;

    public const int MaxEnemies = 64;

    public static IReadOnlyList<ScenarioMode> Modes { get; } = new[]
    {
        new ScenarioMode(Battle, "Обычный бой", "Враги уже на карте; выставьте отряд и победите всех.", true, "v0.34.0"),
        new ScenarioMode(TowerDefense, "Tower Defense", "Враги идут волнами по красным маршрутам карты к базам игрока.", true, "v0.36.0"),
        new ScenarioMode(Defense, "Оборона", "Враги прибывают волнами со своих баз и зон с простым ИИ; базы игрока нет — выстоять время или отбить все волны, сберечь объекты.", true, "v0.37.0"),
        new ScenarioMode(Exploration, "Исследование", "Отряд входит в отмеченном месте и идёт к выходу через бои; победа — дойти до выхода по выбранному правилу.", true, "v0.38.0"),
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

    /// <summary>Tower Defense waves (spec part 7 §4.5); written only when there are some.</summary>
    public List<WaveData> Waves { get; } = new();

    /// <summary>Lives of the player bases (spec part 7 §4.6); a base without an entry has <see cref="BaseLivesData.DefaultLives"/>.</summary>
    public List<BaseLivesData> BaseLives { get; } = new();

    /// <summary>The defeat policy of the base lives: any_critical (default), all_bases, shared_lives.</summary>
    public string Defeat { get; set; } = "any_critical";

    public bool IsTowerDefense => Mode == TowerDefense;

    public bool IsDefense => Mode == Defense;

    /// <summary>Defense: the defended objects (spec part 7 §5.1).</summary>
    public List<MissionObjectData> Objects { get; } = new();

    /// <summary>Defense: «all» — every critical object must survive (default), «any» — at least one.</summary>
    public string ObjectsPolicy { get; set; } = "all";

    /// <summary>Defense: game seconds to hold out; 0 — repel every wave (spec part 7 §5.3).</summary>
    public double SurviveSeconds { get; set; }

    /// <summary>Defense: the death of the whole squad (with an empty reserve) is a defeat.</summary>
    public bool SquadLossDefeat { get; set; } = true;

    /// <summary>Enemies of the scenario's enabled waves — the sum of the groups.</summary>
    public int WaveEnemies => Waves.Where(w => w.Enabled).Sum(w => w.Total);

    public BaseLivesData LivesOf(int baseNumber) => BaseLives.Find(b => b.Base == baseNumber) ?? new BaseLivesData { Base = baseNumber };

    public JsonObject ToJson()
    {
        var json = BaseJson();
        if (Waves.Count > 0)
        {
            json["waves"] = new JsonArray(Waves.Select(w => (JsonNode)w.ToJson()).ToArray());
        }

        if (BaseLives.Count > 0)
        {
            json["bases"] = new JsonArray(BaseLives.Select(b => (JsonNode)new JsonObject { ["base"] = b.Base, ["lives"] = b.Lives, ["critical"] = b.Critical }).ToArray());
        }

        if (Defeat != "any_critical")
        {
            json["defeat"] = Defeat;
        }

        if (Objects.Count > 0)
        {
            json["objects"] = new JsonArray(Objects.Select(o => (JsonNode)o.ToJson()).ToArray());
        }

        if (ObjectsPolicy != "all")
        {
            json["objectsPolicy"] = ObjectsPolicy;
        }

        if (SurviveSeconds > 0)
        {
            json["survive"] = SurviveSeconds;
        }

        if (!SquadLossDefeat)
        {
            json["squadLoss"] = false;
        }

        WriteExploration(json);
        return json;
    }

    private JsonObject BaseJson() => new()
    {
        ["mode"] = Mode, ["map"] = Map, ["ruleset"] = Ruleset, ["autoAttacks"] = AutoAttacks, ["blocking"] = Blocking,
        ["squad"] = new JsonArray(Squad.Select(s => (JsonNode)s).ToArray()),
        ["enemies"] = new JsonArray(Enemies.Select(e => (JsonNode)EnemyJson(e)).ToArray()),
        ["timeLimit"] = TimeLimit, ["startPaused"] = StartPaused,
    };

    private static JsonObject EnemyJson(PlacedUnitData e)
    {
        var json = new JsonObject { ["unit"] = e.Unit, ["col"] = e.Col, ["row"] = e.Row, ["facing"] = e.Facing.ToString() };
        if (!e.Ai.IsNone)
        {
            json["ai"] = e.Ai.ToJson();
        }

        if (e.Target)
        {
            json["target"] = true;
        }

        return json;
    }

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
            Defeat = obj.ContainsKey("defeat") ? JsonFields.Text(obj, "defeat", "any_critical", issues, string.Empty) : "any_critical",
            ObjectsPolicy = obj.ContainsKey("objectsPolicy") ? JsonFields.Text(obj, "objectsPolicy", "all", issues, string.Empty) : "all",
            SurviveSeconds = obj.ContainsKey("survive") ? JsonFields.Number(obj, "survive", 0, issues, string.Empty) : 0,
            SquadLossDefeat = !obj.ContainsKey("squadLoss") || JsonFields.Flag(obj, "squadLoss", true, issues, string.Empty),
        };
        if (obj["objects"] is JsonArray objects)
        {
            for (var i = 0; i < objects.Count; i++)
            {
                if (objects[i] is JsonObject o)
                {
                    data.Objects.Add(MissionObjectData.Read(o, issues, $"objects[{i}]"));
                }
            }
        }

        if (obj["waves"] is JsonArray waves)
        {
            for (var i = 0; i < waves.Count; i++)
            {
                if (waves[i] is JsonObject w)
                {
                    data.Waves.Add(WaveData.Read(w, issues, $"waves[{i}]"));
                }
                else
                {
                    issues.Error($"waves[{i}]", "волна — объект");
                }
            }
        }

        if (obj["bases"] is JsonArray bases)
        {
            for (var i = 0; i < bases.Count; i++)
            {
                if (bases[i] is JsonObject b)
                {
                    data.BaseLives.Add(new BaseLivesData
                    {
                        Base = JsonFields.Integer(b, "base", 1, issues, $"bases[{i}]"),
                        Lives = JsonFields.Integer(b, "lives", BaseLivesData.DefaultLives, issues, $"bases[{i}]"),
                        Critical = JsonFields.Flag(b, "critical", true, issues, $"bases[{i}]"),
                    });
                }
            }
        }

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

                if (e["ai"] is JsonObject ai)
                {
                    placed.Ai = AiData.Read(ai, issues, "enemies");
                }

                placed.Target = e.ContainsKey("target") && JsonFields.Flag(e, "target", false, issues, "enemies");

                data.Enemies.Add(placed);
            }
        }

        data.ReadExploration(obj, issues);
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

        foreach (var unit in Squad.Concat(Enemies.Select(e => e.Unit)).Concat(Waves.SelectMany(w => w.Groups).Select(g => g.Unit)).Where(u => u.Length > 0).Distinct(StringComparer.Ordinal))
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

        foreach (var g in Waves.SelectMany(w => w.Groups))
        {
            g.Unit = Map1(ContentTypeRegistry.Unit, g.Unit); // route ids belong to the map and stay with it
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

        if (IsExploration)
        {
            ValidateExploration(context);
        }

        if (IsTowerDefense && WaveEnemies == 0)
        {
            context.Issues.Error("waves", "в Tower Defense нужна хотя бы одна включённая волна с врагами");
        }

        if (IsDefense)
        {
            // Spec part 7 §5.3: an end must be reachable — a time to hold out, or finite waves to repel.
            if (SurviveSeconds <= 0 && WaveEnemies == 0)
            {
                context.Issues.Error("survive", "оборона без окончания: задайте «Выстоять, с» или хотя бы одну волну с врагами");
            }

            if (!double.IsFinite(SurviveSeconds) || SurviveSeconds < 0 || SurviveSeconds > 36000)
            {
                context.Issues.Error("survive", "«Выстоять» — от 0 до 36000 с");
            }

            if (ObjectsPolicy is not ("all" or "any"))
            {
                context.Issues.Error("objectsPolicy", $"неизвестное правило объектов «{ObjectsPolicy}»");
            }
        }

        if (Objects.Count > MaxObjects)
        {
            context.Issues.Error("objects", $"не больше {MaxObjects} объектов");
        }

        var objectIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < Objects.Count; i++)
        {
            var o = Objects[i];
            if (!ContentIds.IsValid(o.Id) || !objectIds.Add(o.Id))
            {
                context.Issues.Error($"objects[{i}].id", $"ID объекта «{o.Id}» пустой или повторяется");
            }

            if (o.Name.Trim().Length == 0)
            {
                context.Issues.Error($"objects[{i}].name", "у объекта нет названия");
            }

            if (!double.IsFinite(o.Health) || o.Health < 1 || o.Health > 99999)
            {
                context.Issues.Error($"objects[{i}].health", $"объект «{o.Name}»: здоровье — от 1 до 99999");
            }
        }

        foreach (var (e, i) in Enemies.Select((e, i) => (e, i)))
        {
            foreach (var problem in e.Ai.Problems())
            {
                context.Issues.Error($"enemies[{i}].ai", $"враг на {e.Col},{e.Row}: {problem}");
            }
        }

        if (Waves.Count > MaxWaves)
        {
            context.Issues.Error("waves", $"не больше {MaxWaves} волн");
        }

        var waveIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < Waves.Count; i++)
        {
            var w = Waves[i];
            var path = $"waves[{i}]";
            if (!ContentIds.IsValid(w.Id) || !waveIds.Add(w.Id))
            {
                context.Issues.Error(path + ".id", $"ID волны «{w.Id}» пустой, неверный или повторяется");
            }

            if (WaveData.Starts.All(s => s.Value != w.Start))
            {
                context.Issues.Error(path + ".start", $"неизвестное условие старта волны «{w.Start}»");
            }

            if (!double.IsFinite(w.Delay) || w.Delay < 0 || w.Delay > 36000)
            {
                context.Issues.Error(path + ".delay", $"волна «{w.Name}»: задержка — от 0 до 36000 с");
            }

            if (w.Groups.Count > MaxGroups)
            {
                context.Issues.Error(path + ".groups", $"волна «{w.Name}»: не больше {MaxGroups} групп");
            }

            var groupIds = new HashSet<string>(StringComparer.Ordinal);
            for (var k = 0; k < w.Groups.Count; k++)
            {
                var g = w.Groups[k];
                var at = $"волна «{w.Name}», группа {k + 1}";
                if (!ContentIds.IsValid(g.Id) || !groupIds.Add(g.Id))
                {
                    context.Issues.Error($"{path}.groups[{k}].id", $"{at}: ID группы пустой или повторяется");
                }

                if (g.Count is < 0 or > 500)
                {
                    context.Issues.Error($"{path}.groups[{k}].count", $"{at}: число врагов — целое от 0 до 500");
                }
                else if (g.Count == 0 && g.Enabled && w.Enabled)
                {
                    context.Issues.Warning($"{path}.groups[{k}].count", $"{at}: 0 врагов — группа ничего не выпустит");
                }

                foreach (var (value, name) in new[] { (g.FirstDelay, "задержка первого"), (g.Interval, "интервал"), (g.Offset, "сдвиг группы") })
                {
                    if (!double.IsFinite(value) || value < 0 || value > 36000)
                    {
                        context.Issues.Error($"{path}.groups[{k}]", $"{at}: {name} — от 0 до 36000 с");
                    }
                }

                if (g.Route.Length == 0 && g.Source.Length == 0 && g.Enabled)
                {
                    context.Issues.Error($"{path}.groups[{k}].route", IsDefense ? $"{at}: не выбран источник (база врага или зона) или маршрут" : $"{at}: не выбран маршрут");
                }

                foreach (var problem in g.Ai.Problems())
                {
                    context.Issues.Error($"{path}.groups[{k}].ai", $"{at}: {problem}");
                }

                if (g.Unit.Length == 0 && g.Enabled)
                {
                    context.Issues.Error($"{path}.groups[{k}].unit", $"{at}: не выбран юнит врага");
                }
            }
        }

        foreach (var b in BaseLives.Where(b => b.Base is < 1 or > 9 || b.Lives is < 1 or > 9999))
        {
            context.Issues.Error("bases", $"база {b.Base}: номер 1…9, жизни 1…9999");
        }

        if (DefeatPolicies.All(p => p.Value != Defeat))
        {
            context.Issues.Error("defeat", $"неизвестное правило поражения «{Defeat}»");
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
        $"{Modes.FirstOrDefault(m => m.Id == Mode)?.Title ?? Mode} · " + (IsTowerDefense || IsDefense ? $"волн {Waves.Count(w => w.Enabled)} · врагов {WaveEnemies}" : $"врагов {Enemies.Count}")
        + (IsDefense && Objects.Count > 0 ? $" · объектов {Objects.Count}" : string.Empty)
        + (IsExploration ? $" · вход {Entry.Count} · выход {Exit.Count}" : string.Empty) + $" · отряд {Squad.Count}" +
        (TimeLimit > 0 ? $" · {TimeLimit.ToString("0.##", CultureInfo.InvariantCulture)} с" : string.Empty);

    public IContentData Clone()
    {
        var copy = new ScenarioData { Mode = Mode, Map = Map, Ruleset = Ruleset, AutoAttacks = AutoAttacks, Blocking = Blocking, TimeLimit = TimeLimit, StartPaused = StartPaused };
        copy.Squad.Clear();
        copy.Squad.AddRange(Squad);
        copy.Enemies.AddRange(Enemies.Select(e => e.Copy()));
        copy.Waves.AddRange(Waves.Select(w => w.Copy()));
        copy.BaseLives.AddRange(BaseLives.Select(b => b.Copy()));
        copy.Defeat = Defeat;
        copy.Objects.AddRange(Objects.Select(o => o.Copy()));
        copy.ObjectsPolicy = ObjectsPolicy;
        copy.SurviveSeconds = SurviveSeconds;
        copy.SquadLossDefeat = SquadLossDefeat;
        CopyExplorationTo(copy);
        return copy;
    }

    /// <summary>
    /// The goals in plain words (spec part 7 §9.3 «итог понятным языком»): «Победа: все 3 врага побеждены. Поражение: все ваши
    /// юниты погибли и резерв пуст.».
    /// </summary>
    public string GoalsText()
    {
        if (IsExploration)
        {
            return ExplorationGoals();
        }

        if (IsDefense)
        {
            var critical = Objects.Where(o => o.Critical).Select(o => $"«{o.Name}»").ToList();
            var win = SurviveSeconds > 0
                ? $"Победа: продержаться {SurviveSeconds.ToString("0.##", CultureInfo.InvariantCulture)} с боя"
                : $"Победа: отбить все волны ({Waves.Count(w => w.Enabled)}, врагов {WaveEnemies}) — на поле никого";
            var loseParts = new List<string>();
            if (critical.Count > 0)
            {
                loseParts.Add(ObjectsPolicy == "any" ? $"уничтожены все критические объекты ({string.Join(", ", critical)})" : $"уничтожен любой критический объект ({string.Join(", ", critical)})");
            }

            if (SquadLossDefeat)
            {
                loseParts.Add("все ваши юниты погибли и в резерве никого");
            }

            if (TimeLimit > 0 && SurviveSeconds <= 0)
            {
                loseParts.Add($"прошло {TimeLimit.ToString("0.##", CultureInfo.InvariantCulture)} с боя");
            }

            return win + ". " + (loseParts.Count == 0 ? "Поражения нет." : "Поражение: " + string.Join(" или ", loseParts) + ".");
        }

        if (IsTowerDefense)
        {
            var waves = Waves.Count(w => w.Enabled);
            var lose = Defeat switch
            {
                "all_bases" => "все базы игрока потеряли все жизни",
                "shared_lives" => "общий запас жизней баз исчерпан",
                _ => "любая критическая база потеряла все жизни",
            };
            return $"Победа: все волны ({waves}, врагов {WaveEnemies}) пройдены, на поле никого, базы выжили. Поражение: {lose}"
                + (TimeLimit > 0 ? $" или прошло {TimeLimit.ToString("0.##", CultureInfo.InvariantCulture)} с боя." : ".");
        }

        var victory = $"Победа: все враги ({Enemies.Count}) побеждены.";
        var defeat = "Поражение: все ваши юниты погибли и в резерве никого" + (TimeLimit > 0 ? $" или прошло {TimeLimit.ToString("0.##", CultureInfo.InvariantCulture)} с боя." : ".");
        return victory + " " + defeat;
    }
}
