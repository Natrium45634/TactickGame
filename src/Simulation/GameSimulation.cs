using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Hex;
using HexTactics.Core.Map;
using HexTactics.Core.Orders;
using HexTactics.Core.Roster;
using HexTactics.Core.Spawning;
using HexTactics.Core.Units;

namespace HexTactics.Simulation;

/// <summary>
/// Owns the authoritative <see cref="GameState"/> and is the only entry point that changes it.
/// Runs without any graphics, so every rule can be tested headless.
/// </summary>
public sealed class GameSimulation
{
    private readonly List<SimulationEvent> _events = new();
    private readonly List<ISimulationSystem> _systems = new();
    private readonly SimulationClock _clock;
    private readonly SimulationContext _context;

    public GameSimulation(MapDefinition map, SimulationSettings? settings = null, RosterState? roster = null)
    {
        State = new GameState(map, roster);
        Settings = settings ?? SimulationSettings.Default;
        _clock = new SimulationClock(Settings.TickDuration);
        _context = new SimulationContext(State, _events);
        AddSystem(new MovementSystem(State, _events, Settings, Layout));
        Damage = new DamageResolver(State, _events, Settings);
        Effects = new EffectResolver(State, _events, Damage);
        AddSystem(new SkillSystem(State, _events, Settings, Effects));
        AddSystem(new AttackSystem(State, _events, Settings, Damage));
        AddSystem(new TileEffectSystem(State, Settings, Effects));
        AddSystem(new StatusSystem(State, _events, Effects));
    }

    /// <summary>The single executor of configured effects (spec part 4 §6.3).</summary>
    public EffectResolver Effects { get; }

    /// <summary>The single damage calculation (spec part 5 §5).</summary>
    public DamageResolver Damage { get; }

    /// <summary>Map-plane geometry of the simulation (hex size 1): unit positions and motion curves use it.</summary>
    public HexLayout Layout { get; } = new(1.0);

    /// <summary>Continuous position of a unit on the map plane (the centre of its cell until it has moved).</summary>
    public PlaneVector PositionOf(UnitState unit) => unit.Motion.Position ?? Layout.HexToPlane(unit.Cell);

    /// <summary>Systems executed every tick, in order.</summary>
    public IReadOnlyList<ISimulationSystem> Systems => _systems;

    /// <summary>True while the tactical pause is active: no ticks run, game time stands still.</summary>
    public bool IsPaused { get; private set; }

    /// <summary>Registers a per-tick rule system (future abilities, attacks, waves…).</summary>
    public void AddSystem(ISimulationSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        _systems.Add(system);
    }

    /// <summary>
    /// Tactical pause. While paused, <see cref="Advance"/> does not accumulate time, so resuming continues
    /// exactly from the saved state without a jump. Commands (orders, spawns) are still accepted.
    /// </summary>
    public void SetPaused(bool paused)
    {
        if (IsPaused == paused)
        {
            return;
        }

        IsPaused = paused;
        _events.Add(new PauseChanged(Tick, paused));
    }

    public void TogglePause() => SetPaused(!IsPaused);

    public GameState State { get; }

    public SimulationSettings Settings { get; }

    /// <summary>Number of fixed simulation steps executed so far.</summary>
    public long Tick { get; private set; }

    /// <summary>Game time in seconds (Tick × tick duration).</summary>
    public double GameTime => Tick * Settings.TickDuration;

    /// <summary>Fraction of the next tick already elapsed (for smooth rendering between ticks only).</summary>
    public double InterpolationAlpha => _clock.Alpha;

    /// <summary>
    /// Feeds real elapsed time (e.g. a render frame's delta) and runs the due fixed ticks.
    /// Returns the number of ticks executed. Does nothing while paused (time is not accumulated).
    /// </summary>
    public int Advance(double realSeconds)
    {
        if (IsPaused)
        {
            return 0;
        }

        var ticks = _clock.Accumulate(realSeconds);
        for (var i = 0; i < ticks; i++)
        {
            Step();
        }

        return ticks;
    }

    /// <summary>
    /// Runs exactly one fixed simulation tick (all systems). Low-level: used by <see cref="Advance"/> and
    /// tests; it ignores the pause flag on purpose.
    /// </summary>
    public void Step()
    {
        Tick++;
        _context.Tick = Tick;
        _context.DeltaSeconds = Settings.TickDuration;
        foreach (var system in _systems)
        {
            system.Tick(_context);
        }

        // Spec part 5 §8.2: reactions to continuous damage are summed and sent once per game second.
        Damage.AfterTick(Tick);

        // Spec part 3 §7 step 5: deaths are processed once, after all actions of the tick, in id order.
        foreach (var dead in State.RemoveDead())
        {
            _events.Add(new UnitDied(Tick, dead.Id, dead.Side, dead.Cell));
        }

        GroupCoordinator.CompleteFinished(State);
        PruneGroups();
    }

    private void PruneGroups()
    {
        foreach (var id in State.PruneGroups())
        {
            _events.Add(new GroupOrderFinished(Tick, id));
        }
    }

    /// <summary>Creates a unit using one of the three spawn methods. Never relocates or removes existing units.</summary>
    public SpawnResult Spawn(SpawnRequest request)
    {
        var resolution = SpawnRules.Resolve(State, request);
        if (!resolution.IsValid)
        {
            var offending = resolution.Cell ?? request.Cell;
            var occupant = offending is { } c ? State.UnitAt(c)?.Id ?? State.ReservedBy(c) : null;
            _events.Add(new SpawnRejected(Tick, request, resolution.Error, offending, occupant));
            return new SpawnResult(null, offending, resolution.Error, occupant);
        }

        var cell = resolution.Cell!.Value;
        var facing = request.Facing ?? SpawnRules.DefaultFacing(State.Grid, cell);
        var stats = request.Profile?.Stats ?? Settings.DefaultStats;
        var unit = State.CreateUnit(request.Side, cell, facing, stats.MoveSpeedHexPerSecond, stats: stats);
        State.SetSkills(unit.Id, request.Profile?.Skills ?? Settings.DefaultSkillSet);
        State.SetDefinition(unit.Id, request.Profile?.DefinitionId ?? Settings.DefaultUnitId);
        _events.Add(new UnitSpawned(Tick, unit.Id, cell, request.Method));
        return new SpawnResult(unit.Id, cell, SpawnError.None, null);
    }

    // ------------------------------------------------------------------ roster placement (spec part 2 §5)

    /// <summary>The roster card starts being dragged over the map (<see cref="RosterEntryState.PlacementPreview"/>).</summary>
    public CommandResult BeginRosterPlacement(int entryId)
    {
        var entry = State.Roster.Find(entryId);
        if (entry is null || entry.State != RosterEntryState.AvailableInRoster)
        {
            return CommandResult.Fail("not available");
        }

        State.Roster.Set(entry, RosterEntryState.PlacementPreview);
        return CommandResult.Ok;
    }

    /// <summary>The drag was cancelled or dropped on a forbidden hex: the card returns to the roster unchanged.</summary>
    public void CancelRosterPlacement(int entryId)
    {
        if (State.Roster.Find(entryId) is { State: RosterEntryState.PlacementPreview } entry)
        {
            State.Roster.Set(entry, RosterEntryState.AvailableInRoster);
        }
    }

    /// <summary>
    /// Places the roster card's unit on an allowed hex (player base or deploy zone, walkable, free). The unit appears
    /// WITHOUT facing (<see cref="UnitLifecycle.PlacedPendingFacing"/>): it does nothing until its first order with a
    /// final facing is applied. On rejection nothing is created and the card is available again.
    /// </summary>
    public PlacementResult PlaceFromRoster(int entryId, HexCoord cell)
    {
        var entry = State.Roster.Find(entryId);
        PlacementError error;
        if (entry is null)
        {
            error = PlacementError.UnknownEntry;
        }
        else if (entry.State is not (RosterEntryState.AvailableInRoster or RosterEntryState.PlacementPreview))
        {
            error = PlacementError.EntryNotAvailable;
        }
        else
        {
            error = PlacementRules.ValidateCell(State, cell);
        }

        if (error != PlacementError.None)
        {
            if (entry is not null)
            {
                CancelRosterPlacement(entry.Id);
            }

            _events.Add(new PlacementRejected(Tick, entryId, cell, error));
            return new PlacementResult(null, cell, error);
        }

        var stats = entry!.Profile?.Stats ?? Settings.DefaultStats;
        var unit = State.CreateUnit(Side.Player, cell, null, stats.MoveSpeedHexPerSecond, UnitLifecycle.PlacedPendingFacing, entryId, stats);
        State.SetSkills(unit.Id, entry.Profile?.Skills ?? Settings.DefaultSkillSet);
        State.SetDefinition(unit.Id, entry.Profile?.DefinitionId ?? Settings.DefaultUnitId);
        State.Roster.Set(entry!, RosterEntryState.PlacedPendingFacing, unit.Id);
        _events.Add(new UnitPlacedFromRoster(Tick, entryId, unit.Id, cell));
        return new PlacementResult(unit.Id, cell, PlacementError.None);
    }

    /// <summary>Gives a pending unit its first facing; it becomes active (and its roster card too).</summary>
    private void ActivateIfPending(UnitState unit, HexDirection facing)
    {
        if (unit.IsActive)
        {
            return;
        }

        State.Activate(unit.Id, facing);
        if (State.Roster.FindByUnit(unit.Id) is { } entry)
        {
            State.Roster.Set(entry, RosterEntryState.Active);
        }

        _events.Add(new UnitActivated(Tick, unit.Id, facing));
    }

    /// <summary>
    /// Immediate use of a skill (spec part 3 §5.2): planned now (also on pause) and fired once as soon as the unit stands
    /// on a hex centre — from that hex, towards <paramref name="direction"/>. Replaces a previous unfired immediate use.
    /// </summary>
    public CommandResult UseSkill(UnitId id, string skillId, HexDirection direction)
    {
        var unit = State.FindUnit(id);
        if (unit is not { IsAlive: true })
        {
            return CommandResult.Fail($"Unit {id} does not exist.");
        }

        if (!unit.IsActive)
        {
            return CommandResult.Fail("The unit has no facing yet.");
        }

        if (!Enum.IsDefined(direction) || !Settings.Skills.ContainsKey(skillId) || !unit.Skills.Contains(skillId))
        {
            return CommandResult.Fail($"Unit {id} has no skill {skillId}.");
        }

        var action = State.NewSkillAction(skillId, direction);
        State.SetPendingSkill(id, action);
        _events.Add(new SkillPlanned(Tick, id, action.Id, skillId, direction));
        return CommandResult.Ok;
    }

    /// <summary>
    /// Plans a skill at a confirmed, not yet reached waypoint of the unit (spec part 3 §6.2: the edit of a confirmed route
    /// is applied only on confirmation — cancelling the aiming leaves the route as it was). Replaces a skill planned there.
    /// </summary>
    public CommandResult AssignWaypointSkill(UnitId id, int waypointId, string skillId, HexDirection direction)
    {
        var unit = State.FindUnit(id);
        if (unit is not { IsAlive: true, IsActive: true })
        {
            return CommandResult.Fail($"Unit {id} cannot plan skills.");
        }

        var index = unit.Orders.IndexOf(waypointId);
        if (index < 0 || unit.Orders.Items[index].IsDetour)
        {
            return CommandResult.Fail($"Waypoint {waypointId} is not in the route of {id}.");
        }

        if (unit.Orders.GroupId is not null)
        {
            return CommandResult.Fail($"Unit {id} follows a command order: its waypoints carry no skills.");
        }

        if (!Enum.IsDefined(direction) || !Settings.Skills.ContainsKey(skillId) || !unit.Skills.Contains(skillId))
        {
            return CommandResult.Fail($"Unit {id} has no skill {skillId}.");
        }

        var action = State.NewSkillAction(skillId, direction, waypointId);
        State.SetWaypointSkill(id, waypointId, action);
        _events.Add(new SkillPlanned(Tick, id, action.Id, skillId, direction, waypointId));
        return CommandResult.Ok;
    }

    /// <summary>Removes the skill planned at one of the unit's waypoints (the waypoint itself stays).</summary>
    public CommandResult RemoveWaypointSkill(UnitId id, int waypointId)
    {
        var unit = State.FindUnit(id);
        var index = unit?.Orders.IndexOf(waypointId) ?? -1;
        if (unit is null || index < 0 || unit.Orders.Items[index].Skill is null)
        {
            return CommandResult.Fail("No skill planned there.");
        }

        State.SetWaypointSkill(id, waypointId, null);
        _events.Add(new WaypointSkillRemoved(Tick, id, waypointId));
        return CommandResult.Ok;
    }

    /// <summary>Drops the unit's unfired immediate skill use.</summary>
    public void CancelPendingSkill(UnitId id)
    {
        if (State.FindUnit(id) is { PendingSkill: not null })
        {
            State.SetPendingSkill(id, null);
        }
    }

    /// <summary>Turns a unit in place to one of the six directions.</summary>
    public CommandResult SetFacing(UnitId id, HexDirection facing)
    {
        var unit = State.FindUnit(id);
        if (unit is null)
        {
            return CommandResult.Fail($"Unit {id} does not exist.");
        }

        if (!Enum.IsDefined(facing))
        {
            return CommandResult.Fail($"Invalid facing {(int)facing}.");
        }

        if (!unit.IsActive)
        {
            ActivateIfPending(unit, facing);
            _events.Add(new FacingChanged(Tick, id, facing));
            return CommandResult.Ok;
        }

        if (unit.Facing != facing)
        {
            State.SetFacing(id, facing);
            _events.Add(new FacingChanged(Tick, id, facing));
        }

        return CommandResult.Ok;
    }

    /// <summary>
    /// The hex from which new orders of the unit are planned: its logical cell while standing, or — while a
    /// step is in progress — the hex it is stepping into (the nearest safe hex centre). The current step
    /// is always completed first, so a new order never breaks the running interpolation.
    /// </summary>
    public HexCoord CommandAnchor(UnitId id)
    {
        var unit = State.GetUnit(id);
        return unit.Motion.StepTo ?? unit.Cell;
    }

    /// <summary>
    /// Adds a waypoint. <see cref="OrderMode.Replace"/> (drag from the unit) replaces the pending
    /// queue; <see cref="OrderMode.Append"/> (drag from the last waypoint) extends it. Invalid targets
    /// are rejected and the queue stays as it was.
    /// </summary>
    public OrderResult IssueMoveOrder(UnitId id, HexCoord target, OrderMode mode)
    {
        var unit = State.FindUnit(id);
        if (unit is null)
        {
            return new OrderResult(null, WaypointError.UnknownUnit);
        }

        var source = WaypointRules.SegmentSource(unit, mode, CommandAnchor(id));
        var error = WaypointRules.ValidateTargetWithPath(State, id, source, target);
        if (error != WaypointError.None)
        {
            _events.Add(new OrderRejected(Tick, id, target, error));
            return new OrderResult(null, error);
        }

        if (mode == OrderMode.Replace)
        {
            State.ClearWaypoints(id);
            State.SetQueueGroup(id, null);
        }

        var waypoint = State.AppendWaypoint(id, target);
        _events.Add(new OrderIssued(Tick, id, waypoint, mode));
        return new OrderResult(waypoint, WaypointError.None);
    }

    /// <summary>
    /// Applies a complete confirmed order as one transaction (spec part 2 §4.3, §9.5): every segment of the chain
    /// is validated from the unit's command anchor; if any is impossible the whole order is rejected and the
    /// previous route stays untouched. Otherwise the pending queue is replaced and the mandatory final facing is
    /// stored; it is applied when the last waypoint is reached (immediately at the next hex centre for a turn in
    /// place). A running step is always completed first — no teleporting. While paused, the order simply waits.
    /// </summary>
    public OrderApplyResult ApplyOrder(IndividualOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (State.FindUnit(order.Unit) is not { IsAlive: true } unit)
        {
            return new OrderApplyResult(false, 0, WaypointError.UnknownUnit);
        }

        if (!Enum.IsDefined(order.FinalFacing))
        {
            throw new ArgumentOutOfRangeException(nameof(order), "Invalid final facing.");
        }

        UnitState? target = null;
        if (order.AttackTarget is { } targetId)
        {
            target = State.FindUnit(targetId);
            if (target is null || !target.IsAlive || target.Side == unit.Side)
            {
                _events.Add(new OrderApplyRejected(Tick, order.Unit, order.Waypoints.Count, WaypointError.InvalidTarget));
                return new OrderApplyResult(false, order.Waypoints.Count, WaypointError.InvalidTarget);
            }
        }

        if (order.WaypointSkills is { } planned)
        {
            foreach (var (index, spec) in planned)
            {
                if (index < 0 || index >= order.Waypoints.Count || !Settings.Skills.ContainsKey(spec.SkillId) || !unit.Skills.Contains(spec.SkillId))
                {
                    _events.Add(new OrderApplyRejected(Tick, order.Unit, index, WaypointError.InvalidSkill));
                    return new OrderApplyResult(false, index, WaypointError.InvalidSkill);
                }
            }
        }

        var (failed, error) = OrderDraftValidator.ValidateChain(State, order.Unit, CommandAnchor(order.Unit), order.Waypoints);
        if (error != WaypointError.None)
        {
            _events.Add(new OrderApplyRejected(Tick, order.Unit, failed, error));
            return new OrderApplyResult(false, failed, error);
        }

        // Spec part 3 §4.1: an attack order turns the unit to its target at once (also on pause); no facing is chosen.
        var attackFacing = target is null ? (HexDirection?)null : HexDirections.Toward(unit.Cell, target.Cell) ?? order.FinalFacing;
        if (!unit.IsActive)
        {
            // First order of a placed unit: it gets a facing right away (first step of the route, or the chosen one).
            var initial = attackFacing ?? order.FinalFacing;
            if (attackFacing is null && order.Waypoints.Count > 0 && Core.Pathfinding.Pathfinder.FindPath(State.Grid, unit.Cell, order.Waypoints[0], State.ObstaclesFor(unit.Id)) is { Count: > 1 } path)
            {
                initial = unit.Cell.DirectionTo(path[1]) ?? order.FinalFacing;
            }

            ActivateIfPending(unit, initial);
        }
        else if (attackFacing is { } turn && unit.Facing != turn)
        {
            State.SetFacing(unit.Id, turn);
            _events.Add(new FacingChanged(Tick, unit.Id, turn));
        }

        State.ClearWaypoints(order.Unit);
        for (var i = 0; i < order.Waypoints.Count; i++)
        {
            var wp = State.AppendWaypoint(order.Unit, order.Waypoints[i]);
            if (order.WaypointSkills is { } skills && skills.TryGetValue(i, out var spec))
            {
                // Each planned use gets its own stable id; it fires once when the unit reaches this waypoint.
                var action = State.NewSkillAction(spec.SkillId, spec.Direction, wp.Id);
                State.SetWaypointSkill(order.Unit, wp.Id, action);
                _events.Add(new SkillPlanned(Tick, order.Unit, action.Id, spec.SkillId, spec.Direction, wp.Id));
            }
        }

        State.SetFinalFacing(order.Unit, target is null ? order.FinalFacing : null);
        State.SetAttackTarget(order.Unit, target?.Id);
        unit.Combat.Reset();
        State.SetQueueGroup(order.Unit, null); // a personal order takes the unit out of its command order
        PruneGroups();
        _events.Add(new OrderApplied(Tick, order.Unit, order.Waypoints, attackFacing ?? order.FinalFacing, target?.Id));
        return OrderApplyResult.Ok;
    }

    /// <summary>
    /// Applies a command order as one transaction (spec part 2 §6): the whole КТД chain is planned from the members'
    /// command anchors with <see cref="GroupRoutePlanner"/> (captain on every КТД, companions on neighbouring slots);
    /// if any КТД is impossible for the team as a whole nothing changes. Otherwise every member's queue is replaced by
    /// its own destinations, the captain gets the chosen final facing and the companions theirs
    /// (<see cref="CompanionFacings"/>). Members placed from the roster become active.
    /// </summary>
    public GroupOrderResult ApplyGroupOrder(GroupOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (!Enum.IsDefined(order.FinalCaptainFacing))
        {
            throw new ArgumentOutOfRangeException(nameof(order), "Invalid final facing.");
        }

        GroupChainPlan plan;
        if (order.Members.Count > 0 && order.Members[0] != order.Captain)
        {
            plan = new GroupChainPlan(Array.Empty<GroupStagePlan>(), 0, GroupPlanError.CaptainNotFirst);
        }
        else
        {
            var anchors = new List<HexCoord>(order.Members.Count);
            foreach (var member in order.Members)
            {
                anchors.Add(State.FindUnit(member) is null ? default : CommandAnchor(member));
            }

            plan = GroupRoutePlanner.PlanChain(State, order.Members, anchors, order.GroupWaypoints);
        }

        if (!plan.IsValid)
        {
            _events.Add(new GroupOrderRejected(Tick, order.Captain, plan.FailedIndex, plan.Error));
            return new GroupOrderResult(false, plan.FailedIndex, plan.Error, null);
        }

        var stages = new List<IReadOnlyList<GroupSlotAssignment>>(plan.Stages.Count);
        foreach (var stage in plan.Stages)
        {
            stages.Add(stage.Assignments);
        }

        var facings = CompanionFacingRule.Assign(order.Members, order.FinalCaptainFacing, Settings.Seed, State.NextGroupId);
        var group = State.AddGroup(order.Captain, order.Members.ToArray(), order.GroupWaypoints.ToArray(), stages, facings);
        var memberSet = new HashSet<UnitId>(order.Members);
        foreach (var member in order.Members)
        {
            var unit = State.GetUnit(member);
            var destinations = new List<HexCoord>(plan.Stages.Count);
            foreach (var stage in plan.Stages)
            {
                destinations.Add(stage.DestinationOf(member)!.Value);
            }

            if (!unit.IsActive)
            {
                var initial = facings[member];
                var path = Core.Pathfinding.Pathfinder.FindPath(State.Grid, unit.Cell, destinations[0], GroupRoutePlanner.ObstaclesFor(State, memberSet));
                if (path is { Count: > 1 })
                {
                    initial = unit.Cell.DirectionTo(path[1]) ?? initial;
                }

                ActivateIfPending(unit, initial);
            }

            State.ClearWaypoints(member);
            foreach (var cell in destinations)
            {
                State.AppendWaypoint(member, cell);
            }

            State.SetFinalFacing(member, facings[member]);
            State.SetQueueGroup(member, group.Id);
        }

        PruneGroups();
        _events.Add(new GroupOrderApplied(Tick, group.Id, order.Captain, group.Members, group.Waypoints));
        return new GroupOrderResult(true, -1, GroupPlanError.None, group.Id);
    }

    /// <summary>
    /// The facings a command order with these members would assign if confirmed now with <paramref name="captainFacing"/>
    /// (spec part 2 §6.3): the captain's choice, companions the captain's way or ±60°. Stable — depends only on the seed,
    /// the next order id and the unit ids, so the preview never re-rolls while the cursor moves.
    /// </summary>
    public IReadOnlyDictionary<UnitId, HexDirection> PreviewCompanionFacings(IReadOnlyList<UnitId> members, HexDirection captainFacing) =>
        CompanionFacingRule.Assign(members, captainFacing, Settings.Seed, State.NextGroupId);

    /// <summary>
    /// Removes the given waypoint and all following ones from the unit's queue (RMB on a confirmed ТД, spec part 2
    /// §8.2). The remaining prefix of a valid chain stays valid; the final facing is kept and applied at the new end.
    /// </summary>
    public CommandResult TruncateOrders(UnitId id, int waypointId)
    {
        var unit = State.FindUnit(id);
        if (unit is null)
        {
            return CommandResult.Fail($"Unit {id} does not exist.");
        }

        var index = unit.Orders.IndexOf(waypointId);
        if (index < 0)
        {
            return CommandResult.Fail($"Waypoint {waypointId} is not in the queue of {id}.");
        }

        var sequence = unit.Orders.Items[index].Sequence;
        State.TruncateWaypoints(id, index);
        State.SetAttackTarget(id, null); // the attack stage follows the removed waypoints
        if (unit.Orders.GroupId is not null)
        {
            // An edited route is personal: the unit leaves the command order (the rest of the team goes on without it).
            State.SetQueueGroup(id, null);
            PruneGroups();
        }
        _events.Add(new OrdersTruncated(Tick, id, sequence, unit.Orders.Count));
        return CommandResult.Ok;
    }

    /// <summary>
    /// Turns a unit that has no pending movement orders, without moving it (rotate-only gesture).
    /// </summary>
    public CommandResult RotateInPlace(UnitId id, HexDirection facing)
    {
        var unit = State.FindUnit(id);
        if (unit is null)
        {
            return CommandResult.Fail($"Unit {id} does not exist.");
        }

        if (!unit.Orders.IsEmpty || unit.Motion.IsStepping)
        {
            return CommandResult.Fail($"Unit {id} has pending waypoints or is moving; rotate-only is not applied.");
        }

        return SetFacing(id, facing);
    }

    /// <summary>
    /// Debug/test command: turns a hex into an obstacle or back into walkable ground. Hexes with a unit
    /// (or reserved by a stepping unit), the player base and spawn zones cannot be changed. Moving units
    /// re-plan their remaining route at the next hex centre.
    /// </summary>
    public CommandResult SetObstacle(HexCoord cell, bool blocked)
    {
        if (!State.Grid.Contains(cell))
        {
            return CommandResult.Fail("outside");
        }

        var tile = State.Grid[cell];
        if (tile.Type is TileType.PlayerBase or TileType.SpawnZone or TileType.EnemyBase or TileType.DeployZone)
        {
            return CommandResult.Fail("zone");
        }

        if (!tile.Exists)
        {
            return CommandResult.Fail("void");
        }

        if (blocked && (State.IsOccupied(cell) || State.IsReserved(cell)))
        {
            return CommandResult.Fail("occupied");
        }

        var type = blocked ? TileType.Blocked : TileType.Walkable;
        if (tile.Type != type)
        {
            State.Grid.SetTile(cell, type);
            _events.Add(new TerrainChanged(Tick, cell, type));
        }

        return CommandResult.Ok;
    }

    /// <summary>Returns and clears the events produced since the previous call.</summary>
    public IReadOnlyList<SimulationEvent> DrainEvents()
    {
        var copy = _events.ToArray();
        _events.Clear();
        return copy;
    }
}
