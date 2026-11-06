using HexTactics.Core.Hex;

namespace HexTactics.Core.Orders;

/// <summary>Kind of an order point (spec part 2 §7, §10).</summary>
public enum OrderKind
{
    /// <summary>Move to the centre of a hex (ТД).</summary>
    MoveToTile = 0,

    /// <summary>
    /// Reserved for the future attack order: it will reference an enemy unit id (the target can move),
    /// not a hex. Recognised by input routing, never executed in this release (no fake damage).
    /// </summary>
    AttackTarget = 1,
}

/// <summary>
/// A movement point (ТД) bound to the centre of a hex. <see cref="Sequence"/> is the label shown
/// to the player (1, 2, 3 …); it stays stable while earlier points are consumed and restarts at 1
/// when a new chain replaces the queue.
/// </summary>
/// <param name="IsDetour">
/// A temporary step inserted by the simulation (a team member stepping aside to let a teammate pass, spec part 2 §6.2).
/// Not an order of the player: not drawn, not numbered, never held back by the team's stage synchronisation.
/// </param>
public sealed record Waypoint(int Id, int Sequence, HexCoord Cell, OrderKind Kind = OrderKind.MoveToTile, bool IsDetour = false);
