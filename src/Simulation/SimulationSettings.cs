namespace HexTactics.Simulation;

/// <summary>Technical parameters of the simulation (spec §6.3, §7). Not balance values.</summary>
public sealed record SimulationSettings
{
    /// <summary>Fixed simulation rate; rules never depend on render FPS.</summary>
    public int TicksPerSecond { get; init; } = 30;

    /// <summary>Default unit speed in hex transitions per second (prototype value).</summary>
    public double DefaultUnitSpeed { get; init; } = 2.0;

    public double TickDuration => 1.0 / TicksPerSecond;

    public static SimulationSettings Default { get; } = new();
}
