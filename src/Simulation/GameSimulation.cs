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
    private readonly MovementSystem _movement;
    private readonly SimulationClock _clock;

    public GameSimulation(MapDefinition map, SimulationSettings? settings = null)
    {
        State = new GameState(map);
        Settings = settings ?? SimulationSettings.Default;
        _movement = new MovementSystem(State, _events);
        _clock = new SimulationClock(Settings.TickDuration);
    }

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
    /// Returns the number of ticks executed.
    /// </summary>
    public int Advance(double realSeconds)
    {
        var ticks = _clock.Accumulate(realSeconds);
        for (var i = 0; i < ticks; i++)
        {
            Step();
        }

        return ticks;
    }

    /// <summary>Runs exactly one fixed simulation tick.</summary>
    public void Step()
    {
        Tick++;
        _movement.Tick(Tick, Settings.TickDuration);
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
