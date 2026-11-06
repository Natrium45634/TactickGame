using System;
using System.Collections.Generic;
using HexTactics.Core.Hex;
using HexTactics.Core.Pathfinding;
using HexTactics.Core.Units;

namespace HexTactics.Core.Orders;

public enum GroupPlanError
{
    None = 0,
    UnknownUnit,
    EmptyGroup,

    /// <summary>The captain must be the first member (the first unit clicked, spec part 2 §6.1).</summary>
    CaptainNotFirst,
    TooManyMembers,
    DuplicateMember,

    /// <summary>A member is not on the captain's side.</summary>
    NotAlly,
    NoWaypoints,
    OutOfBounds,
    NotWalkable,

    /// <summary>The КТД hex is taken by a unit (only the captain may be standing there).</summary>
    Occupied,
    SameAsSource,
    CaptainUnreachable,

    /// <summary>Not enough free reachable hexes around the КТД for every companion (spec part 2 §6.2).</summary>
    NotEnoughSlots,
}

/// <summary>The formation at one КТД: captain on the КТД, every companion on its own neighbouring slot.</summary>
/// <param name="TotalCost">Summed path length of all members to their destinations.</param>
/// <param name="Conflicts">
/// Arrival-order conflicts of the chosen formation (a place on another member's way, see <see cref="GroupStageEvaluator"/>);
/// 0 means the team can arrive in any order without anyone standing in somebody's way.
/// </param>
public sealed record GroupStagePlan(int WaypointIndex, HexCoord Target, GroupPlanError Error, IReadOnlyList<GroupSlotAssignment> Assignments, int TotalCost, int Conflicts = 0)
{
    public bool IsValid => Error == GroupPlanError.None;

    public HexCoord? DestinationOf(UnitId unit)
    {
        foreach (var a in Assignments)
        {
            if (a.Unit == unit)
            {
                return a.Destination;
            }
        }

        return null;
    }
}

/// <summary>Plans of all stages of a КТД chain; <see cref="FailedIndex"/> is the first impossible КТД (or -1).</summary>
public sealed record GroupChainPlan(IReadOnlyList<GroupStagePlan> Stages, int FailedIndex, GroupPlanError Error)
{
    public bool IsValid => Error == GroupPlanError.None;
}

/// <summary>
/// Evaluates candidate КТД hexes for one stage from fixed member start hexes (spec part 2 §6.2). Shortest-path trees of
/// all members are computed once, so the same evaluator can colour all 120 hexes and follow the cursor cheaply.
/// <list type="bullet">
/// <item>The captain must reach the КТД itself; companions never target it.</item>
/// <item>Companions take distinct free, walkable, reachable hexes of the first ring around the КТД (up to six companions,
/// i.e. teams of at most seven, §6.2; larger teams are not specified yet, §14.8).</item>
/// <item>The team may arrive in any order, so a member waiting on its place must not stand in another member's way
/// ("mutual infinite blocking", §6.2). The assignment therefore first minimises arrival conflicts — a companion's place
/// on the captain's way, a companion's way through the КТД, a companion's way through another companion's place — and
/// then the summed path length (§6.2: short paths, compact formation).</item>
/// <item>Ties are broken by the clockwise slot order (N, NE, SE, S, SW, NW) of the members in collection order. The result
/// depends only on the state and the inputs — stable while the cursor moves (§10 determinism).</item>
/// <item>Hexes of other members do not block (they move with the group); hexes of non-members do.</item>
/// </list>
/// </summary>
public sealed class GroupStageEvaluator
{
    /// <summary>Weight of one conflict in the objective: always more than any possible length difference.</summary>
    private const int ConflictWeight = 4096;

    private readonly GameState _state;
    private readonly IReadOnlyList<UnitId> _members;
    private readonly HashSet<UnitId> _memberSet;
    private readonly IReadOnlyList<HexCoord> _sources;
    private readonly IReadOnlyDictionary<HexCoord, int>[] _distances;
    private readonly IReadOnlyDictionary<HexCoord, HexCoord>[] _parents;
    private readonly int _waypointIndex;

    /// <param name="members">All members, captain first.</param>
    /// <param name="sources">Start hex of every member for this stage (parallel to <paramref name="members"/>).</param>
    public GroupStageEvaluator(GameState state, IReadOnlyList<UnitId> members, IReadOnlyList<HexCoord> sources, int waypointIndex)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(sources);
        if (members.Count == 0 || members.Count != sources.Count)
        {
            throw new ArgumentException("Members and sources must be non-empty and parallel.");
        }

        _state = state;
        _members = members;
        _memberSet = new HashSet<UnitId>(members);
        _sources = sources;
        _waypointIndex = waypointIndex;
        var obstacles = GroupRoutePlanner.ObstaclesFor(state, _memberSet);
        _distances = new IReadOnlyDictionary<HexCoord, int>[members.Count];
        _parents = new IReadOnlyDictionary<HexCoord, HexCoord>[members.Count];
        for (var i = 0; i < members.Count; i++)
        {
            (_distances[i], _parents[i]) = Pathfinder.ShortestPathTree(state.Grid, sources[i], obstacles);
        }
    }

    public UnitId Captain => _members[0];

    /// <summary>Start hex of the captain for this stage (the previous КТД or the captain's anchor).</summary>
    public HexCoord CaptainSource => _sources[0];

    public GroupStagePlan Evaluate(HexCoord target)
    {
        var error = ValidateTarget(target);
        if (error != GroupPlanError.None)
        {
            return Fail(target, error);
        }

        if (!_distances[0].TryGetValue(target, out var captainCost))
        {
            return Fail(target, GroupPlanError.CaptainUnreachable);
        }

        var slots = CandidateSlots(target);
        var companions = _members.Count - 1;
        if (companions > slots.Count)
        {
            return Fail(target, GroupPlanError.NotEnoughSlots);
        }

        // Per companion and slot: path length and the hexes strictly between start and slot on its shortest path.
        var captainWay = Interior(0, target);
        var dist = new int[companions, slots.Count];
        var ways = new HashSet<HexCoord>?[companions, slots.Count];
        var baseConflicts = new int[companions, slots.Count];
        for (var k = 0; k < companions; k++)
        {
            for (var s = 0; s < slots.Count; s++)
            {
                if (!_distances[k + 1].TryGetValue(slots[s], out var d))
                {
                    dist[k, s] = -1;
                    continue;
                }

                dist[k, s] = d;
                ways[k, s] = Interior(k + 1, slots[s]);
                baseConflicts[k, s] = (captainWay.Contains(slots[s]) ? 1 : 0) + (ways[k, s]!.Contains(target) ? 1 : 0);
            }
        }

        // Exhaustive search (≤ 6 companions × ≤ 6 slots = at most 720 leaves): minimal (conflicts, length); the first
        // optimum in slot order wins, which makes the choice deterministic.
        var best = new int[companions];
        var current = new int[companions];
        var used = new bool[slots.Count];
        var bestScore = int.MaxValue;
        var bestConflicts = 0;
        var bestLength = 0;
        Search(0, 0, 0);
        if (companions > 0 && bestScore == int.MaxValue)
        {
            return Fail(target, GroupPlanError.NotEnoughSlots);
        }

        var assignments = new List<GroupSlotAssignment>(_members.Count) { new(_waypointIndex, _members[0], target) };
        for (var k = 0; k < companions; k++)
        {
            assignments.Add(new GroupSlotAssignment(_waypointIndex, _members[k + 1], slots[best[k]]));
        }

        return new GroupStagePlan(_waypointIndex, target, GroupPlanError.None, assignments, captainCost + bestLength, bestConflicts);

        void Search(int k, int conflicts, int length)
        {
            if ((conflicts * ConflictWeight) + length >= bestScore)
            {
                return;
            }

            if (k == companions)
            {
                bestScore = (conflicts * ConflictWeight) + length;
                bestConflicts = conflicts;
                bestLength = length;
                Array.Copy(current, best, companions);
                return;
            }

            for (var s = 0; s < slots.Count; s++)
            {
                if (used[s] || dist[k, s] < 0)
                {
                    continue;
                }

                // Pairwise: this companion's way through an earlier companion's place, and the other way round.
                var pair = 0;
                for (var j = 0; j < k; j++)
                {
                    var t = current[j];
                    pair += (ways[k, s]!.Contains(slots[t]) ? 1 : 0) + (ways[j, t]!.Contains(slots[s]) ? 1 : 0);
                }

                used[s] = true;
                current[k] = s;
                Search(k + 1, conflicts + baseConflicts[k, s] + pair, length + dist[k, s]);
                used[s] = false;
            }
        }
    }

    /// <summary>
    /// Objective of a given formation (diagnostics/tests): arrival conflicts and summed companion path length, or null if
    /// a companion cannot reach its place. <paramref name="companionSlots"/> is parallel to the companions (members 1…).
    /// </summary>
    public (int Conflicts, int Length)? Score(HexCoord target, IReadOnlyList<HexCoord> companionSlots)
    {
        var captainWay = Interior(0, target);
        var conflicts = 0;
        var length = 0;
        var ways = new HashSet<HexCoord>[companionSlots.Count];
        for (var k = 0; k < companionSlots.Count; k++)
        {
            if (!_distances[k + 1].TryGetValue(companionSlots[k], out var d))
            {
                return null;
            }

            length += d;
            ways[k] = Interior(k + 1, companionSlots[k]);
            conflicts += (captainWay.Contains(companionSlots[k]) ? 1 : 0) + (ways[k].Contains(target) ? 1 : 0);
            for (var j = 0; j < k; j++)
            {
                conflicts += (ways[k].Contains(companionSlots[j]) ? 1 : 0) + (ways[j].Contains(companionSlots[k]) ? 1 : 0);
            }
        }

        return (conflicts, length);
    }

    /// <summary>Free places around <paramref name="target"/> in slot order (see the class remarks).</summary>
    public IReadOnlyList<HexCoord> CandidateSlots(HexCoord target)
    {
        // If a companion starts this stage on the КТД itself, it must not take the captain's start hex: the two would
        // wait for each other (captain → its hex, it → captain's hex).
        var captainStartForbidden = false;
        for (var i = 1; i < _sources.Count; i++)
        {
            captainStartForbidden |= _sources[i] == target;
        }

        var slots = new List<HexCoord>(HexDirections.Count);
        foreach (var dir in HexDirections.All)
        {
            var cell = target.Neighbor(dir);
            if (IsFreeSlot(cell) && !(captainStartForbidden && cell == CaptainSource))
            {
                slots.Add(cell);
            }
        }

        return slots;
    }

    /// <summary>Hexes strictly between member <paramref name="index"/>'s start and <paramref name="goal"/> on its shortest path.</summary>
    private HashSet<HexCoord> Interior(int index, HexCoord goal)
    {
        var cells = new HashSet<HexCoord>();
        var parents = _parents[index];
        var start = _sources[index];
        if (goal == start || !parents.TryGetValue(goal, out var cell))
        {
            return cells;
        }

        while (cell != start)
        {
            cells.Add(cell);
            cell = parents[cell];
        }

        return cells;
    }

    private GroupPlanError ValidateTarget(HexCoord target)
    {
        if (!_state.Grid.Contains(target))
        {
            return GroupPlanError.OutOfBounds;
        }

        if (!_state.Grid[target].IsWalkable)
        {
            return GroupPlanError.NotWalkable;
        }

        // Only the captain may stand on its КТД; a hex being entered/left by a non-member is taken as well.
        var reservedByOutsider = _state.ReservedBy(target) is { } holder && !_memberSet.Contains(holder);
        if (_state.IsOccupiedByOther(target, Captain) || reservedByOutsider)
        {
            return GroupPlanError.Occupied;
        }

        return target == CaptainSource ? GroupPlanError.SameAsSource : GroupPlanError.None;
    }

    private bool IsFreeSlot(HexCoord cell)
    {
        if (!_state.Grid.Contains(cell) || !_state.Grid[cell].IsWalkable)
        {
            return false;
        }

        if (_state.UnitAt(cell) is { } occupant && !_memberSet.Contains(occupant.Id))
        {
            return false;
        }

        return _state.ReservedBy(cell) is not { } holder || _memberSet.Contains(holder);
    }

    private GroupStagePlan Fail(HexCoord target, GroupPlanError error) =>
        new(_waypointIndex, target, error, Array.Empty<GroupSlotAssignment>(), 0);
}

/// <summary>
/// Plans whole КТД chains for a group (spec part 2 §6.2, §10 <c>GroupRoutePlanner</c>). The input preview and the
/// simulation command use the same code, so a green КТД is exactly what will be accepted.
/// </summary>
public static class GroupRoutePlanner
{
    /// <summary>Captain + up to six companions on the first ring (spec part 2 §6.2).</summary>
    public const int MaxMembers = 7;

    /// <summary>Path-finding obstacles for a group: hexes of units that are not members.</summary>
    public static Func<HexCoord, bool> ObstaclesFor(GameState state, IReadOnlySet<UnitId> members) =>
        cell => state.UnitAt(cell) is { } u && !members.Contains(u.Id);

    /// <summary>Checks the member list: non-empty, at most <see cref="MaxMembers"/>, distinct, existing, same side.</summary>
    public static GroupPlanError ValidateMembers(GameState state, IReadOnlyList<UnitId> members)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (members is null || members.Count == 0)
        {
            return GroupPlanError.EmptyGroup;
        }

        if (members.Count > MaxMembers)
        {
            return GroupPlanError.TooManyMembers;
        }

        var seen = new HashSet<UnitId>();
        var captain = state.FindUnit(members[0]);
        foreach (var id in members)
        {
            if (!seen.Add(id))
            {
                return GroupPlanError.DuplicateMember;
            }

            var unit = state.FindUnit(id);
            if (unit is null || captain is null)
            {
                return GroupPlanError.UnknownUnit;
            }

            if (unit.Side != captain.Side)
            {
                return GroupPlanError.NotAlly;
            }
        }

        return GroupPlanError.None;
    }

    /// <summary>Plans every stage: stage k starts where stage k−1 left each member (stage 0 from the anchors).</summary>
    public static GroupChainPlan PlanChain(GameState state, IReadOnlyList<UnitId> members, IReadOnlyList<HexCoord> anchors, IReadOnlyList<HexCoord> waypoints)
    {
        var membersError = ValidateMembers(state, members);
        if (membersError != GroupPlanError.None)
        {
            return new GroupChainPlan(Array.Empty<GroupStagePlan>(), 0, membersError);
        }

        if (waypoints.Count == 0)
        {
            return new GroupChainPlan(Array.Empty<GroupStagePlan>(), 0, GroupPlanError.NoWaypoints);
        }

        var stages = new List<GroupStagePlan>(waypoints.Count);
        var sources = new List<HexCoord>(anchors);
        for (var i = 0; i < waypoints.Count; i++)
        {
            var plan = new GroupStageEvaluator(state, members, sources, i).Evaluate(waypoints[i]);
            stages.Add(plan);
            if (!plan.IsValid)
            {
                return new GroupChainPlan(stages, i, plan.Error);
            }

            for (var m = 0; m < members.Count; m++)
            {
                sources[m] = plan.DestinationOf(members[m])!.Value;
            }
        }

        return new GroupChainPlan(stages, -1, GroupPlanError.None);
    }
}
