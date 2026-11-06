namespace HexTactics.Simulation;

/// <summary>Technical parameters of the simulation (spec §6.3, §7). Not balance values.</summary>
public sealed record SimulationSettings
{
    /// <summary>Fixed simulation rate; rules never depend on render FPS.</summary>
    public int TicksPerSecond { get; init; } = 30;

    /// <summary>Default unit speed in hex transitions per second (prototype value).</summary>
    public double DefaultUnitSpeed { get; init; } = 2.0;

    /// <summary>
    /// Seed of the few deliberately random decisions (companion facings of command orders, spec part 2 §6.3, §10).
    /// Same state + same commands + same seed = same result.
    /// </summary>
    public int Seed { get; init; } = 20261106;

    /// <summary>A team member blocked only by teammates asks one of them to step aside after this time.</summary>
    public double TeammateYieldDelaySeconds { get; init; } = 0.8;

    /// <summary>If the way stays closed by teammates (nobody can step aside) the unit reports a blocked route after this time.</summary>
    public double TeammateWaitLimitSeconds { get; init; } = 6.0;

    public double TickDuration => 1.0 / TicksPerSecond;

    public static SimulationSettings Default { get; } = new();
}
