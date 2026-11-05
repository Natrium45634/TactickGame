using System;
using System.Collections.Generic;
using HexTactics.Core.Hex;
using HexTactics.Core.Map;
using HexTactics.Core.Orders;

namespace HexTactics.Core.Units;

/// <summary>
/// The whole authoritative game state: map + units + occupancy. Invariants enforced here:
/// a unit occupies exactly one in-bounds walkable hex and a hex holds at most one unit.
/// </summary>
public sealed class GameState
{
    private readonly SortedDictionary<UnitId, UnitState> _units = new();
    private readonly Dictionary<HexCoord, UnitId> _occupancy = new();
    private readonly Dictionary<HexCoord, UnitId> _reservations = new();
    private int _nextUnitId = 1;
    private int _nextWaypointId = 1;

    public GameState(MapDefinition map)
    {
        Map = map ?? throw new ArgumentNullException(nameof(map));
    }

    public MapDefinition Map { get; }

    public HexGrid Grid => Map.Grid;

    /// <summary>Units ordered by id (deterministic iteration).</summary>
    public IEnumerable<UnitState> Units => _units.Values;

    public int UnitCount => _units.Count;

    /// <summary>Incremented whenever any unit enters/leaves a hex or terrain changes.</summary>
    public int OccupancyVersion { get; private set; }

    /// <summary>Combined version of terrain and occupancy: reachability caches compare against it.</summary>
    public long TopologyVersion => ((long)Grid.Version << 32) | (uint)OccupancyVersion;

    public UnitState? FindUnit(UnitId id) => _units.TryGetValue(id, out var u) ? u : null;

    public UnitState GetUnit(UnitId id) => FindUnit(id) ?? throw new KeyNotFoundException($"Unit {id} does not exist.");

    public UnitState? UnitAt(HexCoord cell) => _occupancy.TryGetValue(cell, out var id) ? _units[id] : null;

    public bool IsOccupied(HexCoord cell) => _occupancy.ContainsKey(cell);

    /// <summary>
    /// True if a stepping unit holds <paramref name="cell"/> as the hex it is entering (before the midpoint)
    /// or leaving (after the midpoint). Reserved hexes cannot be entered or spawned on.
    /// </summary>
    public bool IsReserved(HexCoord cell) => _reservations.ContainsKey(cell);

    public UnitId? ReservedBy(HexCoord cell) => _reservations.TryGetValue(cell, out var id) ? id : null;

    public bool IsReservedByOther(HexCoord cell, UnitId except) => _reservations.TryGetValue(cell, out var id) && id != except;

    /// <summary>Occupied by another unit or reserved by another stepping unit.</summary>
    public bool IsTakenByOther(HexCoord cell, UnitId except) => IsOccupiedByOther(cell, except) || IsReservedByOther(cell, except);

    /// <summary>Path-finding obstacle predicate for <paramref name="unit"/>: hexes of all other units.</summary>
    public Func<HexCoord, bool> ObstaclesFor(UnitId unit) => cell => IsOccupiedByOther(cell, unit);

    /// <summary>True if <paramref name="cell"/> is occupied by a unit other than <paramref name="except"/>.</summary>
    public bool IsOccupiedByOther(HexCoord cell, UnitId except) => _occupancy.TryGetValue(cell, out var id) && id != except;

    /// <summary>Creates a unit. The caller (spawn rules) must have validated the cell; violations throw.</summary>
    public UnitState CreateUnit(Side side, HexCoord cell, HexDirection facing, double speedHexesPerSecond)
    {
        if (!Grid.Contains(cell))
        {
            throw new InvalidOperationException($"Cannot create a unit outside of the map at {cell}.");
        }

        if (!Grid.IsWalkable(cell))
        {
            throw new InvalidOperationException($"Cannot create a unit on a blocked tile {cell}.");
        }

        if (IsOccupied(cell) || IsReserved(cell))
        {
            throw new InvalidOperationException($"Cannot create a unit on occupied or reserved tile {cell}.");
        }

        if (speedHexesPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(speedHexesPerSecond), "Speed must be positive.");
        }

        var unit = new UnitState(new UnitId(_nextUnitId++), side, cell, facing, speedHexesPerSecond);
        _units.Add(unit.Id, unit);
        _occupancy[cell] = unit.Id;
        OccupancyVersion++;
        return unit;
    }

    /// <summary>
    /// Atomically transfers the logical occupancy of a unit to an adjacent or any free walkable hex.
    /// This is the only place where a unit's logical cell changes.
    /// </summary>
    public void MoveUnit(UnitId id, HexCoord to)
    {
        var unit = GetUnit(id);
        if (unit.Cell == to)
        {
            return;
        }

        if (!Grid.IsWalkable(to))
        {
            throw new InvalidOperationException($"Unit {id} cannot enter non-walkable {to}.");
        }

        if (IsOccupiedByOther(to, id))
        {
            throw new InvalidOperationException($"Unit {id} cannot enter {to}: occupied by {_occupancy[to]}.");
        }

        _occupancy.Remove(unit.Cell);
        unit.Cell = to;
        _occupancy[to] = id;
        OccupancyVersion++;
    }

    public void Reserve(HexCoord cell, UnitId id)
    {
        if (IsReservedByOther(cell, id))
        {
            throw new InvalidOperationException($"{cell} is already reserved by {_reservations[cell]}.");
        }

        _reservations[cell] = id;
    }

    public void ReleaseReservation(HexCoord cell, UnitId id)
    {
        if (_reservations.TryGetValue(cell, out var holder) && holder == id)
        {
            _reservations.Remove(cell);
        }
    }

    public void SetFacing(UnitId id, HexDirection facing)
    {
        if (!Enum.IsDefined(facing))
        {
            throw new ArgumentOutOfRangeException(nameof(facing), $"Invalid facing {(int)facing}.");
        }

        GetUnit(id).Facing = facing;
    }

    /// <summary>Finds the unit that owns a waypoint and the waypoint's index in its queue.</summary>
    public (UnitState Unit, int Index)? FindWaypoint(int waypointId)
    {
        foreach (var unit in _units.Values)
        {
            var index = unit.Orders.IndexOf(waypointId);
            if (index >= 0)
            {
                return (unit, index);
            }
        }

        return null;
    }

    // ----- Order queue mutation (all queue changes go through here) -----

    public Waypoint AppendWaypoint(UnitId id, HexCoord cell) => GetUnit(id).Orders.Append(_nextWaypointId++, cell);

    public void ClearWaypoints(UnitId id) => GetUnit(id).Orders.Clear();

    public Waypoint CompleteCurrentWaypoint(UnitId id) => GetUnit(id).Orders.Dequeue();
}
