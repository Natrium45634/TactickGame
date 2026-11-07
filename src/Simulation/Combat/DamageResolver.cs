using System;
using System.Collections.Generic;
using HexTactics.Core.Combat;
using HexTactics.Core.Units;

namespace HexTactics.Simulation;

/// <summary>
/// The single damage calculation of the game (spec part 5 §5): normal attacks, skill hits, tile surfaces, periodic statuses
/// and reactions all hand a typed <see cref="DamageEvent"/> to <see cref="Resolve"/>. Stages (part 5 §5.1): eligibility →
/// D0 (frozen, ≥ 0) → explicit immunity → conversions → numeric resistance with penetration (resistance-affected types only)
/// → flat reduction / cap → shields → health. Immunities, conversions, reductions and shields arrive with the defense rules
/// (v0.22.0); this version runs eligibility, D0, resistance and health. Values are doubles computed in a fixed order on the
/// fixed tick — no rounding until display, identical at any frame rate.
/// </summary>
public sealed class DamageResolver
{
    private readonly GameState _state;
    private readonly List<SimulationEvent> _events;
    private readonly SimulationSettings _settings;
    private long _nextEventId;

    internal DamageResolver(GameState state, List<SimulationEvent> events, SimulationSettings settings)
    {
        _state = state;
        _events = events;
        _settings = settings;
    }

    /// <summary>The last resolved result (tests, traces).</summary>
    public DamageResult? Last { get; private set; }

    /// <summary>The damage type of an id; an unknown id falls back to the legacy neutral type (validation prevents it).</summary>
    public DamageTypeDefinition TypeOf(string id) =>
        _settings.DamageTypes.TryGetValue(id, out var t) ? t
        : DamageTypes.BuiltIn.TryGetValue(id, out var b) ? b
        : DamageTypes.BuiltIn[DamageTypes.Untyped];

    /// <summary>A new primary event (its own root action).</summary>
    public DamageEvent Create(UnitId? source, UnitId target, string damageTypeId, double amount, DamageOrigin origin, double penetration = 0, bool periodic = false)
    {
        var id = ++_nextEventId;
        return new DamageEvent
        {
            EventId = id,
            RootActionId = id,
            Source = source,
            Target = target,
            DamageTypeId = damageTypeId,
            Amount = amount,
            Penetration = penetration,
            Origin = origin,
            Periodic = periodic,
        };
    }

    /// <summary>The numeric resistance R of <paramref name="target"/> against a type after penetration; null for true-like types.</summary>
    public double? EffectiveResistance(UnitState target, string damageTypeId, double penetration = 0)
    {
        var type = TypeOf(damageTypeId);
        if (type.Strategy != DamageStrategy.ResistanceAffected)
        {
            return null;
        }

        return ResistanceMath.AfterPenetration(ResistanceMath.BeforePenetration(target.Stats.Resistances, target.Statuses, type), penetration);
    }

    public DamageResult Resolve(DamageEvent e, long tick)
    {
        // 1. Eligibility: the target exists and is alive at the moment of the hit.
        var target = _state.FindUnit(e.Target);
        if (target is null || !target.IsAlive)
        {
            Last = new DamageResult { Event = e, Missed = true };
            return Last;
        }

        // 2. D0: the attacker's side is already in the event; frozen, never negative or NaN.
        var d0 = double.IsFinite(e.Amount) ? Math.Max(0, e.Amount) : 0;

        // 5. Numeric resistance (resistance-affected types only), then health.
        var r = EffectiveResistance(target, e.DamageTypeId, e.Penetration);
        var after = r is { } value ? d0 * ResistanceMath.Multiplier(value) : d0;
        var hpLoss = _state.ApplyDamage(target.Id, after);
        _events.Add(new DamageApplied(tick, target.Id, e.Source ?? target.Id, hpLoss, target.CurrentHealth, KindOf(e)) { DamageTypeId = TypeOf(e.DamageTypeId).Id });
        Last = new DamageResult
        {
            Event = e,
            D0 = d0,
            Resistance = r,
            AfterResistance = after,
            HpLoss = hpLoss,
            Killed = !target.IsAlive,
        };
        return Last;
    }

    private static DamageKind KindOf(DamageEvent e) => e.Origin switch
    {
        DamageOrigin.NormalAttack => DamageKind.NormalAttack,
        DamageOrigin.Surface => DamageKind.Surface,
        DamageOrigin.Status => DamageKind.Periodic,
        DamageOrigin.Debug => DamageKind.Debug,
        _ => DamageKind.Skill,
    };
}
