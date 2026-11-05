namespace HexTactics.Core.Map;

/// <summary>Logical tile category. Combat properties of terrain are intentionally not defined yet.</summary>
public enum TileType
{
    /// <summary>Ordinary passable ground.</summary>
    Walkable = 0,

    /// <summary>Impassable obstacle.</summary>
    Blocked = 1,

    /// <summary>Passable edge tile where units may appear (identified by <see cref="TileState.ZoneId"/>).</summary>
    SpawnZone = 2,

    /// <summary>Passable tile that belongs to the player base.</summary>
    PlayerBase = 3,
}
