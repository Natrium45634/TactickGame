using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HexTactics.Content.Kinds;
using HexTactics.Content.Library;
using HexTactics.Content.Model;
using HexTactics.Content.Registry;
using HexTactics.Content.Schema;
using HexTactics.Core.Effects;
using HexTactics.Core.Hex;
using HexTactics.Editor.Definitions;

namespace HexTactics.Editor.Skills;

/// <summary>What a click on the skill canvas did.</summary>
public enum SkillCellClick
{
    Nothing = 0,
    Added = 1,
    Selected = 2,
    Removed = 3,
}

/// <summary>
/// Model of the skill editor (spec part 4 §6): cells of the area stored as offsets for a caster looking North (the canvas
/// shows them turned to the preview facing — one rotation function), the skill's base effects, per-cell own effects or
/// per-field overrides of the base ones, editor colours, and a preview of what each cell does with a given Attack Power.
/// </summary>
public sealed class SkillEditorModel : DefinitionEditorModel
{
    public SkillEditorModel(ContentLibrary library, ContentItem item)
        : base(library, item)
    {
        if (item.Data is not SkillData)
        {
            throw new ArgumentException("Not a skill.", nameof(item));
        }
    }

    public SkillData Skill => (SkillData)Item.Data;

    /// <summary>Selected cell (offset for a caster looking North); null = the skill's own settings.</summary>
    public HexCoord? Selected { get; private set; }

    /// <summary>Facing of the central unit on the canvas (preview only).</summary>
    public HexDirection Facing { get; set; } = HexDirection.North;

    /// <summary>Visible radius of the canvas (2…6).</summary>
    public int Radius { get; set; } = 4;

    /// <summary>Clicks remove cells instead of adding/selecting them.</summary>
    public bool EraseMode { get; set; }

    /// <summary>Attack Power of the preview (a unit's value or typed in).</summary>
    public double PreviewAttackPower { get; set; } = 10;

    public double PreviewMaxHealth { get; set; } = 100;

    /// <summary>Offset of a canvas cell (relative to the unit, as drawn for <see cref="Facing"/>) in the stored frame.</summary>
    public HexCoord ToStored(HexCoord drawnOffset) => drawnOffset.RotateClockwise(-(int)Facing);

    /// <summary>Where a stored offset is drawn for <see cref="Facing"/>.</summary>
    public HexCoord ToDrawn(HexCoord storedOffset) => storedOffset.RotateFor(Facing);

    public SkillCellData? CellAt(HexCoord stored) => Skill.FindCell(stored.Q, stored.R);

    /// <summary>
    /// LMB on the canvas (spec part 4 §6.1): an empty hex joins the area (inheriting the base effects) and is selected; a
    /// cell of the area is selected; in the erase mode a cell of the area is removed.
    /// </summary>
    public SkillCellClick Click(HexCoord drawnOffset)
    {
        if (drawnOffset.DistanceTo(default) > Radius)
        {
            return SkillCellClick.Nothing;
        }

        var stored = ToStored(drawnOffset);
        var cell = CellAt(stored);
        if (EraseMode)
        {
            if (cell is null)
            {
                return SkillCellClick.Nothing;
            }

            RemoveCell(stored);
            return SkillCellClick.Removed;
        }

        if (cell is null)
        {
            Apply($"Клетка {Show(stored)} в область", (d, _) => ((SkillData)d).Cells.Add(new SkillCellData { Q = stored.Q, R = stored.R }));
        }

        Select(stored);
        return cell is null ? SkillCellClick.Added : SkillCellClick.Selected;
    }

    public void Select(HexCoord? stored)
    {
        Selected = stored is { } s && CellAt(s) is not null ? s : null;
        NotifyChanged();
    }

    /// <summary>«Убрать из области».</summary>
    public void RemoveCell(HexCoord stored)
    {
        Apply($"Убрать клетку {Show(stored)}", (d, _) => ((SkillData)d).Cells.RemoveAll(c => c.Q == stored.Q && c.R == stored.R));
        if (Selected == stored)
        {
            Selected = null;
        }

        NotifyChanged();
    }

    public void SetIcon(string icon) => Apply("Иконка", (d, _) => ((SkillData)d).Icon = icon.Trim());

    public void SetCellColor(HexCoord stored, string? color, double? alpha) => EditCell(stored, "цвет", c =>
    {
        c.Color = color;
        c.Alpha = alpha;
    });

    /// <summary>Own effects (a copy of the effective ones to edit) or back to inheriting the base effects.</summary>
    public void SetCellOwnEffects(HexCoord stored, bool own) => Apply(own ? $"Клетка {Show(stored)}: свои эффекты" : $"Клетка {Show(stored)}: эффекты навыка", (d, _) =>
    {
        var skill = (SkillData)d;
        var cell = skill.FindCell(stored.Q, stored.R);
        if (cell is null)
        {
            return;
        }

        if (own && cell.Inherits)
        {
            cell.Effects.AddRange(skill.EffectsOf(cell).Select(e => e.Clone()));
            cell.Overrides.Clear();
        }
        else if (!own)
        {
            cell.Effects.Clear();
            cell.Overrides.Clear();
        }
    });

    /// <summary>Adds an effect of a registered type with its defaults (to the base effects, or to a cell's own effects).</summary>
    public string? AddEffect(HexCoord? cell, string type)
    {
        if (Library.Registry.Effects.Find(type) is not { } descriptor || (descriptor.Contexts & EffectContext.Skill) == 0)
        {
            return $"тип эффекта «{type}» нельзя использовать в навыке";
        }

        Apply($"Добавить эффект «{descriptor.Label}»", (d, _) => ListOf((SkillData)d, cell)?.Add(descriptor.CreateDefault()));
        return null;
    }

    public void RemoveEffect(HexCoord? cell, int index) => Apply("Убрать эффект", (d, _) =>
    {
        var skill = (SkillData)d;
        var list = ListOf(skill, cell);
        if (list is null || index < 0 || index >= list.Count)
        {
            return;
        }

        list.RemoveAt(index);
        if (cell is null)
        {
            // Overrides follow the base effects they belong to.
            foreach (var c in skill.Cells)
            {
                var shifted = new SortedDictionary<int, SortedDictionary<string, object>>();
                foreach (var (i, values) in c.Overrides)
                {
                    if (i != index)
                    {
                        shifted[i > index ? i - 1 : i] = values;
                    }
                }

                c.Overrides.Clear();
                foreach (var (i, values) in shifted)
                {
                    c.Overrides[i] = values;
                }
            }
        }
    });

    /// <summary>
    /// Sets a field of an effect from user input. For a cell that inherits the base effects the value becomes an override
    /// of this cell only (equal to the base value = no override). Invalid input is refused with an explanation.
    /// </summary>
    public ContentIssue? SetEffectField(HexCoord? cell, int index, string key, object? input)
    {
        var effect = EffectAt(cell, index);
        var descriptor = effect is null ? null : Library.Registry.Effects.Find(effect.Type);
        if (descriptor?.FindField(key) is not { } field)
        {
            return ContentIssue.Error(key, "нет такого поля");
        }

        var value = input;
        if (field.Kind is FieldKind.Number or FieldKind.Integer && input is string text)
        {
            if (!double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                return ContentIssue.Error(key, $"«{field.Label}»: «{text}» — не число, значение не изменено");
            }

            value = parsed;
        }

        if (field.Check(value, key) is { } issue)
        {
            return issue with { Message = issue.Message + " — значение не изменено" };
        }

        var inherited = cell is { } c && CellAt(c) is { Inherits: true };
        Apply($"«{field.Label}»: {value}", (d, _) =>
        {
            var skill = (SkillData)d;
            if (inherited)
            {
                var target = skill.FindCell(cell!.Value.Q, cell.Value.R)!;
                var baseValue = skill.BaseEffects[index].Get(key) ?? field.Default;
                if (!target.Overrides.TryGetValue(index, out var values))
                {
                    values = new SortedDictionary<string, object>(StringComparer.Ordinal);
                    target.Overrides[index] = values;
                }

                if (Equals(Normalize(baseValue), Normalize(value)))
                {
                    values.Remove(key);
                }
                else
                {
                    values[key] = value!;
                }

                if (values.Count == 0)
                {
                    target.Overrides.Remove(index);
                }
            }
            else
            {
                ListOf(skill, cell)![index].Set(key, value);
            }
        });
        return null;
    }

    /// <summary>Removes a cell's override of one base-effect field (back to the skill's value).</summary>
    public void ResetOverride(HexCoord cell, int index, string key) => EditCell(cell, "сброс переопределения", c =>
    {
        if (c.Overrides.TryGetValue(index, out var values))
        {
            values.Remove(key);
            if (values.Count == 0)
            {
                c.Overrides.Remove(index);
            }
        }
    });

    /// <summary>True if the cell overrides the field of base effect <paramref name="index"/>.</summary>
    public bool IsOverridden(HexCoord cell, int index, string key) =>
        CellAt(cell) is { } c && c.Overrides.TryGetValue(index, out var values) && values.ContainsKey(key);

    /// <summary>The effect being edited: base effect, a cell's own effect, or the effective (inherited + overrides) one.</summary>
    public EffectData? EffectAt(HexCoord? cell, int index)
    {
        if (cell is null)
        {
            return index >= 0 && index < Skill.BaseEffects.Count ? Skill.BaseEffects[index] : null;
        }

        if (CellAt(cell.Value) is not { } c)
        {
            return null;
        }

        var effects = Skill.EffectsOf(c);
        return index >= 0 && index < effects.Count ? effects[index] : null;
    }

    /// <summary>Per-cell behaviour in words with the preview Attack Power (spec part 4 §6.2: the user sees each cell's result).</summary>
    public IReadOnlyList<string> Preview()
    {
        var lines = new List<string>();
        foreach (var cell in Skill.Cells)
        {
            var offset = cell.Offset;
            var where = offset == default ? "сам юнит" : $"{offset.DistanceTo(default)} гекс. {DirectionWord(offset)}";
            var effects = Skill.EffectsOf(cell);
            var text = effects.Count == 0 ? "нет эффектов" : string.Join("; ", effects.Select(e => EffectPreview.Describe(e, PreviewAttackPower, PreviewMaxHealth, Library)));
            lines.Add($"{Show(offset)} ({where}{(cell.Inherits ? (cell.Overrides.Count > 0 ? ", эффекты навыка с изменениями" : ", эффекты навыка") : ", свои эффекты")}): {text}");
        }

        return lines;
    }

    private List<EffectData>? ListOf(SkillData skill, HexCoord? cell) =>
        cell is null ? skill.BaseEffects : skill.FindCell(cell.Value.Q, cell.Value.R)?.Effects;

    private void EditCell(HexCoord stored, string what, Action<SkillCellData> change) =>
        Apply($"Клетка {Show(stored)}: {what}", (d, _) =>
        {
            if (((SkillData)d).FindCell(stored.Q, stored.R) is { } c)
            {
                change(c);
            }
        });

    private void NotifyChanged() => RaiseChanged();

    private static object? Normalize(object? v) => v is int i ? (double)i : v;

    public static string Show(HexCoord offset) => $"({offset.Q},{offset.R})";

    private static string DirectionWord(HexCoord offset)
    {
        foreach (var d in HexDirections.All)
        {
            var step = HexCoord.DirectionOffset(d);
            var n = offset.DistanceTo(default);
            if (new HexCoord(step.Q * n, step.R * n) == offset)
            {
                return d switch
                {
                    HexDirection.North => "вперёд",
                    HexDirection.South => "назад",
                    HexDirection.NorthEast => "вперёд-вправо",
                    HexDirection.NorthWest => "вперёд-влево",
                    HexDirection.SouthEast => "назад-вправо",
                    _ => "назад-влево",
                };
            }
        }

        return "в стороне";
    }
}

/// <summary>An effect in words with numbers for a given caster (shared by the skill and unit editors).</summary>
public static class EffectPreview
{
    /// <summary>The effect with the name of its damage type (spec part 5 §8.1: «тип — режущий»).</summary>
    public static string Describe(EffectData effect, double attackPower, double maxHealth, ContentLibrary library)
    {
        var text = Describe(effect, attackPower, maxHealth, library.Registry.Effects);
        var damaging = effect.Type == "damage" || (effect.Type == "periodic" && (effect.Get("kind") as string ?? "damage") == "damage");
        if (damaging && effect.ToSpec(library.Registry.Effects).Text("damageType") is { Length: > 0 } type)
        {
            text += $" · {library.Find("damage_type", type)?.Name.ToLowerInvariant() ?? type}";
        }

        if (effect.Type == "damage" && (effect.Get("area") as string) == "split")
        {
            text += " · сила делится между целями";
        }

        return text;
    }

    public static string Describe(EffectData effect, double attackPower, double maxHealth, EffectTypeRegistry registry)
    {
        var s = effect.ToSpec(registry);
        var hits = Math.Max(1, (int)Math.Round(s.Number("hits", 1)));
        var interval = s.Number("interval", 0);
        var targets = s.Text("targets", "enemies") switch
        {
            "allies" => "союзникам",
            "self" => "себе",
            "all" => "всем",
            _ => "врагам",
        };
        string Amount(string unitWord)
        {
            var formula = s.Text("formula", "fixed");
            var per = formula switch
            {
                "attack" => s.Number("multiplier", 1) * attackPower,
                "maxHealth" => s.Number("multiplier", 1) * maxHealth,
                _ => s.Number("amount"),
            };
            var source = formula switch
            {
                "attack" => $"{N(s.Number("multiplier", 1) * 100)} % силы атаки {N(attackPower)} = ",
                "maxHealth" => $"{N(s.Number("multiplier", 1) * 100)} % макс. здоровья = ",
                _ => string.Empty,
            };
            var each = $"{source}{N(per)}";
            var timing = hits > 1 ? $", каждые {N(interval)} с" : string.Empty;
            return hits > 1 ? $"{unitWord} {hits} × {each} = {N(per * hits)}{timing}" : $"{unitWord} {each}";
        }

        return effect.Type switch
        {
            "damage" => $"{Amount("урон")} {targets}",
            "heal" => $"{Amount("лечение")} {targets} (не выше максимума)",
            "buff" or "debuff" => $"{(effect.Type == "buff" ? "+" : "−")}{StatSize(s)} {StatWord(s.Text("stat", "moveSpeed"))} на {N(s.Number("duration", 5))} с {targets}{(hits > 1 ? $", {hits} раза" : string.Empty)}",
            "periodic" => $"{(s.Text("kind", "damage") == "heal" ? "лечение" : "урон")} {N(s.Number("perSecond", 5))} HP/с × {N(s.Number("duration", 3))} с = {N(s.Number("perSecond", 5) * s.Number("duration", 3))} {targets}",
            _ => registry.Find(effect.Type)?.Label.ToLowerInvariant() ?? effect.Type,
        };
    }

    /// <summary>«30 %», «1» or, for a resistance, «20 п.п.» (resistance changes are always in percentage points).</summary>
    internal static string StatSize(EffectSpec s) =>
        s.Text("stat", Stats.MoveSpeed) == Stats.Resistance ? N(s.Number("delta")) + " п.п."
        : s.Text("mode", "percent") == "percent" ? N(s.Number("percent") * 100) + " %" : N(s.Number("delta"));

    internal static string StatWord(string stat) => stat switch
    {
        Stats.AttackPower => "силы атаки",
        Stats.AttackRate => "скорости атак",
        Stats.Resistance => "сопротивления",
        Stats.HealingReceived => "получаемого лечения",
        _ => "скорости движения",
    };

    private static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
