using System.Collections.Generic;
using HexTactics.Content.Kinds;
using HexTactics.Content.Model;
using HexTactics.Content.Registry;
using HexTactics.Core.Hex;

namespace HexTactics.Content.Library;

/// <summary>
/// Checks of a map that need the tile library (spec part 4 §4.4): spawn tiles on the edge, places to deploy, fitting the
/// screen. Islands of tiles are not an error by themselves.
/// </summary>
internal static class MapChecks
{
    /// <summary>Largest workspace that fits the game screen at 1366×768 without changing the camera.</summary>
    public const int ScreenColumns = 16;
    public const int ScreenRows = 12;

    public static void Validate(MapData map, ContentLibrary library, IssueList issues)
    {
        var present = new HashSet<HexCoord>();
        foreach (var t in map.Tiles)
        {
            present.Add(t.Coord);
        }

        var deployPlaces = 0;
        for (var i = 0; i < map.Tiles.Count; i++)
        {
            var t = map.Tiles[i];
            if (library.Find(ContentTypeRegistry.Tile, t.Tile)?.Data is not TileData type)
            {
                continue; // reported as a missing reference
            }

            if (type.Category is TileData.PlayerBase or TileData.Deploy)
            {
                deployPlaces++;
            }

            if (type.Category == TileData.Spawn)
            {
                var onEdge = false;
                foreach (var n in t.Coord.Neighbors())
                {
                    var o = n.ToOffset();
                    if (o.Col < 0 || o.Row < 0 || o.Col >= map.Columns || o.Row >= map.Rows || !present.Contains(n))
                    {
                        onEdge = true;
                        break;
                    }
                }

                if (!onEdge)
                {
                    issues.Warning($"data.tiles[{i}]", $"гекс зоны появления ({t.Col},{t.Row}) не на краю карты");
                }
            }
            else if (t.Zone is not null)
            {
                issues.Warning($"data.tiles[{i}].zone", $"номер зоны у гекса ({t.Col},{t.Row}) не используется: тип не «зона появления»");
            }
        }

        if (map.Tiles.Count > 0 && deployPlaces == 0)
        {
            issues.Warning("data.tiles", "на карте нет мест выставления юнитов (база игрока или зона выставления)");
        }

        if (map.Columns > ScreenColumns || map.Rows > ScreenRows)
        {
            issues.Warning("data.columns", $"рабочая область {map.Columns}×{map.Rows} больше {ScreenColumns}×{ScreenRows} — поле может не поместиться на стартовый экран, уменьшите размер");
        }
    }
}
