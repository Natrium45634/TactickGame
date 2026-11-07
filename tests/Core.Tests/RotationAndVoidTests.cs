using System.Linq;
using HexTactics.Core.Hex;
using HexTactics.Core.Map;
using HexTactics.Core.Pathfinding;
using HexTactics.Core.Skills;
using Xunit;

namespace HexTactics.Core.Tests;

/// <summary>One geometric rotation for the six facings (spec part 4 §6.1, §8.2) and empty map places (§4.1).</summary>
public class RotationAndVoidTests
{
    [Fact]
    public void Rotating_an_offset_follows_the_six_neighbour_directions_clockwise()
    {
        var forward = HexCoord.DirectionOffset(HexDirection.North);
        foreach (var d in HexDirections.All)
        {
            Assert.Equal(HexCoord.DirectionOffset(d), forward.RotateFor(d));
        }

        var shape = new HexCoord(1, -3); // an arbitrary far cell
        Assert.Equal(shape, shape.RotateClockwise(6));
        Assert.Equal(shape.RotateClockwise(-1), shape.RotateClockwise(5));
        Assert.Equal(shape.DistanceTo(default), shape.RotateClockwise(2).DistanceTo(default));
    }

    [Fact]
    public void Rotation_never_mirrors_a_shape()
    {
        // An asymmetric "L": forward, forward-forward, then one to the front-right. Orientation (cross product sign on the
        // plane) must stay the same for all six rotations.
        var layout = new HexLayout(1.0);
        var a = new HexCoord(0, -1);
        var b = new HexCoord(1, -2);
        foreach (var d in HexDirections.All)
        {
            var pa = layout.HexToPlane(a.RotateFor(d));
            var pb = layout.HexToPlane(b.RotateFor(d));
            var cross = (pa.X * pb.Z) - (pa.Z * pb.X);
            var pa0 = layout.HexToPlane(a);
            var pb0 = layout.HexToPlane(b);
            Assert.Equal(System.Math.Sign((pa0.X * pb0.Z) - (pa0.Z * pb0.X)), System.Math.Sign(cross));
        }
    }

    [Fact]
    public void Empty_places_are_impassable_not_edges_of_themselves_and_drop_out_of_skill_areas()
    {
        var grid = new HexGrid(5, 5);
        var hole = HexCoord.FromOffset(2, 1);
        grid.SetTile(hole, TileType.Void, null, 0);
        Assert.False(grid.Exists(hole));
        Assert.False(grid.IsWalkable(hole));
        Assert.False(grid.IsEdge(hole));
        Assert.True(grid.IsEdge(HexCoord.FromOffset(2, 2))); // a tile next to an empty place is on the edge

        var path = Pathfinder.FindPath(grid, HexCoord.FromOffset(2, 0), HexCoord.FromOffset(2, 2));
        Assert.NotNull(path);
        Assert.DoesNotContain(hole, path!);

        var area = SkillCatalog.LinearStrike().Area(HexCoord.FromOffset(2, 3), HexDirection.North, grid);
        Assert.Equal(new[] { HexCoord.FromOffset(2, 2) }, area);
    }
}
