using System;
using System.Collections.Generic;
using HexTactics.Core.Effects;
using HexTactics.Core.Units;

namespace HexTactics.Simulation;

/// <summary>
/// Timed statuses (spec part 4 §6.3): periodic effects deal/heal <c>amount per second × game time</c> every tick (exact
/// fractions, independent of FPS; nothing while paused), modifiers run out and the characteristic returns to its value.
/// Runs after skills and attacks, before deaths. Statuses live on after the action that applied them.
/// </summary>
internal sealed class StatusSystem : ISimulationSystem
{
    private readonly GameState _state;
    private readonly List<SimulationEvent> _events;
    private readonly EffectResolver _resolver;

    public StatusSystem(GameState state, List<SimulationEvent> events, EffectResolver resolver)
    {
        _state = state;
        _events = events;
        _resolver = resolver;
    }

    public string Name => "Statuses";

    public void Tick(SimulationContext context)
    {
        foreach (var unit in new List<UnitState>(_state.Units))
        {
            if (!unit.IsAlive || unit.Statuses.Count == 0)
            {
                continue;
            }

            foreach (var status in new List<StatusEffect>(unit.Statuses))
            {
                if (status.Kind != StatusKind.Periodic || !unit.IsAlive)
                {
                    continue;
                }

                var amount = status.AmountPerSecond * Math.Min(context.DeltaSeconds, Math.Max(0, status.Remaining));
                var source = status.Source is { } s ? _state.FindUnit(s) : null;
                if (status.Heals)
                {
                    _resolver.Heal(unit, source, amount, context.Tick, DamageKind.Periodic);
                }
                else
                {
                    _resolver.DealDamage(unit, source, amount, context.Tick, DamageKind.Periodic);
                }
            }

            foreach (var expired in _state.AgeStatuses(unit.Id, context.DeltaSeconds))
            {
                _events.Add(new StatusExpired(context.Tick, unit.Id, expired.Key, expired.Kind, expired.Stat));
            }
        }
    }
}
