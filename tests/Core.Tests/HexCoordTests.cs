using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Hex;
using Xunit;

namespace HexTactics.Core.Tests;

public class HexCoordTests
{
    [Fact]
    public void Offset_to_cube_round_trips_for_every_cell_of_12x10_map()
    {
        for (var row = 0; row < 10; row++)
        {
            for (var col = 0; col < 12; col++)
            {
                var hex = HexCoord.FromOffset(col, row);
                Assert.Equal(0, hex.Q + hex.R + hex.S);
                Assert.Equal(new OffsetCoord(col, row), hex.ToOffset());
            }
        }
    }

    [Fact]
    public void Offset_round_trip_works_for_negative_coordinates()
    {
        foreach (var o in new[] { new OffsetCoord(-1, -1), new OffsetCoord(-3, 2), new OffsetCoord(4, -5), new OffsetCoord(-7, -8) })
        {
            Assert.Equal(o, HexCoord.FromOffset(o).ToOffset());
        }
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(1, 0, 1, 0)]
    [InlineData(0, 1, 0, 1)] // odd row: column 0 is axial q = 0
    [InlineData(1, 2, 0, 2)] // even row 2: q = col - 1
    [InlineData(11, 9, 7, 9)]
    public void Odd_r_conversion_matches_reference_values(int col, int row, int q, int r)
    {
        Assert.Equal(new HexCoord(q, r), HexCoord.FromOffset(col, row));
    }

    [Fact]
    public void Every_hex_has_six_distinct_neighbors_at_distance_one()
    {
        var center = HexCoord.FromOffset(5, 5);
        var neighbors = center.Neighbors().ToList();
        Assert.Equal(6, neighbors.Count);
        Assert.Equal(6, neighbors.Distinct().Count());
        Assert.All(neighbors, n => Assert.Equal(1, center.DistanceTo(n)));
    }

    [Fact]
    public void Neighborhood_is_symmetric_and_directions_are_opposite()
    {
        for (var row = 0; row < 10; row++)
        {
            for (var col = 0; col < 12; col++)
            {
                var hex = HexCoord.FromOffset(col, row);
                foreach (var dir in HexDirections.All)
                {
                    var n = hex.Neighbor(dir);
                    Assert.Contains(hex, n.Neighbors());
                    Assert.Equal(hex, n.Neighbor(dir.Opposite()));
                    Assert.Equal(dir, hex.DirectionTo(n));
                }
            }
        }
    }

    [Fact]
    public void Odd_r_neighbors_match_known_offset_neighbors()
    {
        // Even row (2): neighbours of (4,2)
        var even = HexCoord.FromOffset(4, 2).Neighbors().Select(n => n.ToOffset()).ToHashSet();
        Assert.Equal(new HashSet<OffsetCoord> { new(5, 2), new(3, 2), new(3, 1), new(4, 1), new(3, 3), new(4, 3) }, even);

        // Odd row (3): neighbours of (4,3)
        var odd = HexCoord.FromOffset(4, 3).Neighbors().Select(n => n.ToOffset()).ToHashSet();
        Assert.Equal(new HashSet<OffsetCoord> { new(5, 3), new(3, 3), new(4, 2), new(5, 2), new(4, 4), new(5, 4) }, odd);
    }

    [Fact]
    public void Distance_is_cube_distance()
    {
        var a = HexCoord.FromOffset(0, 0);
        Assert.Equal(0, a.DistanceTo(a));
        Assert.Equal(11, a.DistanceTo(HexCoord.FromOffset(11, 0)));
        Assert.Equal(9, a.DistanceTo(HexCoord.FromOffset(0, 9)));
        Assert.Equal(16, a.DistanceTo(HexCoord.FromOffset(11, 9)));
        Assert.Equal(HexCoord.FromOffset(3, 7).DistanceTo(HexCoord.FromOffset(8, 1)), HexCoord.FromOffset(8, 1).DistanceTo(HexCoord.FromOffset(3, 7)));
    }

    [Fact]
    public void Direction_to_non_adjacent_hex_is_null()
    {
        Assert.Null(HexCoord.FromOffset(0, 0).DirectionTo(HexCoord.FromOffset(2, 0)));
        Assert.Null(HexCoord.FromOffset(0, 0).DirectionTo(HexCoord.FromOffset(0, 0)));
    }

    [Fact]
    public void Compare_orders_row_major()
    {
        var list = new List<HexCoord> { HexCoord.FromOffset(2, 1), HexCoord.FromOffset(0, 1), HexCoord.FromOffset(5, 0) };
        list.Sort();
        Assert.Equal(new[] { new OffsetCoord(5, 0), new OffsetCoord(0, 1), new OffsetCoord(2, 1) }, list.Select(h => h.ToOffset()));
    }
}
