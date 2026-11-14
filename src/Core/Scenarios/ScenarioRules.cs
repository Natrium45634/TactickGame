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

    /// <summary>Defense: the battle lasted the required game seconds (spec part 7 §5.3).</summary>
    Survived = 5,

    /// <summary>Defense: a defended object was destroyed under the objects policy.</summary>
    ObjectLost = 6,

    /// <summary>Exploration: the exit rule and the extra task are done (spec part 7 §6.1).</summary>
    ExitReached = 7,

    /// <summary>Exploration: the key unit died (spec part 7 §6.2).</summary>
    KeyUnitLost = 8,

    /// <summary>Exploration: a required member died — «all required reach the exit» can no longer be done.</summary>
    RequiredLost = 9,
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

    /// <summary>
    /// Defense goals (spec part 7 §5.3): no player base; victory by surviving <see cref="SurviveSeconds"/> or, without it, by
    /// repelling every wave; defeat by the defended objects (<see cref="Objects"/>, <see cref="ObjectsPolicy"/>) or the squad.
    /// </summary>
    public bool Defense { get; init; }

    /// <summary>Defense: game seconds to hold out; 0 — the goal is to repel every wave.</summary>
    public double SurviveSeconds { get; init; }

    /// <summary>The defended objects placed at the start (spec part 7 §5.1).</summary>
    public System.Collections.Generic.IReadOnlyList<MissionObject> Objects { get; init; } = System.Array.Empty<MissionObject>();

    public ObjectsPolicy ObjectsPolicy { get; init; } = ObjectsPolicy.AllMustSurvive;

    /// <summary>
    /// Exploration goals (spec part 7 §6): the squad deploys only on <see cref="EntryCells"/>; victory when <see cref="ExitRule"/>
    /// is met on <see cref="ExitCells"/> and the <see cref="ExtraGoal"/> is done — killing every enemy is not a goal by itself.
    /// </summary>
    public bool Exploration { get; init; }

    /// <summary>Exploration: the hexes where the roster may be deployed (instead of the map's deploy hexes).</summary>
    public System.Collections.Generic.IReadOnlyList<HexTactics.Core.Hex.HexCoord> EntryCells { get; init; } = System.Array.Empty<HexTactics.Core.Hex.HexCoord>();

    /// <summary>Exploration: the exit area; a unit standing on one of these hexes has reached the exit.</summary>
    public System.Collections.Generic.IReadOnlyList<HexTactics.Core.Hex.HexCoord> ExitCells { get; init; } = System.Array.Empty<HexTactics.Core.Hex.HexCoord>();

    public ExitRule ExitRule { get; init; } = ExitRule.KeyUnit;

    /// <summary>Exploration: the roster entry (1-based card of the squad) of the key unit.</summary>
    public int KeyEntry { get; init; } = 1;

    /// <summary>Exploration with <see cref="ExitRule.AllRequired"/>: the roster entries that must all reach the exit.</summary>
    public System.Collections.Generic.IReadOnlyList<int> RequiredEntries { get; init; } = System.Array.Empty<int>();

    public ExtraGoal ExtraGoal { get; init; } = ExtraGoal.None;
}
