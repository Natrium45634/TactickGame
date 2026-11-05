using System.Linq;
using HexTactics.Core.Hex;
using Xunit;

namespace HexTactics.Core.Tests;

public class HexLineTests
{
    [Fact]
    public void Line_has_distance_plus_one_adjacent_hexes()
    {
        var a = HexCoord.FromOffset(1, 1);
        var b = HexCoord.FromOffset(9, 7);
        var line = HexLine.Between(a, b);
        Assert.Equal(a.DistanceTo(b) + 1, line.Count);
        Assert.Equal(a, line[0]);
        Assert.Equal(b, line[^1]);
        for (var i = 1; i < line.Count; i++)
        {
            Assert.Equal(1, line[i - 1].DistanceTo(line[i]));
        }
    }

    [Fact]
    public void First_step_direction_for_neighbors_is_the_neighbor_direction()
    {
        var c = HexCoord.FromOffset(5, 5);
        foreach (var d in HexDirections.All)
        {
            Assert.Equal(d, HexLine.FirstStepDirection(c, c.Neighbor(d)));
        }

        Assert.Null(HexLine.FirstStepDirection(c, c));
    }

    [Fact]
    public void Line_is_deterministic()
    {
        var a = HexCoord.FromOffset(0, 0);
        var b = HexCoord.FromOffset(6, 4);
        Assert.True(HexLine.Between(a, b).SequenceEqual(HexLine.Between(a, b)));
    }
}
