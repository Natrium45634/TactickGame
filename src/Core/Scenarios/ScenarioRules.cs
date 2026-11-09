namespace HexTactics.Core.Scenarios;

/// <summary>States of a scenario match (spec part 7 §2.3); the tactical pause is the simulation's own.</summary>
public enum ScenarioPhase
{
    /// <summary>Before the first running tick: the player deploys and plans; nothing of the scenario advances.</summary>
    Preparing = 0,

    Running = 1,

    Victory = 2,

    Defeat = 3,
}

/// <summary>Why a scenario ended.</summary>
public enum ScenarioEndReason
{
    AllEnemiesDefeated = 0,

    SquadLost = 1,

    TimeUp = 2,

    /// <summary>Tower Defense: every wave resolved, no enemy left (spec part 7 §4.6).</summary>
    AllWavesCleared = 3,

    /// <summary>Tower Defense: the base lives ran out under the defeat policy.</summary>
    BaseLost = 4,
}

/// <summary>
/// Goals of a scenario (spec part 7 §3.2): the ordinary battle is won when every enemy of the scenario is defeated and lost when
/// every deployed unit of the player is dead and no card of the roster can still be deployed; an optional time limit is an
/// additional defeat condition (it never replaces the main goal). Decided from the confirmed state of a tick, once.
/// </summary>
public sealed record ScenarioRules
{
    public bool EliminateAllEnemies { get; init; } = true;

    public bool DefeatWhenSquadLost { get; init; } = true;

    /// <summary>Game seconds of the running phase after which the scenario is lost; 0 — no limit.</summary>
    public double TimeLimitSeconds { get; init; }

    /// <summary>Waves of enemies along routes (Tower Defense, spec part 7 §4.5); empty — no waves.</summary>
    public System.Collections.Generic.IReadOnlyList<WaveDefinition> Waves { get; init; } = System.Array.Empty<WaveDefinition>();

    /// <summary>Lives of the player bases; an enemy finishing its route on one takes one life of that base only.</summary>
    public System.Collections.Generic.IReadOnlyList<BaseLives> Bases { get; init; } = System.Array.Empty<BaseLives>();

    public DefeatPolicy Defeat { get; init; } = DefeatPolicy.AnyCriticalBase;

    /// <summary>
    /// Tower Defense goals instead of the ordinary ones: victory when every wave is resolved and no enemy is left, defeat by
    /// the base lives (spec part 7 §4.6).
    /// </summary>
    public bool TowerDefense { get; init; }
}
