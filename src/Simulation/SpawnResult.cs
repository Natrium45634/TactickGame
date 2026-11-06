using HexTactics.Core.Hex;
using HexTactics.Core.Orders;
using HexTactics.Core.Spawning;
using HexTactics.Core.Units;

namespace HexTactics.Simulation;

/// <summary>Result of a spawn command. On failure nothing in the game state has changed.</summary>
/// <param name="Unit">Created unit id (success only).</param>
/// <param name="Cell">The cell used (success) or the offending cell (failure, if known).</param>
/// <param name="Error">Why the spawn was rejected.</param>
/// <param name="Occupant">The unit standing on <paramref name="Cell"/> when <see cref="SpawnError.Occupied"/>.</param>
public readonly record struct SpawnResult(UnitId? Unit, HexCoord? Cell, SpawnError Error, UnitId? Occupant)
{
    public bool Success => Error == SpawnError.None && Unit is not null;
}

/// <summary>Generic outcome of a command addressed to a unit.</summary>
public readonly record struct CommandResult(bool Success, string? Reason = null)
{
    public static CommandResult Ok { get; } = new(true);

    public static CommandResult Fail(string reason) => new(false, reason);
}

/// <summary>Result of a movement order. On failure the order queue is unchanged.</summary>
public readonly record struct OrderResult(Waypoint? Waypoint, WaypointError Error)
{
    public bool Success => Error == WaypointError.None && Waypoint is not null;
}

/// <summary>Result of applying a complete order. On failure the previous queue is unchanged.</summary>
/// <param name="FailedIndex">Index of the first invalid waypoint (-1 if none / not applicable).</param>
public readonly record struct OrderApplyResult(bool Success, int FailedIndex, WaypointError Error)
{
    public static OrderApplyResult Ok { get; } = new(true, -1, WaypointError.None);
}
