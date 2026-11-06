using System.Collections.Generic;
using HexTactics.Core.Hex;
using HexTactics.Core.Spawning;

namespace HexTactics.Interaction;

/// <summary>Snapshot of a roster card being dragged over the map (spec part 2 §5.2).</summary>
/// <param name="RosterEntry">The dragged card.</param>
/// <param name="TargetHex">Hex under the cursor (may be outside the map or null).</param>
/// <param name="Error"><see cref="PlacementError.None"/> if releasing now places the unit.</param>
/// <param name="AllowedCells">Hexes the map allows for roster placement (base + deploy zones), before occupancy.</param>
public sealed record PlacementView(int RosterEntry, HexCoord? TargetHex, PlacementError Error, IReadOnlyList<HexCoord> AllowedCells)
{
    public bool CanPlace => Error == PlacementError.None && TargetHex is not null;
}
