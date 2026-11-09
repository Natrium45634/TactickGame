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

    /// <summary>Numbered bases of both sides (spec part 7 §4.1); several of each side are allowed.</summary>
    public IReadOnlyList<MapBase> Bases { get; init; } = System.Array.Empty<MapBase>();

    /// <summary>Enemy routes of the map (spec part 7 §4.1, §9.2: the geometry belongs to the map).</summary>
    public IReadOnlyList<EnemyRoute> Routes { get; init; } = System.Array.Empty<EnemyRoute>();

    /// <summary>The base the hex belongs to, or null.</summary>
    public MapBase? BaseAt(HexCoord cell)
    {
        foreach (var b in Bases)
        {
            foreach (var c in b.Cells)
            {
                if (c == cell)
                {
                    return b;
                }
            }
        }

        return null;
    }

    public EnemyRoute? FindRoute(string id)
    {
        foreach (var route in Routes)
        {
            if (route.Id == id)
            {
                return route;
            }
        }

        return null;
    }

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
