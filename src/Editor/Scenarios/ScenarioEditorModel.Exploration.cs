using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Content.Kinds;
using HexTactics.Content.Runtime;
using HexTactics.Core.Hex;
using HexTactics.Core.Spawning;

namespace HexTactics.Editor.Scenarios;

/// <summary>
/// «Исследование» in the scenario editor (spec part 7 §6): the entry and exit areas painted on the board with the brushes «Вход» and
/// «Выход» (a click adds a hex, a second click removes it), the exit rule with the key unit or the required members among the squad
/// cards, the extra task with enemies marked as targets, and the step list «карта → вход → выход → отряд → враги → правило» (§6.3).
/// </summary>
public sealed partial class ScenarioEditorModel
{
    /// <summary>The area painted by board clicks: «entry», «exit», or null — enemies or objects.</summary>
    public string? PlaceArea { get; private set; }

    /// <summary>The brush of the board: «enemies», «objects», «entry» or «exit».</summary>
    public string BoardBrush => PlaceArea ?? (PlaceObjects ? "objects" : "enemies");

    public string? SetBrush(string brush)
    {
        switch (brush)
        {
            case "enemies":
            case "objects":
                PlaceObjects = brush == "objects";
                PlaceArea = null;
                break;
            case "entry":
            case "exit":
                PlaceObjects = false;
                PlaceArea = brush;
                break;
            default:
                return $"неизвестная кисть «{brush}»";
        }

        RaiseChanged();
        return null;
    }

    /// <summary>A click with the brush «Вход» / «Выход»: a free walkable hex joins the area, a hex of the area leaves it.</summary>
    public string? ClickArea(HexCoord cell)
    {
        var entry = PlaceArea == "entry";
        var area = entry ? Scenario.Entry : Scenario.Exit;
        var other = entry ? Scenario.Exit : Scenario.Entry;
        var at = cell.ToOffset();
        var hex = new HexCellData(at.Col, at.Row);
        var name = entry ? "входа" : "выхода";
        if (area.Contains(hex))
        {
            Apply($"Убрать гекс {name} {hex}", (d, _) => (entry ? ((ScenarioData)d).Entry : ((ScenarioData)d).Exit).Remove(hex));
            return null;
        }

        if (Map() is not { } map)
        {
            return "сначала выберите карту сценария";
        }

        if (!map.Grid.Exists(cell) || !map.Grid.IsWalkable(cell))
        {
            return $"на {hex} нет проходимого гекса — {(entry ? "вход" : "выход")} отмечается на земле карты";
        }

        if (other.Contains(hex))
        {
            return $"гекс {hex} уже {(entry ? "выход" : "вход")} — вход и выход в разных местах";
        }

        if (entry && EnemyAt(cell) is not null)
        {
            return $"на {hex} стоит враг — вход должен быть свободен";
        }

        if (area.Count >= ScenarioData.MaxArea)
        {
            return $"гексов {name} не больше {ScenarioData.MaxArea}";
        }

        Apply($"Гекс {name} {hex}", (d, _) => (entry ? ((ScenarioData)d).Entry : ((ScenarioData)d).Exit).Add(hex));
        return null;
    }

    public string? SetExitRule(string rule)
    {
        if (ScenarioData.ExitRules.All(r => r.Value != rule))
        {
            return $"неизвестное правило выхода «{rule}»";
        }

        var label = ScenarioData.ExitRules.First(r => r.Value == rule).Label;
        Apply($"Правило выхода — «{label}»", (d, _) =>
        {
            var s = (ScenarioData)d;
            s.ExitRule = rule;
            if (rule == "all" && s.Required.Count == 0)
            {
                s.Required.AddRange(Enumerable.Range(1, s.Squad.Count)); // a start the author narrows: everyone is required
            }
        });
        return null;
    }

    /// <summary>The key unit: a 1-based card of the squad.</summary>
    public string? SetKeyUnit(int card)
    {
        if (card < 1 || card > Scenario.Squad.Count)
        {
            return $"в отряде нет карточки {card}";
        }

        Apply($"Ключевой юнит — карточка {card}", (d, _) => ((ScenarioData)d).KeyUnit = card);
        return null;
    }

    /// <summary>A card of the squad becomes required or stops being required (rule «все обязательные»).</summary>
    public string? SetRequired(int card, bool on)
    {
        if (card < 1 || card > Scenario.Squad.Count)
        {
            return $"в отряде нет карточки {card}";
        }

        if (on == Scenario.Required.Contains(card))
        {
            return null;
        }

        Apply(on ? $"Карточка {card} обязательна" : $"Карточка {card} не обязательна", (d, _) =>
        {
            var required = ((ScenarioData)d).Required;
            if (on)
            {
                required.Add(card);
                required.Sort();
            }
            else
            {
                required.Remove(card);
            }
        });
        return null;
    }

    public string? SetExtraGoal(string goal)
    {
        if (ScenarioData.ExtraGoals.All(g => g.Value != goal))
        {
            return $"неизвестная задача «{goal}»";
        }

        Apply($"Дополнительная задача — «{ScenarioData.ExtraGoals.First(g => g.Value == goal).Label}»", (d, _) => ((ScenarioData)d).ExtraGoal = goal);
        return null;
    }

    /// <summary>The selected enemy is (or is no longer) a target of the extra task «победить отмеченных врагов».</summary>
    public string? SetEnemyTarget(bool on)
    {
        if (Selected is not { } i || i >= Scenario.Enemies.Count)
        {
            return "сначала выберите врага на поле";
        }

        Apply(on ? "Враг — цель задачи" : "Враг — не цель задачи", (d, _) => ((ScenarioData)d).Enemies[i].Target = on);
        return null;
    }

    /// <summary>Steps of the shortest way from the entry to the exit over walkable hexes (units ignored); null — none or no map.</summary>
    public int? ExitSteps()
    {
        if (Map() is not { } map)
        {
            return null;
        }

        var start = Scenario.Entry.Select(c => c.Cell).Where(map.Grid.IsWalkable).ToList();
        var goals = Scenario.Exit.Select(c => c.Cell).Where(map.Grid.IsWalkable).ToHashSet();
        return start.Count == 0 || goals.Count == 0 ? null : RuntimeFactory.PathSteps(map.Grid, start, goals);
    }

    /// <summary>The steps of «Сценарии → Исследование» (spec part 7 §6.3) and whether each is done.</summary>
    public IReadOnlyList<(string Step, bool Done)> ExplorationSteps()
    {
        var s = Scenario;
        var ruleDone = s.ExitRule switch
        {
            "key" => s.KeyUnit >= 1 && s.KeyUnit <= s.Squad.Count,
            "all" => s.Required.Count > 0 && s.Required.All(r => r >= 1 && r <= s.Squad.Count),
            _ => true,
        } && (s.ExtraGoal != "kill_marked" || s.Enemies.Any(e => e.Target));
        return new[]
        {
            ("Карта", Map() is not null),
            ("Вход — гексы, где выходит отряд", s.Entry.Count > 0),
            ("Выход — достижим от входа", s.Exit.Count > 0 && ExitSteps() is not null),
            ("Отряд", s.Squad.Count > 0),
            ("Враги и охранники по пути (необязательно)", s.Enemies.Count > 0),
            ("Правило победы", ruleDone),
        };
    }

    /// <summary>The squad cards as «1. Оперативник».</summary>
    public IReadOnlyList<(int Card, string Name)> SquadCards() => Scenario.Squad.Select((u, i) => (i + 1, UnitName(u))).ToList();

    /// <summary>When an exploration starts with an empty entry, the map's deploy hexes are a first entry the author can change.</summary>
    private static void DefaultEntry(ScenarioData s, HexTactics.Core.Map.MapDefinition? map)
    {
        if (map is null || s.Entry.Count > 0)
        {
            return;
        }

        foreach (var c in map.Grid.Coords.Where(c => map.Grid.Exists(c) && map.Grid.IsWalkable(c) && PlacementRules.IsDeployTile(map.Grid[c].Type)).Take(ScenarioData.MaxArea))
        {
            var o = c.ToOffset();
            s.Entry.Add(new HexCellData(o.Col, o.Row));
        }
    }

    private IEnumerable<string> ExplorationPreview()
    {
        var s = Scenario;
        var steps = ExitSteps();
        yield return $"Вход: {s.Entry.Count} гекс. · выход: {s.Exit.Count} гекс. · "
            + (steps is { } n ? $"путь от входа до выхода — {n} шаг. без врагов." : s.Entry.Count > 0 && s.Exit.Count > 0 ? "выход недостижим от входа." : "отметьте вход и выход.");
        if (s.ExitRule == "key" && s.KeyUnit >= 1 && s.KeyUnit <= s.Squad.Count)
        {
            yield return $"Ключевой юнит — карточка {s.KeyUnit} «{UnitName(s.Squad[s.KeyUnit - 1])}».";
        }
        else if (s.ExitRule == "all" && s.Required.Count > 0)
        {
            yield return "Обязательные: " + string.Join(", ", s.Required.Where(r => r >= 1 && r <= s.Squad.Count).Select(r => $"{r}. {UnitName(s.Squad[r - 1])}")) + ".";
        }

        if (s.ExtraGoal == "kill_marked")
        {
            var marked = s.Enemies.Where(e => e.Target).Select(e => $"{e.Col},{e.Row}").ToList();
            yield return marked.Count == 0 ? "Отмеченных врагов нет — выберите врага и отметьте «Цель задачи»." : $"Отмеченные враги: {string.Join("; ", marked)}.";
        }
    }
}
