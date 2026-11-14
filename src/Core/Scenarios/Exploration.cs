namespace HexTactics.Core.Scenarios;

/// <summary>Who has to reach the exit in an exploration (spec part 7 §6.1).</summary>
public enum ExitRule
{
    /// <summary>The key unit (one card of the squad) reaches the exit alive — the starting rule.</summary>
    KeyUnit = 0,

    /// <summary>Any surviving unit of the player reaches the exit.</summary>
    AnySurvivor = 1,

    /// <summary>Every required member of the squad reaches the exit alive; one arrival is not enough (M7-33).</summary>
    AllRequired = 2,
}

/// <summary>An optional extra task of an exploration (spec part 7 §6.1); never automatic (M7-34).</summary>
public enum ExtraGoal
{
    None = 0,

    /// <summary>Defeat the enemies marked as targets in the scenario.</summary>
    KillMarked = 1,

    /// <summary>Defeat every enemy of the map.</summary>
    ClearMap = 2,
}

/// <summary>
/// What an exploration still needs (spec part 7 §6.1 «показывать, что именно осталось сделать»): the roster cards that still have
/// to reach the exit (empty when the exit part is done), the marked enemies and all the enemies still alive.
/// </summary>
public sealed record ExplorationProgress(bool ExitDone, System.Collections.Generic.IReadOnlyList<int> Waiting, int MarkedLeft, int EnemiesLeft, bool ExtraDone);
