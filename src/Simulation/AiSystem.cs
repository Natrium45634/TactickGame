using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Combat;
using HexTactics.Core.Hex;
using HexTactics.Core.Orders;
using HexTactics.Core.Scenarios;
using HexTactics.Core.Units;

namespace HexTactics.Simulation;

/// <summary>
/// The simple AI of spec part 7 §5.2 — five presets and nothing more (no behaviour trees, threat maps or planning). It runs before
/// blocks and movement and drives units only through the ordinary orders: an attack order (the attack system walks to the
/// nearest free hex in range and hits) or a move order home. A unit thinks every <see cref="AiProfile.ReevaluateSeconds"/> and at
/// once when its target is lost; it keeps an allowed target instead of switching between equal ones; a target the attack system
/// reports unreachable is skipped for a while, so a fallback is taken; a dead or removed target is never kept.
/// </summary>
internal sealed class AiSystem : ISimulationSystem
{
    private readonly GameState _state;
    private readonly SimulationSettings _settings;
    private readonly Func<IndividualOrder, OrderApplyResult> _order;

    public AiSystem(GameState state, SimulationSettings settings, Func<IndividualOrder, OrderApplyResult> order)
    {
        _state = state;
        _settings = settings;
        _order = order;
    }

    public string Name => "Ai";

    public void Tick(SimulationContext context)
    {
        foreach (var unit in _state.Units.Where(u => u.IsAlive && u.IsActive && u.Ai is not null).ToList())
        {
            Think(unit, context.Tick);
        }
    }

    private long Period(AiState ai) => Math.Max(1, (long)Math.Round(ai.Profile.ReevaluateSeconds * _settings.TicksPerSecond));

    private void Think(UnitState unit, long tick)
    {
        var ai = unit.Ai!;
        if (ai.Target is { } aimed && unit.Orders.AttackTarget == aimed && unit.Combat.Status == AttackStatus.TargetUnreachable)
        {
            // No hex in range can be reached (spec part 7 §5.2: report NoPath, take a fallback): skip it for three periods.
            ai.Unreachable[aimed] = tick + (3 * Period(ai));
            Drop(unit, ai, tick);
        }

        if (ai.Target is { } id && (_state.FindUnit(id) is not { IsAlive: true } current || !Allowed(unit, ai, current)))
        {
            Drop(unit, ai, tick); // never keep a dead, removed or no longer allowed target
        }

        if (tick < ai.NextThinkTick)
        {
            return;
        }

        ai.NextThinkTick = tick + Period(ai);
        foreach (var expired in ai.Unreachable.Where(p => p.Value <= tick).Select(p => p.Key).ToList())
        {
            ai.Unreachable.Remove(expired);
        }

        var choice = Choose(unit, ai);
        if (choice is null)
        {
            if (ai.Target is not null)
            {
                Drop(unit, ai, tick);
            }

            if (ai.Profile.Preset == AiPreset.Guard && unit.Cell != ai.Home && unit.Orders.OrderCount == 0)
            {
                ai.Phase = AiPhase.Return; // back to the guarded hex, never a chase outside the radius
                _order(new IndividualOrder(unit.Id, new[] { ai.Home }, unit.Facing ?? HexDirection.South));
            }
            else if (unit.Orders.OrderCount == 0)
            {
                ai.Phase = AiPhase.Idle;
            }

            return;
        }

        if (choice.Id == ai.Target && unit.Orders.AttackTarget == choice.Id)
        {
            return; // keep the target: no switching between equal candidates
        }

        ai.Target = choice.Id;
        ai.Phase = AiPhase.Engage;
        _order(new IndividualOrder(unit.Id, Array.Empty<HexCoord>(), unit.Facing ?? HexDirection.South, AttackTarget: choice.Id));
    }

    private void Drop(UnitState unit, AiState ai, long tick)
    {
        if (ai.Target is { } old && unit.Orders.AttackTarget == old)
        {
            _state.SetAttackTarget(unit.Id, null);
        }

        ai.Target = null;
        ai.Phase = AiPhase.ChooseTarget;
        ai.NextThinkTick = tick;
    }

    /// <summary>The target the preset wants now; the current one wins among equals.</summary>
    private UnitState? Choose(UnitState unit, AiState ai)
    {
        var profile = ai.Profile;
        var current = ai.Target is { } id ? _state.FindUnit(id) : null;
        UnitState? Best(IEnumerable<UnitState> candidates)
        {
            var list = candidates.Where(c => Allowed(unit, ai, c) && !ai.Unreachable.ContainsKey(c.Id)).ToList();
            if (current is not null && list.Contains(current))
            {
                return current;
            }

            return profile.Priority == AiPriority.LowestHealth
                ? list.OrderBy(c => c.CurrentHealth).ThenBy(c => c.Cell.DistanceTo(unit.Cell)).ThenBy(c => c.Id.Value).FirstOrDefault()
                : list.OrderBy(c => c.Cell.DistanceTo(unit.Cell)).ThenBy(c => c.Id.Value).FirstOrDefault();
        }

        var units = _state.Units.Where(c => c.Role == UnitRole.Combatant);
        var objects = _state.Units.Where(c => c.Role == UnitRole.Objective);
        var blocker = profile.SwitchToBlocker && _state.BlockOf(unit.Id) is { } link ? _state.FindUnit(link.Blocker) : null;
        return profile.Preset switch
        {
            AiPreset.AttackUnits => Best(units),
            AiPreset.AttackObjective => blocker is { IsAlive: true } ? blocker : Best(objects),
            AiPreset.UnitsThenObjective => Best(units) ?? Best(objects),
            AiPreset.ObjectiveThenUnits => blocker is { IsAlive: true } ? blocker : Best(objects) ?? Best(units),
            AiPreset.Guard => Best(units),
            _ => null,
        };
    }

    /// <summary>A living hostile unit the profile lets this unit go for (seen; for a guard — inside the guarded radius).</summary>
    private static bool Allowed(UnitState unit, AiState ai, UnitState target)
    {
        if (!target.IsAlive || target.Side == unit.Side)
        {
            return false;
        }

        if (ai.Profile.Preset == AiPreset.Guard)
        {
            return target.Cell.DistanceTo(ai.Home) <= Math.Max(0, ai.Profile.GuardRadius);
        }

        return ai.Profile.Sees(target.Cell.DistanceTo(unit.Cell)) || ai.Target == target.Id;
    }
}
