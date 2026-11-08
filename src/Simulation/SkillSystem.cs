using System.Collections.Generic;
using HexTactics.Core.Hex;
using HexTactics.Core.Skills;
using HexTactics.Core.Units;

namespace HexTactics.Simulation;

/// <summary>
/// Executes planned skill uses (spec part 3 §5, §7 step 3; part 4 §6.5) — after movement, before normal attacks, units in
/// id order. A use starts when the caster stands on a hex centre: at the waypoint it was planned for (never on another hex)
/// or, for an immediate use, wherever it rests (a running step is completed first). The caster turns to the skill's
/// direction, the cells are resolved from that hex, and the hits are applied over game time in the fixed order
/// time → hit → cell → effect; each hit takes whoever stands in the cell at that moment. Zero interval and one hit =
/// everything in the starting tick (the part-3 behaviour). The caster waits until the last hit, then goes on. Removing
/// the waypoint (or the caster's death) stops the remaining hits; applied effects and statuses stay.
/// </summary>
internal sealed class SkillSystem : ISimulationSystem
{
    private readonly GameState _state;
    private readonly List<SimulationEvent> _events;
    private readonly SimulationSettings _settings;
    private readonly EffectResolver _resolver;

    public SkillSystem(GameState state, List<SimulationEvent> events, SimulationSettings settings, EffectResolver resolver)
    {
        _state = state;
        _events = events;
        _settings = settings;
        _resolver = resolver;
    }

    public string Name => "Skills";

    public void Tick(SimulationContext context)
    {
        foreach (var unit in new List<UnitState>(_state.Units))
        {
            if (!unit.IsAlive)
            {
                continue;
            }

            if (unit.ActiveSkill is { } running)
            {
                running.Elapsed += context.DeltaSeconds;
                Advance(unit, running, context.Tick);
                continue;
            }

            if (!unit.IsActive || unit.Motion.IsStepping)
            {
                continue;
            }

            // Spec part 3 §6.1, §7 step 3: the skill planned at the waypoint the unit stands on starts from that hex.
            if (unit.Orders.Peek() is { Skill: { Status: SkillActionStatus.Planned } planned } wp && wp.Cell == unit.Cell)
            {
                Start(unit, planned, wp.Cell, context.Tick);
            }
            else if (unit.PendingSkill is { Status: SkillActionStatus.Planned } action)
            {
                Start(unit, action, unit.Cell, context.Tick);
            }
        }
    }

    /// <summary>Starts <paramref name="action"/> of <paramref name="caster"/> from <paramref name="origin"/> (once).</summary>
    internal void Start(UnitState caster, SkillAction action, HexCoord origin, long tick)
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

        var cells = skill.Resolve(origin, action.Direction, _state.Grid);
        var execution = new SkillExecution(action, skill, origin, action.Direction, cells, SkillExecution.Schedule(cells));
        _state.SetActiveSkill(caster.Id, execution);
        var hit = Advance(caster, execution, tick);
        var area = new List<HexCoord>(cells.Count);
        foreach (var (cell, _) in cells)
        {
            area.Add(cell);
        }

        _events.Add(new SkillActivated(tick, caster.Id, action.Id, action.SkillId, origin, action.Direction, area, hit));
    }

    /// <summary>Applies the hits that are due; finishes the action after its last hit. Returns the units hit now.</summary>
    private List<UnitId> Advance(UnitState caster, SkillExecution execution, long tick)
    {
        var hitNow = new List<UnitId>();
        if (execution.Action.Status == SkillActionStatus.Cancelled)
        {
            _events.Add(new SkillInterrupted(tick, caster.Id, execution.Action.Id, execution.Action.SkillId, execution.Next, execution.Hits.Count));
            _state.SetActiveSkill(caster.Id, null);
            ClearPending(caster, execution.Action);
            return hitNow;
        }

        var due = new List<ScheduledHit>();
        while (!execution.Done && execution.Hits[execution.Next].Time <= execution.Elapsed + 1e-9)
        {
            due.Add(execution.Hits[execution.Next]);
            execution.Next++;
        }

        // Spec part 5 §2.3: an effect set to «split» shares its strength among the targets it hits in this wave (the same
        // hit of the same effect over all its cells); targets are the units standing in the cells now.
        var shares = new Dictionary<(int Hit, string Group), HashSet<UnitId>>();
        foreach (var h in due)
        {
            var (cell, config) = execution.Cells[h.CellIndex];
            var spec = config.Effects[h.EffectIndex];
            if (spec.Text("area", "full") == "split" && _state.UnitAt(cell) is { IsAlive: true } t && EffectResolver.Matches(spec, caster, t))
            {
                var key = (h.HitIndex, spec.Text("group", $"{h.CellIndex}.{h.EffectIndex}"));
                if (!shares.TryGetValue(key, out var set))
                {
                    set = new HashSet<UnitId>();
                    shares[key] = set;
                }

                set.Add(t.Id);
            }
        }

        foreach (var h in due)
        {
            var (cell, config) = execution.Cells[h.CellIndex];
            var spec = config.Effects[h.EffectIndex];
            var scale = spec.Text("area", "full") == "split" && shares.TryGetValue((h.HitIndex, spec.Text("group", $"{h.CellIndex}.{h.EffectIndex}")), out var group) && group.Count > 0
                ? 1.0 / group.Count
                : 1.0;
            var targets = new List<UnitId>(1);
            if (_state.UnitAt(cell) is { IsAlive: true } target
                && _resolver.Apply(new EffectInvocation(spec, caster, target, tick, EffectSource.Skill, execution.Action.SkillId) { Scale = scale, ActionId = execution.Action.Id, HitIndex = h.HitIndex }))
            {
                targets.Add(target.Id);
                if (!hitNow.Contains(target.Id))
                {
                    hitNow.Add(target.Id);
                }
            }

            if (execution.Hits.Count > 1)
            {
                _events.Add(new SkillHit(tick, caster.Id, execution.Action.Id, h.HitIndex, cell, targets));
            }
        }

        if (execution.Done)
        {
            _state.MarkExecuted(execution.Action, tick);
            _state.SetActiveSkill(caster.Id, null);
            ClearPending(caster, execution.Action);
            _events.Add(new SkillCompleted(tick, caster.Id, execution.Action.Id, execution.Action.SkillId));
        }

        return hitNow;
    }

    private void ClearPending(UnitState caster, SkillAction action)
    {
        if (ReferenceEquals(caster.PendingSkill, action))
        {
            _state.SetPendingSkill(caster.Id, null);
        }
    }
}
