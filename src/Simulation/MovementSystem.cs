using System.Collections.Generic;
using HexTactics.Core.Hex;
using HexTactics.Core.Pathfinding;
using HexTactics.Core.Units;

namespace HexTactics.Simulation;

/// <summary>
/// Executes waypoint queues hex by hex (spec §6.3).
/// <list type="bullet">
/// <item>Path: A* from the current hex to the current waypoint; other units are obstacles. Re-planned at a hex
/// centre when the topology changed, the waypoint changed or the next hex became occupied.</item>
/// <item>Step: the next hex is reserved; progress grows by speed × tick; at progress 0.5 the logical cell
/// switches to the next hex (the single, documented transition moment) and the old hex stays reserved until
/// the step ends, so a unit is never in two logical cells and two bodies never share a hex.</item>
/// <item>Facing follows the direction of every step.</item>
/// <item>No path: the unit stops on a hex centre, keeps its orders and reports the reason; planning is retried
/// whenever the topology changes. Units are never teleported and waypoints are never skipped.</item>
/// </list>
/// </summary>
internal sealed class MovementSystem : ISimulationSystem
{
    private readonly GameState _state;
    private readonly List<SimulationEvent> _events;

    public MovementSystem(GameState state, List<SimulationEvent> events)
    {
        _state = state;
        _events = events;
    }

    public string Name => "Movement";

    public void Tick(SimulationContext context)
    {
        // Units are processed in id order: the outcome of conflicts is deterministic.
        foreach (var unit in new List<UnitState>(_state.Units))
        {
            TickUnit(unit, context.Tick, context.DeltaSeconds);
        }
    }

    private void TickUnit(UnitState unit, long tick, double dt)
    {
        if (!unit.IsActive)
        {
            return; // placed from the roster, waiting for its facing: does not act
        }

        var m = unit.Motion;
        if (!m.IsStepping && !TryStartStep(unit, tick))
        {
            return;
        }

        var from = m.StepFrom!.Value;
        var to = m.StepTo!.Value;
        m.Progress += unit.SpeedHexesPerSecond * dt;

        if (!m.CellSwitched && m.Progress >= 0.5)
        {
            // The one moment the logical cell changes: the destination reservation becomes occupancy,
            // the origin becomes a trailing reservation until the body has fully left it.
            _state.ReleaseReservation(to, unit.Id);
            _state.MoveUnit(unit.Id, to);
            _state.Reserve(from, unit.Id);
            m.CellSwitched = true;
            _events.Add(new UnitEnteredCell(tick, unit.Id, from, to));
        }

        if (m.Progress >= 1.0 - 1e-9)
        {
            _state.ReleaseReservation(from, unit.Id);
            m.StepFrom = null;
            m.StepTo = null;
            m.Progress = 0;
            m.CellSwitched = false;
            if (m.PathCells.Count > 0 && m.PathCells[0] == to)
            {
                m.PathCells.RemoveAt(0);
            }

            ArriveAtCentre(unit, tick);
        }
    }

    /// <summary>At a hex centre: completes reached waypoints, plans and starts the next step.</summary>
    private bool TryStartStep(UnitState unit, long tick)
    {
        var m = unit.Motion;
        ArriveAtCentre(unit, tick);
        if (unit.Orders.Peek() is not { } target)
        {
            return false;
        }

        var needsPlan = m.PathCells.Count == 0
            || m.PathWaypointId != target.Id
            || m.PathTopologyVersion != _state.TopologyVersion
            || _state.IsOccupiedByOther(m.PathCells[0], unit.Id);

        if (needsPlan)
        {
            if (m.Status is MovementStatus.Blocked or MovementStatus.Waiting && m.PathCells.Count == 0
                && m.BlockedAtTopologyVersion == _state.TopologyVersion && m.PathWaypointId == target.Id)
            {
                return false; // nothing changed since the last failed attempt
            }

            var path = Pathfinder.FindPath(_state.Grid, unit.Cell, target.Cell, _state.ObstaclesFor(unit.Id));
            m.PathWaypointId = target.Id;
            if (path is null)
            {
                Block(unit, target, tick);
                return false;
            }

            m.PathCells.Clear();
            for (var i = 1; i < path.Count; i++)
            {
                m.PathCells.Add(path[i]);
            }

            m.PathTopologyVersion = _state.TopologyVersion;
            if (m.Status == MovementStatus.Blocked)
            {
                _events.Add(new UnitUnblocked(tick, unit.Id));
            }

            m.BlockReason = BlockReason.None;
            m.Status = MovementStatus.Moving;
        }

        var next = m.PathCells[0];
        if (_state.IsReservedByOther(next, unit.Id))
        {
            m.Status = MovementStatus.Waiting;
            return false;
        }

        _state.Reserve(next, unit.Id);
        m.StepFrom = unit.Cell;
        m.StepTo = next;
        m.Progress = 0;
        m.CellSwitched = false;
        m.Status = MovementStatus.Moving;

        if (unit.Cell.DirectionTo(next) is { } dir && dir != unit.Facing)
        {
            _state.SetFacing(unit.Id, dir);
            _events.Add(new FacingChanged(tick, unit.Id, dir));
        }

        _events.Add(new StepStarted(tick, unit.Id, unit.Cell, next));
        return true;
    }

    private void ArriveAtCentre(UnitState unit, long tick)
    {
        var m = unit.Motion;
        while (unit.Orders.Peek() is { } wp && wp.Cell == unit.Cell)
        {
            _state.CompleteCurrentWaypoint(unit.Id);
            m.PathCells.Clear();
            _events.Add(new WaypointReached(tick, unit.Id, wp));
        }

        if (unit.Orders.IsEmpty && unit.Orders.FinalFacing is { } finalFacing)
        {
            // Mandatory final facing of the completed order, regardless of the direction of the last step.
            _state.SetFinalFacing(unit.Id, null);
            if (unit.Facing != finalFacing)
            {
                _state.SetFacing(unit.Id, finalFacing);
                _events.Add(new FacingChanged(tick, unit.Id, finalFacing));
            }

            _events.Add(new FinalFacingApplied(tick, unit.Id, finalFacing));
        }

        if (unit.Orders.IsEmpty && unit.Orders.GroupId is not null)
        {
            _state.SetQueueGroup(unit.Id, null); // this member has completed its part of the command order
        }

        if (unit.Orders.IsEmpty && m.Status != MovementStatus.Idle)
        {
            m.Status = MovementStatus.Idle;
            m.BlockReason = BlockReason.None;
            m.PathCells.Clear();
            m.PathWaypointId = null;
            _events.Add(new MovementFinished(tick, unit.Id, unit.Cell));
        }
    }

    /// <summary>True if <paramref name="cell"/> is occupied by another member of the unit's command order.</summary>
    private bool IsHeldByTeammate(UnitState unit, HexCoord cell)
    {
        if (unit.Orders.GroupId is not { } group || _state.UnitAt(cell) is not { } occupant || occupant.Id == unit.Id)
        {
            return false;
        }

        return occupant.Orders.GroupId == group || (_state.FindGroup(group)?.IsMember(occupant.Id) ?? false);
    }

    private void Block(UnitState unit, HexTactics.Core.Orders.Waypoint target, long tick)
    {
        var m = unit.Motion;
        if (IsHeldByTeammate(unit, target.Cell))
        {
            // A member of the same command order stands on (or passes through) this member's slot: that is a normal,
            // temporary situation inside a group move — wait quietly and re-plan when occupancy changes.
            m.PathCells.Clear();
            m.BlockedAtTopologyVersion = _state.TopologyVersion;
            m.Status = MovementStatus.Waiting;
            return;
        }

        var reason = _state.IsOccupiedByOther(target.Cell, unit.Id) ? BlockReason.TargetOccupied : BlockReason.NoPath;
        m.PathCells.Clear();
        m.BlockedAtTopologyVersion = _state.TopologyVersion;
        if (m.Status != MovementStatus.Blocked || m.BlockReason != reason)
        {
            m.Status = MovementStatus.Blocked;
            m.BlockReason = reason;
            _events.Add(new UnitBlocked(tick, unit.Id, target, reason));
        }
    }
}
