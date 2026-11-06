using System.Collections.Generic;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Map;

/// <summary>A spawn zone on the outer edge of the map. Cells are in deterministic (row-major) order.</summary>
public sealed record SpawnZoneDefinition(int Id, string Name, IReadOnlyList<HexCoord> Cells);

/// <summary>The player base. Cells are in deterministic (row-major) order.</summary>
public sealed record PlayerBaseDefinition(string Name, IReadOnlyList<HexCoord> Cells);

/// <summary>Everything a level contributes: terrain plus named spawn locations.</summary>
public sealed class MapDefinition
{
    public MapDefinition(string name, HexGrid grid, IReadOnlyList<SpawnZoneDefinition> spawnZones, PlayerBaseDefinition? playerBase, IReadOnlyList<HexCoord>? deployCells = null)
    {
        Name = name;
        Grid = grid;
        SpawnZones = spawnZones;
        PlayerBase = playerBase;
        DeployCells = deployCells ?? System.Array.Empty<HexCoord>();
    }

    /// <summary>Extra hexes where the player may deploy units from the roster (besides the base), row-major.</summary>
    public IReadOnlyList<HexCoord> DeployCells { get; }

    public string Name { get; }

    public HexGrid Grid { get; }

    public IReadOnlyList<SpawnZoneDefinition> SpawnZones { get; }

    public PlayerBaseDefinition? PlayerBase { get; }

    public SpawnZoneDefinition? FindZone(int id)
    {
        foreach (var zone in SpawnZones)
        {
            if (zone.Id == id)
            {
                return zone;
            }
        }

        return null;
    }
}
