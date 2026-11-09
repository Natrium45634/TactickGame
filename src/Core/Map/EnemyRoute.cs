using System.Collections.Generic;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Map;

/// <summary>Whose base a base marker of the map is (spec part 7 §4.1).</summary>
public enum BaseSide
{
    Enemy = 0,
    Player = 1,
}

/// <summary>
/// A numbered base of the map (spec part 7 §4.1, §10 <c>baseMarkers</c>): the base tiles of one side with one number. A map may
/// have several bases of each side; the role comes from the tile type data, never from the colour.
/// </summary>
public sealed record MapBase(BaseSide Side, int Number, string Name, IReadOnlyList<HexCoord> Cells);

/// <summary>
/// An enemy route (spec part 7 §4.1, §10 <c>EnemyRouteDefinition</c>): an ordered list of hex visits from an enemy base to a
/// player base. Visits may repeat and routes may cross; a walker keeps its own index into <see cref="Cells"/>.
/// </summary>
public sealed record EnemyRoute(string Id, string Name, int SourceBase, int TargetBase, IReadOnlyList<HexCoord> Cells, bool Enabled = true);

/// <summary>What is wrong with a route.</summary>
public enum RouteProblemKind
{
    TooShort = 0,
    StartNotEnemyBase = 1,
    EndNotPlayerBase = 2,
    NotAdjacent = 3,
    MissingHex = 4,
    NotWalkable = 5,
}

/// <summary>One problem of a route: the visit index, the hex and a sentence for the author.</summary>
public sealed record RouteProblem(RouteProblemKind Kind, int Index, HexCoord Cell, string Message);

/// <summary>
/// The route check (spec part 7 §4.3): the same walkability model as real movement (<see cref="TileState.IsWalkable"/>); a route
/// starts on its enemy base, ends on its player base, every pair of visits is one hex step, every visit is an existing walkable
/// hex. Repeated visits and crossings are allowed. Relief levels do not restrict movement in this model, so they are not checked.
/// </summary>
public static class RouteCheck
{
    public static IReadOnlyList<RouteProblem> Check(MapDefinition map, EnemyRoute route)
    {
        var problems = new List<RouteProblem>();
        var cells = route.Cells;
        if (cells.Count < 2)
        {
            problems.Add(new RouteProblem(RouteProblemKind.TooShort, 0, cells.Count > 0 ? cells[0] : default,
                "в маршруте меньше двух гексов — нужен путь от базы врага до базы игрока"));
            return problems;
        }

        if (map.BaseAt(cells[0]) is not { Side: BaseSide.Enemy } source || source.Number != route.SourceBase)
        {
            problems.Add(new RouteProblem(RouteProblemKind.StartNotEnemyBase, 0, cells[0],
                $"начало {Show(cells[0])} — не гекс базы врага {route.SourceBase}"));
        }

        var last = cells.Count - 1;
        if (map.BaseAt(cells[last]) is not { Side: BaseSide.Player } target || target.Number != route.TargetBase)
        {
            problems.Add(new RouteProblem(RouteProblemKind.EndNotPlayerBase, last, cells[last],
                $"конец {Show(cells[last])} — не гекс базы игрока {route.TargetBase}"));
        }

        for (var i = 0; i < cells.Count; i++)
        {
            var c = cells[i];
            if (i > 0 && cells[i - 1].DistanceTo(c) != 1)
            {
                problems.Add(new RouteProblem(RouteProblemKind.NotAdjacent, i, c, $"шаг {i}: {Show(cells[i - 1])} → {Show(c)} — не соседние гексы"));
            }

            if (!map.Grid.Exists(c))
            {
                problems.Add(new RouteProblem(RouteProblemKind.MissingHex, i, c, $"шаг {i}: гекса {Show(c)} нет на карте"));
            }
            else if (!map.Grid.IsWalkable(c))
            {
                problems.Add(new RouteProblem(RouteProblemKind.NotWalkable, i, c, $"шаг {i}: гекс {Show(c)} непроходим"));
            }
        }

        return problems;
    }

    /// <summary>«(6,2)» — offset coordinates as the map editor shows them.</summary>
    public static string Show(HexCoord c)
    {
        var o = c.ToOffset();
        return $"({o.Col},{o.Row})";
    }
}
