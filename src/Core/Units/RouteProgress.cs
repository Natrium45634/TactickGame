using HexTactics.Core.Hex;
using HexTactics.Core.Map;

namespace HexTactics.Core.Units;

/// <summary>
/// Progress of a unit along an enemy route (spec part 7 §4.4, §10 <c>EnemyRuntimeState</c>): the route and the index of the visit
/// the unit stands on. It is the source of truth: the unit steps only to the next visit, never to another crossing line, and
/// after a wait it goes on from the same index — no skipped or repeated visits.
/// </summary>
public sealed class RouteProgress
{
    public RouteProgress(EnemyRoute route, int index = 0)
    {
        Route = route;
        Index = index;
    }

    public EnemyRoute Route { get; }

    /// <summary>Index in <see cref="EnemyRoute.Cells"/> of the visit the unit has reached.</summary>
    public int Index { get; internal set; }

    public bool Finished => Index >= Route.Cells.Count - 1;

    /// <summary>The next visit, or null at the end of the route.</summary>
    public HexCoord? Next => Finished ? null : Route.Cells[Index + 1];

    /// <summary>The visit after the next one (smooth curves look one hex ahead), or null.</summary>
    public HexCoord? AfterNext => Index + 2 < Route.Cells.Count ? Route.Cells[Index + 2] : null;
}
