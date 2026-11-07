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

    /// <summary>
    /// Passable tile where the player may additionally deploy units from the roster
    /// (spec part 2 §5.6: extra direct-placement areas defined by the map).
    /// </summary>
    DeployZone = 4,

    /// <summary>
    /// Passable tile marked as the enemy base (spec part 4 §4.2): only a label of the terrain — no AI, no waves.
    /// </summary>
    EnemyBase = 5,

    /// <summary>
    /// No tile here (spec part 4 §4.1): an empty place of the workspace. Impassable, not a spawn or deploy place, never a
    /// waypoint target or a skill/effect cell, not drawn as terrain.
    /// </summary>
    Void = 6,
}
