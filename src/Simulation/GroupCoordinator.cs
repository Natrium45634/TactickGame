using System.Collections.Generic;
using HexTactics.Core.Hex;
using HexTactics.Core.Orders;
using HexTactics.Core.Units;

namespace HexTactics.Simulation;

/// <summary>
/// Synchronised execution of command orders (spec part 2 §6.2): the team visits the КТД in order — no member heads for
/// the formation of КТД k+1 before every member has reached its place at КТД k. A member's progress is the number of
/// stages it has completed (detours do not count). The order ends for everybody at once when every member has
/// finished, so finished members still count as teammates (they can be asked to step aside).
/// </summary>
internal static class GroupCoordinator
{
    /// <summary>Completed stages of a member (0 … number of КТД).</summary>
    public static int Progress(GroupOrderState group, UnitState unit) => group.Waypoints.Count - unit.Orders.OrderCount;

    /// <summary>Smallest progress among the units still executing the order.</summary>
    public static int TeamProgress(GameState state, GroupOrderState group)
    {
        var min = int.MaxValue;
        foreach (var id in group.Members)
        {
            if (state.FindUnit(id) is { } u && u.Orders.GroupId == group.Id)
            {
                min = System.Math.Min(min, Progress(group, u));
            }
        }

        return min == int.MaxValue ? group.Waypoints.Count : min;
    }

    /// <summary>True if the unit already stands at a later stage than the slowest teammate and must wait there.</summary>
    public static bool IsAheadOfTeam(GameState state, GroupOrderState group, UnitState unit) =>
        Progress(group, unit) > TeamProgress(state, group);

    public static bool AreTeammates(GameState state, UnitState unit, UnitState other) =>
        other.Id != unit.Id && unit.Orders.GroupId is { } g && other.Orders.GroupId == g && state.FindGroup(g) is not null;

    /// <summary>Path-finding obstacles for a member that ignore its teammates (to tell "closed by teammates" apart).</summary>
    public static System.Func<HexCoord, bool> ObstaclesIgnoringTeammates(GameState state, UnitState unit) =>
        cell => state.UnitAt(cell) is { } u && u.Id != unit.Id && !AreTeammates(state, unit, u);

    /// <summary>
    /// Ends command orders whose members have all finished (empty queues, final facings applied, standing still):
    /// the members' queues become personal again and the order is dropped.
    /// </summary>
    public static void CompleteFinished(GameState state)
    {
        List<GroupOrderState>? done = null;
        foreach (var group in state.Groups)
        {
            var finished = true;
            foreach (var id in group.Members)
            {
                if (state.FindUnit(id) is { } u && u.Orders.GroupId == group.Id
                    && (!u.Orders.IsEmpty || u.Orders.FinalFacing is not null || u.Motion.IsStepping))
                {
                    finished = false;
                    break;
                }
            }

            if (finished)
            {
                (done ??= new List<GroupOrderState>()).Add(group);
            }
        }

        if (done is null)
        {
            return;
        }

        foreach (var group in done)
        {
            foreach (var id in group.Members)
            {
                if (state.FindUnit(id) is { } u && u.Orders.GroupId == group.Id)
                {
                    state.SetQueueGroup(id, null);
                }
            }
        }
    }
}
