using System;
using HexTactics.Core.Hex;
using HexTactics.Core.Map;
using Xunit;

namespace HexTactics.Core.Tests;

public class HexLayoutTests
{
    private readonly HexLayout _layout = new(1.0);

    [Fact]
    public void Centers_round_trip_through_plane_for_whole_map()
    {
        var grid = new HexGrid(12, 10);
        foreach (var hex in grid.Coords)
        {
            Assert.Equal(hex, _layout.PlaneToHex(_layout.HexToPlane(hex)));
        }
    }

    [Fact]
    public void Odd_rows_are_shifted_right_by_half_width()
    {
        var a = _layout.HexToPlane(HexCoord.FromOffset(0, 0));
        var b = _layout.HexToPlane(HexCoord.FromOffset(0, 1));
        Assert.Equal(_layout.HexWidth / 2.0, b.X - a.X, 9);
        Assert.Equal(_layout.RowSpacing, b.Z - a.Z, 9);
    }

    [Fact]
    public void Adjacent_centers_are_exactly_one_hex_width_apart_so_there_are_no_gaps()
    {
        var c = HexCoord.FromOffset(5, 4);
        foreach (var n in c.Neighbors())
        {
            Assert.Equal(_layout.HexWidth, _layout.HexToPlane(c).DistanceTo(_layout.HexToPlane(n)), 9);
        }
    }

    [Fact]
    public void Points_near_corners_and_edges_resolve_to_the_containing_hex()
    {
        var hex = HexCoord.FromOffset(6, 5);
        var center = _layout.HexToPlane(hex);
        for (var i = 0; i < 6; i++)
        {
            // 95% of the way to every corner is still inside this hex.
            var corner = _layout.Corner(hex, i);
            var inside = PlaneVector.Lerp(center, corner, 0.95);
            Assert.Equal(hex, _layout.PlaneToHex(inside));

            // 5% beyond an edge midpoint is in the neighbour.
            var dir = HexDirections.All[i];
            var beyond = center + (HexLayout.DirectionVector(dir) * (_layout.Apothem * 1.05));
            Assert.Equal(hex.Neighbor(dir), _layout.PlaneToHex(beyond));
        }
    }

    [Fact]
    public void Corner_zero_is_the_top_vertex_for_pointy_top_hexes()
    {
        var c = _layout.Corner(new HexCoord(0, 0), 0);
        Assert.Equal(0.0, c.X, 9);
        Assert.Equal(-1.0, c.Z, 9);
    }

    [Fact]
    public void Map_bounds_are_compact_rectangle_about_12_5_by_15_5()
    {
        var grid = new HexGrid(12, 10);
        var (min, max) = grid.PlaneBounds(_layout);
        Assert.Equal(12.5 * Math.Sqrt(3), max.X - min.X, 6);
        Assert.Equal(15.5, max.Z - min.Z, 6);
    }

    [Theory]
    [InlineData(1, 0, HexDirection.East)]
    [InlineData(1, -1.7, HexDirection.NorthEast)]
    [InlineData(-1, -1.7, HexDirection.NorthWest)]
    [InlineData(-1, 0.1, HexDirection.West)]
    [InlineData(-1, 1.7, HexDirection.SouthWest)]
    [InlineData(1, 1.7, HexDirection.SouthEast)]
    public void Plane_vector_quantises_to_six_directions(double x, double z, HexDirection expected)
    {
        Assert.Equal(expected, HexDirections.FromPlaneVector(x, z));
    }

    [Fact]
    public void Zero_vector_has_no_direction()
    {
        Assert.Null(HexDirections.FromPlaneVector(0, 0));
    }

    [Fact]
    public void Direction_vectors_point_to_neighbor_centers()
    {
        var c = HexCoord.FromOffset(3, 3);
        foreach (var dir in HexDirections.All)
        {
            var delta = _layout.HexToPlane(c.Neighbor(dir)) - _layout.HexToPlane(c);
            Assert.Equal(dir, HexDirections.FromPlaneVector(delta.X, delta.Z));
            var v = HexLayout.DirectionVector(dir);
            Assert.Equal(delta.X / delta.Length, v.X, 9);
            Assert.Equal(delta.Z / delta.Length, v.Z, 9);
        }
    }
}
