using System;
using System.Collections.Generic;
using HexTactics.Content.Kinds;
using HexTactics.Content.Schema;
using HexTactics.Core.Combat;

namespace HexTactics.Content.Registry;

/// <summary>
/// A registered action of a defense rule (spec part 5 §4.1, §9.4 <c>DefenseRuleRegistry</c>): its stored id, the runtime
/// kind executed by the damage resolver, the trigger it belongs to and its fields. The rule editor builds its forms from
/// these descriptors; validation and import check against them. A new action = one descriptor here + one stage in the
/// damage resolver.
/// </summary>
public sealed record DefenseActionDescriptor(string Effect, string Label, string Description, DefenseActionKind Kind, string Trigger, IReadOnlyList<FieldSchema> Fields)
{
    /// <summary>Spends a share of D0 (stage 4): the shares of one rule may not exceed 100 % together.</summary>
    public bool IsConversion => Kind is DefenseActionKind.Negate or DefenseActionKind.ConvertToHealing or DefenseActionKind.ConvertToShield or DefenseActionKind.RedirectAsDamage;

    public FieldSchema? FindField(string key)
    {
        foreach (var f in Fields)
        {
            if (f.Key == key)
            {
                return f;
            }
        }

        return null;
    }

    /// <summary>A new action of this kind with every field at its default.</summary>
    public EffectData CreateDefault()
    {
        var action = new EffectData(Effect);
        foreach (var f in Fields)
        {
            if (f.Default is { } d)
            {
                action.Set(f.Key, d);
            }
        }

        return action;
    }
}

/// <summary>Registry of defense rule actions and the triggers they belong to.</summary>
public sealed class DefenseActionRegistry
{
    public const string OnHitContact = "on_hit_contact";
    public const string OnHealthLoss = "on_health_loss";
    public const string OnDamageDealt = "on_damage_dealt";
    public const string OnStatusApplied = "on_status_applied";

    public const string SameAsIncoming = "same_as_incoming";
    public const string Attacker = "attacker";
    public const string AreaAround = "adjacent_hex_ring";

    private readonly List<DefenseActionDescriptor> _actions = new();

    public IReadOnlyList<DefenseActionDescriptor> All => _actions;

    private static readonly Lazy<DefenseActionRegistry> SharedDefault = new(CreateDefault);

    /// <summary>The shared default registry (descriptors are immutable; built after the option lists are initialised).</summary>
    public static DefenseActionRegistry Default => SharedDefault.Value;

    /// <summary>«КОГДА» of the rule constructor (spec part 5 §6.3).</summary>
    public static IReadOnlyList<FieldOption> Triggers { get; } = new[]
    {
        new FieldOption(OnHitContact, "получен удар (при контакте)"),
        new FieldOption(OnHealthLoss, "потеряно HP или прочность щита"),
        new FieldOption(OnDamageDealt, "нанесён урон своим ударом"),
        new FieldOption(OnStatusApplied, "накладывается статус"),
    };

    public static DefenseTrigger TriggerOf(string trigger) => trigger switch
    {
        OnHealthLoss => DefenseTrigger.OnHealthLoss,
        OnDamageDealt => DefenseTrigger.OnDamageDealt,
        OnStatusApplied => DefenseTrigger.OnStatusApplied,
        _ => DefenseTrigger.OnHit,
    };

    public static IReadOnlyList<FieldOption> TargetingOptions { get; } = new[]
    {
        new FieldOption(Attacker, "атакующему"),
        new FieldOption(AreaAround, "вокруг себя (гексы в радиусе)"),
    };

    public static IReadOnlyList<FieldOption> FilterOptions { get; } = new[]
    {
        new FieldOption("enemies_of_defender", "только врагам"),
        new FieldOption("allies_of_defender", "только союзникам"),
        new FieldOption("all_units", "всем юнитам"),
    };

    public static IReadOnlyList<FieldOption> DamageModeOptions { get; } = new[]
    {
        new FieldOption("full_per_target", "каждой цели полностью"),
        new FieldOption("split_between_targets", "разделить между целями"),
    };

    public static IReadOnlyList<FieldOption> StatusCategories { get; } = new[]
    {
        new FieldOption("debuff", "дебаффы"),
        new FieldOption("buff", "баффы"),
        new FieldOption("periodic", "периодические эффекты"),
        new FieldOption("all", "все статусы"),
    };

    public void Register(DefenseActionDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (Find(descriptor.Effect) is not null)
        {
            throw new InvalidOperationException($"Defense action '{descriptor.Effect}' is already registered.");
        }

        _actions.Add(descriptor);
    }

    public DefenseActionDescriptor? Find(string effect) => _actions.Find(a => a.Effect == effect);

    public IEnumerable<DefenseActionDescriptor> For(string trigger)
    {
        foreach (var a in _actions)
        {
            if (a.Trigger == trigger)
            {
                yield return a;
            }
        }
    }

    private static FieldSchema Fraction(double fallback, string label, string hint, double max = 1) =>
        EffectTypeRegistry.Param("fraction", label, FieldKind.Number, fallback) with { Min = 0, Max = max, Percent = true, Hint = hint };

    private static FieldSchema Amount(double fallback, string label, string hint) =>
        EffectTypeRegistry.Param("amount", label, FieldKind.Number, fallback) with { Min = 0, Max = 100000, Unit = "HP", Hint = hint };

    private static bool Area(object o) => ((EffectData)o).Get("targeting") as string == AreaAround;

    /// <summary>Who receives a produced packet; shared by redirection and retaliation.</summary>
    private static IEnumerable<FieldSchema> PacketFields()
    {
        yield return EffectTypeRegistry.Param("targeting", "Кому", FieldKind.Choice, Attacker) with { Options = TargetingOptions };
        yield return EffectTypeRegistry.Param("radiusHexes", "Радиус", FieldKind.Integer, 1.0) with { Min = 1, Max = 3, Unit = "гекс", VisibleWhen = Area, Hint = "1 — шесть соседних гексов." };
        yield return EffectTypeRegistry.Param("targetFilter", "Фильтр целей", FieldKind.Choice, "enemies_of_defender") with { Options = FilterOptions };
        yield return EffectTypeRegistry.Param("targetDamageMode", "Сила по площади", FieldKind.Choice, "full_per_target") with
        {
            Options = DamageModeOptions, VisibleWhen = Area,
            Hint = "Каждая цель получает весь пакет или пакет делится между целями.",
        };
        yield return EffectTypeRegistry.Param("includeCenter", "Включая свой гекс", FieldKind.Flag, false) with { VisibleWhen = Area, Advanced = true };
        yield return EffectTypeRegistry.Param("responseDamageType", "Тип ответного урона", FieldKind.Reference, SameAsIncoming) with
        {
            RefKind = ContentTypeRegistry.DamageType, SpecialValue = SameAsIncoming, SpecialLabel = "как у входящего удара", Advanced = true,
        };
    }

    /// <summary>The actions of spec part 5 §4.1 (the «Иммунитет к статусу» and «Запрет лечения» rows: status immunity here,
    /// the healing block is a debuff of <c>healingReceived</c>).</summary>
    public static DefenseActionRegistry CreateDefault()
    {
        var r = new DefenseActionRegistry();
        r.Register(new DefenseActionDescriptor("immunity", "Иммунитет к урону",
            "Подходящий удар не отнимает HP. Это не R = 100 %: контактные реакции по умолчанию не срабатывают.",
            DefenseActionKind.Immunity, OnHitContact, Array.Empty<FieldSchema>()));
        r.Register(new DefenseActionDescriptor("negate", "Поглотить долю",
            "Доля силы удара исчезает до сопротивления, без другого эффекта.",
            DefenseActionKind.Negate, OnHitContact, new[] { Fraction(0.25, "Доля силы удара", "Доля D0 — силы удара до защиты.") }));
        r.Register(new DefenseActionDescriptor("convert_to_healing", "Поглотить и лечиться",
            "Доля силы удара лечит защитника до остального урона; не выше максимума, неиспользованное не возвращается.",
            DefenseActionKind.ConvertToHealing, OnHitContact, new[]
            {
                Fraction(0.25, "Доля силы удара", "Эта доля не наносит урон, а лечит."),
                EffectTypeRegistry.Param("recipient", "Кого лечить", FieldKind.Choice, "self") with { Options = new[] { new FieldOption("self", "себя") } },
            }));
        r.Register(new DefenseActionDescriptor("convert_to_shield", "Поглотить в щит",
            "Доля силы удара пополняет названный щит юнита (не выше его прочности).",
            DefenseActionKind.ConvertToShield, OnHitContact, new[]
            {
                Fraction(0.25, "Доля силы удара", "Эта доля не наносит урон, а пополняет щит."),
                EffectTypeRegistry.Param("shield", "Щит", FieldKind.Text, "Щит") with { MaxLength = 40, Hint = "Название щита в определении юнита." },
            }));
        var redirect = new List<FieldSchema> { Fraction(0.75, "Доля силы удара", "Эта доля не наносит урон защитнику, а уходит новым ударом.") };
        redirect.AddRange(PacketFields());
        r.Register(new DefenseActionDescriptor("redirect_as_damage", "Отразить часть урона",
            "Доля силы удара становится новым ударом; без допустимых целей она исчезает. Ответные удары не отражаются.",
            DefenseActionKind.RedirectAsDamage, OnHitContact, redirect));
        r.Register(new DefenseActionDescriptor("flat_reduction", "Плоское снижение",
            "Вычитает HP из урона после сопротивления, не ниже нуля; не действует на чистый урон.",
            DefenseActionKind.FlatReduction, OnHitContact, new[] { Amount(5, "Снижение", "Сколько HP вычесть из каждого удара.") }));
        r.Register(new DefenseActionDescriptor("damage_cap", "Предел урона за удар",
            "Один удар компонента не отнимает больше заданного (до щитов). Это не лимит за секунду.",
            DefenseActionKind.DamageCap, OnHitContact, new[] { Amount(30, "Предел", "Наибольший урон одного удара.") }));
        var retaliate = new List<FieldSchema>
        {
            Fraction(0.5, "Доля потери", "Доля фактически потерянного HP (или прочности щита); входящий удар не уменьшается.", max: 5),
            EffectTypeRegistry.Param("base", "База ответа", FieldKind.Choice, "health_loss") with
            {
                Options = new[] { new FieldOption("health_loss", "потерянное HP"), new FieldOption("shield_loss", "потеря прочности щита") },
            },
        };
        retaliate.AddRange(PacketFields());
        r.Register(new DefenseActionDescriptor("retaliate", "Ответный удар (шипы)",
            "После потери HP — независимый ответ; на ответные удары не срабатывает.",
            DefenseActionKind.Retaliate, OnHealthLoss, retaliate));
        r.Register(new DefenseActionDescriptor("vampirism", "Вампиризм",
            "Лечит владельца правила на долю HP, действительно снятых его ударом (не от номинального урона).",
            DefenseActionKind.Vampirism, OnDamageDealt, new[] { Fraction(0.2, "Доля снятого HP", "Сколько от снятого HP вернуть себе.") }));
        r.Register(new DefenseActionDescriptor("status_immunity", "Иммунитет к статусам",
            "Статусы выбранной категории не накладываются на юнита.",
            DefenseActionKind.StatusImmunity, OnStatusApplied, new[]
            {
                EffectTypeRegistry.Param("statusCategory", "Какие статусы", FieldKind.Choice, "debuff") with { Options = StatusCategories },
            }));
        return r;
    }
}
