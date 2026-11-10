using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HexTactics.Content.Kinds;
using HexTactics.Content.Library;
using HexTactics.Content.Model;
using HexTactics.Content.Registry;
using HexTactics.Content.Runtime;
using HexTactics.Core.Hex;
using HexTactics.Core.Map;
using HexTactics.Core.Spawning;
using HexTactics.Editor.Definitions;

namespace HexTactics.Editor.Scenarios;

/// <summary>
/// «Сценарий» editor (spec part 7 §3.1, §3.4, §9.3): the mode card, the map, the enemies already on the map (a click on a free
/// walkable hex places one of the brush unit, a click on an enemy selects it, the selected one turns and is removed), the
/// player's squad (1…5 library units), the rules of the battle and the goals in plain words. Every change is undoable; the
/// checks include the map and the units (an enemy on a missing hex, a unit that is not in the library), so a broken
/// scenario is saved as a draft but never started.
/// </summary>
public sealed partial class ScenarioEditorModel : DefinitionEditorModel
{
    public ScenarioEditorModel(ContentLibrary library, ContentItem item)
        : base(library, item)
    {
        if (item.Data is not ScenarioData)
        {
            throw new ArgumentException("Not a scenario.", nameof(item));
        }

        Brush = Scenario.Enemies.LastOrDefault()?.Unit is { Length: > 0 } last ? last : FirstUnit() ?? string.Empty;
    }

    public ScenarioData Scenario => (ScenarioData)Item.Data;

    /// <summary>The unit of the enemies placed by the next clicks.</summary>
    public string Brush { get; set; }

    /// <summary>The facing of the enemies placed by the next clicks.</summary>
    public HexDirection BrushFacing { get; set; } = HexDirection.South;

    /// <summary>Index of the selected enemy in <see cref="ScenarioData.Enemies"/>, or null.</summary>
    public int? Selected { get; private set; }

    public PlacedUnitData? SelectedEnemy => Selected is { } i && i < Scenario.Enemies.Count ? Scenario.Enemies[i] : null;

    /// <summary>The data checks and the checks with the map and the units (<see cref="RuntimeFactory.CheckScenario"/>).</summary>
    public override IReadOnlyList<ContentIssue> Validate()
    {
        var issues = Library.Validate(Item).Where(i => !i.IsError).ToList();
        issues.AddRange(RuntimeFactory.CheckScenario(Library, Item));
        if (Map() is { } map)
        {
            foreach (var e in Scenario.Enemies.Where(e => map.Grid.Exists(e.Cell) && PlacementRules.IsDeployTile(map.Grid[e.Cell].Type)))
            {
                issues.Add(ContentIssue.Warning("enemies", $"враг на {e.Col},{e.Row} стоит на гексе выставления игрока — этот гекс занят до его гибели"));
            }

            var deploy = map.Grid.Coords.Count(c => map.Grid.Exists(c) && PlacementRules.IsDeployTile(map.Grid[c].Type));
            if (deploy > 0 && deploy < Math.Min(Scenario.Squad.Count, ScenarioData.MaxSquad) && issues.All(i => i.Path != "map"))
            {
                issues.Add(ContentIssue.Warning("squad", $"гексов выставления {deploy}, а в отряде {Scenario.Squad.Count} — остальные выйдут, когда гексы освободятся"));
            }
        }

        return issues;
    }

    /// <summary>The map of the scenario as the battle builds it; null when it is missing or not a map.</summary>
    public MapDefinition? Map() =>
        Library.Find(ContentTypeRegistry.Map, Scenario.Map) is { Data: MapData } item ? RuntimeFactory.BuildMap(item, Library, new IssueList()) : null;

    public int? EnemyAt(HexCoord cell)
    {
        var i = Scenario.Enemies.FindIndex(e => e.Cell == cell);
        return i >= 0 ? i : null;
    }

    /// <summary>
    /// LMB on the map: an enemy there is selected; a free walkable hex gets an enemy of the brush unit (selected at once);
    /// a hole, an obstacle or a hex outside the map is refused with the reason.
    /// </summary>
    public string? Click(HexCoord cell)
    {
        if (PlaceObjects)
        {
            return ClickObject(cell);
        }

        if (ObjectAt(cell) is not null)
        {
            var spot = cell.ToOffset();
            return $"на {spot.Col},{spot.Row} стоит объект миссии — один гекс, один юнит";
        }

        if (EnemyAt(cell) is { } index)
        {
            Selected = index;
            RaiseChanged();
            return null;
        }

        if (Map() is not { } map)
        {
            return "сначала выберите карту сценария";
        }

        if (!map.Grid.Exists(cell) || !map.Grid.IsWalkable(cell))
        {
            var o = cell.ToOffset();
            return $"на {o.Col},{o.Row} нет проходимого гекса — враг ставится на землю карты";
        }

        if (Library.Find(ContentTypeRegistry.Unit, Brush) is not { } unit)
        {
            return "выберите юнит врага в списке «Ставить»";
        }

        if (Scenario.Enemies.Count >= ScenarioData.MaxEnemies)
        {
            return $"не больше {ScenarioData.MaxEnemies} врагов на карте";
        }

        var at = cell.ToOffset();
        var placed = new PlacedUnitData { Unit = Brush, Col = at.Col, Row = at.Row, Facing = BrushFacing };
        Apply($"Враг «{unit.Name}» на {at.Col},{at.Row}", (d, _) => ((ScenarioData)d).Enemies.Add(placed.Copy()));
        Selected = Scenario.Enemies.Count - 1;
        RaiseChanged();
        return null;
    }

    public void Deselect()
    {
        Selected = null;
        RaiseChanged();
    }

    /// <summary>Removes the enemy on a hex (RMB) or the selected one.</summary>
    public bool RemoveEnemy(HexCoord? cell = null)
    {
        var index = cell is { } c ? EnemyAt(c) : Selected;
        if (index is not { } i || i >= Scenario.Enemies.Count)
        {
            return false;
        }

        var e = Scenario.Enemies[i];
        Apply($"Убрать врага с {e.Col},{e.Row}", (d, _) => ((ScenarioData)d).Enemies.RemoveAt(i));
        Selected = null;
        RaiseChanged();
        return true;
    }

    /// <summary>Turns the selected enemy (or, with none, the brush) by sixths clockwise.</summary>
    public void Turn(int steps)
    {
        if (Selected is { } i && i < Scenario.Enemies.Count)
        {
            var facing = Scenario.Enemies[i].Facing.RotateClockwise(steps);
            Apply("Взгляд врага", (d, _) => ((ScenarioData)d).Enemies[i].Facing = facing);
        }
        else
        {
            BrushFacing = BrushFacing.RotateClockwise(steps);
            RaiseChanged();
        }
    }

    /// <summary>The unit of the selected enemy; the brush follows it, so the next enemies are of the same kind.</summary>
    public string? SetSelectedUnit(string unitId)
    {
        if (Library.Find(ContentTypeRegistry.Unit, unitId) is not { } unit)
        {
            return $"юнит «{unitId}» не найден";
        }

        Brush = unitId;
        if (Selected is { } i && i < Scenario.Enemies.Count)
        {
            Apply($"Враг — «{unit.Name}»", (d, _) => ((ScenarioData)d).Enemies[i].Unit = unitId);
        }
        else
        {
            RaiseChanged();
        }

        return null;
    }

    /// <summary>Removes the enemies the map cannot hold (after the map changed); one undo step.</summary>
    public int RemoveMisplaced()
    {
        if (Map() is not { } map)
        {
            return 0;
        }

        var bad = Scenario.Enemies.Count(e => !map.Grid.Exists(e.Cell) || !map.Grid.IsWalkable(e.Cell));
        if (bad > 0)
        {
            Apply($"Убрать врагов вне карты ({bad})", (d, _) => ((ScenarioData)d).Enemies.RemoveAll(e => !map.Grid.Exists(e.Cell) || !map.Grid.IsWalkable(e.Cell)));
            Selected = null;
            RaiseChanged();
        }

        return bad;
    }

    public string? SetMode(string mode)
    {
        if (ScenarioData.Modes.FirstOrDefault(m => m.Id == mode) is not { } m)
        {
            return $"неизвестный режим «{mode}»";
        }

        if (!m.Available)
        {
            return $"режим «{m.Title}» появится в {m.AvailableFrom}; пока доступен «Обычный бой»";
        }

        Apply($"Режим — «{m.Title}»", (d, _) => ((ScenarioData)d).Mode = mode);
        return null;
    }

    public string? SetMap(string mapId)
    {
        if (Library.Find(ContentTypeRegistry.Map, mapId) is not { } map)
        {
            return $"карта «{mapId}» не найдена";
        }

        Apply($"Карта — «{map.Name}»", (d, _) => ((ScenarioData)d).Map = mapId);
        Selected = null;
        RaiseChanged();
        return null;
    }

    /// <summary>Combat rules; empty — the map's.</summary>
    public string? SetRuleset(string rulesetId)
    {
        if (rulesetId.Length > 0 && Library.Find(ContentTypeRegistry.CombatRuleset, rulesetId) is null)
        {
            return $"правила боя «{rulesetId}» не найдены";
        }

        Apply("Правила боя сценария", (d, _) => ((ScenarioData)d).Ruleset = rulesetId);
        return null;
    }

    public void SetFlag(string key, bool on) => Apply(key switch { "autoAttacks" => "Автоатака", "blocking" => "Блок", _ => "Подготовка на паузе" }, (d, _) =>
    {
        var s = (ScenarioData)d;
        switch (key)
        {
            case "autoAttacks": s.AutoAttacks = on; break;
            case "blocking": s.Blocking = on; break;
            case "startPaused": s.StartPaused = on; break;
        }
    });

    public string? SetTimeLimit(object? input)
    {
        var text = Convert.ToString(input, CultureInfo.InvariantCulture)?.Trim().Replace(',', '.') ?? string.Empty;
        if (text.Length == 0)
        {
            text = "0";
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || !double.IsFinite(v) || v < 0 || v > 36000)
        {
            return $"ограничение времени — секунды от 0 (нет) до 36000, а не «{input}»";
        }

        Apply("Ограничение времени", (d, _) => ((ScenarioData)d).TimeLimit = v);
        return null;
    }

    public string? AddToSquad(string unitId)
    {
        if (Library.Find(ContentTypeRegistry.Unit, unitId) is not { } unit)
        {
            return $"юнит «{unitId}» не найден";
        }

        if (Scenario.Squad.Count >= ScenarioData.MaxSquad)
        {
            return $"в отряде не больше {ScenarioData.MaxSquad} юнитов (нижняя панель игрока)";
        }

        Apply($"В отряд — «{unit.Name}»", (d, _) => ((ScenarioData)d).Squad.Add(unitId));
        return null;
    }

    public string? RemoveFromSquad(int index)
    {
        if (index < 0 || index >= Scenario.Squad.Count)
        {
            return null;
        }

        if (Scenario.Squad.Count == 1)
        {
            return "в отряде должен остаться хотя бы один юнит";
        }

        Apply($"Убрать из отряда №{index + 1}", (d, _) => ((ScenarioData)d).Squad.RemoveAt(index));
        return null;
    }

    public string UnitName(string id) => Library.Find(ContentTypeRegistry.Unit, id)?.Name ?? $"«{id}» — нет в библиотеке";

    /// <summary>«3 × «Оперативник», 1 × «Страж»» — the enemies by kind, in the order of placing.</summary>
    public string EnemiesText() => Scenario.Enemies.Count == 0
        ? "врагов нет — щёлкните гекс карты"
        : string.Join(", ", Scenario.Enemies.GroupBy(e => e.Unit).Select(g => $"{g.Count()} × «{UnitName(g.Key)}»"));

    /// <summary>The scenario in words for the preview column and the bottom line of the editor (spec part 7 §9.3).</summary>
    public IReadOnlyList<string> Preview()
    {
        var s = Scenario;
        var mode = ScenarioData.Modes.FirstOrDefault(m => m.Id == s.Mode);
        var map = Library.Find(ContentTypeRegistry.Map, s.Map);
        var ruleset = s.Ruleset.Length == 0 ? "правила боя карты" : $"правила боя «{Library.Find(ContentTypeRegistry.CombatRuleset, s.Ruleset)?.Name ?? s.Ruleset}»";
        var lines = new List<string>
        {
            $"{mode?.Title ?? s.Mode} на карте «{map?.Name ?? s.Map}» · {ruleset}.",
            s.IsTowerDefense || s.IsDefense
                ? $"Волн: {s.Waves.Count(w => w.Enabled)} · врагов по волнам: {s.WaveEnemies}" + (RouteTotals() is { Count: > 0 } totals ? " · " + string.Join(", ", totals.Select(t => $"{FromName(t.Route)} — {t.Enemies}")) : string.Empty) + "."
                    + (s.Enemies.Count > 0 ? $" Враги на карте с начала: {EnemiesText()}." : string.Empty)
                : $"Враги на карте: {EnemiesText()}.",
            $"Отряд игрока (нижняя панель): {string.Join(", ", s.Squad.Select((u, i) => $"{i + 1}. {UnitName(u)}"))}.",
            (s.StartPaused ? "Старт на паузе «Подготовка»: игрок выставляет отряд и задаёт маршруты, затем «Начать бой»." : "Бой идёт сразу, без подготовки на паузе.")
                + " " + (s.AutoAttacks ? "Автоатака включена" : "Автоатака выключена") + " · " + (s.Blocking ? "блок действует." : "блок выключен."),
            s.GoalsText(),
        };
        if (s.IsDefense)
        {
            lines.AddRange(DefensePreview());
        }
        else if (s.IsTowerDefense)
        {
            lines.AddRange(WaveTimeline());
            lines.Add("Базы: " + string.Join(", ", PlayerBases().Select(b => $"«{b.Name}» — {b.Lives} жизн." + (b.Critical ? " (критическая)" : string.Empty))) + ".");
        }

        return lines;
    }

    // ------------------------------------------------------------------ Tower Defense (spec part 7 §4.5–4.6)

    /// <summary>The wave whose card is open in the editor, or null.</summary>
    public string? SelectedWave { get; set; }

    /// <summary>Routes of the scenario's map (their geometry is edited in the map editor, spec part 7 §9.2).</summary>
    public IReadOnlyList<MapRouteData> MapRoutes() =>
        Library.Find(ContentTypeRegistry.Map, Scenario.Map)?.Data is MapData map ? map.Routes : Array.Empty<MapRouteData>();

    public string RouteName(string id) => MapRoutes().FirstOrDefault(r => r.Id == id)?.Name ?? $"«{id}» — нет на карте";

    /// <summary>A new wave after the others with one group of 5 enemies on the first route; selected. Returns its id.</summary>
    public string AddWave()
    {
        var id = NextId(Scenario.Waves.Select(w => w.Id), "wave");
        var wave = new WaveData { Id = id, Name = $"Волна {Scenario.Waves.Count + 1}" };
        wave.Groups.Add(NewGroup("group1"));
        Apply($"Новая волна «{wave.Name}»", (d, _) => ((ScenarioData)d).Waves.Add(wave.Copy()));
        SelectedWave = id;
        RaiseChanged();
        return id;
    }

    /// <summary>A copy right after the wave: same groups, routes and units, a new id; the sums follow by themselves (M7-25).</summary>
    public string? DuplicateWave(string id)
    {
        var index = Scenario.Waves.FindIndex(w => w.Id == id);
        if (index < 0)
        {
            return null;
        }

        var copy = Scenario.Waves[index].Copy();
        copy.Id = NextId(Scenario.Waves.Select(w => w.Id), "wave");
        copy.Name = $"{copy.Name} (копия)";
        Apply($"Копия волны «{Scenario.Waves[index].Name}»", (d, _) => ((ScenarioData)d).Waves.Insert(index + 1, copy.Copy()));
        SelectedWave = copy.Id;
        RaiseChanged();
        return copy.Id;
    }

    public void MoveWave(string id, int delta)
    {
        var index = Scenario.Waves.FindIndex(w => w.Id == id);
        var to = index + delta;
        if (index < 0 || to < 0 || to >= Scenario.Waves.Count)
        {
            return;
        }

        Apply(delta < 0 ? "Волна выше" : "Волна ниже", (d, _) =>
        {
            var list = ((ScenarioData)d).Waves;
            var wave = list[index];
            list.RemoveAt(index);
            list.Insert(to, wave);
        });
    }

    public void RemoveWave(string id)
    {
        if (Scenario.Waves.FirstOrDefault(w => w.Id == id) is { } wave)
        {
            Apply($"Удалить волну «{wave.Name}»", (d, _) => ((ScenarioData)d).Waves.RemoveAll(w => w.Id == id));
            SelectedWave = SelectedWave == id ? null : SelectedWave;
            RaiseChanged();
        }
    }

    public void SetWaveEnabled(string id, bool on) => EditWave(id, on ? "Волна включена" : "Волна выключена", w => w.Enabled = on);

    public string? SetWaveName(string id, string name)
    {
        if (name.Trim().Length == 0)
        {
            return "у волны должно быть название";
        }

        EditWave(id, "Название волны", w => w.Name = name.Trim());
        return null;
    }

    public string? SetWaveStart(string id, string start)
    {
        if (WaveData.Starts.All(s => s.Value != start))
        {
            return $"неизвестное условие старта «{start}»";
        }

        EditWave(id, "Старт волны", w => w.Start = start);
        return null;
    }

    public string? SetWaveDelay(string id, object? input)
    {
        if (!TrySeconds(input, "задержка волны", out var v, out var refusal))
        {
            return refusal;
        }

        EditWave(id, "Задержка волны", w => w.Delay = v);
        return null;
    }

    /// <summary>One more group of the wave on the first route of the map; returns its id.</summary>
    public string? AddGroup(string waveId)
    {
        if (Scenario.Waves.FirstOrDefault(w => w.Id == waveId) is not { } wave)
        {
            return null;
        }

        if (wave.Groups.Count >= ScenarioData.MaxGroups)
        {
            return null;
        }

        var group = NewGroup(NextId(wave.Groups.Select(g => g.Id), "group"));
        EditWave(waveId, "Новая группа", w => w.Groups.Add(group.Copy()));
        return group.Id;
    }

    public void RemoveGroup(string waveId, string groupId) => EditWave(waveId, "Удалить группу", w => w.Groups.RemoveAll(g => g.Id == groupId));

    public string? SetGroupRoute(string waveId, string groupId, string routeId)
    {
        if (MapRoutes().All(r => r.Id != routeId))
        {
            return $"маршрута «{routeId}» нет на карте сценария — нарисуйте его в редакторе карт («Маршруты»)";
        }

        EditGroup(waveId, groupId, "Маршрут группы", g =>
        {
            g.Route = routeId;
            g.Source = string.Empty; // a route walker comes in at its first visit
        });
        return null;
    }

    public string? SetGroupUnit(string waveId, string groupId, string unitId)
    {
        if (Library.Find(ContentTypeRegistry.Unit, unitId) is null)
        {
            return $"юнит «{unitId}» не найден";
        }

        EditGroup(waveId, groupId, "Юнит группы", g => g.Unit = unitId);
        return null;
    }

    public string? SetGroupCount(string waveId, string groupId, object? input)
    {
        if (!int.TryParse(Convert.ToString(input, CultureInfo.InvariantCulture)?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n is < 0 or > 500)
        {
            return $"число врагов — целое от 0 до 500, а не «{input}»";
        }

        EditGroup(waveId, groupId, "Число врагов группы", g => g.Count = n);
        return null;
    }

    /// <summary>«firstDelay», «interval» or «offset» of a group, in game seconds ≥ 0.</summary>
    public string? SetGroupTime(string waveId, string groupId, string field, object? input)
    {
        var name = field switch { "firstDelay" => "задержка первого", "interval" => "интервал", _ => "сдвиг группы" };
        if (!TrySeconds(input, name, out var v, out var refusal))
        {
            return refusal;
        }

        EditGroup(waveId, groupId, $"Группа: {name}", g =>
        {
            switch (field)
            {
                case "firstDelay": g.FirstDelay = v; break;
                case "interval": g.Interval = v; break;
                default: g.Offset = v; break;
            }
        });
        return null;
    }

    public void SetGroupEnabled(string waveId, string groupId, bool on) => EditGroup(waveId, groupId, on ? "Группа включена" : "Группа выключена", g => g.Enabled = on);

    /// <summary>Player bases of the scenario's map with their lives (the default for bases the author did not set).</summary>
    public IReadOnlyList<(int Number, string Name, int Lives, bool Critical)> PlayerBases()
    {
        if (Map() is not { } map)
        {
            return Array.Empty<(int, string, int, bool)>();
        }

        return map.Bases.Where(b => b.Side == BaseSide.Player).Select(b => (b.Number, b.Name, Scenario.LivesOf(b.Number).Lives, Scenario.LivesOf(b.Number).Critical)).ToList();
    }

    public string? SetBaseLives(int baseNumber, object? input)
    {
        if (!int.TryParse(Convert.ToString(input, CultureInfo.InvariantCulture)?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var lives) || lives is < 1 or > 9999)
        {
            return $"жизни базы — целое от 1 до 9999, а не «{input}»";
        }

        EditBase(baseNumber, $"Жизни базы {baseNumber}", b => b.Lives = lives);
        return null;
    }

    public void SetBaseCritical(int baseNumber, bool on) => EditBase(baseNumber, on ? $"База {baseNumber} критическая" : $"База {baseNumber} не критическая", b => b.Critical = on);

    public void SetDefeat(string policy)
    {
        if (ScenarioData.DefeatPolicies.Any(p => p.Value == policy))
        {
            Apply("Правило поражения", (d, _) => ((ScenarioData)d).Defeat = policy);
        }
    }

    /// <summary>
    /// Enemies per route (or, for groups without one, per source «base:N» / «zone:N») over the enabled waves — computed, never
    /// stored (spec part 7 §4.5).
    /// </summary>
    public IReadOnlyList<(string Route, int Enemies)> RouteTotals() => Scenario.Waves.Where(w => w.Enabled)
        .SelectMany(w => w.Groups).Where(g => g.Enabled).GroupBy(g => g.Route.Length > 0 ? g.Route : g.Source).Select(g => (g.Key, g.Sum(x => Math.Max(0, x.Count)))).ToList();

    /// <summary>The quoted name of a route of the map, or of a source («base:N» → «База врага N», «zone:N» → зона «…»).</summary>
    public string FromName(string routeOrSource) => !routeOrSource.Contains(':', StringComparison.Ordinal) ? $"«{RouteName(routeOrSource)}»"
        : routeOrSource.StartsWith("zone:", StringComparison.Ordinal) ? SourceName(routeOrSource) : $"«{SourceName(routeOrSource)}»";

    /// <summary>The schedule in words: one line per wave with its groups, start and sum.</summary>
    public IReadOnlyList<string> WaveTimeline()
    {
        var lines = new List<string>();
        for (var i = 0; i < Scenario.Waves.Count; i++)
        {
            var w = Scenario.Waves[i];
            var groups = w.Groups.Where(g => g.Enabled).Select(g =>
                $"{g.Count} × «{UnitName(g.Unit)}» " + (g.Route.Length > 0 ? $"по «{RouteName(g.Route)}»" : $"из «{SourceName(g.Source)}» ({g.Ai.Short()})")
                + $" с {N(g.Offset + g.FirstDelay)} с" + (g.Count > 1 ? $" каждые {N(g.Interval)} с" : string.Empty));
            lines.Add($"{i + 1}. «{w.Name}» {(w.Enabled ? string.Empty : "(выключена) ")}— {w.StartText(i == 0)}: {string.Join("; ", groups)} · всего {w.Total}");
        }

        return lines;
    }

    private SpawnGroupData NewGroup(string id) => Scenario.IsDefense
        ? new SpawnGroupData
        {
            // Spec part 7 §5.1: a defense group comes out of a source with a simple AI; the object first when there is one.
            Id = id,
            Route = string.Empty,
            Source = Sources().FirstOrDefault().Value ?? string.Empty,
            Unit = Brush.Length > 0 ? Brush : FirstUnit() ?? string.Empty,
            Count = 5,
            Interval = 1,
            Ai = new AiData { Preset = Scenario.Objects.Count > 0 ? "attack_objective" : "attack_units" },
        }
        : new SpawnGroupData
        {
            Id = id,
            Route = MapRoutes().FirstOrDefault(r => r.Enabled)?.Id ?? string.Empty,
            Unit = Brush.Length > 0 ? Brush : FirstUnit() ?? string.Empty,
            Count = 5,
            Interval = 1,
        };

    private void EditWave(string id, string what, Action<WaveData> change)
    {
        var index = Scenario.Waves.FindIndex(w => w.Id == id);
        if (index >= 0)
        {
            Apply(what, (d, _) => change(((ScenarioData)d).Waves[index]));
        }
    }

    private void EditGroup(string waveId, string groupId, string what, Action<SpawnGroupData> change) =>
        EditWave(waveId, what, w =>
        {
            if (w.Groups.Find(g => g.Id == groupId) is { } group)
            {
                change(group);
            }
        });

    private void EditBase(int number, string what, Action<BaseLivesData> change) => Apply(what, (d, _) =>
    {
        var s = (ScenarioData)d;
        if (s.BaseLives.Find(b => b.Base == number) is not { } entry)
        {
            entry = new BaseLivesData { Base = number };
            s.BaseLives.Add(entry);
            s.BaseLives.Sort((a, b) => a.Base.CompareTo(b.Base));
        }

        change(entry);
    });

    /// <summary>Game seconds 0…36000 (empty — 0, a comma is a decimal point); otherwise a refusal for the author.</summary>
    private static bool TrySeconds(object? input, string name, out double value, out string? refusal)
    {
        var text = Convert.ToString(input, CultureInfo.InvariantCulture)?.Trim().Replace(',', '.') ?? string.Empty;
        if (double.TryParse(text.Length == 0 ? "0" : text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value) && value is >= 0 and <= 36000)
        {
            refusal = null;
            return true;
        }

        refusal = $"{name} — секунды от 0 до 36000, а не «{input}»";
        return false;
    }

    private static string NextId(IEnumerable<string> ids, string prefix)
    {
        var taken = ids.ToHashSet(StringComparer.Ordinal);
        for (var n = 1; ; n++)
        {
            var id = prefix + n.ToString(CultureInfo.InvariantCulture);
            if (!taken.Contains(id))
            {
                return id;
            }
        }
    }

    private static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private string? FirstUnit() =>
        Library.Items(ContentTypeRegistry.Unit).FirstOrDefault(u => !u.Archived)?.Id;
}
