using System.IO;
using System.Linq;
using HexTactics.Core.Hex;
using HexTactics.Core.Map;
using Xunit;

namespace HexTactics.Core.Tests;

public class MapTests
{
    private static MapDefinition LoadPrototype() => MapParser.Parse(File.ReadAllText(TestPaths.PrototypeMap));

    [Fact]
    public void Prototype_map_has_exactly_12_columns_10_rows_120_cells()
    {
        var map = LoadPrototype();
        Assert.Equal(12, map.Grid.Columns);
        Assert.Equal(10, map.Grid.Rows);
        Assert.Equal(120, map.Grid.CellCount);
        Assert.Equal(120, map.Grid.Coords.Distinct().Count());
    }

    [Fact]
    public void Prototype_map_has_base_spawn_zones_and_obstacles()
    {
        var map = LoadPrototype();
        Assert.NotNull(map.PlayerBase);
        Assert.NotEmpty(map.PlayerBase!.Cells);
        Assert.True(map.SpawnZones.Count >= 1);
        Assert.Contains(map.Grid.Coords, c => map.Grid[c].Type == TileType.Blocked);
        Assert.All(map.SpawnZones.SelectMany(z => z.Cells), c => Assert.True(map.Grid.IsEdge(c)));
    }

    [Fact]
    public void Passability_comes_from_tile_data()
    {
        var grid = new HexGrid(12, 10);
        var c = HexCoord.FromOffset(3, 3);
        Assert.True(grid.IsWalkable(c));
        grid.SetTile(c, TileType.Blocked);
        Assert.False(grid.IsWalkable(c));
        grid.SetTile(c, TileType.PlayerBase);
        Assert.True(grid.IsWalkable(c));
        Assert.False(grid.IsWalkable(HexCoord.FromOffset(12, 0)));
        Assert.False(grid.IsWalkable(HexCoord.FromOffset(-1, 0)));
    }

    [Fact]
    public void Grid_bounds_check_matches_12x10()
    {
        var grid = new HexGrid(12, 10);
        Assert.True(grid.Contains(HexCoord.FromOffset(0, 0)));
        Assert.True(grid.Contains(HexCoord.FromOffset(11, 9)));
        Assert.False(grid.Contains(HexCoord.FromOffset(12, 9)));
        Assert.False(grid.Contains(HexCoord.FromOffset(0, 10)));
        Assert.False(grid.Contains(HexCoord.FromOffset(0, -1)));
    }

    [Fact]
    public void Edge_detection()
    {
        var grid = new HexGrid(12, 10);
        Assert.True(grid.IsEdge(HexCoord.FromOffset(0, 5)));
        Assert.True(grid.IsEdge(HexCoord.FromOffset(11, 4)));
        Assert.True(grid.IsEdge(HexCoord.FromOffset(5, 0)));
        Assert.True(grid.IsEdge(HexCoord.FromOffset(5, 9)));
        Assert.False(grid.IsEdge(HexCoord.FromOffset(5, 5)));
    }

    [Fact]
    public void Parser_reads_tokens_and_zones()
    {
        const string text = """
            // test
            @name Test
            @size 3 2
            @zone 1 North
            @base Home
            1 . #
            B . .
            """;
        var map = MapParser.Parse(text);
        Assert.Equal("Test", map.Name);
        Assert.Equal(TileType.SpawnZone, map.Grid[HexCoord.FromOffset(0, 0)].Type);
        Assert.Equal(1, map.Grid[HexCoord.FromOffset(0, 0)].ZoneId);
        Assert.Equal(TileType.Blocked, map.Grid[HexCoord.FromOffset(2, 0)].Type);
        Assert.Equal(TileType.PlayerBase, map.Grid[HexCoord.FromOffset(0, 1)].Type);
        Assert.Equal("North", map.FindZone(1)!.Name);
        Assert.Equal("Home", map.PlayerBase!.Name);
    }

    [Theory]
    [InlineData("@size 2 1\n. . .")]
    [InlineData("@size 2 2\n. .")]
    [InlineData("@size 2 1\n. X")]
    [InlineData(". .")]
    [InlineData("@size 3 3\n. . .\n. 1 .\n. . .")] // zone not on the edge
    public void Parser_rejects_malformed_maps(string text)
    {
        Assert.Throws<MapFormatException>(() => MapParser.Parse(text));
    }
}
