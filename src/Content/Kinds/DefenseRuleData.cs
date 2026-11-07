using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using HexTactics.Content.Model;
using HexTactics.Content.Registry;
using HexTactics.Content.Schema;
using HexTactics.Core.Combat;

namespace HexTactics.Content.Kinds;

/// <summary>
/// A defense rule of the library (spec part 5 §6.3, §9.1, §9.3 <c>defense_rule</c>): «КОГДА → ОТ КАКОГО → ПРИ УСЛОВИИ →
/// ЧТО СДЕЛАТЬ». Conditions are AND-filters; the actions come from the <see cref="DefenseActionRegistry"/> and are executed
/// by the damage resolver in its fixed stages. Units refer to rules by id (no copies).
/// </summary>
public sealed class DefenseRuleData : IContentData
{
    /// <summary>Value of <see cref="DamageType"/> meaning «любой тип урона» (true damage included).</summary>
    public const string AnyType = "any";

    public const int MaxActions = 8;

    public string Trigger { get; set; } = DefenseActionRegistry.OnHitContact;

    /// <summary>Damage type or family id, or <see cref="AnyType"/>.</summary>
    public string DamageType { get; set; } = AnyType;

    /// <summary>Additional filter: the type carries this tag; empty = no filter.</summary>
    public string DamageTag { get; set; } = string.Empty;

    /// <summary>Condition: effective R (after penetration) against the hit is at least this value; null = always.</summary>
    public double? MinEffectiveResistance { get; set; }

    /// <summary>Condition: health of the rule owner below this percentage before the hit; null = always.</summary>
    public double? HealthBelowPercent { get; set; }

    public bool TriggerOnImmuneContact { get; set; }

    public int Priority { get; set; } = 100;

    public double Cooldown { get; set; }

    public int MaxTriggers { get; set; }

    /// <summary>Actions: <see cref="EffectData.Type"/> is the action id of the registry, the parameters are its fields.</summary>
    public List<EffectData> Actions { get; } = new();

    /// <summary>
    /// Reactions of reactions (spec part 5 §5.5) are forbidden in this edition: the stored flag is accepted only as false so
    /// files written by the contract of §9.3 load unchanged.
    /// </summary>
    private bool _reactiveChainRequested;

    private static bool HitTrigger(object o) => ((DefenseRuleData)o).Trigger is DefenseActionRegistry.OnHitContact or DefenseActionRegistry.OnHealthLoss;

    private static bool DamageTrigger(object o) => ((DefenseRuleData)o).Trigger != DefenseActionRegistry.OnStatusApplied;

    /// <summary>A new rule: «получен удар любым типом → поглотить 25 %» (a draft the templates replace).</summary>
    public static DefenseRuleData CreateDefault()
    {
        var rule = new DefenseRuleData();
        rule.Actions.Add(DefenseActionRegistry.Default.Find("negate")!.CreateDefault());
        return rule;
    }

    public static IReadOnlyList<FieldSchema> Fields { get; } = new FieldSchema[]
    {
        new()
        {
            Key = "trigger", Label = "Когда", Kind = FieldKind.Choice, Default = DefenseActionRegistry.OnHitContact, Options = DefenseActionRegistry.Triggers,
            Hint = "От этого зависит, какие реакции доступны.",
            Get = o => ((DefenseRuleData)o).Trigger, Set = (o, v) => ((DefenseRuleData)o).Trigger = (string)v!,
        },
        new()
        {
            Key = "damageTypeId", Label = "От какого урона", Kind = FieldKind.Reference, RefKind = ContentTypeRegistry.DamageType, Default = AnyType,
            SpecialValue = AnyType, SpecialLabel = "любой тип (и чистый)", VisibleWhen = DamageTrigger,
            Hint = "Семейство подходит для всех своих подтипов: «Физический» — и для колющего, режущего, дробящего.",
            Get = o => ((DefenseRuleData)o).DamageType, Set = (o, v) => ((DefenseRuleData)o).DamageType = (string)v!,
        },
        new()
        {
            Key = "useResistanceThreshold", Label = "Только при сопротивлении не ниже порога", Kind = FieldKind.Flag, Default = false, VisibleWhen = HitTrigger,
            Get = o => ((DefenseRuleData)o).MinEffectiveResistance is not null,
            Set = (o, v) => ((DefenseRuleData)o).MinEffectiveResistance = (bool)v! ? ((DefenseRuleData)o).MinEffectiveResistance ?? 100 : null,
        },
        new()
        {
            Key = "minimumEffectiveResistancePercent", Label = "Порог эффективного R", Kind = FieldKind.Number, Default = 100.0, Min = -500, Max = 500, Unit = "%",
            VisibleWhen = o => HitTrigger(o) && ((DefenseRuleData)o).MinEffectiveResistance is not null,
            Hint = "R после пробития против этого удара. У чистого урона R нет — условие не выполняется.",
            Get = o => ((DefenseRuleData)o).MinEffectiveResistance ?? 100.0, Set = (o, v) => ((DefenseRuleData)o).MinEffectiveResistance = (double)v!,
        },
        new()
        {
            Key = "useHealthThreshold", Label = "Только при здоровье ниже порога", Kind = FieldKind.Flag, Default = false, VisibleWhen = DamageTrigger,
            Get = o => ((DefenseRuleData)o).HealthBelowPercent is not null,
            Set = (o, v) => ((DefenseRuleData)o).HealthBelowPercent = (bool)v! ? ((DefenseRuleData)o).HealthBelowPercent ?? 50 : null,
        },
        new()
        {
            Key = "healthBelowPercent", Label = "Здоровье ниже", Kind = FieldKind.Number, Default = 50.0, Min = 1, Max = 100, Unit = "%",
            VisibleWhen = o => DamageTrigger(o) && ((DefenseRuleData)o).HealthBelowPercent is not null,
            Hint = "Здоровье владельца правила до удара, в процентах от максимума.",
            Get = o => ((DefenseRuleData)o).HealthBelowPercent ?? 50.0, Set = (o, v) => ((DefenseRuleData)o).HealthBelowPercent = (double)v!,
        },
        new()
        {
            Key = "damageTag", Label = "Тег типа урона", Kind = FieldKind.Text, Default = string.Empty, MaxLength = 40, Section = FieldSections.Advanced, Advanced = true,
            VisibleWhen = DamageTrigger, Hint = "Дополнительный фильтр: тип урона должен иметь этот тег (пусто — не проверять).",
            Get = o => ((DefenseRuleData)o).DamageTag, Set = (o, v) => ((DefenseRuleData)o).DamageTag = ((string?)v ?? string.Empty).Trim(),
        },
        new()
        {
            Key = "triggerOnImmuneContact", Label = "Срабатывать при иммунитете", Kind = FieldKind.Flag, Default = false, Section = FieldSections.Advanced, Advanced = true,
            VisibleWhen = o => ((DefenseRuleData)o).Trigger == DefenseActionRegistry.OnHitContact,
            Hint = "Обычно правило молчит, если юнит невосприимчив к удару.",
            Get = o => ((DefenseRuleData)o).TriggerOnImmuneContact, Set = (o, v) => ((DefenseRuleData)o).TriggerOnImmuneContact = (bool)v!,
        },
        new()
        {
            Key = "priority", Label = "Приоритет", Kind = FieldKind.Integer, Default = 100.0, Min = 0, Max = 1000, Section = FieldSections.Advanced, Advanced = true,
            Hint = "Меньше — раньше. Следующее правило получает только неизрасходованную силу удара.",
            Get = o => (double)((DefenseRuleData)o).Priority, Set = (o, v) => ((DefenseRuleData)o).Priority = (int)Math.Round((double)v!),
        },
        new()
        {
            Key = "cooldownSeconds", Label = "Перезарядка", Kind = FieldKind.Number, Default = 0.0, Min = 0, Max = 600, Unit = "с", Section = FieldSections.Advanced, Advanced = true,
            Hint = "Игровые секунды между срабатываниями; 0 — без перезарядки.",
            Get = o => ((DefenseRuleData)o).Cooldown, Set = (o, v) => ((DefenseRuleData)o).Cooldown = (double)v!,
        },
        new()
        {
            Key = "maxTriggers", Label = "Срабатываний за бой", Kind = FieldKind.Integer, Default = 0.0, Min = 0, Max = 10000, Section = FieldSections.Advanced, Advanced = true,
            Hint = "0 — без ограничения.",
            Get = o => (double)((DefenseRuleData)o).MaxTriggers, Set = (o, v) => ((DefenseRuleData)o).MaxTriggers = (int)Math.Round((double)v!),
        },
    };

    public JsonObject ToJson()
    {
        var actions = new JsonArray();
        foreach (var a in Actions)
        {
            var obj = new JsonObject { ["effect"] = a.Type };
            foreach (var (key, value) in a.Params)
            {
                obj[key] = JsonFields.ToNode(value);
            }

            actions.Add(obj);
        }

        return new JsonObject
        {
            ["trigger"] = Trigger,
            ["damageTypeId"] = DamageType,
            ["damageTag"] = DamageTag,
            ["minimumEffectiveResistancePercent"] = MinEffectiveResistance is { } r ? r : null,
            ["healthBelowPercent"] = HealthBelowPercent is { } h ? h : null,
            ["triggerOnImmuneContact"] = TriggerOnImmuneContact,
            ["priority"] = Priority,
            ["cooldownSeconds"] = Cooldown,
            ["maxTriggers"] = MaxTriggers,
            ["actions"] = actions,
        };
    }

    public static DefenseRuleData Read(JsonObject obj, IssueList issues)
    {
        var data = new DefenseRuleData
        {
            Trigger = JsonFields.Text(obj, "trigger", DefenseActionRegistry.OnHitContact, issues, string.Empty),
            DamageType = JsonFields.Text(obj, "damageTypeId", AnyType, issues, string.Empty),
            DamageTag = JsonFields.Text(obj, "damageTag", string.Empty, issues, string.Empty),
            TriggerOnImmuneContact = JsonFields.Flag(obj, "triggerOnImmuneContact", false, issues, string.Empty),
            Priority = JsonFields.Integer(obj, "priority", 100, issues, string.Empty),
            Cooldown = JsonFields.Number(obj, "cooldownSeconds", 0, issues, string.Empty),
            MaxTriggers = JsonFields.Integer(obj, "maxTriggers", 0, issues, string.Empty),
        };
        if (data.DamageType.Length == 0)
        {
            data.DamageType = AnyType;
        }

        if (obj["minimumEffectiveResistancePercent"] is not null)
        {
            data.MinEffectiveResistance = JsonFields.Number(obj, "minimumEffectiveResistancePercent", 0, issues, string.Empty);
        }

        if (obj["healthBelowPercent"] is not null)
        {
            data.HealthBelowPercent = JsonFields.Number(obj, "healthBelowPercent", 50, issues, string.Empty);
        }

        if (JsonFields.Array(obj, "actions", issues, string.Empty) is { } list)
        {
            for (var i = 0; i < list.Count; i++)
            {
                var path = $"actions[{i}]";
                if (list[i] is not JsonObject a)
                {
                    issues.Error(path, "реакция должна быть объектом");
                    continue;
                }

                var effect = JsonFields.Text(a, "effect", string.Empty, issues, path);
                var action = new EffectData(effect.Length == 0 ? "invalid" : effect);
                foreach (var (key, value) in a)
                {
                    if (key == "effect")
                    {
                        continue;
                    }

                    if (key == "allowReactiveChain")
                    {
                        data._reactiveChainRequested |= value is JsonValue v && v.TryGetValue<bool>(out var chain) && chain;
                        continue;
                    }

                    if (JsonFields.Scalar(value) is { } scalar)
                    {
                        action.Params[key] = scalar;
                    }
                    else
                    {
                        issues.Error(JsonFields.Join(path, key), "значение должно быть числом, строкой или true/false");
                    }
                }

                data.Actions.Add(action);
            }
        }

        return data;
    }

    public IEnumerable<ContentRef> References()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (DamageType != AnyType && DamageType.Length > 0 && seen.Add(DamageType))
        {
            yield return new ContentRef(ContentTypeRegistry.DamageType, DamageType);
        }

        foreach (var a in Actions)
        {
            if (a.Get("responseDamageType") is string id && id.Length > 0 && id != DefenseActionRegistry.SameAsIncoming && seen.Add(id))
            {
                yield return new ContentRef(ContentTypeRegistry.DamageType, id);
            }
        }
    }

    public void RemapReferences(IReadOnlyDictionary<ContentRef, string> newIds)
    {
        if (newIds.TryGetValue(new ContentRef(ContentTypeRegistry.DamageType, DamageType), out var type))
        {
            DamageType = type;
        }

        foreach (var a in Actions)
        {
            if (a.Get("responseDamageType") is string id && newIds.TryGetValue(new ContentRef(ContentTypeRegistry.DamageType, id), out var renamed))
            {
                a.Set("responseDamageType", renamed);
            }
        }
    }

    public void Validate(ValidationContext context)
    {
        var issues = context.Issues;
        foreach (var field in Fields)
        {
            if ((field.VisibleWhen is null || field.VisibleWhen(this)) && field.Check(field.Get!(this), field.Key) is { } issue)
            {
                issues.AddRange(new[] { issue });
            }
        }

        if (_reactiveChainRequested)
        {
            issues.Error("actions", "цепочки реакций на ответные удары запрещены (ТЗ ч.5 §5.5): allowReactiveChain должно быть false");
        }

        if (Actions.Count == 0)
        {
            issues.Error("actions", "нет ни одной реакции — добавьте «что сделать»");
        }
        else if (Actions.Count > MaxActions)
        {
            issues.Error("actions", $"не больше {MaxActions} реакций в одном правиле");
        }

        if (Trigger == DefenseActionRegistry.OnDamageDealt && MinEffectiveResistance is not null)
        {
            issues.Error("minimumEffectiveResistancePercent", "порог сопротивления не применяется к «нанесён урон»: это условие о защите цели");
        }

        if (Trigger == DefenseActionRegistry.OnStatusApplied && (MinEffectiveResistance is not null || HealthBelowPercent is not null || DamageType != AnyType))
        {
            issues.Error("trigger", "у правила «накладывается статус» нет условий по урону и здоровью");
        }

        var conversions = 0.0;
        var immunity = false;
        for (var i = 0; i < Actions.Count; i++)
        {
            var a = Actions[i];
            var path = $"actions[{i}]";
            var descriptor = context.DefenseActions.Find(a.Type);
            if (descriptor is null)
            {
                issues.Error(path, $"неизвестная реакция «{a.Type}»");
                continue;
            }

            if (descriptor.Trigger != Trigger)
            {
                var when = DefenseActionRegistry.Triggers.FirstOrDefault(t => t.Value == descriptor.Trigger)?.Label ?? descriptor.Trigger;
                issues.Error(path, $"«{descriptor.Label}» действует только в правиле «когда: {when}»");
            }

            foreach (var field in descriptor.Fields)
            {
                if (field.VisibleWhen is { } visible && !visible(a))
                {
                    continue;
                }

                if (field.Check(a.Get(field.Key) ?? field.Default, JsonFields.Join(path, field.Key)) is { } issue)
                {
                    issues.AddRange(new[] { issue });
                }
            }

            foreach (var key in a.Params.Keys)
            {
                if (descriptor.FindField(key) is null)
                {
                    issues.Warning(JsonFields.Join(path, key), $"поле «{key}» не используется реакцией «{descriptor.Label}»");
                }
            }

            if (descriptor.IsConversion && FieldSchema.TryNumber(a.Get("fraction") ?? 0.0, out var share))
            {
                conversions += share;
            }

            immunity |= descriptor.Kind == DefenseActionKind.Immunity;
        }

        // Spec part 5 §4.3: the shares taken from one incoming component never exceed 100 %.
        if (conversions > 1 + 1e-9)
        {
            issues.Error("actions", $"сумма долей поглощения и отражения {Percent(conversions)} больше 100 % силы удара");
        }

        if (immunity && conversions > 0 && !TriggerOnImmuneContact)
        {
            issues.Warning("triggerOnImmuneContact", "при иммунитете поглощение и отражение этого правила не сработают — включите «Срабатывать при иммунитете» или разделите правило");
        }
    }

    internal static string Percent(double fraction) => (fraction * 100).ToString("0.##", CultureInfo.InvariantCulture) + " %";

    /// <summary>The runtime rule (ids are kept; unknown actions are dropped — such a rule does not pass validation).</summary>
    public DefenseRule ToRule(string id, string name, DefenseActionRegistry registry)
    {
        var actions = new List<DefenseAction>();
        foreach (var a in Actions)
        {
            if (registry.Find(a.Type) is not { } d)
            {
                continue;
            }

            double Num(string key, double fallback) => FieldSchema.TryNumber(a.Get(key) ?? d.FindField(key)?.Default, out var v) ? v : fallback;
            string Text(string key, string fallback) => a.Get(key) as string ?? d.FindField(key)?.Default as string ?? fallback;
            var response = Text("responseDamageType", DefenseActionRegistry.SameAsIncoming);
            actions.Add(new DefenseAction
            {
                Kind = d.Kind,
                Fraction = Num("fraction", 0),
                Amount = Num("amount", 0),
                Targeting = Text("targeting", DefenseActionRegistry.Attacker) == DefenseActionRegistry.AreaAround ? ReactionTargeting.Area : ReactionTargeting.Attacker,
                Radius = (int)Math.Round(Num("radiusHexes", 1)),
                IncludeCenter = a.Get("includeCenter") is true,
                Filter = Text("targetFilter", "enemies_of_defender") switch
                {
                    "allies_of_defender" => ReactionFilter.AlliesOfDefender,
                    "all_units" => ReactionFilter.All,
                    _ => ReactionFilter.EnemiesOfDefender,
                },
                Split = Text("targetDamageMode", "full_per_target") == "split_between_targets",
                ResponseDamageType = response == DefenseActionRegistry.SameAsIncoming ? null : response,
                Base = Text("base", "health_loss") == "shield_loss" ? RetaliationBase.ShieldLoss : RetaliationBase.HealthLoss,
                Shield = d.Kind == DefenseActionKind.ConvertToShield ? Text("shield", "Щит") : null,
                StatusCategory = Text("statusCategory", "debuff"),
            });
        }

        return new DefenseRule
        {
            Id = id,
            Name = name,
            Trigger = DefenseActionRegistry.TriggerOf(Trigger),
            DamageType = DamageType == AnyType || DamageType.Length == 0 ? null : DamageType,
            DamageTag = DamageTag.Length == 0 ? null : DamageTag,
            MinEffectiveResistance = MinEffectiveResistance,
            HealthBelowPercent = HealthBelowPercent,
            TriggerOnImmuneContact = TriggerOnImmuneContact,
            Priority = Priority,
            Cooldown = Cooldown,
            MaxTriggers = MaxTriggers,
            Actions = actions,
        };
    }

    public string Summary()
    {
        var when = DefenseActionRegistry.Triggers.FirstOrDefault(t => t.Value == Trigger)?.Label ?? Trigger;
        return $"когда: {when}; реакций: {Actions.Count}";
    }

    public IContentData Clone()
    {
        var copy = new DefenseRuleData
        {
            Trigger = Trigger, DamageType = DamageType, DamageTag = DamageTag, MinEffectiveResistance = MinEffectiveResistance,
            HealthBelowPercent = HealthBelowPercent, TriggerOnImmuneContact = TriggerOnImmuneContact, Priority = Priority,
            Cooldown = Cooldown, MaxTriggers = MaxTriggers, _reactiveChainRequested = _reactiveChainRequested,
        };
        foreach (var a in Actions)
        {
            copy.Actions.Add(a.Clone());
        }

        return copy;
    }
}
