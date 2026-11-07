using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HexTactics.Content.Kinds;
using HexTactics.Content.Library;
using HexTactics.Content.Registry;
using HexTactics.Editor.Definitions;

namespace HexTactics.Editor.Units;

/// <summary>A skill that can be added to the unit (catalog entry of the skill picker).</summary>
public sealed record SkillChoice(string Id, string Name, string Icon, string Summary, bool IsBuiltIn);

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
}

/// <summary>Human-readable combat numbers of a unit definition (shared by editors and tests).</summary>
public static class CombatPreviewText
{
    public static IReadOnlyList<string> Describe(UnitData unit, ContentLibrary library)
    {
        var lines = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture, $"Обычная атака: {N(unit.AttackPower)} урона за удар × {N(unit.AttackRate)} удар/с ≈ {N(unit.AttackPower * unit.AttackRate)} урона/с, дальность {unit.AttackRange} гекс."),
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
                    effects.Add(EffectText(e, unit.AttackPower, library.Registry.Effects));
                }
            }

            var distinct = effects.Distinct().ToList();
            lines.Add($"{skill.Icon} «{item.Name}»: клеток {skill.Cells.Count}; " + (distinct.Count == 0 ? "без эффектов" : string.Join("; ", distinct)) + ".");
        }

        return lines;
    }

    /// <summary>One effect in words, with the numbers computed for the given attack power.</summary>
    public static string EffectText(EffectData effect, double attackPower, EffectTypeRegistry registry)
    {
        var label = registry.Find(effect.Type)?.Label ?? effect.Type;
        var spec = effect.ToSpec(registry);
        return effect.Type switch
        {
            "damage" => $"{label.ToLowerInvariant()} {N(spec.Number("amount"))}",
            _ => label.ToLowerInvariant(),
        };
    }

    private static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
