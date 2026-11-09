using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Map;
using HexTactics.Core.Units;

namespace HexTactics.Core.Scenarios;

/// <summary>When a wave starts (spec part 7 §4.5); one way per wave, so the schedule never starts a wave twice.</summary>
public enum WaveStart
{
    /// <summary>The previous wave is fully resolved (nobody of it alive or waiting), then the delay.</summary>
    AfterPrevious = 0,

    /// <summary>At a game second of the running scenario.</summary>
    AtTime = 1,

    /// <summary>A number of game seconds after the previous wave started.</summary>
    AfterPreviousStart = 2,
}

/// <summary>
/// A group of a wave (spec part 7 §4.5): <see cref="Count"/> enemies of one library unit along one route; the k-th appears at
/// wave start + offset + first delay + k × interval (game seconds). Interval 0 asks for all at once — the spawn queue lets them
/// in one by one.
/// </summary>
public sealed record SpawnGroupDefinition(string Id, EnemyRoute Route, UnitProfile Profile, int Count, double FirstDelaySeconds = 0, double IntervalSeconds = 1, double StartOffsetSeconds = 0)
{
    public double SpawnTime(double waveStart, int k) => waveStart + StartOffsetSeconds + FirstDelaySeconds + (k * IntervalSeconds);
}

/// <summary>A wave: its start rule and its groups (several routes at once are fine).</summary>
public sealed record WaveDefinition(string Id, string Name, WaveStart Start, double StartSeconds, IReadOnlyList<SpawnGroupDefinition> Groups)
{
    /// <summary>Enemies of the wave — always the sum of the groups, never a second stored number (spec part 7 §4.5).</summary>
    public int Total => Groups.Sum(g => g.Count);
}

/// <summary>How the loss of base lives ends the battle (spec part 7 §4.6); chosen explicitly by the author.</summary>
public enum DefeatPolicy
{
    /// <summary>Any base marked critical reaches 0 lives (the standard template).</summary>
    AnyCriticalBase = 0,

    /// <summary>Every base reaches 0 lives.</summary>
    AllBases = 1,

    /// <summary>The bases share one pool of lives (the sum); the pool reaching 0 is the defeat.</summary>
    SharedLives = 2,
}

/// <summary>The lives of a player base in a scenario.</summary>
public sealed record BaseLives(int Base, int Lives, bool Critical = true);
