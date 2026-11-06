using System.Collections.Generic;
using HexTactics.Core.Hex;
using HexTactics.Core.Map;
using HexTactics.Core.Units;

namespace HexTactics.Core.Spawning;

public enum PlacementError
{
    None = 0,
    OutOfBounds,

    /// <summary>Not a hex where the map lets the player deploy from the roster (base or deploy zone).</summary>
    NotAllowedHere,
    NotWalkable,
    Occupied,
    UnknownEntry,

    /// <summary>The roster card is not in a state that allows placing it.</summary>
    EntryNotAvailable,
}

/// <summary>
/// Where the player may deploy units from the roster (spec part 2 §5.2, §5.6): the player base and the map's
/// deploy zones (<see cref="TileType.DeployZone"/>). Edge spawn zones are NOT automatically allowed for the player.
/// </summary>
public static class PlacementRules
{
    public static bool IsDeployTile(TileType type) => type is TileType.PlayerBase or TileType.DeployZone;

    /// <summary>All hexes of the map where roster placement is allowed by the map data (row-major).</summary>
    public static IReadOnlyList<HexCoord> AllowedCells(MapDefinition map)
    {
        var cells = new List<HexCoord>();
        foreach (var c in map.Grid.Coords)
        {
            if (IsDeployTile(map.Grid[c].Type))
            {
                cells.Add(c);
            }
        }

        return cells;
    }

    public static PlacementError ValidateCell(GameState state, HexCoord cell)
    {
        if (!state.Grid.Contains(cell))
        {
            return PlacementError.OutOfBounds;
        }

        var tile = state.Grid[cell];
        if (!IsDeployTile(tile.Type))
        {
            return PlacementError.NotAllowedHere;
        }

        if (!tile.IsWalkable)
        {
            return PlacementError.NotWalkable;
        }

        return state.IsOccupied(cell) || state.IsReserved(cell) ? PlacementError.Occupied : PlacementError.None;
    }
}
