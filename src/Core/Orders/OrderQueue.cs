using System;
using System.Collections.Generic;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Orders;

/// <summary>
/// Ordered list of pending waypoints of one unit: unit → WP1 → WP2 → … . The unit never skips
/// ahead: only <see cref="Peek"/> is the active target. Mutated only through <see cref="Units.GameState"/>
/// command paths so that every change is observable (<see cref="Version"/>).
/// </summary>
public sealed class OrderQueue
{
    private readonly List<Waypoint> _items = new();

    public int Count => _items.Count;

    public bool IsEmpty => _items.Count == 0;

    public IReadOnlyList<Waypoint> Items => _items;

    /// <summary>Incremented on every change of the queue (including the final facing).</summary>
    public int Version { get; private set; }

    /// <summary>
    /// Mandatory facing applied when the unit has reached the last waypoint (or, for an order without
    /// waypoints, as soon as it stands on a hex centre). Null once applied.
    /// </summary>
    public HexDirection? FinalFacing { get; private set; }

    /// <summary>
    /// The command order (<see cref="GroupOrderState.Id"/>) this queue belongs to, or null for a personal order.
    /// Cleared when the unit gets a personal order or finishes the route.
    /// </summary>
    public int? GroupId { get; private set; }

    /// <summary>
    /// Final stage of the order (spec part 3 §4): after the waypoints the unit attacks this enemy unit. Null for a plain
    /// move order. An attack order has no player-chosen final facing — the target sets it.
    /// </summary>
    public HexTactics.Core.Units.UnitId? AttackTarget { get; private set; }

    internal void SetAttackTarget(HexTactics.Core.Units.UnitId? target)
    {
        if (AttackTarget != target)
        {
            AttackTarget = target;
            Version++;
        }
    }

    /// <summary>Removes the detour steps at the front of the queue.</summary>
    internal void RemoveLeadingDetours()
    {
        var n = 0;
        while (n < _items.Count && _items[n].IsDetour)
        {
            n++;
        }

        if (n > 0)
        {
            _items.RemoveRange(0, n);
            Version++;
        }
    }

    internal void SetGroup(int? groupId)
    {
        if (GroupId != groupId)
        {
            GroupId = groupId;
            Version++;
        }
    }

    internal void SetFinalFacing(HexDirection? facing)
    {
        if (FinalFacing != facing)
        {
            FinalFacing = facing;
            Version++;
        }
    }

    /// <summary>Removes the waypoint at <paramref name="index"/> and all following ones (their planned skills are cancelled).</summary>
    internal void TruncateFrom(int index)
    {
        if (index < 0 || index >= _items.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        for (var i = index; i < _items.Count; i++)
        {
            CancelSkill(_items[i]);
        }

        _items.RemoveRange(index, _items.Count - index);
        Version++;
    }

    /// <summary>Attaches (or with null removes) the skill use planned at a waypoint; a replaced one is cancelled.</summary>
    internal bool SetSkill(int waypointId, HexTactics.Core.Skills.SkillAction? action)
    {
        var i = IndexOf(waypointId);
        if (i < 0)
        {
            return false;
        }

        if (_items[i].Skill is { } old && !ReferenceEquals(old, action))
        {
            CancelSkill(_items[i]);
        }

        _items[i] = _items[i] with { Skill = action };
        Version++;
        return true;
    }

    private static void CancelSkill(Waypoint wp)
    {
        if (wp.Skill is { Status: HexTactics.Core.Skills.SkillActionStatus.Planned } action)
        {
            action.Status = HexTactics.Core.Skills.SkillActionStatus.Cancelled;
        }
    }

    public Waypoint? Peek() => _items.Count > 0 ? _items[0] : null;

    public Waypoint? Last => _items.Count > 0 ? _items[^1] : null;

    public int IndexOf(int waypointId) => _items.FindIndex(w => w.Id == waypointId);

    internal int NextSequence => _items.Count > 0 ? _items[^1].Sequence + 1 : 1;

    internal Waypoint Append(int id, HexCoord cell)
    {
        var wp = new Waypoint(id, NextSequence, cell);
        _items.Add(wp);
        Version++;
        return wp;
    }

    /// <summary>Number of queued player waypoints (detours excluded).</summary>
    public int OrderCount
    {
        get
        {
            var n = 0;
            foreach (var w in _items)
            {
                if (!w.IsDetour)
                {
                    n++;
                }
            }

            return n;
        }
    }

    /// <summary>Inserts detour steps in front of the queue (they are executed first, in the given order).</summary>
    internal IReadOnlyList<Waypoint> InsertDetours(Func<int> nextId, IReadOnlyList<HexCoord> cells)
    {
        var inserted = new List<Waypoint>(cells.Count);
        foreach (var cell in cells)
        {
            inserted.Add(new Waypoint(nextId(), 0, cell, OrderKind.MoveToTile, IsDetour: true));
        }

        _items.InsertRange(0, inserted);
        Version++;
        return inserted;
    }

    internal Waypoint Dequeue()
    {
        if (_items.Count == 0)
        {
            throw new InvalidOperationException("Order queue is empty.");
        }

        var wp = _items[0];
        _items.RemoveAt(0);
        Version++;
        return wp;
    }

    internal void Clear()
    {
        foreach (var wp in _items)
        {
            CancelSkill(wp);
        }

        if (_items.Count > 0)
        {
            _items.Clear();
            Version++;
        }
    }
}
