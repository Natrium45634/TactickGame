using System;
using System.Collections.Generic;
using HexTactics.Content.Kinds;
using HexTactics.Content.Schema;
using HexTactics.Core.Effects;
using Stats = HexTactics.Core.Effects.Stats;

namespace HexTactics.Content.Registry;

/// <summary>Where an effect type may be used.</summary>
[Flags]
public enum EffectContext
{
    None = 0,

    /// <summary>On a cell of a skill (spec part 4 §6.4).</summary>
    Skill = 1,

    /// <summary>On a tile type (spec part 4 §7).</summary>
    Tile = 2,
}

/// <summary>
/// A registered effect type: its fields (with ranges and defaults) and where it may be used. <see cref="Fields"/> are the
/// fields on a skill cell; <see cref="TileFields"/> — on a tile type (no source unit: fixed amounts, a trigger and a side
/// filter instead of «enemies/allies», spec part 4 §7.1).
/// </summary>
public sealed record EffectTypeDescriptor(string Type, string Label, string Description, EffectContext Contexts, IReadOnlyList<FieldSchema> Fields)
{
    public IReadOnlyList<FieldSchema>? TileFields { get; init; }

    /// <summary>The fields of the effect where it is used.</summary>
    public IReadOnlyList<FieldSchema> FieldsFor(EffectContext context) =>
        context == EffectContext.Tile && TileFields is not null ? TileFields : Fields;

    public FieldSchema? FindField(string key, EffectContext context = EffectContext.Skill)
    {
        foreach (var f in FieldsFor(context))
        {
            if (f.Key == key)
            {
                return f;
            }
        }

        return null;
    }

    /// <summary>A new effect of this type with every field at its default.</summary>
    public EffectData CreateDefault(EffectContext context = EffectContext.Skill)
    {
        var effect = new EffectData(Type);
        foreach (var f in FieldsFor(context))
        {
            if (f.Default is { } d)
            {
                effect.Set(f.Key, d);
            }
        }

        return effect;
    }
}

/// <summary>
/// Registry of effect types (spec part 4 §9.2 <c>EffectTypeRegistry</c>). The editors build their effect forms from the
/// descriptors, validation and import check against them, and the simulation executes the same type ids with its
/// <c>EffectResolver</c>. A new effect type = one descriptor here + one handler in the simulation.
/// </summary>
public sealed class EffectTypeRegistry
{
    private readonly List<EffectTypeDescriptor> _types = new();

    public IReadOnlyList<EffectTypeDescriptor> All => _types;

    public void Register(EffectTypeDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (Find(descriptor.Type) is not null)
        {
            throw new InvalidOperationException($"Effect type '{descriptor.Type}' is already registered.");
        }

        _types.Add(descriptor);
    }

    public EffectTypeDescriptor? Find(string type) => _types.Find(t => t.Type == type);

    public IEnumerable<EffectTypeDescriptor> For(EffectContext context)
    {
        foreach (var t in _types)
        {
            if ((t.Contexts & context) != 0)
            {
                yield return t;
            }
        }
    }

    public static IReadOnlyList<FieldOption> TargetOptions { get; } = new[]
    {
        new FieldOption("enemies", "только враги"),
        new FieldOption("allies", "союзники (и сам)"),
        new FieldOption("self", "сам юнит"),
        new FieldOption("all", "все"),
    };

    /// <summary>A field stored in the parameter bag of an <see cref="EffectData"/>.</summary>
    public static FieldSchema Param(string key, string label, FieldKind kind, object? fallback) => new()
    {
        Key = key,
        Label = label,
        Kind = kind,
        Default = fallback,
        Section = FieldSections.Effects,
        Get = o => ((EffectData)o).Get(key) ?? fallback,
        Set = (o, v) => ((EffectData)o).Set(key, v),
    };

    public static IReadOnlyList<FieldOption> StatOptions { get; } = new[]
    {
        new FieldOption(Stats.MoveSpeed, "скорость движения"),
        new FieldOption(Stats.AttackPower, "сила атаки (Attack Power)"),
        new FieldOption(Stats.AttackRate, "скорость атак"),
        new FieldOption(Stats.Resistance, "сопротивление (процентные пункты)"),
        new FieldOption(Stats.Armor, "защита A (очки)"),
        new FieldOption(Stats.HealingReceived, "получаемое лечение (дебафф 100 % — запрет лечения)"),
    };

    /// <summary>The built-in physical type: the default damage type of new damage effects (spec part 5 §8.1).</summary>
    public const string DefaultDamageType = "core.damage.physical";

    /// <summary>A damage type of the library (spec part 5 §8: every damage effect has one).</summary>
    public static FieldSchema DamageTypeField(string fallback = DefaultDamageType) => Param("damageType", "Тип урона", FieldKind.Reference, fallback) with
    {
        RefKind = "damage_type",
        Hint = "Природа урона: от неё зависят сопротивления цели. Форма (одна цель / область) задаётся отдельно.",
    };

    private static FieldSchema AreaField() => Param("area", "Область: сила", FieldKind.Choice, "full") with
    {
        Options = new[] { new FieldOption("full", "полная сила по каждой цели"), new FieldOption("split", "разделить силу между целями") },
        Hint = "«Разделить»: сила этого эффекта в одном ударе делится поровну между всеми целями, которых он задел.",
    };

    private static FieldSchema PenetrationField() => Param("penetration", "Пробитие сопротивления", FieldKind.Number, 0.0) with
    {
        Min = 0, Max = 500, Unit = "п.п.", Advanced = true,
        Hint = "Снижает только положительное сопротивление цели на столько процентных пунктов; иммунитет не пробивает.",
    };

    private static FieldSchema ArmorPenetrationField() => Param("armorPenetration", "Пробитие защиты A", FieldKind.Number, 0.0) with
    {
        Min = 0, Max = 1_000_000, Unit = "очк.", Advanced = true,
        Hint = "Вычитается из защиты A цели (не ниже 0); сопротивление R не меняет.",
    };

    private static FieldSchema ArmorPenetrationPercentField() => Param("armorPenetrationPercent", "Пробитие защиты A, %", FieldKind.Number, 0.0) with
    {
        Min = 0, Max = 100, Unit = "%", Advanced = true,
        Hint = "Доля оставшейся защиты A, которую удар не учитывает (после пробития в очках).",
    };

    private static FieldSchema ArmorOfField() => Param("armorOf", "Защита A против", FieldKind.Reference, "global") with
    {
        RefKind = "damage_type", SpecialValue = "global", SpecialLabel = "всех сопротивляемых типов", RefFilter = ReferenceFilter.ResistanceAffected,
        VisibleWhen = o => Is(o, "stat", Stats.Armor),
    };

    private static bool Layered(object o) => Is(o, "stat", Stats.Resistance) || Is(o, "stat", Stats.Armor);

    private static FieldSchema ResistanceOfField() => Param("resistanceOf", "Сопротивление к", FieldKind.Reference, "global") with
    {
        RefKind = "damage_type", SpecialValue = "global", SpecialLabel = "всем сопротивляемым типам", RefFilter = ReferenceFilter.ResistanceAffected,
        VisibleWhen = o => Is(o, "stat", Stats.Resistance),
    };

    public static IReadOnlyList<FieldOption> StackingOptions { get; } = new[]
    {
        new FieldOption("refresh", "обновить длительность"),
        new FieldOption("stack", "складывать"),
        new FieldOption("replace", "заменить"),
        new FieldOption("ignore", "игнорировать повтор"),
    };

    private static bool Is(object o, string key, string value) => (((EffectData)o).Get(key) as string ?? DefaultOf(o, key)) == value;

    private static string? DefaultOf(object o, string key) => key switch
    {
        "formula" => "fixed",
        "mode" => "percent",
        "stat" => Stats.MoveSpeed,
        "kind" => "damage",
        _ => null,
    };

    private static FieldSchema Hits() => Param("hits", "Число попаданий", FieldKind.Integer, 1.0) with
    {
        Min = 1, Max = 20, Hint = "Одно действие — несколько событий попадания; цели берутся в момент каждого удара.",
    };

    private static FieldSchema Interval() => Param("interval", "Интервал между попаданиями", FieldKind.Number, 0.0) with
    {
        Min = 0, Max = 10, Unit = "с", Hint = "Игровые секунды; юнит ждёт в точке до последнего удара. Пауза останавливает таймер.",
    };

    private static FieldSchema Targets(string fallback) => Param("targets", "Цели", FieldKind.Choice, fallback) with { Options = TargetOptions };

    private static FieldSchema Duration(double fallback) => Param("duration", "Длительность", FieldKind.Number, fallback) with { Min = 0.1, Max = 600, Unit = "с" };

    private static FieldSchema Stacking() => Param("stacking", "Повторное наложение", FieldKind.Choice, "refresh") with
    {
        Options = StackingOptions, Hint = "Что делать, если цель уже под этим эффектом.",
    };

    public static IReadOnlyList<FieldOption> SideOptions { get; } = new[]
    {
        new FieldOption(TileSides.All, "все юниты"),
        new FieldOption(TileSides.Player, "только юниты игрока"),
        new FieldOption(TileSides.Enemy, "только враги"),
    };

    public static IReadOnlyList<FieldOption> TriggerOptions { get; } = new[]
    {
        new FieldOption("stay", "пока юнит на гексе"),
        new FieldOption("enter", "при входе на гекс"),
        new FieldOption("exit", "при выходе с гекса"),
    };

    private static FieldSchema Sides() => Param("sides", "На кого действует", FieldKind.Choice, TileSides.All) with
    {
        Options = SideOptions, Hint = "У гекса нет «своих» и «чужих»: выбирается сторона юнитов.",
    };

    private static FieldSchema Trigger(bool stay) => Param("trigger", "Когда", FieldKind.Choice, stay ? "stay" : "enter") with
    {
        Options = stay ? TriggerOptions : new[] { TriggerOptions[1], TriggerOptions[2] },
        Hint = stay
            ? "«Пока на гексе» — непрерывно по игровому времени нахождения в логическом гексе (5 HP/с × 2 с = 10 HP)."
            : "Один раз, когда логический гекс юнита сменяется (появление на гексе — тоже вход).",
    };

    private static bool NotStay(object o) => (((EffectData)o).Get("trigger") as string ?? "stay") != "stay";

    /// <summary>The effect types of this version of the game (spec part 4 §6.3).</summary>
    public static EffectTypeRegistry CreateDefault()
    {
        var registry = new EffectTypeRegistry();
        var amountFormula = new[] { new FieldOption("fixed", "фиксированное число HP"), new FieldOption("attack", "% от силы атаки (Attack Power)") };
        registry.Register(new EffectTypeDescriptor(
            EffectTypes.Damage,
            "Урон",
            "Отнимает здоровье у целей в клетке: фиксированное число или процент от силы атаки источника.",
            EffectContext.Skill | EffectContext.Tile,
            new[]
            {
                Param("formula", "Расчёт", FieldKind.Choice, "fixed") with { Options = amountFormula, Hint = "150 % — это 1,5 × сила атаки, а не доля здоровья цели." },
                Param("amount", "Урон", FieldKind.Number, 10.0) with { Min = 0, Max = 10000, Unit = "HP", VisibleWhen = o => Is(o, "formula", "fixed") },
                Param("multiplier", "Множитель силы атаки", FieldKind.Number, 1.0) with { Min = 0, Max = 50, Percent = true, VisibleWhen = o => Is(o, "formula", "attack") },
                DamageTypeField(),
                Hits(),
                Interval(),
                Targets("enemies"),
                AreaField(),
                PenetrationField(),
                ArmorPenetrationField(),
                ArmorPenetrationPercentField(),
            })
        {
            TileFields = new[]
            {
                Trigger(stay: false),
                Param("amount", "Урон", FieldKind.Number, 10.0) with { Min = 0, Max = 10000, Unit = "HP" },
                DamageTypeField(),
                Sides(),
            },
        });
        registry.Register(new EffectTypeDescriptor(
            EffectTypes.Heal,
            "Лечение",
            "Восстанавливает здоровье целей, не выше максимума.",
            EffectContext.Skill | EffectContext.Tile,
            new[]
            {
                Param("formula", "Расчёт", FieldKind.Choice, "fixed") with
                {
                    Options = new[] { new FieldOption("fixed", "фиксированное число HP"), new FieldOption("attack", "% от силы атаки источника"), new FieldOption("maxHealth", "% от макс. здоровья источника") },
                },
                Param("amount", "Лечение", FieldKind.Number, 10.0) with { Min = 0, Max = 10000, Unit = "HP", VisibleWhen = o => Is(o, "formula", "fixed") },
                Param("multiplier", "Множитель", FieldKind.Number, 0.5) with { Min = 0, Max = 50, Percent = true, VisibleWhen = o => !Is(o, "formula", "fixed") },
                Hits(),
                Interval(),
                Targets("allies"),
            })
        {
            TileFields = new[]
            {
                Trigger(stay: false),
                Param("amount", "Лечение", FieldKind.Number, 10.0) with { Min = 0, Max = 10000, Unit = "HP" },
                Sides(),
            },
        });
        foreach (var (type, label, text, targets) in new[]
        {
            (EffectTypes.Buff, "Бафф", "Временно увеличивает характеристику целей.", "allies"),
            (EffectTypes.Debuff, "Дебафф", "Временно уменьшает характеристику целей (не ниже безопасного минимума).", "enemies"),
        })
        {
            var stat = Param("stat", "Характеристика", FieldKind.Choice, Stats.MoveSpeed) with { Options = StatOptions };
            var mode = Param("mode", "Величина", FieldKind.Choice, "percent") with { Options = new[] { new FieldOption("percent", "в процентах"), new FieldOption("fixed", "фиксированная") }, VisibleWhen = o => !Layered(o) };
            var percent = Param("percent", "Изменение", FieldKind.Number, 0.3) with { Min = 0, Max = 10, Percent = true, VisibleWhen = o => !Layered(o) && Is(o, "mode", "percent") };
            var delta = Param("delta", "Изменение", FieldKind.Number, 1.0) with { Min = 0, Max = 10000, VisibleWhen = o => Layered(o) || Is(o, "mode", "fixed"), Hint = "В единицах характеристики: гекс/с, удар/с, урон; для сопротивления — процентные пункты (итог R ограничен −500…+500); для защиты A — очки (итог A в расчёте не ниже 0)." };
            var resistanceOf = ResistanceOfField();
            var armorOf = ArmorOfField();
            registry.Register(new EffectTypeDescriptor(type, label, text, EffectContext.Skill | EffectContext.Tile, new[]
            {
                stat, resistanceOf, armorOf, mode, percent, delta,
                Duration(5),
                Stacking(),
                Hits(),
                Interval(),
                Targets(targets),
            })
            {
                TileFields = new[] { Trigger(stay: false), stat, resistanceOf, armorOf, mode, percent, delta, Duration(5), Stacking(), Sides() },
            });
        }

        registry.Register(new EffectTypeDescriptor(
            EffectTypes.Periodic,
            "Периодический эффект",
            "Урон или лечение N HP за игровую секунду — непрерывно по игровому времени (дробно, без зависимости от FPS).",
            EffectContext.Skill | EffectContext.Tile,
            new[]
            {
                Param("kind", "Что", FieldKind.Choice, "damage") with { Options = new[] { new FieldOption("damage", "урон"), new FieldOption("heal", "лечение") } },
                Param("perSecond", "В секунду", FieldKind.Number, 5.0) with { Min = 0, Max = 10000, Unit = "HP/с" },
                DamageTypeField() with { VisibleWhen = o => Is(o, "kind", "damage") },
                Duration(3),
                Stacking(),
                Targets("enemies"),
            })
        {
            TileFields = new[]
            {
                Trigger(stay: true),
                Param("kind", "Что", FieldKind.Choice, "damage") with { Options = new[] { new FieldOption("damage", "урон"), new FieldOption("heal", "лечение") } },
                Param("perSecond", "В секунду", FieldKind.Number, 5.0) with { Min = 0, Max = 10000, Unit = "HP/с" },
                DamageTypeField() with { VisibleWhen = o => Is(o, "kind", "damage") },
                Duration(3) with { VisibleWhen = NotStay, Hint = "После входа/выхода эффект действует столько игровых секунд (статус)." },
                Stacking() with { VisibleWhen = NotStay },
                Sides(),
            },
        });
        return registry;
    }
}
