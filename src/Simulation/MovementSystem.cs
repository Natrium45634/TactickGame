using System;
using System.Collections.Generic;
using HexTactics.Core.Hex;
using HexTactics.Core.Motion;
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
    private readonly SimulationSettings _settings;
    private readonly HexLayout _layout;

    public MovementSystem(GameState state, List<SimulationEvent> events, SimulationSettings? settings = null, HexLayout? layout = null)
    {
        _state = state;
        _events = events;
        _settings = settings ?? SimulationSettings.Default;
        _layout = layout ?? new HexLayout(1.0);
    }

    public string Name => "Movement";

    private readonly Dictionary<UnitId, IReadOnlyList<HexCoord>> _teamPaths = new();
    private long _currentTick;

    public void Tick(SimulationContext context)
    {
        _currentTick = context.Tick;
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
            // At rest: the next step starts from standstill (no momentum carried over).
            m.Heading = null;
            m.HeadingControl = null;
            return;
        }

        var from = m.StepFrom!.Value;
        var to = m.StepTo!.Value;
        if (unit.Orders.Version != m.StepOrdersVersion)
        {
            RetargetIfNeeded(unit, from, to); // a new order mid-step: re-plan the rest of the curve from here, no jump
        }

        m.Progress = Math.Min(1.0, m.Progress + (unit.SpeedHexesPerSecond * dt));
        var position = m.PositionAtProgress(m.Progress) ?? _layout.HexToPlane(from);
        m.Position = position;

        // One deterministic rule (spec part 3 §2.3): the logical cell switches when the body crosses the shared edge,
        // i.e. as soon as its position lies in `to` — drawn position and occupancy never disagree.
        if (!m.CellSwitched && (_layout.PlaneToHex(position) == to || m.Progress >= 1.0 - 1e-9))
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
            if (m.Plan is { } done)
            {
                m.Position = done.End;
                m.Heading = done.EndTangent;
                m.HeadingControl = done.EndControl;
            }

            m.Plan = null;
            m.StepLookahead = null;
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

        // Stepped aside for a teammate: stay there until it has gone past (no ping-pong back into its way).
        if (m.YieldFor is not null && unit.Cell != m.YieldVacated && !YieldReleased(unit, tick))
        {
            m.PathCells.Clear();
            m.Status = MovementStatus.Waiting;
            m.WaitReason = WaitReason.Yielding;
            return false;
        }

        // Command order: wait at the current КТД until the whole team has arrived (detours are never held back).
        var group = unit.Orders.GroupId is { } groupId ? _state.FindGroup(groupId) : null;
        if (group is not null && !target.IsDetour && GroupCoordinator.IsAheadOfTeam(_state, group, unit))
        {
            m.PathCells.Clear();
            m.WaitingSinceTick = null;
            m.Status = MovementStatus.Waiting;
            m.WaitReason = WaitReason.TeamStage;
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
                if (m.WaitReason == WaitReason.Teammate)
                {
                    WaitForTeammates(unit, target, tick); // only the yield timer runs while nothing changes
                }

                return false; // nothing changed since the last failed attempt
            }

            var path = Pathfinder.FindPath(_state.Grid, unit.Cell, target.Cell, _state.ObstaclesFor(unit.Id));
            m.PathWaypointId = target.Id;
            if (path is null)
            {
                if (group is not null && !IsHeldByOutsider(unit, target.Cell)
                    && Pathfinder.FindPath(_state.Grid, unit.Cell, target.Cell, GroupCoordinator.ObstaclesIgnoringTeammates(_state, unit)) is { } teamPath)
                {
                    // Only teammates close the way: a normal moment of a group move — wait quietly (re-planned whenever
                    // occupancy changes) and, if it lasts, ask one of them to step aside (spec part 2 §6.2).
                    m.PathCells.Clear();
                    m.BlockedAtTopologyVersion = _state.TopologyVersion;
                    if (m.WaitReason != WaitReason.Teammate || m.Status != MovementStatus.Waiting)
                    {
                        m.WaitingSinceTick = tick;
                    }

                    m.Status = MovementStatus.Waiting;
                    m.WaitReason = WaitReason.Teammate;
                    _teamPaths[unit.Id] = teamPath;
                    WaitForTeammates(unit, target, tick);
                    return false;
                }

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
            m.WaitReason = WaitReason.None;
            m.WaitingSinceTick = null;
            _teamPaths.Remove(unit.Id);
        }

        var next = m.PathCells[0];
        if (_state.IsReservedByOther(next, unit.Id))
        {
            m.Status = MovementStatus.Waiting;
            m.WaitReason = WaitReason.Reservation;
            return false;
        }

        m.WaitReason = WaitReason.None;

        _state.Reserve(next, unit.Id);
        m.StepFrom = unit.Cell;
        m.StepTo = next;
        m.Progress = 0;
        m.CellSwitched = false;
        m.Status = MovementStatus.Moving;

        // Smooth curve of this step (spec part 3 §2.2): it turns towards the next hex of the path unless the unit stops
        // in `next` (a waypoint is a hard anchor — its centre is reached).
        var lookahead = next != target.Cell && m.PathCells.Count >= 2 && m.PathCells[0] == next ? m.PathCells[1] : (HexCoord?)null;
        var start = m.Position ?? _layout.HexToPlane(unit.Cell);
        var plan = StepGeometry.Build(_layout, unit.Cell, next, start, m.Heading, m.HeadingControl, lookahead);
        m.Plan = plan;
        m.PlanProgressStart = 0;
        m.SwitchAt = plan.Curve.Length < 1e-9 ? 0.5 : Math.Clamp(plan.SwitchArc / plan.Curve.Length, 0, 1);
        m.StepLookahead = lookahead;
        m.StepOrdersVersion = unit.Orders.Version;

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

        if (unit.Orders.IsEmpty && m.Status != MovementStatus.Idle)
        {
            m.Status = MovementStatus.Idle;
            m.BlockReason = BlockReason.None;
            m.PathCells.Clear();
            m.PathWaypointId = null;
            _events.Add(new MovementFinished(tick, unit.Id, unit.Cell));
        }
    }

    /// <summary>
    /// The unit's orders changed during a step: find the hex it will continue to after <paramref name="to"/> and, if that
    /// differs from what the running curve turns towards, re-plan the rest of the curve from the current position.
    /// </summary>
    private void RetargetIfNeeded(UnitState unit, HexCoord from, HexCoord to)
    {
        var m = unit.Motion;
        m.StepOrdersVersion = unit.Orders.Version;
        HexCoord? lookahead = null;
        if (unit.Orders.Peek() is { } target && target.Cell != to
            && Pathfinder.FindPath(_state.Grid, to, target.Cell, _state.ObstaclesFor(unit.Id)) is { Count: > 1 } path)
        {
            lookahead = path[1];
        }

        if (lookahead == m.StepLookahead || m.Plan is null)
        {
            return;
        }

        var p = m.Progress;
        var plan = StepGeometry.Retarget(_layout, from, to, m.PositionAtProgress(p) ?? _layout.HexToPlane(from), m.TangentAtProgress(p), m.CellSwitched, lookahead);
        m.Plan = plan;
        m.PlanProgressStart = p;
        if (!m.CellSwitched)
        {
            m.SwitchAt = plan.Curve.Length < 1e-9 ? p : p + ((plan.SwitchArc / plan.Curve.Length) * (1.0 - p));
        }

        m.StepLookahead = lookahead;
    }

    /// <summary>True if <paramref name="cell"/> is occupied by a unit that is not a teammate in the same command order.</summary>
    private bool IsHeldByOutsider(UnitState unit, HexCoord cell) =>
        _state.UnitAt(cell) is { } occupant && occupant.Id != unit.Id && !GroupCoordinator.AreTeammates(_state, unit, occupant);

    /// <summary>
    /// The way of <paramref name="unit"/> is closed only by teammates. After <see cref="SimulationSettings.TeammateYieldDelaySeconds"/>
    /// the first standing teammate on the path is asked to step aside to a free neighbouring hex off the path (it then
    /// carries on with its own route, or returns to its final place and facing). If nobody can step aside for
    /// <see cref="SimulationSettings.TeammateWaitLimitSeconds"/>, the unit reports a blocked route instead of waiting silently.
    /// </summary>
    private void WaitForTeammates(UnitState unit, HexTactics.Core.Orders.Waypoint target, long tick)
    {
        var m = unit.Motion;
        if (m.WaitingSinceTick is not { } since || !_teamPaths.TryGetValue(unit.Id, out var path))
        {
            return;
        }

        var waited = (tick - since) * _settings.TickDuration;
        if (waited < _settings.TeammateYieldDelaySeconds)
        {
            return;
        }

        if (TryRequestYield(unit, path))
        {
            m.WaitingSinceTick = tick; // give the teammate time to move before asking again
            return;
        }

        if (waited >= _settings.TeammateWaitLimitSeconds)
        {
            Block(unit, target, tick, BlockReason.BlockedByTeammates);
        }
    }

    /// <summary>
    /// True once the unit that asked this one to step aside has gone past the vacated hex (or no longer needs it, or a
    /// safety timeout elapsed); clears the yield state.
    /// </summary>
    private bool YieldReleased(UnitState unit, long tick)
    {
        var m = unit.Motion;
        var vacated = m.YieldVacated!.Value;
        var requester = _state.FindUnit(m.YieldFor!.Value);
        var released = requester is null
            || !GroupCoordinator.AreTeammates(_state, unit, requester)
            || (tick - m.YieldSinceTick) * _settings.TickDuration >= _settings.TeammateWaitLimitSeconds
            || (requester.Cell != m.YieldRequesterCell
                && requester.Cell != vacated
                && requester.Motion.StepTo != vacated
                && !System.Linq.Enumerable.Contains(requester.Motion.Path, vacated));
        if (released)
        {
            m.YieldFor = null;
            m.YieldVacated = null;
            m.YieldRequesterCell = null;
        }

        return released;
    }

    private bool TryRequestYield(UnitState unit, IReadOnlyList<HexCoord> path)
    {
        var onPath = new HashSet<HexCoord>(path);
        for (var i = 1; i < path.Count; i++)
        {
            if (_state.UnitAt(path[i]) is not { } mate || !GroupCoordinator.AreTeammates(_state, unit, mate))
            {
                continue;
            }

            if (mate.Motion.IsStepping || mate.Orders.Peek() is { IsDetour: true })
            {
                return true; // it is already moving / stepping aside: just keep waiting
            }

            // Free neighbour of the teammate, off the waiting unit's path; closest to where the teammate is heading.
            var heading = mate.Orders.Peek()?.Cell ?? mate.Cell;
            HexCoord? best = null;
            var bestScore = int.MaxValue;
            foreach (var dir in HexDirections.All)
            {
                var c = mate.Cell.Neighbor(dir);
                if (!_state.Grid.Contains(c) || !_state.Grid[c].IsWalkable || onPath.Contains(c) || _state.IsOccupied(c) || _state.IsReserved(c))
                {
                    continue;
                }

                var score = c.DistanceTo(heading);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = c;
                }
            }

            if (best is not { } aside)
            {
                return false;
            }

            var group = _state.FindGroup(mate.Orders.GroupId!.Value)!;
            if (mate.Orders.IsEmpty)
            {
                // Finished member: step aside and come back to its place, then look where the order said.
                _state.InsertDetours(mate.Id, new[] { aside, mate.Cell });
                if (group.FinalFacings.TryGetValue(mate.Id, out var facing))
                {
                    _state.SetFinalFacing(mate.Id, facing);
                }
            }
            else
            {
                _state.InsertDetours(mate.Id, new[] { aside });
            }

            mate.Motion.PathCells.Clear();
            mate.Motion.YieldFor = unit.Id;
            mate.Motion.YieldVacated = mate.Cell;
            mate.Motion.YieldRequesterCell = unit.Cell;
            mate.Motion.YieldSinceTick = _currentTick;
            _events.Add(new TeammateYielded(_currentTick, mate.Id, unit.Id, aside));
            return true;
        }

        return false;
    }

    private void Block(UnitState unit, HexTactics.Core.Orders.Waypoint target, long tick, BlockReason? forced = null)
    {
        var m = unit.Motion;
        var reason = forced ?? (_state.IsOccupiedByOther(target.Cell, unit.Id) ? BlockReason.TargetOccupied : BlockReason.NoPath);
        m.WaitReason = WaitReason.None;
        m.WaitingSinceTick = null;
        _teamPaths.Remove(unit.Id);
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
