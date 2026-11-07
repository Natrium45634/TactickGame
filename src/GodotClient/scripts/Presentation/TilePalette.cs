using System.Collections.Generic;
using Godot;
using HexTactics.Content.Runtime;
using HexTactics.Core.Map;

namespace HexTactics.Client.Presentation;

/// <summary>
/// How tiles look (spec part 4 §4.2, §7.1): colour and relief height come from the tile type of the content library
/// (<see cref="TileVisual"/>); tiles without a library type (legacy maps, debug obstacles) use the role colours of
/// <see cref="VisualConfig"/>. Presentation only — rules read <see cref="TileState.Type"/>, never the colour.
/// </summary>
public sealed class TilePalette
{
    private readonly Dictionary<string, (Color Color, string Name)> _types = new();

    /// <summary>The palette of the scene currently shown (one mode is active at a time).</summary>
    public static TilePalette Current { get; set; } = new();

    public TilePalette()
    {
    }

    public TilePalette(IReadOnlyDictionary<string, TileVisual> visuals)
    {
        foreach (var (id, v) in visuals)
        {
            _types[id] = (Color.FromHtml(v.Color), v.Name);
        }
    }

    /// <summary>Height of the top face above the ground for a relief level 0…3.</summary>
    public static float LevelHeight(int level) => level switch
    {
        <= 0 => 0f,
        1 => 0.18f,
        2 => 0.36f,
        _ => VisualConfig.BlockedHeight,
    };

    public Color ColorOf(TileState tile) =>
        tile.TileId is { } id && _types.TryGetValue(id, out var t) ? t.Color : VisualConfig.TileColor(tile.Type);

    public string? NameOf(TileState tile) => tile.TileId is { } id && _types.TryGetValue(id, out var t) ? t.Name : null;

    public float HeightOf(TileState tile)
    {
        if (!tile.Exists)
        {
            return 0f;
        }

        return tile.TileId is { } id && _types.ContainsKey(id) ? LevelHeight(tile.Level) : VisualConfig.TileTopHeight(tile.Type);
    }

    public float HeightOf(HexGrid grid, HexTactics.Core.Hex.HexCoord coord) => grid.Contains(coord) ? HeightOf(grid[coord]) : 0f;
}
