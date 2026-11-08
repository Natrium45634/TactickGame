using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HexTactics.Content.Kinds;
using HexTactics.Content.Library;
using HexTactics.Content.Model;
using HexTactics.Content.Registry;
using HexTactics.Content.Schema;
using HexTactics.Editor.Definitions;

namespace HexTactics.Editor.Defense;

/// <summary>A starting point of the rule constructor (spec part 5 §6.3 «Добавить правило» → шаблоны).</summary>
public sealed record DefenseRuleTemplate(string Id, string Label, string Hint, Func<DefenseActionRegistry, DefenseRuleData> Build);

/// <summary>The registered templates; a new template is one entry (old ids and serialization stay).</summary>
public static class DefenseRuleTemplates
{
    private static DefenseRuleData Rule(DefenseActionRegistry r, string trigger, params (string Effect, (string Key, object Value)[] Values)[] actions)
    {
        var rule = new DefenseRuleData { Trigger = trigger };
        foreach (var (effect, values) in actions)
        {
            var action = r.Find(effect)!.CreateDefault();
            foreach (var (key, value) in values)
            {
                action.Set(key, value);
            }

            rule.Actions.Add(action);
        }

        return rule;
    }

    public static IReadOnlyList<DefenseRuleTemplate> All { get; } = new DefenseRuleTemplate[]
    {
        new("immunity", "Иммунитет", "Выбранный тип урона не отнимает HP.",
            r => Rule(r, DefenseActionRegistry.OnHitContact, ("immunity", Array.Empty<(string, object)>()))),
        new("absorb", "Поглощать урон", "Часть силы удара исчезает до сопротивления.",
            r => Rule(r, DefenseActionRegistry.OnHitContact, ("negate", new (string, object)[] { ("fraction", 0.25) }))),
        new("absorb_heal", "Поглощать и лечиться", "Часть силы удара лечит юнита.",
            r => Rule(r, DefenseActionRegistry.OnHitContact, ("convert_to_healing", new (string, object)[] { ("fraction", 0.25) }))),
        new("reflect", "Отражать часть урона", "Часть силы удара уходит атакующему; юнит её не получает.",
            r => Rule(r, DefenseActionRegistry.OnHitContact, ("redirect_as_damage", new (string, object)[] { ("fraction", 0.3), ("targeting", DefenseActionRegistry.Attacker) }))),
        new("retaliate", "Ответный удар", "После потери HP — ответ атакующему; входящий урон не меньше.",
            r => Rule(r, DefenseActionRegistry.OnHealthLoss, ("retaliate", new (string, object)[] { ("fraction", 0.5) }))),
        new("shield", "Щит", "Часть силы удара пополняет щит юнита (щит задаётся в редакторе юнита).",
            r => Rule(r, DefenseActionRegistry.OnHitContact, ("convert_to_shield", new (string, object)[] { ("fraction", 0.25) }))),
        new("flat", "Плоское снижение", "Каждый удар слабее на несколько HP (не чистый урон).",
            r => Rule(r, DefenseActionRegistry.OnHitContact, ("flat_reduction", new (string, object)[] { ("amount", 5.0) }))),
        new("cap", "Предел урона", "Один удар не отнимает больше заданного.",
            r => Rule(r, DefenseActionRegistry.OnHitContact, ("damage_cap", new (string, object)[] { ("amount", 30.0) }))),
        new("vampirism", "Вампиризм", "Свои удары лечат на долю действительно снятого HP.",
            r => Rule(r, DefenseActionRegistry.OnDamageDealt, ("vampirism", new (string, object)[] { ("fraction", 0.2) }))),
        new("status_immunity", "Иммунитет к дебаффам", "Дебаффы не накладываются.",
            r => Rule(r, DefenseActionRegistry.OnStatusApplied, ("status_immunity", new (string, object)[] { ("statusCategory", "debuff") }))),
        new("complex", "Сложная реакция", "Пример ТЗ: при R ≥ 300 % — 25 % лечат, 75 % уходят каждому врагу вокруг.",
            r =>
            {
                var rule = Rule(r, DefenseActionRegistry.OnHitContact,
                    ("convert_to_healing", new (string, object)[] { ("fraction", 0.25) }),
                    ("redirect_as_damage", new (string, object)[] { ("fraction", 0.75), ("targeting", DefenseActionRegistry.AreaAround), ("radiusHexes", 1.0) }));
                rule.MinEffectiveResistance = 300;
                return rule;
            }),
    };

    public static DefenseRuleTemplate? Find(string id) => All.FirstOrDefault(t => t.Id == id);
}

/// <summary>
/// Model of the defense rule editor (spec part 5 §6.3): the conditions come from <see cref="DefenseRuleData.Fields"/>, the
/// actions from the <see cref="DefenseActionRegistry"/> filtered by the trigger; templates fill the rule in one undoable step;
/// the summary explains the rule in words.
/// </summary>
public sealed class DefenseRuleEditorModel : DefinitionEditorModel
{
    public DefenseRuleEditorModel(ContentLibrary library, ContentItem item)
        : base(library, item)
    {
        if (item.Data is not DefenseRuleData)
        {
            throw new ArgumentException("Not a defense rule.", nameof(item));
        }
    }

    public DefenseRuleData Rule => (DefenseRuleData)Item.Data;

    public DefenseActionRegistry Actions => Library.Registry.DefenseActions;

    /// <summary>Actions that fit the current trigger («ЧТО СДЕЛАТЬ» depends on «КОГДА»).</summary>
    public IEnumerable<DefenseActionDescriptor> AvailableActions => Actions.For(Rule.Trigger);

    /// <summary>Replaces the conditions and actions by a template (name and description stay); one undo step.</summary>
    public string? ApplyTemplate(string templateId)
    {
        if (DefenseRuleTemplates.Find(templateId) is not { } template)
        {
            return $"нет шаблона «{templateId}»";
        }

        var built = template.Build(Actions);
        built.DamageType = Rule.DamageType;
        Apply($"Шаблон «{template.Label}»", (_, i) => i.Data = built.Clone());
        return null;
    }

    public string? AddAction(string effect)
    {
        if (Actions.Find(effect) is not { } descriptor || descriptor.Trigger != Rule.Trigger)
        {
            return $"реакция «{effect}» не подходит к условию «когда»";
        }

        if (Rule.Actions.Count >= DefenseRuleData.MaxActions)
        {
            return $"в правиле не больше {DefenseRuleData.MaxActions} реакций";
        }

        Apply($"Добавить реакцию «{descriptor.Label}»", (d, _) => ((DefenseRuleData)d).Actions.Add(descriptor.CreateDefault()));
        return null;
    }

    public void RemoveAction(int index) => Apply("Убрать реакцию", (d, _) =>
    {
        var actions = ((DefenseRuleData)d).Actions;
        if (index >= 0 && index < actions.Count)
        {
            actions.RemoveAt(index);
        }
    });

    /// <summary>Sets a field of an action from input; invalid input is refused with an explanation.</summary>
    public ContentIssue? SetActionField(int index, string key, object? input)
    {
        var action = index >= 0 && index < Rule.Actions.Count ? Rule.Actions[index] : null;
        if (action is null || Actions.Find(action.Type)?.FindField(key) is not { } field)
        {
            return ContentIssue.Error(key, "нет такого поля");
        }

        var value = input;
        if (field.Kind is FieldKind.Number or FieldKind.Integer && input is string text)
        {
            var clean = text.Trim().Replace(',', '.');
            var percent = clean.EndsWith('%');
            if (!double.TryParse(clean.TrimEnd('%').Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                return ContentIssue.Error(key, $"«{field.Label}»: «{text}» — не число, значение не изменено");
            }

            value = field.Percent && percent ? parsed / 100 : parsed;
        }

        if (value is int i)
        {
            value = (double)i;
        }

        if (field.Check(value, key) is { } issue)
        {
            return issue with { Message = issue.Message + " — значение не изменено" };
        }

        Apply($"«{field.Label}»: {value}", (d, _) => ((DefenseRuleData)d).Actions[index].Set(key, value));
        return null;
    }

    /// <summary>Share of the hit spent by the conversions of this rule (shown with the 100 % limit).</summary>
    public double ConversionTotal() => Rule.Actions
        .Where(a => Actions.Find(a.Type)?.IsConversion == true)
        .Sum(a => FieldSchema.TryNumber(a.Get("fraction") ?? 0.0, out var f) ? f : 0);

    public string Summary() => DefenseRuleSummary.Describe(Rule, Library);
}

/// <summary>The rule in words (spec part 5 §6.3 «человеческое резюме»); shared by the rule and unit editors and tests.</summary>
public static class DefenseRuleSummary
{
    public static string Describe(DefenseRuleData rule, ContentLibrary library)
    {
        var registry = library.Registry.DefenseActions;
        var type = TypePart(rule, library);
        var when = rule.Trigger switch
        {
            DefenseActionRegistry.OnHealthLoss => "При потере HP" + type,
            DefenseActionRegistry.OnDamageDealt => "Когда свой удар снимает HP" + type,
            DefenseActionRegistry.OnStatusApplied => "При наложении статуса",
            _ => "При попадании" + type,
        };
        var conditions = new List<string>();
        if (rule.MinEffectiveResistance is { } r)
        {
            conditions.Add($"сопротивлении ≥ {N(r)} %");
        }

        if (rule.HealthBelowPercent is { } h)
        {
            conditions.Add($"здоровье ниже {N(h)} %");
        }

        var head = when + (conditions.Count > 0 ? " и " + string.Join(" и ", conditions) : string.Empty);
        var parts = rule.Actions.Select(a => Action(a, registry, library)).ToList();
        var text = head + ": " + (parts.Count == 0 ? "ничего не делать" : string.Join("; ", parts)) + ".";
        var notes = new List<string>();
        if (rule.Actions.Any(a => a.Type is "redirect_as_damage" or "retaliate"))
        {
            notes.Add("ответные удары не отражаются");
        }

        if (rule.TriggerOnImmuneContact)
        {
            notes.Add("срабатывает и при иммунитете");
        }

        if (rule.Cooldown > 0)
        {
            notes.Add($"не чаще раза в {N(rule.Cooldown)} с");
        }

        if (rule.MaxTriggers > 0)
        {
            notes.Add($"не больше {rule.MaxTriggers} раз за бой");
        }

        if (notes.Count > 0)
        {
            text += " " + char.ToUpperInvariant(notes[0][0]) + string.Join("; ", notes)[1..] + ".";
        }

        return text;
    }

    /// <summary>A card line: «огонь (пример) · лечение 25 % + отражение 75 % · R ≥ 300 %».</summary>
    public static string Short(DefenseRuleData rule, ContentLibrary library)
    {
        var registry = library.Registry.DefenseActions;
        string P(EffectData a) => N((FieldSchema.TryNumber(a.Get("fraction") ?? registry.Find(a.Type)?.FindField("fraction")?.Default, out var f) ? f : 0) * 100) + " %";
        string H(EffectData a) => N(FieldSchema.TryNumber(a.Get("amount") ?? registry.Find(a.Type)?.FindField("amount")?.Default, out var v) ? v : 0) + " HP";
        var actions = rule.Actions.Select(a => a.Type switch
        {
            "immunity" => "иммунитет",
            "negate" => $"поглощение {P(a)}",
            "convert_to_healing" => $"лечение {P(a)}",
            "convert_to_shield" => $"в щит {P(a)}",
            "redirect_as_damage" => $"отражение {P(a)}",
            "flat_reduction" => $"−{H(a)}",
            "damage_cap" => $"не больше {H(a)}",
            "retaliate" => $"ответ {P(a)}",
            "vampirism" => $"вампиризм {P(a)}",
            "status_immunity" => "без статусов",
            _ => a.Type,
        });
        var type = rule.Trigger == DefenseActionRegistry.OnStatusApplied ? "статусы"
            : rule.DamageType == DefenseRuleData.AnyType ? "любой урон"
            : library.Find(ContentTypeRegistry.DamageType, rule.DamageType)?.Name.ToLowerInvariant() ?? rule.DamageType;
        var text = $"{type} · {string.Join(" + ", actions)}";
        if (rule.MinEffectiveResistance is { } r)
        {
            text += $" · R ≥ {N(r)} %";
        }

        if (rule.HealthBelowPercent is { } h)
        {
            text += $" · HP < {N(h)} %";
        }

        return text;
    }

    private static string TypePart(DefenseRuleData rule, ContentLibrary library)
    {
        var text = rule.DamageType == DefenseRuleData.AnyType
            ? " любым уроном"
            : library.Find(ContentTypeRegistry.DamageType, rule.DamageType) is { Data: DamageTypeData d } t
                ? $" урона «{t.Name}»" + (d.Family.Length == 0 && library.Items(ContentTypeRegistry.DamageType).Any(x => x.Data is DamageTypeData s && s.Family == t.Id) ? " (и его подтипов)" : string.Empty)
                : $" урона «{rule.DamageType}» (тип не найден)";
        return rule.DamageTag.Length > 0 ? text + $" с тегом «{rule.DamageTag}»" : text;
    }

    private static string Action(EffectData a, DefenseActionRegistry registry, ContentLibrary library)
    {
        if (registry.Find(a.Type) is not { } d)
        {
            return $"✗ неизвестная реакция «{a.Type}»";
        }

        double Num(string key) => FieldSchema.TryNumber(a.Get(key) ?? d.FindField(key)?.Default, out var v) ? v : 0;
        string Text(string key) => a.Get(key) as string ?? d.FindField(key)?.Default as string ?? string.Empty;
        var share = N(Num("fraction") * 100) + " %";
        return a.Type switch
        {
            "immunity" => "не терять HP (иммунитет)",
            "negate" => $"поглотить {share} силы удара",
            "convert_to_healing" => $"вылечить себя на {share} силы удара",
            "convert_to_shield" => $"{share} силы удара — в щит «{Text("shield")}»",
            "redirect_as_damage" => $"{share} силы удара отразить {Targets(a, Text, Num)}{Response(Text("responseDamageType"), library)}",
            "flat_reduction" => $"снизить каждый удар на {N(Num("amount"))} HP после сопротивления (кроме чистого урона)",
            "damage_cap" => $"не терять больше {N(Num("amount"))} HP за удар",
            "retaliate" => $"ответить на {share} {(Text("base") == "shield_loss" ? "потери прочности щита" : "потерянного HP")} {Targets(a, Text, Num)}{Response(Text("responseDamageType"), library)}",
            "vampirism" => $"вылечить себя на {share} снятого HP",
            "status_immunity" => Text("statusCategory") switch
            {
                "buff" => "не получать баффы",
                "periodic" => "не получать периодические эффекты",
                "all" => "не получать никакие статусы",
                _ => "не получать дебаффы",
            },
            _ => d.Label.ToLowerInvariant(),
        };
    }

    private static string Targets(EffectData a, Func<string, string> text, Func<string, double> num)
    {
        var filter = text("targetFilter");
        if (text("targeting") != DefenseActionRegistry.AreaAround)
        {
            return filter switch
            {
                "allies_of_defender" => "атакующему, если он союзник",
                "all_units" => "атакующему",
                _ => "атакующему",
            };
        }

        var radius = (int)Math.Round(num("radiusHexes"));
        var where = radius == 1 ? "на шести соседних гексах" : $"в радиусе {radius} гекс.";
        if (a.Get("includeCenter") is true)
        {
            where += " (включая свой гекс)";
        }

        var who = filter switch
        {
            "allies_of_defender" => ("каждому союзнику", "союзниками"),
            "all_units" => ("каждому юниту", "юнитами"),
            _ => ("каждому врагу", "врагами"),
        };
        return text("targetDamageMode") == "split_between_targets" ? $"поровну между {who.Item2} {where}" : $"{who.Item1} {where}";
    }

    private static string Response(string type, ContentLibrary library) =>
        type.Length == 0 || type == DefenseActionRegistry.SameAsIncoming
            ? string.Empty
            : $" уроном «{library.Find(ContentTypeRegistry.DamageType, type)?.Name ?? type}»";

    private static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
