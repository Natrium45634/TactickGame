using System.Collections.Generic;
using HexTactics.Core.Hex;
using HexTactics.Core.Skills;
using HexTactics.Core.Units;

namespace HexTactics.Simulation;

/// <summary>
/// Executes planned skill uses (spec part 3 §5, §7 step 3) — after movement, before normal attacks, units in id order.
/// An immediate use fires as soon as the caster stands on a hex centre (a running step is completed first); the caster
/// turns to the skill's direction, the area is computed from its hex with pure hex arithmetic, and every living enemy
/// standing in it at this tick takes the damage once. Who stood there when the skill was planned does not matter.
/// </summary>
internal sealed class SkillSystem : ISimulationSystem
{
    private readonly GameState _state;
    private readonly List<SimulationEvent> _events;
    private readonly SimulationSettings _settings;

    public SkillSystem(GameState state, List<SimulationEvent> events, SimulationSettings settings)
    {
        _state = state;
        _events = events;
        _settings = settings;
    }

    public string Name => "Skills";

    public void Tick(SimulationContext context)
    {
        foreach (var unit in new List<UnitState>(_state.Units))
        {
            if (!unit.IsAlive || !unit.IsActive || unit.Motion.IsStepping)
            {
                continue;
            }

            // Spec part 3 §6.1, §7 step 3: the skill planned at the waypoint the unit stands on fires once, from the waypoint
            // hex itself (never from another hex, e.g. when the waypoint became unreachable).
            if (unit.Orders.Peek() is { Skill: { Status: SkillActionStatus.Planned } planned } wp && wp.Cell == unit.Cell)
            {
                Execute(unit, planned, wp.Cell, context.Tick);
            }

            if (unit.PendingSkill is { Status: SkillActionStatus.Planned } action)
            {
                Execute(unit, action, unit.Cell, context.Tick);
                _state.SetPendingSkill(unit.Id, null);
            }
        }
    }

    /// <summary>Fires <paramref name="action"/> of <paramref name="caster"/> from <paramref name="origin"/> (once).</summary>
    internal void Execute(UnitState caster, SkillAction action, HexCoord origin, long tick)
    {
        if (action.Status != SkillActionStatus.Planned || !_settings.Skills.TryGetValue(action.SkillId, out var skill))
        {
            return;
        }

        if (caster.Facing != action.Direction)
        {
            _state.SetFacing(caster.Id, action.Direction); // the strike goes where the unit looks (spec part 3 §6.3)
            _events.Add(new FacingChanged(tick, caster.Id, action.Direction));
        }

        var cells = skill.Area(origin, action.Direction, _state.Grid);
        var hit = new List<UnitId>();
        foreach (var cell in cells)
        {
            if (_state.UnitAt(cell) is not { IsAlive: true } victim || victim.Id == caster.Id)
            {
                continue;
            }

            if (!skill.FriendlyFire && victim.Side == caster.Side)
            {
                continue;
            }

            var dealt = _state.ApplyDamage(victim.Id, skill.Damage);
            hit.Add(victim.Id);
            _events.Add(new DamageApplied(tick, victim.Id, caster.Id, dealt, victim.CurrentHealth, DamageKind.Skill));
        }

        _state.MarkExecuted(action, tick);
        _events.Add(new SkillActivated(tick, caster.Id, action.Id, action.SkillId, origin, action.Direction, cells, hit));
    }
}
