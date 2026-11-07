using System;
using System.Collections.Generic;
using System.Globalization;

namespace HexTactics.Core.Effects;

/// <summary>Who an effect applies to, relative to its source (spec part 4 §6.2 «Цели»).</summary>
public enum EffectTargets
{
    /// <summary>Units of the other side.</summary>
    Enemies = 0,

    /// <summary>Units of the source's side, the source itself included.</summary>
    Allies = 1,

    /// <summary>Only the source.</summary>
    Self = 2,

    /// <summary>Every unit.</summary>
    All = 3,
}

/// <summary>
/// One configured effect (spec part 4 §6.3): a registered effect type and its parameter values. Parameters are plain
/// data (numbers, texts, flags) validated by the content schema of the type; the simulation's effect resolver executes
/// them with a handler registered for the type — never as code.
/// </summary>
public sealed record EffectSpec
{
    public EffectSpec(string type, IReadOnlyDictionary<string, object>? parameters = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(type);
        Type = type;
        Params = parameters ?? new Dictionary<string, object>();
    }

    /// <summary>Registered effect type id, e.g. <c>damage</c>.</summary>
    public string Type { get; }

    /// <summary>Parameter values by field key: <see cref="double"/>, <see cref="string"/> or <see cref="bool"/>.</summary>
    public IReadOnlyDictionary<string, object> Params { get; }

    public double Number(string key, double fallback = 0) => Params.TryGetValue(key, out var v) ? v switch
    {
        double d => d,
        int i => i,
        long l => l,
        float f => f,
        bool b => b ? 1 : 0,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) => p,
        _ => fallback,
    } : fallback;

    public string Text(string key, string fallback = "") => Params.TryGetValue(key, out var v) && v is string s ? s : fallback;

    public bool Flag(string key, bool fallback = false) => Params.TryGetValue(key, out var v) && v is bool b ? b : fallback;

    /// <summary>The <c>targets</c> parameter (default: enemies).</summary>
    public EffectTargets Targets => Text("targets", "enemies") switch
    {
        "allies" => EffectTargets.Allies,
        "self" => EffectTargets.Self,
        "all" => EffectTargets.All,
        _ => EffectTargets.Enemies,
    };

    /// <summary>Fixed damage effect of the given amount for enemies (the effect of the part-3 «Линейный удар»).</summary>
    public static EffectSpec FixedDamage(double amount, string targets = "enemies") =>
        new(EffectTypes.Damage, new Dictionary<string, object> { ["amount"] = amount, ["targets"] = targets });
}

/// <summary>Ids of the effect types the simulation can execute (the content registry describes their fields).</summary>
public static class EffectTypes
{
    public const string Damage = "damage";
    public const string Heal = "heal";
    public const string Buff = "buff";
    public const string Debuff = "debuff";
    public const string Periodic = "periodic";
}
