using System;
using System.Collections.Generic;
using HexTactics.Core.Hex;
using HexTactics.Core.Map;
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

    public GameSimulation(MapDefinition map, SimulationSettings? settings = null)
    {
        State = new GameState(map);
        Settings = settings ?? SimulationSettings.Default;
    }

    public GameState State { get; }

    public SimulationSettings Settings { get; }

    /// <summary>Number of fixed simulation steps executed so far.</summary>
    public long Tick { get; private set; }

    /// <summary>Creates a unit using one of the three spawn methods. Never relocates or removes existing units.</summary>
    public SpawnResult Spawn(SpawnRequest request)
    {
        var resolution = SpawnRules.Resolve(State, request);
        if (!resolution.IsValid)
        {
            var offending = resolution.Cell ?? request.Cell;
            var occupant = offending is { } c ? State.UnitAt(c)?.Id : null;
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

    /// <summary>Returns and clears the events produced since the previous call.</summary>
    public IReadOnlyList<SimulationEvent> DrainEvents()
    {
        var copy = _events.ToArray();
        _events.Clear();
        return copy;
    }
}
