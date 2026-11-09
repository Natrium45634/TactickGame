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
    private readonly DamageResolver _damage;

    public AttackSystem(GameState state, List<SimulationEvent> events, SimulationSettings settings, DamageResolver damage)
    {
        _damage = damage;
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

            AutoAttack(unit, tick, dt);
            return;
        }

        combat.AutoTimer = 0; // an explicit order owns the cadence: no free extra hit (spec part 7 §8.3 p.3, M7-47)

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
        combat.HitsDelivered++;
        Hit(unit, target, tick);
    }

    /// <summary>
    /// One normal attack (spec part 5 §2.3, §8.3): a typed damage event per target — the target only, or every enemy of the
    /// attacker within the area radius of the target's hex at this moment (stable id order), full or split strength.
    /// </summary>
    private void Hit(UnitState unit, UnitState target, long tick)
    {
        var stats = unit.Stats;
        var targets = new List<UnitState> { target };
        if (stats.AttackAreaRadius > 0)
        {
            targets = _state.Units
                .Where(u => u.IsAlive && u.Side != unit.Side && u.Cell.DistanceTo(target.Cell) <= stats.AttackAreaRadius)
                .OrderBy(u => u.Id.Value)
                .ToList();
        }

        var amount = stats.AttackAreaSplit && targets.Count > 0 ? stats.AttackPower / targets.Count : stats.AttackPower;
        foreach (var t in targets)
        {
            _damage.Resolve(_damage.Create(unit.Id, t.Id, stats.AttackDamageTypeId, amount, DamageOrigin.NormalAttack,
                armorPenetration: stats.AttackArmorPenetration, armorPenetrationPercent: stats.AttackArmorPenetrationPercent,
                canCrit: true, canSurprise: true, rollKey: $"atk:{unit.Id.Value}:{unit.Combat.HitsDelivered}:{t.Id.Value}"), tick);
        }
    }

    /// <summary>
    /// The automatic base attack (spec part 7 §8): only enemies in the unit's own area, turned with its logical facing; the unit
    /// never turns to them and never walks after them. One hit per attack interval of game time while an enemy is in the area
    /// (the first one interval after it entered); no enemy — the timer resets, nothing is spent. While walking only if allowed.
    /// </summary>
    private void AutoAttack(UnitState unit, long tick, double dt)
    {
        var combat = unit.Combat;
        var stats = unit.Stats;
        if (!_settings.AutoAttacks || !stats.AutoAttackEnabled || stats.BasicAttack is not { } basic || unit.Facing is not { } facing
            || (!basic.WhileMoving && unit.Motion.IsStepping))
        {
            combat.AutoTimer = 0;
            return;
        }

        var targets = AutoTargets(unit, basic, facing);
        if (targets.Count == 0)
        {
            combat.AutoTimer = 0;
            return;
        }

        combat.AutoTimer += dt;
        var interval = stats.AttackInterval;
        for (var n = 0; n < BasicAttackDefinition.MaxHitsPerTick && combat.AutoTimer + 1e-9 >= interval && targets.Count > 0; n++)
        {
            combat.AutoTimer -= interval;
            combat.AutoHits++;
            var amount = stats.AttackPower * System.Math.Max(0, basic.PowerPercent) / 100;
            var type = basic.DamageTypeId is { Length: > 0 } own ? own : stats.AttackDamageTypeId;
            foreach (var t in targets)
            {
                _damage.Resolve(_damage.Create(unit.Id, t.Id, type, amount, DamageOrigin.NormalAttack,
                    armorPenetration: stats.AttackArmorPenetration, armorPenetrationPercent: stats.AttackArmorPenetrationPercent,
                    canCrit: basic.CanCrit, canSurprise: basic.CanSurprise, rollKey: $"auto:{unit.Id.Value}:{combat.AutoHits}:{t.Id.Value}"), tick);
            }

            targets = AutoTargets(unit, basic, facing); // a target may have died: no hit into the void
        }

        if (combat.AutoTimer > interval)
        {
            combat.AutoTimer = interval; // the safety limit caps a backlog, never a burst after it
        }
    }

    private List<UnitState> AutoTargets(UnitState unit, BasicAttackDefinition basic, HexDirection facing)
    {
        var area = new HashSet<HexCoord>(basic.Area(unit.Cell, facing));
        var candidates = _state.Units.Where(u => u.IsAlive && u.IsActive && u.Side != unit.Side && area.Contains(u.Cell));
        return basic.Choose(unit.Cell, facing, candidates, u => u.Cell, u => u.CurrentHealth, u => u.Id.Value).ToList();
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
