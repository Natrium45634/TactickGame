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

    internal void SetFinalFacing(HexDirection? facing)
    {
        if (FinalFacing != facing)
        {
            FinalFacing = facing;
            Version++;
        }
    }

    /// <summary>Removes the waypoint at <paramref name="index"/> and all following ones.</summary>
    internal void TruncateFrom(int index)
    {
        if (index < 0 || index >= _items.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        _items.RemoveRange(index, _items.Count - index);
        Version++;
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
        if (_items.Count > 0)
        {
            _items.Clear();
            Version++;
        }
    }
}
