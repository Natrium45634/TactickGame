using System;
using System.Collections.Generic;
using HexTactics.Core.Hex;
using HexTactics.Core.Map;

namespace HexTactics.Core.Pathfinding;

/// <summary>
/// Shortest paths on the six-neighbour hex grid. A* with the cube-distance heuristic (admissible
/// because every step costs at least 1) and tile weights (<see cref="TileState.MoveCost"/>).
/// <para>
/// Determinism: neighbours are always expanded in the fixed <see cref="HexDirections.All"/> order and
/// ties in the open list are broken by (f, h, deviation from the straight start→goal line, insertion
/// order). A predecessor is only replaced by a strictly cheaper one. The same input therefore always
/// yields the same path, and among equally short paths the one closest to the straight line wins,
/// which keeps previews visually stable.
/// </para>
/// </summary>
public static class Pathfinder
{
    private static readonly HexLayout UnitLayout = new(1.0);

    /// <summary>
    /// Finds a shortest path from <paramref name="start"/> to <paramref name="goal"/> (both included).
    /// <paramref name="isBlocked"/> marks dynamic obstacles (e.g. other units); terrain passability is
    /// always taken from the grid. The start hex itself is never treated as blocked.
    /// Returns null if the goal is outside the map, blocked or unreachable.
    /// </summary>
    public static IReadOnlyList<HexCoord>? FindPath(HexGrid grid, HexCoord start, HexCoord goal, Func<HexCoord, bool>? isBlocked = null)
    {
        ArgumentNullException.ThrowIfNull(grid);
        if (!grid.Contains(start) || !grid.Contains(goal))
        {
            return null;
        }

        if (start == goal)
        {
            return new[] { start };
        }

        if (!Passable(grid, goal, start, isBlocked))
        {
            return null;
        }

        var startPos = UnitLayout.HexToPlane(start);
        var goalPos = UnitLayout.HexToPlane(goal);
        var line = goalPos - startPos;
        var lineLength = Math.Max(line.Length, 1e-9);

        var open = new PriorityQueue<HexCoord, NodeKey>(NodeKeyComparer.Instance);
        var g = new Dictionary<HexCoord, int> { [start] = 0 };
        var cameFrom = new Dictionary<HexCoord, HexCoord>();
        var closed = new HashSet<HexCoord>();
        long seq = 0;

        open.Enqueue(start, new NodeKey(start.DistanceTo(goal), start.DistanceTo(goal), 0, seq++));
        while (open.TryDequeue(out var current, out _))
        {
            if (!closed.Add(current))
            {
                continue; // stale entry
            }

            if (current == goal)
            {
                return Reconstruct(cameFrom, start, goal);
            }

            var gCurrent = g[current];
            foreach (var dir in HexDirections.All)
            {
                var next = current.Neighbor(dir);
                if (closed.Contains(next) || !Passable(grid, next, start, isBlocked))
                {
                    continue;
                }

                var tentative = gCurrent + grid[next].MoveCost;
                if (g.TryGetValue(next, out var known) && tentative >= known)
                {
                    continue;
                }

                g[next] = tentative;
                cameFrom[next] = current;
                var h = next.DistanceTo(goal);
                open.Enqueue(next, new NodeKey(tentative + h, h, Deviation(UnitLayout.HexToPlane(next) - startPos, line, lineLength), seq++));
            }
        }

        return null;
    }

    /// <summary>
    /// Minimal movement cost from <paramref name="start"/> to every reachable hex (Dijkstra; with unit
    /// costs this is a breadth-first search). The start hex is included with cost 0.
    /// </summary>
    public static IReadOnlyDictionary<HexCoord, int> DistanceField(HexGrid grid, HexCoord start, Func<HexCoord, bool>? isBlocked = null)
    {
        ArgumentNullException.ThrowIfNull(grid);
        var dist = new Dictionary<HexCoord, int>();
        if (!grid.Contains(start))
        {
            return dist;
        }

        var open = new PriorityQueue<HexCoord, (int Cost, long Seq)>();
        long seq = 0;
        dist[start] = 0;
        open.Enqueue(start, (0, seq++));
        while (open.TryDequeue(out var current, out var key))
        {
            if (key.Cost > dist[current])
            {
                continue;
            }

            foreach (var dir in HexDirections.All)
            {
                var next = current.Neighbor(dir);
                if (!Passable(grid, next, start, isBlocked))
                {
                    continue;
                }

                var cost = key.Cost + grid[next].MoveCost;
                if (!dist.TryGetValue(next, out var known) || cost < known)
                {
                    dist[next] = cost;
                    open.Enqueue(next, (cost, seq++));
                }
            }
        }

        return dist;
    }

    /// <summary>Set of hexes reachable from <paramref name="start"/> (including it).</summary>
    public static IReadOnlySet<HexCoord> Reachable(HexGrid grid, HexCoord start, Func<HexCoord, bool>? isBlocked = null) =>
        new HashSet<HexCoord>(DistanceField(grid, start, isBlocked).Keys);

    /// <summary>Total movement cost of a path (sum of entered tiles' costs).</summary>
    public static int Cost(HexGrid grid, IReadOnlyList<HexCoord> path)
    {
        var cost = 0;
        for (var i = 1; i < path.Count; i++)
        {
            cost += grid[path[i]].MoveCost;
        }

        return cost;
    }

    private static bool Passable(HexGrid grid, HexCoord cell, HexCoord start, Func<HexCoord, bool>? isBlocked) =>
        grid.Contains(cell) && grid[cell].IsWalkable && (cell == start || isBlocked is null || !isBlocked(cell));

    private static IReadOnlyList<HexCoord> Reconstruct(Dictionary<HexCoord, HexCoord> cameFrom, HexCoord start, HexCoord goal)
    {
        var path = new List<HexCoord> { goal };
        var current = goal;
        while (current != start)
        {
            current = cameFrom[current];
            path.Add(current);
        }

        path.Reverse();
        return path;
    }

    /// <summary>Perpendicular distance of a point (relative to start) from the start→goal line.</summary>
    private static double Deviation(PlaneVector p, PlaneVector line, double lineLength) =>
        Math.Round(Math.Abs((p.X * line.Z) - (p.Z * line.X)) / lineLength, 9);

    private readonly record struct NodeKey(int F, int H, double Deviation, long Seq);

    private sealed class NodeKeyComparer : IComparer<NodeKey>
    {
        public static readonly NodeKeyComparer Instance = new();

        public int Compare(NodeKey a, NodeKey b)
        {
            var c = a.F.CompareTo(b.F);
            if (c != 0)
            {
                return c;
            }

            c = a.H.CompareTo(b.H);
            if (c != 0)
            {
                return c;
            }

            c = a.Deviation.CompareTo(b.Deviation);
            return c != 0 ? c : a.Seq.CompareTo(b.Seq);
        }
    }
}
