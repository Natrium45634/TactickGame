using HexTactics.Core.Hex;

namespace HexTactics.Core.Orders;

/// <summary>
/// A movement point (ТД) bound to the centre of a hex. <see cref="Sequence"/> is the label shown
/// to the player (1, 2, 3 …); it stays stable while earlier points are consumed and restarts at 1
/// when a new chain replaces the queue.
/// </summary>
public sealed record Waypoint(int Id, int Sequence, HexCoord Cell);
