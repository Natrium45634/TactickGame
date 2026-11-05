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
public readonly record struct TileState(HexCoord Coord, TileType Type, int ZoneId = 0, int MoveCost = 1)
{
    public bool IsWalkable => Type != TileType.Blocked;
}
