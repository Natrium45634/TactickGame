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

/// <summary>A saved defense rule that can be attached to the unit (entry of the rule picker).</summary>
public sealed record DefenseRuleChoice(string Id, string Name, string Summary, bool IsBuiltIn);

/// <summary>A skill that can be added to the unit (catalog entry of the skill picker).</summary>
public sealed record SkillChoice(string Id, string Name, string Icon, string Summary, bool IsBuiltIn);

/// <summary>
/// One row of the «Сопротивления» table (spec part 5 §7.2): the global row, a family, or a subtype under its family.
/// <see cref="Bypass"/> rows (true-like types) have no number — they ignore numeric resistance.
/// </summary>
public sealed record ResistanceRow(string Key, string Label, double Value, int Depth, bool Bypass, string? Source = null, bool Own = false)
{
    /// <summary>The value comes from a set (not the object's own) — «наследовано».</summary>
    public bool Inherited => Source is not null && !Own;
}

/// <summary>A row of the compact resistance table (spec part 5 §7.2): a family row knows whether it has subtypes and is open.</summary>
public sealed record ResistanceViewRow(ResistanceRow Row, bool HasChildren, bool Expanded);

/// <summary>The visible rows and how many rows the filter hides («Показать все» / search shows them).</summary>
public sealed record ResistanceView(IReadOnlyList<ResistanceViewRow> Rows, int Hidden);

/// <summary>A compact card of «Особые защиты и реакции» (spec part 5 §7.2): «Огненная конверсия 25/75», «Щит 50 HP».</summary>
public sealed record DefenseCard(string Id, string Title, string Short, string Summary, bool Problem);

/// <summary>One line of the section headers of the unit inspector (spec part 5 §7.1).</summary>
public sealed record UnitSections(string Main, string Attack, string Resistances, string More);

/// <summary>A saved resistance set that can be applied to the unit.</summary>
public sealed record ProfileChoice(string Id, string Name, string Summary, bool IsBuiltIn);

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

    // ------------------------------------------------------------------ defense rules and shields (spec part 5 §4, §6.4, §8.3)

    /// <summary>Saved rules without errors that are not attached yet, filtered by name, id or summary.</summary>
    public IReadOnlyList<DefenseRuleChoice> AvailableRules(string filter = "")
    {
        var list = new List<DefenseRuleChoice>();
        foreach (var r in Library.Items(ContentTypeRegistry.DefenseRule))
        {
            if (Unit.DefenseRules.Contains(r.Id) || r.HasErrors || r.Data is not DefenseRuleData data)
            {
                continue;
            }

            var summary = Defense.DefenseRuleSummary.Describe(data, Library);
            if (filter.Length > 0 && !r.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase) && !r.Id.Contains(filter, StringComparison.OrdinalIgnoreCase) && !summary.Contains(filter, StringComparison.CurrentCultureIgnoreCase))
            {
                continue;
            }

            list.Add(new DefenseRuleChoice(r.Id, r.Name, summary, r.IsBuiltIn));
        }

        return list;
    }

    /// <summary>Attaches a saved rule by reference (refused for unknown rules, duplicates and beyond the limit).</summary>
    public string? AddRule(string ruleId)
    {
        if (Library.Find(ContentTypeRegistry.DefenseRule, ruleId) is not { } rule)
        {
            return $"правило «{ruleId}» не найдено в библиотеке";
        }

        if (Unit.DefenseRules.Contains(ruleId))
        {
            return $"правило «{rule.Name}» уже есть у юнита";
        }

        if (Unit.DefenseRules.Count >= UnitData.MaxDefenseRules)
        {
            return $"у юнита не больше {UnitData.MaxDefenseRules} правил защиты";
        }

        Apply($"Добавить правило «{rule.Name}»", (d, _) => ((UnitData)d).DefenseRules.Add(ruleId));
        return null;
    }

    public void RemoveRule(string ruleId)
    {
        var name = Library.Find(ContentTypeRegistry.DefenseRule, ruleId)?.Name ?? ruleId;
        Apply($"Убрать правило «{name}»", (d, _) => ((UnitData)d).DefenseRules.Remove(ruleId));
    }

    /// <summary>The attached rules in words, in the order the resolver applies them (priority, then id).</summary>
    public IReadOnlyList<(string Id, string Name, string Summary)> RuleSummaries() => Unit.DefenseRules
        .Select(id => (Id: id, Item: Library.Find(ContentTypeRegistry.DefenseRule, id)))
        .OrderBy(x => (x.Item?.Data as DefenseRuleData)?.Priority ?? int.MaxValue).ThenBy(x => x.Id, StringComparer.Ordinal)
        .Select(x => x.Item is { Data: DefenseRuleData data } item
            ? (x.Id, item.Name, (item.HasErrors ? "✗ правило с ошибками — не действует. " : string.Empty) + Defense.DefenseRuleSummary.Describe(data, Library))
            : (x.Id, x.Id, $"✗ правило «{x.Id}» не найдено"))
        .ToList();

    public string? AddShield()
    {
        if (Unit.Shields.Count >= UnitData.MaxShields)
        {
            return $"у юнита не больше {UnitData.MaxShields} щитов";
        }

        var n = 1;
        while (Unit.Shields.Any(s => s.Name == (n == 1 ? "Щит" : $"Щит {n}")))
        {
            n++;
        }

        var name = n == 1 ? "Щит" : $"Щит {n}";
        Apply($"Добавить щит «{name}»", (d, _) => ((UnitData)d).Shields.Add(new ShieldData { Name = name, Order = ((UnitData)d).Shields.Count }));
        return null;
    }

    public void RemoveShield(int index) => Apply("Убрать щит", (d, _) =>
    {
        var shields = ((UnitData)d).Shields;
        if (index >= 0 && index < shields.Count)
        {
            shields.RemoveAt(index);
        }
    });

    /// <summary>Sets a shield field: <c>name</c>, <c>capacity</c> (HP), <c>damageTypeId</c> (type id or «any»), <c>order</c>.</summary>
    public ContentIssue? SetShield(int index, string key, object? input)
    {
        if (index < 0 || index >= Unit.Shields.Count)
        {
            return ContentIssue.Error($"shields[{index}]", "нет такого щита");
        }

        var probe = Unit.Shields[index].Clone();
        string? Number(out double value)
        {
            value = 0;
            if (input is double d)
            {
                value = d;
                return null;
            }

            return input is string t && double.TryParse(t.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? null : $"«{input}» — не число";
        }

        switch (key)
        {
            case "name":
                probe.Name = ((input as string) ?? string.Empty).Trim();
                if (Unit.Shields.Where((_, i) => i != index).Any(s => s.Name == probe.Name))
                {
                    return ContentIssue.Error($"shields[{index}].name", $"щит «{probe.Name}» уже есть — значение не изменено");
                }

                break;
            case "capacity":
                if (Number(out var capacity) is { } bad)
                {
                    return ContentIssue.Error($"shields[{index}].capacity", $"прочность: {bad}, значение не изменено");
                }

                probe.Capacity = capacity;
                break;
            case "order":
                if (Number(out var order) is { } badOrder || Math.Abs(order - Math.Round(order)) > 1e-9)
                {
                    return ContentIssue.Error($"shields[{index}].order", "порядок — целое число, значение не изменено");
                }

                probe.Order = (int)Math.Round(order);
                break;
            case "damageTypeId":
                probe.DamageType = input as string ?? ShieldData.AnyType;
                if (probe.DamageType != ShieldData.AnyType && Library.Find(ContentTypeRegistry.DamageType, probe.DamageType) is null)
                {
                    return ContentIssue.Error($"shields[{index}].damageTypeId", $"нет типа урона «{probe.DamageType}» — значение не изменено");
                }

                break;
            default:
                return ContentIssue.Error(key, "нет такого поля щита");
        }

        var issues = new IssueList();
        probe.Validate(issues, $"shields[{index}]");
        if (issues.Items.FirstOrDefault(i => i.IsError) is { } issue)
        {
            return issue with { Message = issue.Message + " — значение не изменено" };
        }

        Apply($"Щит «{probe.Name}»", (d, _) => ((UnitData)d).Shields[index] = probe.Clone());
        return null;
    }

    // ------------------------------------------------------------------ progressive disclosure (spec part 5 §7.1–§7.5)

    /// <summary>Prefix of template choices in <see cref="AddRuleChoices"/>.</summary>
    public const string TemplatePrefix = "template:";

    /// <summary>The summaries of the four inspector sections; only one section is open at a time.</summary>
    public UnitSections Sections()
    {
        var u = Unit;
        var type = Library.Find(ContentTypeRegistry.DamageType, u.AttackDamageType)?.Name.ToLowerInvariant() ?? u.AttackDamageType;
        string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        var reactions = u.DefenseRules.Count + u.Shields.Count;
        var res = ResistanceStrengths();
        return new UnitSections(
            $"{N(u.MaxHealth)} HP · {N(u.MoveSpeed)} гекс/с · {N(u.AttackRate)} удар/с",
            $"атака {N(u.AttackPower)}, {type}{(u.AttackAreaRadius > 0 ? $", область {u.AttackAreaRadius}" : string.Empty)} · навыков: {u.Skills.Count}",
            (res.Length == 0 ? "всё 0 %" : res) + (reactions > 0 ? $" · реакций: {reactions}" : string.Empty),
            $"ред. {Item.Revision} · схема {Item.Kind.SchemaVersion}" + (u.ResistanceProfiles.Count > 0 ? $" · наборов: {u.ResistanceProfiles.Count}" : string.Empty));
    }

    /// <summary>«физический +70 %, дробящий −120 %» — the values the unit has (sets and own), in table order (section header).</summary>
    private string ResistanceStrengths()
    {
        var parts = ResistanceRows().Where(r => r.Value != 0)
            .Select(r => $"{(r.Key == UnitData.GlobalResistance ? "все типы" : r.Label.ToLowerInvariant())} {r.Value.ToString("+0.##;−0.##", CultureInfo.InvariantCulture)} %")
            .ToList();

        return parts.Count <= 3 ? string.Join(", ", parts) : string.Join(", ", parts.Take(3)) + $" и ещё {parts.Count - 3}";
    }

    /// <summary>
    /// The compact table (spec part 5 §7.2, §7.5): «все типы», the built-in families and every non-zero row are always shown;
    /// subtypes appear under an open family; other (custom) types only through the search (name, id, tag, family) or «Показать
    /// все». Nothing is lost: hidden rows are counted.
    /// </summary>
    public ResistanceView ResistanceView(string search, bool showAll, IReadOnlySet<string> expanded)
    {
        var all = ResistanceRows();
        var familyOf = new Dictionary<string, string>(StringComparer.Ordinal);
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var t in Library.Items(ContentTypeRegistry.DamageType))
        {
            if (t.Data is DamageTypeData d)
            {
                familyOf[t.Id] = d.Family.Length == 0 ? t.Id : d.Family;
                tags[t.Id] = string.Join(" ", d.Tags);
            }
        }

        bool Matches(ResistanceRow r) => search.Length > 0 &&
            (r.Label.Contains(search, StringComparison.CurrentCultureIgnoreCase) || r.Key.Contains(search, StringComparison.OrdinalIgnoreCase) ||
             (tags.TryGetValue(r.Key, out var tg) && tg.Contains(search, StringComparison.OrdinalIgnoreCase)) ||
             (familyOf.TryGetValue(r.Key, out var f) && f != r.Key && all.FirstOrDefault(x => x.Key == f)?.Label.Contains(search, StringComparison.CurrentCultureIgnoreCase) == true));

        var rows = new List<ResistanceViewRow>();
        var hidden = 0;
        foreach (var r in all)
        {
            var family = r.Depth == 0 ? r.Key : familyOf.GetValueOrDefault(r.Key, r.Key);
            var hasChildren = r.Depth == 0 && all.Any(x => x.Depth == 1 && familyOf.GetValueOrDefault(x.Key) == r.Key);
            var builtInGroup = r.Depth == 0 && (r.Key == UnitData.GlobalResistance || r.Key.StartsWith("core.", StringComparison.Ordinal));
            var subtreeSearch = r.Depth == 0 && all.Any(x => x.Depth == 1 && familyOf.GetValueOrDefault(x.Key) == r.Key && Matches(x));
            var subtreeValue = r.Depth == 0 && all.Any(x => x.Depth == 1 && familyOf.GetValueOrDefault(x.Key) == r.Key && x.Value != 0);
            bool visible;
            if (showAll)
            {
                visible = true;
            }
            else if (search.Length > 0)
            {
                // A search narrows the table to what matches (and the families around matching subtypes).
                visible = r.Key == UnitData.GlobalResistance || Matches(r) || subtreeSearch;
            }
            else
            {
                visible = r.Value != 0 || r.Own || (r.Depth == 0 ? builtInGroup || subtreeValue : expanded.Contains(family));
            }

            if (visible)
            {
                rows.Add(new ResistanceViewRow(r, hasChildren, expanded.Contains(r.Key) || showAll || (search.Length > 0 && subtreeSearch)));
            }
            else
            {
                hidden++;
            }
        }

        return new ResistanceView(rows, hidden);
    }

    /// <summary>
    /// Takes over the resistance sets and own values of another unit (spec part 5 §7.5 «копирование профиля между юнитами»);
    /// rules and shields stay. One undo step.
    /// </summary>
    public string? CopyResistancesFrom(string unitId)
    {
        if (Library.Find(ContentTypeRegistry.Unit, unitId) is not { Data: UnitData other } source)
        {
            return $"юнит «{unitId}» не найден";
        }

        Apply($"Сопротивления как у «{source.Name}»", (d, _) =>
        {
            var u = (UnitData)d;
            u.ResistanceProfiles.Clear();
            u.ResistanceProfiles.AddRange(other.ResistanceProfiles);
            u.Resistances.Clear();
            foreach (var (key, value) in other.Resistances)
            {
                u.Resistances[key] = value;
            }
        });
        return null;
    }

    /// <summary>The attached rules and the shields as short cards, in the order the rules act.</summary>
    public IReadOnlyList<DefenseCard> DefenseCards()
    {
        var cards = new List<DefenseCard>();
        foreach (var (id, name, summary) in RuleSummaries())
        {
            var rule = Library.Find(ContentTypeRegistry.DefenseRule, id)?.Data as DefenseRuleData;
            cards.Add(new DefenseCard(id, name, rule is null ? "не найдено" : Defense.DefenseRuleSummary.Short(rule, Library), summary, summary.StartsWith('✗')));
        }

        foreach (var s in Unit.Shields.OrderBy(s => s.Order).ThenBy(s => s.Name, StringComparer.Ordinal))
        {
            var accepts = s.DamageType == ShieldData.AnyType ? "любой урон" : Library.Find(ContentTypeRegistry.DamageType, s.DamageType)?.Name.ToLowerInvariant() ?? s.DamageType;
            cards.Add(new DefenseCard("shield:" + s.Name, $"Щит «{s.Name}»", $"{s.Capacity.ToString("0.##", CultureInfo.InvariantCulture)} HP · {accepts}",
                "Принимает уже рассчитанный урон до HP; в бою начинается полным.", false));
        }

        return cards;
    }

    /// <summary>«Добавить правило»: templates first (a new rule for this unit), then saved rules (attached by reference).</summary>
    public IReadOnlyList<(string Value, string Label, string Hint)> AddRuleChoices(string filter = "")
    {
        var list = new List<(string, string, string)>();
        foreach (var t in Defense.DefenseRuleTemplates.All)
        {
            if (filter.Length == 0 || t.Label.Contains(filter, StringComparison.CurrentCultureIgnoreCase) || t.Hint.Contains(filter, StringComparison.CurrentCultureIgnoreCase))
            {
                list.Add((TemplatePrefix + t.Id, $"шаблон: {t.Label}", t.Hint));
            }
        }

        foreach (var c in AvailableRules(filter))
        {
            list.Add((c.Id, c.Name + (c.IsBuiltIn ? string.Empty : " (своё)"), c.Summary));
        }

        return list;
    }

    /// <summary>A new, unsaved rule from a template, named after the unit (opened in the rule editor, attached when saved).</summary>
    public ContentItem? NewRuleFromTemplate(string templateId)
    {
        if (Defense.DefenseRuleTemplates.Find(templateId) is not { } template)
        {
            return null;
        }

        var item = Library.Create(ContentTypeRegistry.DefenseRule, $"{Item.Name}: {template.Label}");
        item.Data = template.Build(Library.Registry.DefenseActions);
        return item;
    }

    /// <summary>An unsaved copy of a saved rule with a new id (spec part 5 §6.4 «скопировать правило только для этого юнита»).</summary>
    public ContentItem? CopyOfRule(string ruleId)
    {
        if (Library.Find(ContentTypeRegistry.DefenseRule, ruleId) is not { } rule)
        {
            return null;
        }

        var copy = rule.CopyAs(Library.NewId(ContentTypeRegistry.DefenseRule), ContentOrigin.User, $"{rule.Name} ({Item.Name})");
        copy.Revision = 0;
        return copy;
    }

    /// <summary>The unit refers to <paramref name="newId"/> instead of <paramref name="oldId"/>, in the same place (one undo step).</summary>
    public void ReplaceRule(string oldId, string newId) => Apply("Правило заменено копией", (d, _) =>
    {
        var rules = ((UnitData)d).DefenseRules;
        var index = rules.IndexOf(oldId);
        if (index >= 0 && !rules.Contains(newId))
        {
            rules[index] = newId;
        }
    });

    // ------------------------------------------------------------------ resistances (spec part 5 §3, §7)

    /// <summary>The unit's base resistances: its sets in order, then its own values (spec part 5 §6.4).</summary>
    public IReadOnlyDictionary<string, ResistanceValue> Layered => ResistanceLayers.Effective(Unit, Library);

    /// <summary>
    /// The rows of the resistance table: «все сопротивляемые типы», then every family with its subtypes (physical, magical,
    /// custom families; legacy types only if this unit has a value for them), true-like types last as rows without a number.
    /// Every row says whether its value is the unit's own or comes from a set.
    /// </summary>
    public IReadOnlyList<ResistanceRow> ResistanceRows() => Mechanics.ResistanceTable.Rows(Library, Layered);

    /// <summary>
    /// Sets the unit's own value from input (−500…+500 %, decimal comma or dot). 0 removes the own value unless a set gives
    /// this key a value — then 0 is kept as an explicit override.
    /// </summary>
    public ContentIssue? SetResistance(string key, object? input)
    {
        if (Mechanics.ResistanceTable.Parse(Library, key, input, $"resistances.{key}", out var rounded) is { } issue)
        {
            return issue;
        }

        var inherited = Unit.ResistanceProfiles.Any(p => ResistanceLayers.Of(Library, p).ContainsKey(key));
        Apply($"Сопротивление «{Mechanics.ResistanceTable.KeyName(Library, key)}»: {Explain(rounded)}", (d, _) =>
        {
            var u = (UnitData)d;
            if (rounded == 0 && !inherited)
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

    /// <summary>Drops the unit's own value: the set's value (or 0) applies again.</summary>
    public void ResetResistance(string key) =>
        Apply($"Сбросить своё сопротивление «{Mechanics.ResistanceTable.KeyName(Library, key)}»", (d, _) => ((UnitData)d).Resistances.Remove(key));

    /// <summary>Saved sets without errors not applied yet, filtered by name or id.</summary>
    public IReadOnlyList<ProfileChoice> AvailableProfiles(string filter = "") => Library.Items(ContentTypeRegistry.ResistanceProfile)
        .Where(p => !p.HasErrors && !Unit.ResistanceProfiles.Contains(p.Id))
        .Where(p => filter.Length == 0 || p.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase) || p.Id.Contains(filter, StringComparison.OrdinalIgnoreCase))
        .Select(p => new ProfileChoice(p.Id, p.Name, ProfileSummary(Library, p.Id), p.IsBuiltIn))
        .ToList();

    /// <summary>«физический +50 %, колющий −50 %» — the effective values of a set.</summary>
    public static string ProfileSummary(ContentLibrary library, string profileId)
    {
        var values = ResistanceLayers.Of(library, profileId);
        return values.Count == 0 ? "пустой набор" : string.Join(", ", values.OrderBy(v => v.Key, StringComparer.Ordinal)
            .Select(v => $"{Mechanics.ResistanceTable.KeyName(library, v.Key).ToLowerInvariant()} {v.Value.ToString("+0.##;−0.##;0", CultureInfo.InvariantCulture)} %"));
    }

    /// <summary>Applies a set by reference (after the others: it replaces their values per key).</summary>
    public string? AddProfile(string profileId)
    {
        if (Library.Find(ContentTypeRegistry.ResistanceProfile, profileId) is not { } profile)
        {
            return $"набор «{profileId}» не найден";
        }

        if (Unit.ResistanceProfiles.Contains(profileId))
        {
            return $"набор «{profile.Name}» уже применён";
        }

        if (Unit.ResistanceProfiles.Count >= UnitData.MaxResistanceProfiles)
        {
            return $"не больше {UnitData.MaxResistanceProfiles} наборов";
        }

        Apply($"Применить набор «{profile.Name}»", (d, _) => ((UnitData)d).ResistanceProfiles.Add(profileId));
        return null;
    }

    public void RemoveProfile(string profileId)
    {
        var name = Library.Find(ContentTypeRegistry.ResistanceProfile, profileId)?.Name ?? profileId;
        Apply($"Убрать набор «{name}»", (d, _) => ((UnitData)d).ResistanceProfiles.Remove(profileId));
    }

    /// <summary>
    /// Saves the unit's current resistances (sets + own values) as a new set for other units (spec part 5 §6.4); the unit
    /// itself does not change. The set is written to the library at once.
    /// </summary>
    public ContentOperationResult SaveAsProfile(string name)
    {
        var item = Library.Create(ContentTypeRegistry.ResistanceProfile, name.Trim().Length == 0 ? $"{Item.Name}: защита" : name.Trim());
        var data = (ResistanceProfileData)item.Data;
        foreach (var (key, value) in Layered)
        {
            if (value.Value != 0)
            {
                data.Values[key] = value.Value;
            }
        }

        item.Description = $"Сохранено из юнита «{Item.Name}».";
        return Library.Save(item);
    }

    private double Value(string key) => Layered.TryGetValue(key, out var v) ? v.Value : 0;

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
            if (t.Data is not DamageTypeData { Legacy: false, Strategy: DamageTypeData.ResistanceAffected } d)
            {
                continue;
            }

            var r = EffectiveResistance(t.Id);
            if (d.Family.Length > 0 && Math.Abs(r - EffectiveResistance(d.Family)) < 1e-9)
            {
                continue; // a subtype like its family is already said by the family («физический +50 %»)
            }

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

        var reactions = Unit.DefenseRules.Count + Unit.Shields.Count;
        var tail = reactions > 0 ? $"; реакции: {Unit.DefenseRules.Count}" + (Unit.Shields.Count > 0 ? $", щиты: {Unit.Shields.Count}" : string.Empty) : string.Empty;
        if (strong.Count == 0 && weak.Count == 0)
        {
            return reactions > 0 ? $"Сопротивлений нет{tail}." : "Сопротивлений нет: весь урон проходит полностью.";
        }

        return $"Сильные стороны: {(strong.Count == 0 ? "нет" : string.Join(", ", strong))}; слабости: {(weak.Count == 0 ? "нет" : string.Join(", ", weak))}{tail}.";
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
