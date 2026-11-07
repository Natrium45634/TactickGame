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

/// <summary>A registered effect type: its fields (with ranges and defaults) and where it may be used.</summary>
public sealed record EffectTypeDescriptor(string Type, string Label, string Description, EffectContext Contexts, IReadOnlyList<FieldSchema> Fields)
{
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

    /// <summary>A new effect of this type with every field at its default.</summary>
    public EffectData CreateDefault()
    {
        var effect = new EffectData(Type);
        foreach (var f in Fields)
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

    /// <summary>The effect types of this version of the game (spec part 4 §6.3).</summary>
    public static EffectTypeRegistry CreateDefault()
    {
        var registry = new EffectTypeRegistry();
        var amountFormula = new[] { new FieldOption("fixed", "фиксированное число HP"), new FieldOption("attack", "% от силы атаки (Attack Power)") };
        registry.Register(new EffectTypeDescriptor(
            EffectTypes.Damage,
            "Урон",
            "Отнимает здоровье у целей в клетке: фиксированное число или процент от силы атаки источника.",
            EffectContext.Skill,
            new[]
            {
                Param("formula", "Расчёт", FieldKind.Choice, "fixed") with { Options = amountFormula, Hint = "150 % — это 1,5 × сила атаки, а не доля здоровья цели." },
                Param("amount", "Урон", FieldKind.Number, 10.0) with { Min = 0, Max = 10000, Unit = "HP", VisibleWhen = o => Is(o, "formula", "fixed") },
                Param("multiplier", "Множитель силы атаки", FieldKind.Number, 1.0) with { Min = 0, Max = 50, Percent = true, VisibleWhen = o => Is(o, "formula", "attack") },
                Hits(),
                Interval(),
                Targets("enemies"),
            }));
        registry.Register(new EffectTypeDescriptor(
            EffectTypes.Heal,
            "Лечение",
            "Восстанавливает здоровье целей, не выше максимума.",
            EffectContext.Skill,
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
            }));
        foreach (var (type, label, text, targets) in new[]
        {
            (EffectTypes.Buff, "Бафф", "Временно увеличивает характеристику целей.", "allies"),
            (EffectTypes.Debuff, "Дебафф", "Временно уменьшает характеристику целей (не ниже безопасного минимума).", "enemies"),
        })
        {
            registry.Register(new EffectTypeDescriptor(type, label, text, EffectContext.Skill, new[]
            {
                Param("stat", "Характеристика", FieldKind.Choice, Stats.MoveSpeed) with { Options = StatOptions },
                Param("mode", "Величина", FieldKind.Choice, "percent") with { Options = new[] { new FieldOption("percent", "в процентах"), new FieldOption("fixed", "фиксированная") } },
                Param("percent", "Изменение", FieldKind.Number, 0.3) with { Min = 0, Max = 10, Percent = true, VisibleWhen = o => Is(o, "mode", "percent") },
                Param("delta", "Изменение", FieldKind.Number, 1.0) with { Min = 0, Max = 10000, VisibleWhen = o => Is(o, "mode", "fixed"), Hint = "В единицах характеристики: гекс/с, удар/с или урон." },
                Duration(5),
                Stacking(),
                Hits(),
                Interval(),
                Targets(targets),
            }));
        }

        registry.Register(new EffectTypeDescriptor(
            EffectTypes.Periodic,
            "Периодический эффект",
            "Урон или лечение каждую игровую секунду в течение длительности.",
            EffectContext.Skill,
            new[]
            {
                Param("kind", "Что", FieldKind.Choice, "damage") with { Options = new[] { new FieldOption("damage", "урон"), new FieldOption("heal", "лечение") } },
                Param("perSecond", "В секунду", FieldKind.Number, 5.0) with { Min = 0, Max = 10000, Unit = "HP/с" },
                Duration(3),
                Stacking(),
                Targets("enemies"),
            }));
        return registry;
    }
}
