using System;
using System.Collections.Generic;
using HexTactics.Core.Hex;
using HexTactics.Core.Pathfinding;
using HexTactics.Core.Units;

namespace HexTactics.Core.Orders;

public enum DraftKind
{
    /// <summary>Waypoints (ТД) of one unit.</summary>
    Single = 0,

    /// <summary>Command waypoints (КТД) of a group (from v0.9.0).</summary>
    Group = 1,
}

public enum DraftState
{
    /// <summary>Waypoints can be added; the final facing has not been confirmed yet.</summary>
    Drafting = 0,

    /// <summary>The facing was confirmed and the order was applied as a whole.</summary>
    Committed = 1,

    /// <summary>Discarded (RMB, Esc or an attempt to confirm an invalid hex). Nothing was applied.</summary>
    Cancelled = 2,
}

/// <summary>
/// Transactional order draft (spec part 2 §4.3): while the player places waypoints and chooses the final
/// facing, everything is only a preview; the whole chain is applied at once when the facing is confirmed,
/// or discarded entirely. The draft never changes the game state by itself.
/// </summary>
public sealed class OrderDraft
{
    private readonly List<HexCoord> _waypoints = new();
    private readonly Dictionary<int, HexTactics.Core.Skills.WaypointSkillSpec> _skills = new();

    public OrderDraft(
        UnitId owner,
        DraftKind kind = DraftKind.Single,
        IEnumerable<HexCoord>? initialWaypoints = null,
        bool isEdit = false,
        IReadOnlyDictionary<int, HexTactics.Core.Skills.WaypointSkillSpec>? initialSkills = null)
    {
        Owner = owner;
        Kind = kind;
        IsEdit = isEdit;
        if (initialWaypoints is not null)
        {
            _waypoints.AddRange(initialWaypoints);
        }

        if (initialSkills is not null)
        {
            foreach (var (index, spec) in initialSkills)
            {
                _skills[index] = spec;
            }
        }
    }

    /// <summary>
    /// Skills planned at draft waypoints (index → skill and direction, spec part 3 §6.2): part of the same transaction,
    /// applied together with the final facing; at most one per waypoint.
    /// </summary>
    public IReadOnlyDictionary<int, HexTactics.Core.Skills.WaypointSkillSpec> Skills => _skills;

    /// <summary>Plans (or with null removes) the skill at draft waypoint <paramref name="index"/>.</summary>
    public void SetSkill(int index, HexTactics.Core.Skills.WaypointSkillSpec? spec)
    {
        EnsureDrafting();
        if (index < 0 || index >= _waypoints.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        if (spec is null)
        {
            _skills.Remove(index);
        }
        else
        {
            _skills[index] = spec;
        }
    }

    /// <summary>The unit the draft is for (the captain for group drafts).</summary>
    public UnitId Owner { get; }

    public DraftKind Kind { get; }

    /// <summary>True if the draft started from the unit's existing confirmed route (editing / appending).</summary>
    public bool IsEdit { get; }

    public DraftState State { get; private set; } = DraftState.Drafting;

    public IReadOnlyList<HexCoord> Waypoints => _waypoints;

    /// <summary>Facing currently previewed by hovering a sector (not stored in the unit).</summary>
    public HexDirection? PreviewFacing { get; set; }

    /// <summary>Start hex of the next segment: the last waypoint, or the unit's command anchor.</summary>
    public HexCoord SourceFor(HexCoord anchor) => _waypoints.Count > 0 ? _waypoints[^1] : anchor;

    public void Add(HexCoord cell)
    {
        EnsureDrafting();
        _waypoints.Add(cell);
    }

    /// <summary>Removes the waypoint at <paramref name="index"/> and all following ones (RMB on a draft ТД).</summary>
    public void TruncateFrom(int index)
    {
        EnsureDrafting();
        if (index < 0 || index >= _waypoints.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        _waypoints.RemoveRange(index, _waypoints.Count - index);
        foreach (var key in new List<int>(_skills.Keys))
        {
            if (key >= index)
            {
                _skills.Remove(key); // a removed waypoint takes its skill with it (spec part 3 §6.5)
            }
        }
    }

    public IndividualOrder ToOrder(HexDirection finalFacing) =>
        new(Owner, _waypoints.ToArray(), finalFacing, null, new Dictionary<int, HexTactics.Core.Skills.WaypointSkillSpec>(_skills));

    public IndividualOrder ToAttackOrder(UnitId target, HexDirection facing) =>
        new(Owner, _waypoints.ToArray(), facing, target, new Dictionary<int, HexTactics.Core.Skills.WaypointSkillSpec>(_skills));

    public void MarkCommitted()
    {
        EnsureDrafting();
        State = DraftState.Committed;
    }

    public void MarkCancelled()
    {
        EnsureDrafting();
        State = DraftState.Cancelled;
    }

    private void EnsureDrafting()
    {
        if (State != DraftState.Drafting)
        {
            throw new InvalidOperationException($"Draft is {State}.");
        }
    }
}

/// <summary>
/// Validation shared by the input preview and the simulation command, so that what is green on screen is
/// exactly what will be accepted (spec part 2 §10 <c>OrderDraftValidator</c>).
/// </summary>
public static class OrderDraftValidator
{
    /// <summary>
    /// Validates a whole chain from <paramref name="anchor"/> through all waypoints against the current state:
    /// every segment must be a valid, reachable target from the previous point. Returns the index of the first
    /// invalid waypoint (or -1) and its error.
    /// </summary>
    public static (int FailedIndex, WaypointError Error) ValidateChain(GameState state, UnitId unit, HexCoord anchor, IReadOnlyList<HexCoord> waypoints)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.FindUnit(unit) is null)
        {
            return (0, WaypointError.UnknownUnit);
        }

        var source = anchor;
        for (var i = 0; i < waypoints.Count; i++)
        {
            var error = WaypointRules.ValidateTargetWithPath(state, unit, source, waypoints[i]);
            if (error != WaypointError.None)
            {
                return (i, error);
            }

            source = waypoints[i];
        }

        return (-1, WaypointError.None);
    }

    /// <summary>Shortest path of every segment of a chain (for previews). Null entries mark impossible segments.</summary>
    public static IReadOnlyList<IReadOnlyList<HexCoord>?> SegmentPaths(GameState state, UnitId unit, HexCoord anchor, IReadOnlyList<HexCoord> waypoints)
    {
        var result = new List<IReadOnlyList<HexCoord>?>();
        var source = anchor;
        foreach (var wp in waypoints)
        {
            result.Add(Pathfinder.FindPath(state.Grid, source, wp, state.ObstaclesFor(unit)));
            source = wp;
        }

        return result;
    }
}
