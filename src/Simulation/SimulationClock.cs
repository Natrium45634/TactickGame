using System;

namespace HexTactics.Simulation;

/// <summary>
/// Fixed-step accumulator: converts variable frame time into a whole number of simulation ticks
/// (default 1/30 s), so game rules never depend on the rendering frame rate.
/// </summary>
public sealed class SimulationClock
{
    private double _accumulator;

    public SimulationClock(double tickDuration, int maxTicksPerAdvance = 8)
    {
        if (tickDuration <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tickDuration));
        }

        TickDuration = tickDuration;
        MaxTicksPerAdvance = maxTicksPerAdvance;
    }

    public double TickDuration { get; }

    /// <summary>Upper bound of ticks per call; protects against a "spiral of death" after a long frame hitch.</summary>
    public int MaxTicksPerAdvance { get; }

    /// <summary>Fraction (0..1) of the next tick already accumulated — used only for visual interpolation.</summary>
    public double Alpha => _accumulator / TickDuration;

    /// <summary>Adds real elapsed time and returns how many fixed ticks are due now.</summary>
    public int Accumulate(double realSeconds)
    {
        if (realSeconds <= 0 || double.IsNaN(realSeconds))
        {
            return 0;
        }

        _accumulator += realSeconds;
        var ticks = (int)Math.Floor((_accumulator + 1e-9) / TickDuration);
        if (ticks > MaxTicksPerAdvance)
        {
            ticks = MaxTicksPerAdvance;
            _accumulator = 0; // drop the backlog instead of fast-forwarding
            return ticks;
        }

        _accumulator = Math.Max(0, _accumulator - (ticks * TickDuration));
        return ticks;
    }

    public void Reset() => _accumulator = 0;
}
