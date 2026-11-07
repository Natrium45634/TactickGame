using System;
using System.Collections.Generic;
using HexTactics.Core.Effects;
using HexTactics.Core.Units;

namespace HexTactics.Simulation;

/// <summary>Why an effect is applied (for events and visuals).</summary>
public enum EffectSource
{
    Skill = 0,
}

/// <summary>One application of an effect to one target.</summary>
public sealed record EffectInvocation(EffectSpec Effect, UnitState? Source, UnitState Target, long Tick, EffectSource Origin);

/// <summary>Executes one registered effect type (spec part 4 §6.3, §9.2: only shared services resolve effects).</summary>
public interface IEffectHandler
{
    string Type { get; }

    void Apply(EffectInvocation invocation, EffectResolver resolver);
}

/// <summary>
/// The single executor of configured effects (spec part 4 §6.3 <c>EffectResolver</c>): skills (and, later, tiles and
/// statuses) describe effects as data; this service checks the target rules and runs the handler registered for the
/// effect type. Unknown types are never executed. New effect types are added by registering a handler here and a field
/// schema in the content registry — no second implementation of damage or health rules elsewhere.
/// </summary>
public sealed class EffectResolver
{
    private readonly Dictionary<string, IEffectHandler> _handlers = new(StringComparer.Ordinal);

    internal EffectResolver(GameState state, List<SimulationEvent> events)
    {
        State = state;
        Events = events;
        Register(new DamageEffectHandler());
    }

    internal GameState State { get; }

    internal List<SimulationEvent> Events { get; }

    /// <summary>Effect types this simulation can execute.</summary>
    public IReadOnlyCollection<string> Types => _handlers.Keys;

    public void Register(IEffectHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handlers[handler.Type] = handler;
    }

    public bool CanExecute(string type) => _handlers.ContainsKey(type);

    /// <summary>True if <paramref name="target"/> is a valid target of <paramref name="effect"/> from <paramref name="source"/>.</summary>
    public static bool Matches(EffectSpec effect, UnitState? source, UnitState target)
    {
        if (!target.IsAlive)
        {
            return false;
        }

        if (source is null)
        {
            return true;
        }

        return effect.Targets switch
        {
            EffectTargets.Enemies => target.Side != source.Side,
            EffectTargets.Allies => target.Side == source.Side,
            EffectTargets.Self => target.Id == source.Id,
            _ => true,
        };
    }

    /// <summary>Applies the effect if its type is known and the target matches; returns whether it was applied.</summary>
    public bool Apply(EffectInvocation invocation)
    {
        if (!_handlers.TryGetValue(invocation.Effect.Type, out var handler) || !Matches(invocation.Effect, invocation.Source, invocation.Target))
        {
            return false;
        }

        handler.Apply(invocation, this);
        return true;
    }

    /// <summary>Shared health rule: lowers health (not below 0) and reports the damage; death is processed after the tick.</summary>
    public void DealDamage(UnitState target, UnitState? source, double amount, long tick, DamageKind kind)
    {
        var dealt = State.ApplyDamage(target.Id, (int)Math.Round(Math.Max(0, amount), MidpointRounding.AwayFromZero));
        Events.Add(new DamageApplied(tick, target.Id, source?.Id ?? target.Id, dealt, target.CurrentHealth, kind));
    }
}

/// <summary><c>damage</c>: a fixed amount of health (spec part 4 §6.3 p.1; more formulas follow with the skill editor).</summary>
internal sealed class DamageEffectHandler : IEffectHandler
{
    public string Type => EffectTypes.Damage;

    public void Apply(EffectInvocation invocation, EffectResolver resolver) =>
        resolver.DealDamage(invocation.Target, invocation.Source, invocation.Effect.Number("amount"), invocation.Tick, DamageKind.Skill);
}
