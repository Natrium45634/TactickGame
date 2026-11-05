using System.Collections.Generic;
using HexTactics.Core.Units;

namespace HexTactics.Simulation;

/// <summary>Data passed to every system on each fixed tick.</summary>
public sealed class SimulationContext
{
    private readonly List<SimulationEvent> _events;

    internal SimulationContext(GameState state, List<SimulationEvent> events)
    {
        State = state;
        _events = events;
    }

    public GameState State { get; }

    /// <summary>Number of the tick being executed.</summary>
    public long Tick { get; internal set; }

    /// <summary>Fixed tick duration in seconds (game time, independent of the frame rate).</summary>
    public double DeltaSeconds { get; internal set; }

    public void Emit(SimulationEvent e) => _events.Add(e);
}

/// <summary>
/// Extension point for game rules that run every tick (movement now; abilities, attacks, waves, effects later).
/// Systems run in registration order, only while the simulation is not paused, and communicate through
/// <see cref="SimulationEvent"/>s — never through input handlers.
/// </summary>
public interface ISimulationSystem
{
    string Name { get; }

    void Tick(SimulationContext context);
}
