using System;
using System.Collections.Generic;
using HexTactics.Core.Hex;
using HexTactics.Core.Map;
using HexTactics.Core.Orders;
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

    public GameSimulation(MapDefinition map, SimulationSettings? settings = null)
    {
        State = new GameState(map);
        Settings = settings ?? SimulationSettings.Default;
        _clock = new SimulationClock(Settings.TickDuration);
        _context = new SimulationContext(State, _events);
        AddSystem(new MovementSystem(State, _events));
    }

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
        var unit = State.CreateUnit(request.Side, cell, facing, Settings.DefaultUnitSpeed);
        _events.Add(new UnitSpawned(Tick, unit.Id, cell, request.Method));
        return new SpawnResult(unit.Id, cell, SpawnError.None, null);
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
        if (State.FindUnit(order.Unit) is null)
        {
            return new OrderApplyResult(false, 0, WaypointError.UnknownUnit);
        }

        if (!Enum.IsDefined(order.FinalFacing))
        {
            throw new ArgumentOutOfRangeException(nameof(order), "Invalid final facing.");
        }

        var (failed, error) = OrderDraftValidator.ValidateChain(State, order.Unit, CommandAnchor(order.Unit), order.Waypoints);
        if (error != WaypointError.None)
        {
            _events.Add(new OrderApplyRejected(Tick, order.Unit, failed, error));
            return new OrderApplyResult(false, failed, error);
        }

        State.ClearWaypoints(order.Unit);
        foreach (var cell in order.Waypoints)
        {
            State.AppendWaypoint(order.Unit, cell);
        }

        State.SetFinalFacing(order.Unit, order.FinalFacing);
        _events.Add(new OrderApplied(Tick, order.Unit, order.Waypoints, order.FinalFacing));
        return OrderApplyResult.Ok;
    }

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
        if (tile.Type is TileType.PlayerBase or TileType.SpawnZone)
        {
            return CommandResult.Fail("zone");
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
