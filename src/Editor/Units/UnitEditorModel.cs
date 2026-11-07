using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HexTactics.Content.Kinds;
using HexTactics.Content.Model;
using HexTactics.Content.Library;
using HexTactics.Content.Registry;
using HexTactics.Editor.Definitions;

namespace HexTactics.Editor.Units;

/// <summary>A skill that can be added to the unit (catalog entry of the skill picker).</summary>
public sealed record SkillChoice(string Id, string Name, string Icon, string Summary, bool IsBuiltIn);

/// <summary>
/// One row of the «Сопротивления» table (spec part 5 §7.2): the global row, a family, or a subtype under its family.
/// <see cref="Bypass"/> rows (true-like types) have no number — they ignore numeric resistance.
/// </summary>
public sealed record ResistanceRow(string Key, string Label, double Value, int Depth, bool Bypass);

/// <summary>
/// Model of the unit editor (spec part 4 §5): the characteristics come from <see cref="UnitData.Fields"/>; the skill set is
/// a list of references to saved skills (never copies of them), added from a filtered catalog, removed, reordered. The
/// combat preview shows what the numbers mean for this unit.
/// </summary>
public sealed class UnitEditorModel : DefinitionEditorModel
{
    public UnitEditorModel(ContentLibrary library, ContentItem item)
        : base(library, item)
    {
        if (item.Data is not UnitData)
        {
            throw new ArgumentException("Not a unit.", nameof(item));
        }
    }

    public UnitData Unit => (UnitData)Item.Data;

    /// <summary>Saved skills not yet in the set, filtered by name, id or summary.</summary>
    public IReadOnlyList<SkillChoice> AvailableSkills(string filter = "")
    {
        var list = new List<SkillChoice>();
        foreach (var s in Library.Items(ContentTypeRegistry.Skill))
        {
            if (Unit.Skills.Contains(s.Id) || s.HasErrors)
            {
                continue;
            }

            var data = (SkillData)s.Data;
            var summary = data.Summary();
            if (filter.Length > 0 && !s.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase) && !s.Id.Contains(filter, StringComparison.OrdinalIgnoreCase) && !summary.Contains(filter, StringComparison.CurrentCultureIgnoreCase))
            {
                continue;
            }

            list.Add(new SkillChoice(s.Id, s.Name, data.Icon, summary, s.IsBuiltIn));
        }

        return list;
    }

    /// <summary>Adds a reference to a saved skill (refused for unknown skills, duplicates and beyond the limit).</summary>
    public string? AddSkill(string skillId)
    {
        if (Library.Find(ContentTypeRegistry.Skill, skillId) is not { } skill)
        {
            return $"навык «{skillId}» не найден в библиотеке";
        }

        if (Unit.Skills.Contains(skillId))
        {
            return $"навык «{skill.Name}» уже есть у юнита";
        }

        if (Unit.Skills.Count >= Library.Limits.MaxSkillsPerUnit)
        {
            return $"у юнита не больше {Library.Limits.MaxSkillsPerUnit} навыков";
        }

        Apply($"Добавить навык «{skill.Name}»", (d, _) => ((UnitData)d).Skills.Add(skillId));
        return null;
    }

    public void RemoveSkill(string skillId)
    {
        var name = Library.Find(ContentTypeRegistry.Skill, skillId)?.Name ?? skillId;
        Apply($"Убрать навык «{name}»", (d, _) => ((UnitData)d).Skills.Remove(skillId));
    }

    /// <summary>Moves a skill up (−1) or down (+1); the first skill is the one of the R key and the HUD button.</summary>
    public void MoveSkill(string skillId, int delta)
    {
        var index = Unit.Skills.IndexOf(skillId);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= Unit.Skills.Count)
        {
            return;
        }

        Apply("Порядок навыков", (d, _) =>
        {
            var skills = ((UnitData)d).Skills;
            skills.RemoveAt(index);
            skills.Insert(target, skillId);
        });
    }

    /// <summary>
    /// What the characteristics mean in combat for this unit (spec part 4 §5.2): normal attack per hit and per second
    /// (attack power ≠ attack rate) and the effects of each skill with this unit's attack power.
    /// </summary>
    public IReadOnlyList<string> CombatPreview() => CombatPreviewText.Describe(Unit, Library);

    // ------------------------------------------------------------------ resistances (spec part 5 §3, §7)

    /// <summary>
    /// The rows of the resistance table: «все сопротивляемые типы», then every family with its subtypes (physical, magical,
    /// custom families; legacy types only if this unit has a value for them), true-like types last as rows without a number.
    /// </summary>
    public IReadOnlyList<ResistanceRow> ResistanceRows()
    {
        var rows = new List<ResistanceRow> { new(UnitData.GlobalResistance, "Все сопротивляемые типы", Value(UnitData.GlobalResistance), 0, false) };
        var types = Library.Items(ContentTypeRegistry.DamageType)
            .Where(t => t.Data is DamageTypeData d && (!d.Legacy || Unit.Resistances.ContainsKey(t.Id)))
            .ToList();
        var families = types.Where(t => ((DamageTypeData)t.Data).Family.Length == 0)
            .OrderBy(t => ((DamageTypeData)t.Data).Strategy == DamageTypeData.TrueLike ? 1 : 0)
            .ThenBy(t => t.Id == "core.damage.physical" ? 0 : t.Id == "core.damage.magical" ? 1 : 2)
            .ThenBy(t => t.Name, StringComparer.CurrentCulture);
        foreach (var family in families)
        {
            var bypass = ((DamageTypeData)family.Data).Strategy == DamageTypeData.TrueLike;
            rows.Add(new ResistanceRow(family.Id, family.Name, Value(family.Id), 0, bypass));
            foreach (var sub in types.Where(t => ((DamageTypeData)t.Data).Family == family.Id).OrderBy(t => t.Name, StringComparer.CurrentCulture))
            {
                rows.Add(new ResistanceRow(sub.Id, sub.Name, Value(sub.Id), 1, bypass));
            }
        }

        return rows;
    }

    /// <summary>Sets one resistance from input (−500…+500 %, decimal comma or dot); 0 removes the row's value.</summary>
    public ContentIssue? SetResistance(string key, object? input)
    {
        double value;
        switch (input)
        {
            case double d:
                value = d;
                break;
            case string text when double.TryParse(text.Trim().Replace(',', '.').TrimEnd('%').Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed):
                value = parsed;
                break;
            default:
                return ContentIssue.Error($"resistances.{key}", $"сопротивление: «{input}» — не число, значение не изменено");
        }

        if (!double.IsFinite(value) || value < UnitData.MinResistance || value > UnitData.MaxResistance)
        {
            return ContentIssue.Error($"resistances.{key}", $"сопротивление: от −500 до +500 %, а не {value.ToString("0.##", CultureInfo.InvariantCulture)} — значение не изменено");
        }

        if (key != UnitData.GlobalResistance && Library.Find(ContentTypeRegistry.DamageType, key) is null)
        {
            return ContentIssue.Error($"resistances.{key}", $"нет типа урона «{key}»");
        }

        var rounded = (double)Math.Round((decimal)value, 2, MidpointRounding.AwayFromZero); // spec part 5 §3.1: 0.01 point
        var name = key == UnitData.GlobalResistance ? "все типы" : Library.Find(ContentTypeRegistry.DamageType, key)!.Name;
        Apply($"Сопротивление «{name}»: {Explain(rounded)}", (d, _) =>
        {
            var u = (UnitData)d;
            if (rounded == 0)
            {
                u.Resistances.Remove(key);
            }
            else
            {
                u.Resistances[key] = rounded;
            }
        });
        return null;
    }

    private double Value(string key) => Unit.Resistances.TryGetValue(key, out var v) ? v : 0;

    /// <summary>The resistance against a type with every layer of this unit (global + family + subtype, clamped).</summary>
    public double EffectiveResistance(string typeId)
    {
        if (Library.Find(ContentTypeRegistry.DamageType, typeId)?.Data is not DamageTypeData d)
        {
            return 0;
        }

        var family = d.Family.Length == 0 ? typeId : d.Family;
        var r = Value(UnitData.GlobalResistance) + Value(family) + (family == typeId ? 0 : Value(typeId));
        return Math.Clamp(r, UnitData.MinResistance, UnitData.MaxResistance);
    }

    /// <summary>«−50 % = 150 % входящего урона» (spec part 5 §7.2): what a value means; never calls more than 100 % healing.</summary>
    public static string Explain(double r)
    {
        var n = r.ToString("+0.##;−0.##;0", CultureInfo.InvariantCulture);
        var multiplier = Math.Max(0, 1 - (r / 100)) * 100;
        return r switch
        {
            > 100 => $"{n} % = 0 урона без дополнительных реакций (сверх 100 % не лечит)",
            100 => $"{n} % = 0 урона",
            _ => $"{n} % = {multiplier.ToString("0.##", CultureInfo.InvariantCulture)} % входящего урона",
        };
    }

    /// <summary>«Сильные стороны: режущий +70 %; слабости: дробящий −50 %» — by the effective value of every non-legacy type.</summary>
    public string ResistanceSummary()
    {
        var strong = new List<string>();
        var weak = new List<string>();
        foreach (var t in Library.Items(ContentTypeRegistry.DamageType))
        {
            if (t.Data is not DamageTypeData { Legacy: false, Strategy: DamageTypeData.ResistanceAffected })
            {
                continue;
            }

            var r = EffectiveResistance(t.Id);
            var text = $"{t.Name.ToLowerInvariant()} {r.ToString("+0.##;−0.##", CultureInfo.InvariantCulture)} %";
            if (r > 0)
            {
                strong.Add(text);
            }
            else if (r < 0)
            {
                weak.Add(text);
            }
        }

        if (strong.Count == 0 && weak.Count == 0)
        {
            return "Сопротивлений нет: весь урон проходит полностью.";
        }

        return $"Сильные стороны: {(strong.Count == 0 ? "нет" : string.Join(", ", strong))}; слабости: {(weak.Count == 0 ? "нет" : string.Join(", ", weak))}.";
    }
}

/// <summary>Human-readable combat numbers of a unit definition (shared by editors and tests).</summary>
public static class CombatPreviewText
{
    public static IReadOnlyList<string> Describe(UnitData unit, ContentLibrary library)
    {
        var lines = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture, $"Обычная атака: {N(unit.AttackPower)} урона за удар ({library.Find(ContentTypeRegistry.DamageType, unit.AttackDamageType)?.Name.ToLowerInvariant() ?? unit.AttackDamageType}) × {N(unit.AttackRate)} удар/с ≈ {N(unit.AttackPower * unit.AttackRate)} урона/с, дальность {unit.AttackRange} гекс")
                + (unit.AttackAreaRadius > 0 ? $"; область {unit.AttackAreaRadius} гекс. вокруг цели, {(unit.AttackAreaSplit ? "сила делится между целями" : "полная сила каждой цели")}." : "."),
            string.Create(CultureInfo.InvariantCulture, $"Здоровье {N(unit.MaxHealth)} HP · движение {N(unit.MoveSpeed)} гекс/с (переход одного гекса за {N(1 / Math.Max(0.01, unit.MoveSpeed))} с)."),
        };
        foreach (var id in unit.Skills)
        {
            if (library.Find(ContentTypeRegistry.Skill, id) is not { Data: SkillData skill } item)
            {
                lines.Add($"✗ навык «{id}» не найден");
                continue;
            }

            var effects = new List<string>();
            foreach (var cell in skill.Cells)
            {
                foreach (var e in skill.EffectsOf(cell))
                {
                    effects.Add(HexTactics.Editor.Skills.EffectPreview.Describe(e, unit.AttackPower, unit.MaxHealth, library));
                }
            }

            var distinct = effects.Distinct().ToList();
            lines.Add($"{skill.Icon} «{item.Name}»: клеток {skill.Cells.Count}; " + (distinct.Count == 0 ? "без эффектов" : string.Join("; ", distinct)) + ".");
        }

        return lines;
    }

    private static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
