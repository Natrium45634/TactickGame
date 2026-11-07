using System;
using System.Collections.Generic;
using HexTactics.Content.Kinds;
using HexTactics.Content.Schema;
using HexTactics.Core.Effects;

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

    /// <summary>The effect types of this version of the game.</summary>
    public static EffectTypeRegistry CreateDefault()
    {
        var registry = new EffectTypeRegistry();
        registry.Register(new EffectTypeDescriptor(
            EffectTypes.Damage,
            "Урон",
            "Отнимает здоровье у целей в клетке.",
            EffectContext.Skill,
            new[]
            {
                Param("amount", "Урон", FieldKind.Number, 10.0) with { Min = 0, Max = 10000, Unit = "HP", Hint = "Фиксированное число HP за попадание." },
                Param("targets", "Цели", FieldKind.Choice, "enemies") with { Options = TargetOptions, Hint = "Кому наносится урон." },
            }));
        return registry;
    }
}
