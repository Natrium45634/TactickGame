using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Hex;
using HexTactics.Core.Units;

namespace HexTactics.Core.Orders;

/// <summary>Where one member of a group stands at one command waypoint (КТД) of the chain (spec part 2 §6.2, §10).</summary>
public sealed record GroupSlotAssignment(int WaypointIndex, UnitId Unit, HexCoord Destination);

/// <summary>
/// One assembled command order (spec part 2 §6.4): a fixed member list, the captain (always the first member, chosen by
/// the first click), the chain of command waypoints and the captain's mandatory final facing chosen by the player.
/// Companions get their slots and facings automatically.
/// </summary>
public sealed record GroupOrder(UnitId Captain, IReadOnlyList<UnitId> Members, IReadOnlyList<HexCoord> GroupWaypoints, HexDirection FinalCaptainFacing);

/// <summary>
/// A command order accepted by the simulation: the plan of every stage (who goes where) and the final facings of all
/// members, fixed at confirmation. Members execute their individual routes inside it (spec part 2 §6.4).
/// </summary>
public sealed class GroupOrderState
{
    internal GroupOrderState(
        int id,
        UnitId captain,
        IReadOnlyList<UnitId> members,
        IReadOnlyList<HexCoord> waypoints,
        IReadOnlyList<IReadOnlyList<GroupSlotAssignment>> stages,
        IReadOnlyDictionary<UnitId, HexDirection> finalFacings)
    {
        Id = id;
        Captain = captain;
        Members = members;
        Waypoints = waypoints;
        Stages = stages;
        FinalFacings = finalFacings;
    }

    public int Id { get; }

    public UnitId Captain { get; }

    /// <summary>All members, captain first (in the order they were collected).</summary>
    public IReadOnlyList<UnitId> Members { get; }

    /// <summary>The command waypoints (КТД) — the captain's targets.</summary>
    public IReadOnlyList<HexCoord> Waypoints { get; }

    /// <summary>Per КТД: the destination of every member (captain on the КТД, companions on neighbouring slots).</summary>
    public IReadOnlyList<IReadOnlyList<GroupSlotAssignment>> Stages { get; }

    /// <summary>Final facing of every member (captain: chosen by the player; companions: assigned at confirmation).</summary>
    public IReadOnlyDictionary<UnitId, HexDirection> FinalFacings { get; }

    public bool IsMember(UnitId unit) => Members.Contains(unit);

    public HexCoord DestinationOf(UnitId unit, int waypointIndex)
    {
        foreach (var a in Stages[waypointIndex])
        {
            if (a.Unit == unit)
            {
                return a.Destination;
            }
        }

        throw new ArgumentException($"Unit {unit} is not a member of group {Id}.", nameof(unit));
    }
}
