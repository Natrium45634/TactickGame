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
public sealed class ScenarioEditorModel : DefinitionEditorModel
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
            $"Враги на карте: {EnemiesText()}.",
            $"Отряд игрока (нижняя панель): {string.Join(", ", s.Squad.Select((u, i) => $"{i + 1}. {UnitName(u)}"))}.",
            (s.StartPaused ? "Старт на паузе «Подготовка»: игрок выставляет отряд и задаёт маршруты, затем «Начать бой»." : "Бой идёт сразу, без подготовки на паузе.")
                + " " + (s.AutoAttacks ? "Автоатака включена" : "Автоатака выключена") + " · " + (s.Blocking ? "блок действует." : "блок выключен."),
            s.GoalsText(),
        };
        return lines;
    }

    private string? FirstUnit() =>
        Library.Items(ContentTypeRegistry.Unit).FirstOrDefault(u => !u.Archived)?.Id;
}
