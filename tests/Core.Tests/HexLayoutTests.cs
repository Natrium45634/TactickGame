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
    public void Odd_columns_are_shifted_down_by_half_a_hex_height()
    {
        var a = _layout.HexToPlane(HexCoord.FromOffset(0, 0));
        var b = _layout.HexToPlane(HexCoord.FromOffset(1, 0));
        var c = _layout.HexToPlane(HexCoord.FromOffset(0, 1));
        Assert.Equal(_layout.ColumnSpacing, b.X - a.X, 9);
        Assert.Equal(_layout.HexHeight / 2.0, b.Z - a.Z, 9);
        Assert.Equal(0.0, c.X - a.X, 9);
        Assert.Equal(_layout.HexHeight, c.Z - a.Z, 9);
    }

    [Fact]
    public void Adjacent_centers_are_equally_spaced_so_there_are_no_gaps()
    {
        var c = HexCoord.FromOffset(5, 4);
        foreach (var n in c.Neighbors())
        {
            Assert.Equal(_layout.NeighborDistance, _layout.HexToPlane(c).DistanceTo(_layout.HexToPlane(n)), 9);
        }

        Assert.Equal(2 * _layout.Apothem, _layout.NeighborDistance, 9);
    }

    [Fact]
    public void Flat_top_hex_has_horizontal_top_and_bottom_edges()
    {
        var h = new HexCoord(0, 0);
        // Corners 1-2 form the top edge, 4-5 the bottom edge: equal Z within each pair.
        Assert.Equal(_layout.Corner(h, 1).Z, _layout.Corner(h, 2).Z, 9);
        Assert.Equal(_layout.Corner(h, 4).Z, _layout.Corner(h, 5).Z, 9);
        Assert.Equal(-_layout.Apothem, _layout.Corner(h, 1).Z, 9);
        // Corner 0 is the east vertex, corner 3 the west vertex.
        Assert.Equal(new PlaneVector(1, 0), _layout.Corner(h, 0));
        Assert.Equal(-1.0, _layout.Corner(h, 3).X, 9);
        Assert.Equal(0.0, _layout.Corner(h, 3).Z, 9);
    }

    [Fact]
    public void Points_near_corners_and_edges_resolve_to_the_containing_hex()
    {
        var hex = HexCoord.FromOffset(6, 5);
        var center = _layout.HexToPlane(hex);
        for (var i = 0; i < 6; i++)
        {
            var corner = _layout.Corner(hex, i);
            Assert.Equal(hex, _layout.PlaneToHex(PlaneVector.Lerp(center, corner, 0.95)));

            var dir = HexDirections.All[i];
            var beyond = center + (HexLayout.DirectionVector(dir) * (_layout.Apothem * 1.05));
            Assert.Equal(hex.Neighbor(dir), _layout.PlaneToHex(beyond));
            Assert.Equal(hex, _layout.PlaneToHex(center + (HexLayout.DirectionVector(dir) * (_layout.Apothem * 0.95))));
        }
    }

    [Fact]
    public void Each_direction_points_at_the_middle_of_one_edge()
    {
        var hex = HexCoord.FromOffset(4, 4);
        for (var i = 0; i < 6; i++)
        {
            // Edge between corners i and i+1 faces direction NE, N, NW, SW, S, SE respectively.
            var mid = PlaneVector.Lerp(_layout.Corner(hex, i), _layout.Corner(hex, (i + 1) % 6), 0.5);
            var d = mid - _layout.HexToPlane(hex);
            var dir = HexDirections.FromPlaneVector(d.X, d.Z)!.Value;
            Assert.Equal(_layout.EdgeMidpoint(hex, dir).X, mid.X, 9);
            Assert.Equal(_layout.EdgeMidpoint(hex, dir).Z, mid.Z, 9);
        }

        Assert.Equal(HexDirection.North, HexDirections.FromPlaneVector(0, -1)); // the flat top edge
    }

    [Fact]
    public void Map_bounds_are_compact_rectangle_18_5_by_10_5_sqrt3()
    {
        var grid = new HexGrid(12, 10);
        var (min, max) = grid.PlaneBounds(_layout);
        Assert.Equal(18.5, max.X - min.X, 6);
        Assert.Equal(10.5 * Math.Sqrt(3), max.Z - min.Z, 6);
    }

    [Theory]
    [InlineData(0, -1, HexDirection.North)]
    [InlineData(1, -0.5, HexDirection.NorthEast)]
    [InlineData(1, 0.5, HexDirection.SouthEast)]
    [InlineData(0.1, 1, HexDirection.South)]
    [InlineData(-1, 0.5, HexDirection.SouthWest)]
    [InlineData(-1, -0.5, HexDirection.NorthWest)]
    public void Plane_vector_quantises_to_six_edge_directions(double x, double z, HexDirection expected)
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
