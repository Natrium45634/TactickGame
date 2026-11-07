using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using HexTactics.Content.Model;
using HexTactics.Content.Registry;
using HexTactics.Core.Hex;

namespace HexTactics.Content.Kinds;

/// <summary>
/// One cell of a skill's area (spec part 4 §6.1, §6.4): axial offset from the caster for a caster looking North
/// (<c>q, r</c>), its own effects (empty = the skill's base effects are inherited) and an editor colour that is only a
/// marker for the author — never a source of rules.
/// </summary>
public sealed class SkillCellData
{
    public int Q { get; set; }

    public int R { get; set; }

    public string? Color { get; set; }

    /// <summary>Opacity of the editor colour, 0.05…1.</summary>
    public double? Alpha { get; set; }

    public List<EffectData> Effects { get; } = new();

    /// <summary>
    /// For a cell that inherits the skill's base effects: values overridden in this cell only, by base effect index and
    /// field (spec part 4 §6.2: the cell's own values win only in the explicitly overridden fields).
    /// </summary>
    public SortedDictionary<int, SortedDictionary<string, object>> Overrides { get; } = new();

    public HexCoord Offset => new(Q, R);

    public bool Inherits => Effects.Count == 0;

    public SkillCellData Clone()
    {
        var copy = new SkillCellData { Q = Q, R = R, Color = Color, Alpha = Alpha };
        foreach (var e in Effects)
        {
            copy.Effects.Add(e.Clone());
        }

        foreach (var (index, values) in Overrides)
        {
            copy.Overrides[index] = new SortedDictionary<string, object>(values, StringComparer.Ordinal);
        }

        return copy;
    }
}

/// <summary>
/// A skill definition of the library (spec part 4 §6, <c>SkillDefinition</c>): icon, the cells of the area relative to
/// the caster and the effects of every cell. The part-3 «Линейный удар» is an ordinary instance of this data.
/// </summary>
public sealed class SkillData : IContentData
{
    /// <summary>Short symbol for the ТД menu and the skill button.</summary>
    public string Icon { get; set; } = "✦";

    /// <summary>Effects of cells that have no effects of their own.</summary>
    public List<EffectData> BaseEffects { get; } = new();

    public List<SkillCellData> Cells { get; } = new();

    /// <summary>
    /// The effects that apply to <paramref name="cell"/>: its own ones, or the base effects of the skill with the cell's
    /// overridden fields applied.
    /// </summary>
    public IReadOnlyList<EffectData> EffectsOf(SkillCellData cell)
    {
        if (!cell.Inherits)
        {
            return cell.Effects;
        }

        var list = new List<EffectData>(BaseEffects.Count);
        for (var i = 0; i < BaseEffects.Count; i++)
        {
            var effect = BaseEffects[i].Clone();
            if (cell.Overrides.TryGetValue(i, out var values))
            {
                foreach (var (key, value) in values)
                {
                    effect.Set(key, value);
                }
            }

            list.Add(effect);
        }

        return list;
    }

    public SkillCellData? FindCell(int q, int r) => Cells.Find(c => c.Q == q && c.R == r);

    public JsonObject ToJson()
    {
        var baseEffects = new JsonArray();
        foreach (var e in BaseEffects)
        {
            baseEffects.Add(e.ToJson());
        }

        var cells = new JsonArray();
        foreach (var c in Cells)
        {
            var effects = new JsonArray();
            foreach (var e in c.Effects)
            {
                effects.Add(e.ToJson());
            }

            var cell = new JsonObject { ["q"] = c.Q, ["r"] = c.R };
            if (c.Color is not null)
            {
                cell["color"] = c.Color;
            }

            if (c.Alpha is { } a)
            {
                cell["alpha"] = a;
            }

            cell["effects"] = effects;
            if (c.Overrides.Count > 0)
            {
                var overrides = new JsonObject();
                foreach (var (index, values) in c.Overrides)
                {
                    var o = new JsonObject();
                    foreach (var (key, value) in values)
                    {
                        o[key] = JsonFields.ToNode(value);
                    }

                    overrides[index.ToString(CultureInfo.InvariantCulture)] = o;
                }

                cell["overrides"] = overrides;
            }

            cells.Add(cell);
        }

        return new JsonObject
        {
            ["icon"] = Icon,
            ["anchor"] = "caster",
            ["rotationMode"] = "facing",
            ["baseEffects"] = baseEffects,
            ["cells"] = cells,
        };
    }

    public static SkillData Read(JsonObject obj, IssueList issues)
    {
        var data = new SkillData { Icon = JsonFields.Text(obj, "icon", "✦", issues, string.Empty) };
        var anchor = JsonFields.Text(obj, "anchor", "caster", issues, string.Empty);
        if (anchor != "caster")
        {
            issues.Error("anchor", $"неподдерживаемая привязка «{anchor}» (есть только caster)");
        }

        var rotation = JsonFields.Text(obj, "rotationMode", "facing", issues, string.Empty);
        if (rotation != "facing")
        {
            issues.Error("rotationMode", $"неподдерживаемый поворот «{rotation}» (есть только facing)");
        }

        if (JsonFields.Array(obj, "baseEffects", issues, string.Empty) is { } baseEffects)
        {
            for (var i = 0; i < baseEffects.Count; i++)
            {
                data.BaseEffects.Add(EffectData.Read(baseEffects[i], issues, $"baseEffects[{i}]"));
            }
        }

        if (JsonFields.Array(obj, "cells", issues, string.Empty) is { } cells)
        {
            for (var i = 0; i < cells.Count; i++)
            {
                var path = $"cells[{i}]";
                if (cells[i] is not JsonObject c)
                {
                    issues.Error(path, "клетка должна быть объектом");
                    continue;
                }

                var cell = new SkillCellData
                {
                    Q = JsonFields.Integer(c, "q", 0, issues, path),
                    R = JsonFields.Integer(c, "r", 0, issues, path),
                };
                if (c.ContainsKey("color"))
                {
                    cell.Color = JsonFields.Text(c, "color", "#2A2B36", issues, path);
                }

                if (c.ContainsKey("alpha"))
                {
                    cell.Alpha = JsonFields.Number(c, "alpha", 0.28, issues, path);
                }

                if (JsonFields.Array(c, "effects", issues, path) is { } effects)
                {
                    for (var e = 0; e < effects.Count; e++)
                    {
                        cell.Effects.Add(EffectData.Read(effects[e], issues, $"{path}.effects[{e}]"));
                    }
                }

                if (c["overrides"] is JsonObject overrides)
                {
                    foreach (var (key, node) in overrides)
                    {
                        if (!int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var index) || node is not JsonObject values)
                        {
                            issues.Error($"{path}.overrides.{key}", "переопределение — объект по номеру базового эффекта");
                            continue;
                        }

                        var dict = new SortedDictionary<string, object>(StringComparer.Ordinal);
                        foreach (var (field, value) in values)
                        {
                            if (JsonFields.Scalar(value) is { } scalar)
                            {
                                dict[field] = scalar;
                            }
                            else
                            {
                                issues.Error($"{path}.overrides.{key}.{field}", "значение должно быть числом, строкой или true/false");
                            }
                        }

                        cell.Overrides[index] = dict;
                    }
                }
                else if (c.ContainsKey("overrides"))
                {
                    issues.Error(path + ".overrides", "ожидается объект");
                }

                data.Cells.Add(cell);
            }
        }

        return data;
    }

    public IEnumerable<ContentRef> References() => Array.Empty<ContentRef>();

    public void Validate(ValidationContext context)
    {
        var issues = context.Issues;
        if (Icon.Length is 0 or > 4)
        {
            issues.Error("icon", "иконка — 1–4 символа");
        }

        if (Cells.Count == 0)
        {
            issues.Warning("cells", "область навыка пуста: навык ничего не делает");
        }

        if (Cells.Count > context.Limits.MaxSkillCells)
        {
            issues.Error("cells", $"не больше {context.Limits.MaxSkillCells} клеток в области");
        }

        for (var i = 0; i < BaseEffects.Count; i++)
        {
            BaseEffects[i].Validate(context, $"baseEffects[{i}]", EffectContext.Skill);
        }

        var seen = new HashSet<(int, int)>();
        for (var i = 0; i < Cells.Count; i++)
        {
            var cell = Cells[i];
            var path = $"cells[{i}]";
            if (!seen.Add((cell.Q, cell.R)))
            {
                issues.Error(path, $"клетка ({cell.Q}, {cell.R}) указана дважды");
            }

            if (cell.Offset.DistanceTo(default) > context.Limits.MaxSkillRadius)
            {
                issues.Error(path, $"клетка дальше {context.Limits.MaxSkillRadius} гексов от юнита");
            }

            if (cell.Color is not null && !Schema.FieldSchema.IsColor(cell.Color))
            {
                issues.Error(path + ".color", "ожидается цвет #RRGGBB");
            }

            if (cell.Alpha is { } a && (double.IsNaN(a) || a < 0.05 || a > 1))
            {
                issues.Error(path + ".alpha", "прозрачность — от 0.05 до 1");
            }

            if (cell.Effects.Count > context.Limits.MaxEffectsPerCell)
            {
                issues.Error(path + ".effects", $"не больше {context.Limits.MaxEffectsPerCell} эффектов в клетке");
            }

            for (var e = 0; e < cell.Effects.Count; e++)
            {
                cell.Effects[e].Validate(context, $"{path}.effects[{e}]", EffectContext.Skill);
            }

            foreach (var (index, values) in cell.Overrides)
            {
                if (!cell.Inherits)
                {
                    issues.Warning($"{path}.overrides", "у клетки свои эффекты — переопределения базовых не используются");
                    break;
                }

                if (index < 0 || index >= BaseEffects.Count)
                {
                    issues.Error($"{path}.overrides.{index}", $"нет базового эффекта №{index + 1}");
                    continue;
                }

                var descriptor = context.Effects.Find(BaseEffects[index].Type);
                foreach (var (key, value) in values)
                {
                    var field = descriptor?.FindField(key);
                    if (field is null)
                    {
                        issues.Error($"{path}.overrides.{index}.{key}", $"поле «{key}» не принадлежит эффекту");
                    }
                    else if (field.Check(value, $"{path}.overrides.{index}.{key}") is { } bad)
                    {
                        issues.AddRange(new[] { bad });
                    }
                }
            }

            if (cell.Inherits && cell.Overrides.Count > 0)
            {
                var effective = EffectsOf(cell);
                for (var e = 0; e < effective.Count; e++)
                {
                    effective[e].Validate(context, $"{path}.effective[{e}]", EffectContext.Skill);
                }
            }

            if (EffectsOf(cell).Count == 0)
            {
                issues.Warning(path, $"у клетки ({cell.Q}, {cell.R}) нет эффектов");
            }
        }
    }

    public string Summary() => string.Create(CultureInfo.InvariantCulture, $"{Icon} клеток: {Cells.Count}");

    public IContentData Clone()
    {
        var copy = new SkillData { Icon = Icon };
        foreach (var e in BaseEffects)
        {
            copy.BaseEffects.Add(e.Clone());
        }

        foreach (var c in Cells)
        {
            copy.Cells.Add(c.Clone());
        }

        return copy;
    }
}
