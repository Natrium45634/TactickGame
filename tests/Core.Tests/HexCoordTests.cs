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
    [InlineData(1, 0, 1, 0)]  // odd column: r = row - (col - 1) / 2
    [InlineData(0, 1, 0, 1)]
    [InlineData(2, 1, 2, 0)]  // even column 2: r = row - 1
    [InlineData(3, 5, 3, 4)]
    [InlineData(11, 9, 11, 4)]
    public void Odd_q_conversion_matches_reference_values(int col, int row, int q, int r)
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
    public void Odd_q_neighbors_match_known_offset_neighbors_by_direction()
    {
        // Even column (4,2): N, NE, SE, S, SW, NW
        var even = HexDirections.All.Select(d => HexCoord.FromOffset(4, 2).Neighbor(d).ToOffset()).ToArray();
        Assert.Equal(new OffsetCoord[] { new(4, 1), new(5, 1), new(5, 2), new(4, 3), new(3, 2), new(3, 1) }, even);

        // Odd column (5,3): N, NE, SE, S, SW, NW
        var odd = HexDirections.All.Select(d => HexCoord.FromOffset(5, 3).Neighbor(d).ToOffset()).ToArray();
        Assert.Equal(new OffsetCoord[] { new(5, 2), new(6, 3), new(6, 4), new(5, 4), new(4, 4), new(4, 3) }, odd);
    }

    [Fact]
    public void North_and_south_neighbors_stay_in_the_same_column()
    {
        // Flat-top: the top edge faces North, so a column is a straight vertical line of hexes.
        for (var col = 0; col < 12; col++)
        {
            var h = HexCoord.FromOffset(col, 5);
            Assert.Equal(new OffsetCoord(col, 4), h.Neighbor(HexDirection.North).ToOffset());
            Assert.Equal(new OffsetCoord(col, 6), h.Neighbor(HexDirection.South).ToOffset());
        }
    }

    [Fact]
    public void Distance_is_cube_distance()
    {
        var a = HexCoord.FromOffset(0, 0);
        Assert.Equal(0, a.DistanceTo(a));
        Assert.Equal(11, a.DistanceTo(HexCoord.FromOffset(11, 0)));
        Assert.Equal(9, a.DistanceTo(HexCoord.FromOffset(0, 9)));
        Assert.Equal(15, a.DistanceTo(HexCoord.FromOffset(11, 9)));
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

    [Fact]
    public void Clockwise_rotation_cycles_through_all_six_directions()
    {
        var d = HexDirection.North;
        var seen = new List<HexDirection>();
        for (var i = 0; i < 6; i++)
        {
            seen.Add(d);
            d = d.RotateClockwise();
        }

        Assert.Equal(HexDirections.All, seen);
        Assert.Equal(HexDirection.North, d);
        Assert.Equal(HexDirection.NorthWest, HexDirection.North.RotateClockwise(-1));
    }
}
