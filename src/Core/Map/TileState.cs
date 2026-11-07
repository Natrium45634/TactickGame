using HexTactics.Core.Hex;

namespace HexTactics.Core.Map;

/// <summary>
/// Immutable description of one map cell. Passability is a property of the data,
/// never derived from how the tile is drawn.
/// </summary>
/// <param name="Coord">Logical coordinate of the cell.</param>
/// <param name="Type">Tile category.</param>
/// <param name="ZoneId">Spawn zone id for <see cref="TileType.SpawnZone"/> tiles, otherwise 0.</param>
/// <param name="MoveCost">Cost of entering this tile. Stage 1 uses 1 for every walkable tile.</param>
/// <param name="TileId">
/// Library tile type (<c>TileDefinition</c> id, spec part 4 §7) this cell is an instance of; null for maps that do not use
/// the content library (tests, legacy <c>.hexmap</c>).
/// </param>
/// <param name="Level">Relief level of the cell (spec part 4 §4.2) — appearance only, no combat rules.</param>
/// <param name="AllowsWaypoints">
/// False if the tile type forbids ТД/КТД on it (spec part 4 §7.1: «возможность стать целью ТД»); units may still walk
/// across it and stand on it by other means (spawn, companions of a command order).
/// </param>
public readonly record struct TileState(HexCoord Coord, TileType Type, int ZoneId = 0, int MoveCost = 1, string? TileId = null, int Level = 0, bool AllowsWaypoints = true)
{
    public bool IsWalkable => Type is not (TileType.Blocked or TileType.Void);

    /// <summary>False for an empty place of the workspace (spec part 4 §4.1).</summary>
    public bool Exists => Type != TileType.Void;
}
