using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HexTactics.Content.Kinds;
using HexTactics.Core.Hex;
using HexTactics.Core.Map;

namespace HexTactics.Editor.Scenarios;

/// <summary>
/// «Оборона» in the scenario editor (spec part 7 §5): mission objects placed on the board like enemies (the brush «Ставить:
/// объекты»), the goal (hold out N seconds or repel every wave), the objects policy and the squad loss; wave groups that come out
/// of an enemy base or a spawn zone with a simple AI preset; the AI of a selected placed enemy («Охранник» in a battle too).
/// </summary>
public sealed partial class ScenarioEditorModel
{
    /// <summary>Clicks on the board place mission objects instead of enemies.</summary>
    public bool PlaceObjects { get; set; }

    public int? SelectedObject { get; private set; }

    public MissionObjectData? SelectedObjectData => SelectedObject is { } i && i < Scenario.Objects.Count ? Scenario.Objects[i] : null;

    public int? ObjectAt(HexCoord cell)
    {
        var i = Scenario.Objects.FindIndex(o => o.Cell == cell);
        return i >= 0 ? i : null;
    }

    /// <summary>LMB with the objects brush: an object there is selected; a free walkable hex gets a new object (selected).</summary>
    public string? ClickObject(HexCoord cell)
    {
        if (ObjectAt(cell) is { } index)
        {
            SelectedObject = index;
            RaiseChanged();
            return null;
        }

        var o = cell.ToOffset();
        if (Map() is not { } map || !map.Grid.Exists(cell) || !map.Grid.IsWalkable(cell))
        {
            return $"на {o.Col},{o.Row} нет проходимого гекса — объект ставится на землю карты";
        }

        if (EnemyAt(cell) is not null)
        {
            return $"на {o.Col},{o.Row} стоит враг — один гекс, один юнит";
        }

        if (Scenario.Objects.Count >= ScenarioData.MaxObjects)
        {
            return $"не больше {ScenarioData.MaxObjects} объектов";
        }

        var id = NextId(Scenario.Objects.Select(x => x.Id), "object");
        var obj = new MissionObjectData { Id = id, Name = $"Объект {Scenario.Objects.Count + 1}", Col = o.Col, Row = o.Row };
        Apply($"Объект «{obj.Name}» на {o.Col},{o.Row}", (d, _) => ((ScenarioData)d).Objects.Add(obj.Copy()));
        SelectedObject = Scenario.Objects.Count - 1;
        RaiseChanged();
        return null;
    }

    public bool RemoveObject()
    {
        if (SelectedObject is not { } i || i >= Scenario.Objects.Count)
        {
            return false;
        }

        var name = Scenario.Objects[i].Name;
        Apply($"Убрать объект «{name}»", (d, _) => ((ScenarioData)d).Objects.RemoveAt(i));
        SelectedObject = null;
        RaiseChanged();
        return true;
    }

    public void DeselectObject()
    {
        SelectedObject = null;
        RaiseChanged();
    }

    public string? SetObjectName(string name)
    {
        if (name.Trim().Length == 0)
        {
            return "у объекта должно быть название";
        }

        EditObject("Название объекта", o => o.Name = name.Trim());
        return null;
    }

    public string? SetObjectHealth(object? input)
    {
        if (!double.TryParse(Convert.ToString(input, CultureInfo.InvariantCulture)?.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var hp) || hp is < 1 or > 99999)
        {
            return $"здоровье объекта — от 1 до 99999, а не «{input}»";
        }

        EditObject("Здоровье объекта", o => o.Health = Math.Round(hp));
        return null;
    }

    public void SetObjectCritical(bool on) => EditObject(on ? "Объект критический" : "Объект не критический", o => o.Critical = on);

    public void SetObjectsPolicy(string policy)
    {
        if (policy is "all" or "any")
        {
            Apply("Правило объектов", (d, _) => ((ScenarioData)d).ObjectsPolicy = policy);
        }
    }

    /// <summary>«Выстоять, с»: 0 — repel every wave instead.</summary>
    public string? SetSurvive(object? input)
    {
        if (!TrySeconds(input, "«Выстоять»", out var v, out var refusal))
        {
            return refusal;
        }

        Apply("Выстоять", (d, _) => ((ScenarioData)d).SurviveSeconds = v);
        return null;
    }

    public void SetSquadLoss(bool on) => Apply(on ? "Гибель отряда — поражение" : "Гибель отряда — не поражение", (d, _) => ((ScenarioData)d).SquadLossDefeat = on);

    /// <summary>The templates of «Сценарии → Оборона» (spec part 7 §5.4): the goal in one click, then the details by hand.</summary>
    public static IReadOnlyList<(string Id, string Label, string Hint)> DefenseTemplates { get; } = new[]
    {
        ("survive", "Выстоять время", "Победа через 60 с боя; гибель всего отряда — поражение."),
        ("protect", "Защитить объект", "Победа — все волны отбиты; враги идут к объектам; кисть «объекты» включена."),
        ("waves", "Отбить все волны", "Победа — все волны отбиты и врагов нет; гибель всего отряда — поражение."),
    };

    /// <summary>
    /// Sets the goal fields of a template in one undoable step and adds a first wave when there is none, so the defense has an end at
    /// once. Objects, enemies and existing waves are kept; «Защитить объект» points the groups that come out of sources at the objects.
    /// </summary>
    public string? ApplyDefenseTemplate(string id)
    {
        if (!Scenario.IsDefense)
        {
            return "шаблоны — для режима «Оборона»";
        }

        if (DefenseTemplates.All(t => t.Id != id))
        {
            return $"неизвестный шаблон «{id}»";
        }

        var label = DefenseTemplates.First(t => t.Id == id).Label;
        WaveData? first = null;
        if (Scenario.Waves.Count == 0)
        {
            first = new WaveData { Id = NextId(Scenario.Waves.Select(w => w.Id), "wave"), Name = "Волна 1" };
            first.Groups.Add(NewGroup("group1"));
        }

        Apply($"Шаблон «{label}»", (d, _) =>
        {
            var s = (ScenarioData)d;
            if (first is not null)
            {
                s.Waves.Add(first.Copy());
            }

            s.SurviveSeconds = id == "survive" ? (s.SurviveSeconds > 0 ? s.SurviveSeconds : 60) : 0; // PROTOTYPE_DEFAULT 60 s
            s.SquadLossDefeat = id != "protect";
            if (id == "protect")
            {
                foreach (var g in s.Waves.SelectMany(w => w.Groups).Where(g => g.Route.Length == 0))
                {
                    g.Ai.Preset = "attack_objective";
                }
            }
        });
        if (first is not null)
        {
            SelectedWave = first.Id;
        }

        PlaceObjects = id == "protect";
        RaiseChanged();
        return null;
    }

    /// <summary>Sources of the scenario's map: its enemy bases and spawn zones («base:N», «zone:N»).</summary>
    public IReadOnlyList<(string Value, string Label)> Sources()
    {
        if (Map() is not { } map)
        {
            return Array.Empty<(string, string)>();
        }

        return map.Bases.Where(b => b.Side == BaseSide.Enemy).Select(b => ($"base:{b.Number}", b.Name))
            .Concat(map.SpawnZones.Select(z => ($"zone:{z.Id}", $"зона «{z.Name}»"))).ToList();
    }

    public string SourceName(string source) => Sources().FirstOrDefault(s => s.Value == source).Label ?? $"«{source}» — нет на карте";

    /// <summary>The group comes out of a source (its route is cleared): «base:N» or «zone:N» of the map.</summary>
    public string? SetGroupSource(string waveId, string groupId, string source)
    {
        if (Sources().All(s => s.Value != source))
        {
            return $"источника «{source}» нет на карте — нужна база врага или зона появления";
        }

        EditGroup(waveId, groupId, "Источник группы", g =>
        {
            g.Source = source;
            g.Route = string.Empty;
        });
        return null;
    }

    public string? SetGroupAi(string waveId, string groupId, string preset)
    {
        if (AiData.Presets.All(p => p.Value != preset))
        {
            return $"неизвестный пресет «{preset}»";
        }

        EditGroup(waveId, groupId, "ИИ группы", g => g.Ai.Preset = preset);
        return null;
    }

    /// <summary>«detectRange», «guardRadius» (hexes 0…99) or «reevaluate» (seconds 0.1…60) of the AI of a group or (null wave) the selected enemy.</summary>
    public string? SetAiNumber(string? waveId, string? groupId, string field, object? input)
    {
        var text = Convert.ToString(input, CultureInfo.InvariantCulture)?.Trim().Replace(',', '.') ?? string.Empty;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || (field == "reevaluate" ? v is < 0.1 or > 60 : v is < 0 or > 99))
        {
            return field == "reevaluate" ? $"пересмотр цели — от 0,1 до 60 с, а не «{input}»" : $"дальность — целое от 0 до 99 гексов, а не «{input}»";
        }

        void Set(AiData ai)
        {
            switch (field)
            {
                case "detectRange": ai.DetectRange = (int)Math.Round(v); break;
                case "guardRadius": ai.GuardRadius = (int)Math.Round(v); break;
                default: ai.Reevaluate = v; break;
            }
        }

        if (waveId is not null && groupId is not null)
        {
            EditGroup(waveId, groupId, "ИИ группы", g => Set(g.Ai));
        }
        else if (Selected is { } i && i < Scenario.Enemies.Count)
        {
            Apply("ИИ врага", (d, _) => Set(((ScenarioData)d).Enemies[i].Ai));
        }

        return null;
    }

    /// <summary>The target priority of the AI of a group or (null wave) the selected enemy: «nearest» or «lowest_health».</summary>
    public string? SetAiPriority(string? waveId, string? groupId, string priority)
    {
        if (AiData.Priorities.All(p => p.Value != priority))
        {
            return $"неизвестный приоритет «{priority}»";
        }

        return EditAi(waveId, groupId, "Приоритет цели", ai => ai.Priority = priority);
    }

    /// <summary>Whether a blocked enemy turns on the unit that blocks it (spec part 7 §5.2 «переключение при блоке»).</summary>
    public string? SetAiSwitch(string? waveId, string? groupId, bool on) =>
        EditAi(waveId, groupId, on ? "Бить блокирующего" : "Не отвлекаться на блок", ai => ai.SwitchToBlocker = on);

    private string? EditAi(string? waveId, string? groupId, string what, Action<AiData> change)
    {
        if (waveId is not null && groupId is not null)
        {
            EditGroup(waveId, groupId, what, g => change(g.Ai));
            return null;
        }

        if (Selected is not { } i || i >= Scenario.Enemies.Count)
        {
            return "сначала выберите врага на поле";
        }

        Apply(what, (d, _) => change(((ScenarioData)d).Enemies[i].Ai));
        return null;
    }

    /// <summary>The AI preset of the selected placed enemy (spec part 7 §3.3: «Охранник» in a battle).</summary>
    public string? SetEnemyAi(string preset)
    {
        if (AiData.Presets.All(p => p.Value != preset))
        {
            return $"неизвестный пресет «{preset}»";
        }

        if (Selected is not { } i || i >= Scenario.Enemies.Count)
        {
            return "сначала выберите врага на поле";
        }

        Apply("ИИ врага", (d, _) => ((ScenarioData)d).Enemies[i].Ai.Preset = preset);
        return null;
    }

    private void EditObject(string what, Action<MissionObjectData> change)
    {
        if (SelectedObject is { } i && i < Scenario.Objects.Count)
        {
            Apply(what, (d, _) => change(((ScenarioData)d).Objects[i]));
        }
    }

    private IEnumerable<string> DefensePreview()
    {
        var s = Scenario;
        yield return s.Objects.Count == 0
            ? "Объектов нет — щёлкните гекс с кистью «объекты»."
            : "Объекты: " + string.Join("; ", s.Objects.Select(o => o.Describe())) + ".";
        foreach (var line in WaveTimeline())
        {
            yield return line;
        }
    }
}
