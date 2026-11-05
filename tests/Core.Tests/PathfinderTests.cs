using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Hex;
using HexTactics.Core.Map;
using HexTactics.Core.Pathfinding;
using HexTactics.Tests.Shared;
using Xunit;

namespace HexTactics.Core.Tests;

public class PathfinderTests
{
    private static HexCoord C(int col, int row) => HexCoord.FromOffset(col, row);

    private static void AssertValidPath(HexGrid grid, IReadOnlyList<HexCoord> path, HexCoord start, HexCoord goal)
    {
        Assert.Equal(start, path[0]);
        Assert.Equal(goal, path[^1]);
        for (var i = 1; i < path.Count; i++)
        {
            Assert.Equal(1, path[i - 1].DistanceTo(path[i]));
            Assert.True(grid.IsWalkable(path[i]), $"{path[i]} is not walkable");
        }

        Assert.Equal(path.Count, path.Distinct().Count());
    }

    /// <summary>Reference shortest distance by plain BFS (independent of the A* implementation).</summary>
    private static int? BfsDistance(HexGrid grid, HexCoord start, HexCoord goal, ISet<HexCoord>? blocked = null)
    {
        var dist = new Dictionary<HexCoord, int> { [start] = 0 };
        var q = new Queue<HexCoord>();
        q.Enqueue(start);
        while (q.Count > 0)
        {
            var c = q.Dequeue();
            if (c == goal)
            {
                return dist[c];
            }

            foreach (var n in c.Neighbors())
            {
                if (grid.IsWalkable(n) && !(blocked?.Contains(n) ?? false) && !dist.ContainsKey(n))
                {
                    dist[n] = dist[c] + 1;
                    q.Enqueue(n);
                }
            }
        }

        return null;
    }

    [Fact]
    public void Empty_map_path_length_equals_cube_distance()
    {
        var grid = TestMaps.Empty().Grid;
        foreach (var (a, b) in new[] { (C(0, 0), C(11, 9)), (C(3, 7), C(8, 1)), (C(11, 0), C(0, 9)), (C(5, 5), C(6, 5)) })
        {
            var path = Pathfinder.FindPath(grid, a, b)!;
            AssertValidPath(grid, path, a, b);
            Assert.Equal(a.DistanceTo(b) + 1, path.Count);
        }
    }

    [Fact]
    public void Path_to_self_is_single_hex()
    {
        var grid = TestMaps.Empty().Grid;
        Assert.Equal(new[] { C(4, 4) }, Pathfinder.FindPath(grid, C(4, 4), C(4, 4)));
    }

    [Fact]
    public void Shortest_detour_around_wall_on_prototype_map()
    {
        var grid = TestMaps.LoadPrototype().Grid;
        // The wall at column 4 (rows 2..6) lies between these hexes.
        var a = C(2, 4);
        var b = C(6, 4);
        var path = Pathfinder.FindPath(grid, a, b)!;
        AssertValidPath(grid, path, a, b);
        Assert.Equal(BfsDistance(grid, a, b), path.Count - 1);
        Assert.True(path.Count - 1 > a.DistanceTo(b), "the wall must force a detour");
        Assert.DoesNotContain(path, c => grid[c].Type == TileType.Blocked);
    }

    [Fact]
    public void Isolated_target_is_unreachable_even_though_free()
    {
        var grid = TestMaps.LoadPrototype().Grid;
        var pocket = C(8, 3);
        Assert.True(grid.IsWalkable(pocket));
        Assert.Null(Pathfinder.FindPath(grid, C(0, 0), pocket));
        Assert.DoesNotContain(pocket, Pathfinder.Reachable(grid, C(0, 0)));
    }

    [Fact]
    public void Blocked_or_outside_goal_returns_null()
    {
        var grid = TestMaps.LoadPrototype().Grid;
        Assert.Null(Pathfinder.FindPath(grid, C(0, 0), C(4, 4)));
        Assert.Null(Pathfinder.FindPath(grid, C(0, 0), C(12, 4)));
        Assert.Null(Pathfinder.FindPath(grid, C(0, 0), C(-1, -1)));
    }

    [Fact]
    public void Dynamic_obstacles_are_avoided_on_every_step_and_start_is_never_blocked()
    {
        var grid = TestMaps.Empty().Grid;
        var start = C(2, 5);
        var goal = C(8, 5);
        var blocked = new HashSet<HexCoord> { start, C(4, 4), C(4, 5), C(4, 6), C(5, 4), C(5, 5), C(5, 6) };
        var path = Pathfinder.FindPath(grid, start, goal, blocked.Contains)!;
        AssertValidPath(grid, path, start, goal);
        Assert.DoesNotContain(path.Skip(1), blocked.Contains);
        Assert.Equal(BfsDistance(grid, start, goal, blocked), path.Count - 1);
    }

    [Fact]
    public void Result_is_deterministic_for_equal_paths()
    {
        var grid = TestMaps.LoadPrototype().Grid;
        var a = C(1, 8);
        var b = C(10, 1);
        var first = Pathfinder.FindPath(grid, a, b)!;
        for (var i = 0; i < 20; i++)
        {
            // Unrelated queries in between must not influence the result.
            Pathfinder.FindPath(grid, C(i % 12, i % 10), C(11 - (i % 12), 9 - (i % 10)));
            Assert.Equal(first, Pathfinder.FindPath(grid, a, b));
        }
    }

    [Fact]
    public void Equal_length_alternatives_prefer_the_straight_line()
    {
        var grid = TestMaps.Empty().Grid;
        // Straight east along a row: the unique straight path is preferred among many equal-length zig-zags.
        var path = Pathfinder.FindPath(grid, C(1, 4), C(9, 4))!;
        Assert.All(path, c => Assert.Equal(4, c.ToOffset().Row));
    }

    [Fact]
    public void Shortest_path_matches_bfs_for_all_pairs_on_prototype_map()
    {
        var grid = TestMaps.LoadPrototype().Grid;
        var cells = grid.Coords.Where(grid.IsWalkable).ToList();
        foreach (var a in cells.Where((_, i) => i % 7 == 0))
        {
            foreach (var b in cells)
            {
                var expected = BfsDistance(grid, a, b);
                var path = Pathfinder.FindPath(grid, a, b);
                if (expected is null)
                {
                    Assert.Null(path);
                }
                else
                {
                    Assert.NotNull(path);
                    Assert.Equal(expected, path!.Count - 1);
                }
            }
        }
    }

    [Fact]
    public void Weighted_tiles_are_respected()
    {
        var grid = new HexGrid(7, 3);
        // Make the direct middle row expensive: the cheaper path goes around through row 0.
        for (var col = 1; col <= 5; col++)
        {
            grid.SetTile(C(col, 1), TileType.Walkable, moveCost: 5);
        }

        var path = Pathfinder.FindPath(grid, C(0, 1), C(6, 1))!;
        Assert.DoesNotContain(path, c => grid[c].MoveCost > 1);
        var direct = new[] { C(0, 1), C(1, 1), C(2, 1), C(3, 1), C(4, 1), C(5, 1), C(6, 1) };
        Assert.True(Pathfinder.Cost(grid, path) < Pathfinder.Cost(grid, direct));
    }

    [Fact]
    public void Distance_field_counts_steps()
    {
        var grid = TestMaps.Empty().Grid;
        var field = Pathfinder.DistanceField(grid, C(5, 5));
        Assert.Equal(120, field.Count);
        Assert.All(field, kv => Assert.Equal(C(5, 5).DistanceTo(kv.Key), kv.Value));
    }

    [Fact]
    public void Reachable_set_on_prototype_excludes_blocked_and_pocket()
    {
        var map = TestMaps.LoadPrototype();
        var reachable = Pathfinder.Reachable(map.Grid, C(0, 4));
        var walkable = map.Grid.Coords.Count(map.Grid.IsWalkable);
        Assert.Equal(walkable - 1, reachable.Count); // everything except the isolated pocket
    }
}
