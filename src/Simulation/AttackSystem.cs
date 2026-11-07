using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Combat;
using HexTactics.Core.Hex;
using HexTactics.Core.Pathfinding;
using HexTactics.Core.Units;

namespace HexTactics.Simulation;

/// <summary>
/// Normal attack of a chosen enemy (spec part 3 §4, §7 step 4). Runs after movement, units in id order:
/// <list type="bullet">
/// <item>While the order still has waypoints the unit follows them (the attack is the final stage of the chain).</item>
/// <item>Out of range: the unit walks to the nearest free reachable hex from which the target is in range (path length,
/// ties by hex order — deterministic) — never into the target's hex. Re-planned at most every 0.5 s. No such hex: status
/// <see cref="AttackStatus.TargetUnreachable"/> and an event; the unit waits, nothing is teleported.</item>
/// <item>In range and standing: it faces the target (nearest of six, stable tie rule) and hits once per
/// <see cref="UnitStats.AttackInterval"/> of game time — the first hit one interval after the attack starts.</item>
/// <item>Target dead or gone: the order ends (<see cref="AttackTargetLost"/>). No new target is chosen automatically.</item>
/// </list>
/// Dead units never act; deaths are processed by the simulation after all systems of the tick.
/// </summary>
internal sealed class AttackSystem : ISimulationSystem
{
    private readonly GameState _state;
    private readonly List<SimulationEvent> _events;
    private readonly SimulationSettings _settings;

    public AttackSystem(GameState state, List<SimulationEvent> events, SimulationSettings settings)
    {
        _state = state;
        _events = events;
        _settings = settings;
    }

    public string Name => "Attack";

    private int ReplanTicks => System.Math.Max(1, (int)System.Math.Round(0.5 * _settings.TicksPerSecond));

    public void Tick(SimulationContext context)
    {
        foreach (var unit in new List<UnitState>(_state.Units))
        {
            if (unit.IsAlive && unit.IsActive && unit.ActiveSkill is null)
            {
                TickUnit(unit, context.Tick, context.DeltaSeconds); // no normal attacks while a skill action runs
            }
        }
    }

    private void TickUnit(UnitState unit, long tick, double dt)
    {
        var combat = unit.Combat;
        if (unit.Orders.AttackTarget is not { } targetId)
        {
            if (combat.Status != AttackStatus.None)
            {
                combat.Reset();
            }

            return;
        }

        var target = _state.FindUnit(targetId);
        if (target is null || !target.IsAlive)
        {
            if (combat.ApproachCell is not null)
            {
                _state.RemoveLeadingDetours(unit.Id);
            }

            _state.SetAttackTarget(unit.Id, null);
            combat.Reset();
            var died = target is not null || _state.Fallen.Any(f => f.Id == targetId);
            _events.Add(new AttackTargetLost(tick, unit.Id, targetId, died ? AttackLostReason.TargetDied : AttackLostReason.TargetRemoved));
            return;
        }

        if (unit.Orders.OrderCount > 0)
        {
            combat.Status = AttackStatus.FollowingRoute; // the waypoints before the attack come first
            combat.AttackTimer = 0;
            return;
        }

        var inRange = unit.Cell.DistanceTo(target.Cell) <= unit.Stats.NormalAttackRangeHex;
        if (inRange && !unit.Motion.IsStepping)
        {
            Attack(unit, target, tick, dt);
            return;
        }

        if (combat.Status == AttackStatus.Attacking)
        {
            combat.Status = AttackStatus.Approaching; // the target left the range: walk again, the timer restarts
            combat.AttackTimer = 0;
        }

        if (unit.Motion.IsStepping && combat.ApproachCell is not null && tick - combat.ApproachPlannedTick < ReplanTicks)
        {
            return;
        }

        if (combat.ApproachCell is null && combat.Status != AttackStatus.TargetUnreachable || tick - combat.ApproachPlannedTick >= ReplanTicks)
        {
            PlanApproach(unit, target, tick);
        }
    }

    private void Attack(UnitState unit, UnitState target, long tick, double dt)
    {
        var combat = unit.Combat;
        if (combat.ApproachCell is not null)
        {
            _state.RemoveLeadingDetours(unit.Id);
            combat.ApproachCell = null;
        }

        if (combat.Status != AttackStatus.Attacking)
        {
            combat.Status = AttackStatus.Attacking;
            combat.AttackTimer = 0;
            _events.Add(new AttackStarted(tick, unit.Id, target.Id));
        }

        if (HexDirections.Toward(unit.Cell, target.Cell) is { } facing && unit.Facing != facing)
        {
            _state.SetFacing(unit.Id, facing); // keeps facing a moving target
            _events.Add(new FacingChanged(tick, unit.Id, facing));
        }

        // Not more often than once per interval of game time; the first hit after one full interval (spec part 3 §3).
        combat.AttackTimer += dt;
        if (combat.AttackTimer + 1e-9 < unit.Stats.AttackInterval)
        {
            return;
        }

        combat.AttackTimer -= unit.Stats.AttackInterval;
        var dealt = _state.ApplyDamage(target.Id, unit.Stats.AttackPower);
        combat.HitsDelivered++;
        _events.Add(new DamageApplied(tick, target.Id, unit.Id, dealt, target.CurrentHealth, DamageKind.NormalAttack));
    }

    /// <summary>Chooses the nearest free reachable hex within range of the target and walks there (a detour step).</summary>
    private void PlanApproach(UnitState unit, UnitState target, long tick)
    {
        var combat = unit.Combat;
        combat.ApproachPlannedTick = tick;
        var distances = Pathfinder.DistanceField(_state.Grid, unit.Cell, _state.ObstaclesFor(unit.Id));
        HexCoord? best = null;
        var bestDistance = int.MaxValue;
        foreach (var (cell, d) in distances)
        {
            if (cell == target.Cell || cell.DistanceTo(target.Cell) > unit.Stats.NormalAttackRangeHex)
            {
                continue;
            }

            if (_state.IsOccupiedByOther(cell, unit.Id) || _state.IsReservedByOther(cell, unit.Id))
            {
                continue;
            }

            if (d < bestDistance || d == bestDistance && best is { } b && cell.CompareTo(b) < 0)
            {
                best = cell;
                bestDistance = d;
            }
        }

        if (best is not { } approach)
        {
            if (combat.ApproachCell is not null)
            {
                _state.RemoveLeadingDetours(unit.Id);
                combat.ApproachCell = null;
            }

            if (combat.Status != AttackStatus.TargetUnreachable)
            {
                combat.Status = AttackStatus.TargetUnreachable;
                _events.Add(new AttackTargetUnreachable(tick, unit.Id, target.Id));
            }

            return;
        }

        combat.Status = AttackStatus.Approaching;
        if (combat.ApproachCell == approach)
        {
            return;
        }

        _state.RemoveLeadingDetours(unit.Id);
        if (approach != unit.Cell)
        {
            _state.InsertDetours(unit.Id, new[] { approach });
        }

        combat.ApproachCell = approach;
    }
}
