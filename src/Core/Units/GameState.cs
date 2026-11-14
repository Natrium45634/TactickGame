using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Combat;
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
    private readonly SortedDictionary<int, GroupOrderState> _groups = new();
    private int _nextUnitId = 1;
    private int _nextWaypointId = 1;
    private int _nextGroupId = 1;
    private readonly List<UnitState> _fallen = new();
    private int _nextSkillActionId = 1;

    public GameState(MapDefinition map, HexTactics.Core.Roster.RosterState? roster = null)
    {
        Map = map ?? throw new ArgumentNullException(nameof(map));
        Roster = roster ?? HexTactics.Core.Roster.RosterState.Empty();
    }

    /// <summary>The player's roster (bottom panel cards).</summary>
    public HexTactics.Core.Roster.RosterState Roster { get; }

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

    /// <summary>
    /// The hexes where the roster may be deployed when a scenario names them (exploration entry, spec part 7 §6.1); null — the
    /// map's deploy hexes (player base and deploy zones), as before.
    /// </summary>
    public IReadOnlySet<HexCoord>? EntryCells { get; private set; }

    public void SetEntryCells(IEnumerable<HexCoord>? cells) => EntryCells = cells is null ? null : new HashSet<HexCoord>(cells);

    public UnitId? ReservedBy(HexCoord cell) => _reservations.TryGetValue(cell, out var id) ? id : null;

    public bool IsReservedByOther(HexCoord cell, UnitId except) => _reservations.TryGetValue(cell, out var id) && id != except;

    /// <summary>Occupied by another unit or reserved by another stepping unit.</summary>
    public bool IsTakenByOther(HexCoord cell, UnitId except) => IsOccupiedByOther(cell, except) || IsReservedByOther(cell, except);

    /// <summary>Path-finding obstacle predicate for <paramref name="unit"/>: hexes of all other units.</summary>
    public Func<HexCoord, bool> ObstaclesFor(UnitId unit) => cell => IsOccupiedByOther(cell, unit);

    /// <summary>True if <paramref name="cell"/> is occupied by a unit other than <paramref name="except"/>.</summary>
    public bool IsOccupiedByOther(HexCoord cell, UnitId except) => _occupancy.TryGetValue(cell, out var id) && id != except;

    /// <summary>Creates a unit. The caller (spawn rules) must have validated the cell; violations throw.</summary>
    public UnitState CreateUnit(Side side, HexCoord cell, HexDirection? facing, double speedHexesPerSecond, UnitLifecycle lifecycle = UnitLifecycle.Active, int? rosterEntry = null, UnitStats? stats = null)
    {
        if (lifecycle == UnitLifecycle.Active && facing is null)
        {
            throw new InvalidOperationException("An active unit must have a facing.");
        }

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

        var unitStats = (stats ?? UnitStats.Default) with { MoveSpeedHexPerSecond = speedHexesPerSecond };
        var unit = new UnitState(new UnitId(_nextUnitId++), side, cell, facing, unitStats, lifecycle, rosterEntry);
        _units.Add(unit.Id, unit);
        _occupancy[cell] = unit.Id;
        OccupancyVersion++;
        RosterVersion++;
        return unit;
    }

    /// <summary>Units removed from the map after their death (for statistics and the UI; never act again).</summary>
    public IReadOnlyList<UnitState> Fallen => _fallen;

    /// <summary>
    /// Lowers the unit's health (never below 0); at 0 the unit is dead at once — it no longer attacks, moves or accepts
    /// orders — and is removed by <see cref="RemoveDead"/> at the end of the tick. Returns the damage actually dealt.
    /// </summary>
    public double ApplyDamage(UnitId id, double amount)
    {
        var unit = GetUnit(id);
        if (!unit.IsAlive || !(amount > 0) || double.IsInfinity(amount))
        {
            return 0;
        }

        var dealt = Math.Min(amount, unit.CurrentHealth);
        unit.CurrentHealth -= dealt;
        if (unit.CurrentHealth <= HealthEpsilon)
        {
            unit.CurrentHealth = 0;
            unit.LifeState = LifeState.Dead;
            RosterVersion++;
        }

        return dealt;
    }

    /// <summary>Below this health a unit counts as dead (guards against floating-point remainders).</summary>
    public const double HealthEpsilon = 1e-9;

    /// <summary>Raises the health of a living unit, never above its maximum (spec part 4 §6.3 p.2). Returns the amount healed.</summary>
    public double Heal(UnitId id, double amount)
    {
        var unit = GetUnit(id);
        if (!unit.IsAlive || !(amount > 0) || double.IsInfinity(amount))
        {
            return 0;
        }

        var healed = Math.Min(amount, unit.MaxHealth - unit.CurrentHealth);
        unit.CurrentHealth += healed;
        return Math.Max(0, healed);
    }

    /// <summary>
    /// Applies a timed status with a stacking policy (spec part 4 §6.4); returns false if it was ignored. The effective
    /// characteristics are recomputed at once.
    /// </summary>
    public bool AddStatus(UnitId id, HexTactics.Core.Effects.StatusEffect status, HexTactics.Core.Effects.StackingPolicy policy)
    {
        var unit = GetUnit(id);
        if (!unit.IsAlive)
        {
            return false;
        }

        var existing = unit._statuses.FindIndex(s => s.Key == status.Key);
        switch (policy)
        {
            case HexTactics.Core.Effects.StackingPolicy.Ignore when existing >= 0:
                return false;
            case HexTactics.Core.Effects.StackingPolicy.Refresh when existing >= 0:
                unit._statuses[existing].Remaining = unit._statuses[existing].Duration;
                return true;
            case HexTactics.Core.Effects.StackingPolicy.Replace:
                unit._statuses.RemoveAll(s => s.Key == status.Key);
                break;
        }

        var added = status.Copy();
        added.Remaining = status.Duration;
        unit._statuses.Add(added);
        RecomputeStats(unit);
        return true;
    }

    /// <summary>Advances the timers of a unit's statuses by <paramref name="dt"/>; returns the expired ones (removed).</summary>
    public IReadOnlyList<HexTactics.Core.Effects.StatusEffect> AgeStatuses(UnitId id, double dt)
    {
        var unit = GetUnit(id);
        var expired = new List<HexTactics.Core.Effects.StatusEffect>();
        foreach (var s in unit._statuses)
        {
            s.Remaining -= dt;
            if (s.Remaining <= 1e-9)
            {
                expired.Add(s);
            }
        }

        if (expired.Count > 0)
        {
            unit._statuses.RemoveAll(expired.Contains);
            RecomputeStats(unit);
        }

        return expired;
    }

    private static void RecomputeStats(UnitState unit) =>
        unit.Stats = HexTactics.Core.Effects.StatModifiers.Effective(unit.BaseStats, unit._statuses, unit.PackPercent);

    /// <summary>
    /// Changes whenever a unit appears or dies (spec part 6 §5.5): systems that depend on who is on the map recount only
    /// then, not every frame.
    /// </summary>
    public long RosterVersion { get; private set; }

    private readonly List<BlockLink> _blocks = new();

    /// <summary>Active block links (spec part 7 §7.2), in creation order.</summary>
    public IReadOnlyList<BlockLink> BlockLinks => _blocks;

    /// <summary>The unit holding <paramref name="enemy"/>, if any.</summary>
    public BlockLink? BlockOf(UnitId enemy) => _blocks.Find(b => b.Enemy == enemy);

    /// <summary>How many enemies <paramref name="blocker"/> holds now.</summary>
    public int BlockCount(UnitId blocker) => _blocks.Count(b => b.Blocker == blocker);

    public void AddBlock(BlockLink link) => _blocks.Add(link);

    public bool RemoveBlock(BlockLink link) => _blocks.Remove(link);

    /// <summary>Sets the «per matching ally» bonuses of a unit (percentage points by characteristic) and recomputes its stats.</summary>
    public void SetPackPercent(UnitId id, IReadOnlyDictionary<string, double> percent)
    {
        var unit = GetUnit(id);
        unit.PackPercent = percent;
        RecomputeStats(unit);
    }

    public void SetActiveSkill(UnitId id, HexTactics.Core.Skills.SkillExecution? execution)
    {
        var unit = GetUnit(id);
        unit.ActiveSkill = execution;
        if (execution is not null)
        {
            execution.Action.Status = HexTactics.Core.Skills.SkillActionStatus.Executing;
        }
    }

    /// <summary>Records which unit definition a unit was created from.</summary>
    public void SetDefinition(UnitId id, string? definitionId) => GetUnit(id).DefinitionId = definitionId;

    /// <summary>
    /// Removes dead units from the map (deterministic id order): their hex and every reservation they hold are freed,
    /// their orders are dropped. Returns them.
    /// </summary>
    public IReadOnlyList<UnitState> RemoveDead()
    {
        List<UnitState>? dead = null;
        foreach (var unit in _units.Values)
        {
            if (!unit.IsAlive)
            {
                (dead ??= new List<UnitState>()).Add(unit);
            }
        }

        if (dead is null)
        {
            return Array.Empty<UnitState>();
        }

        foreach (var unit in dead)
        {
            Detach(unit);
            _fallen.Add(unit);
            if (unit.RosterEntryId is { } entry && Roster.Find(entry) is { } card)
            {
                Roster.Set(card, HexTactics.Core.Roster.RosterEntryState.Unavailable, unit.Id);
            }
        }

        OccupancyVersion++;
        return dead;
    }

    /// <summary>
    /// Takes a living unit off the map without a death (spec part 7 §4.6: an enemy that leaked into a player base stops being an
    /// active unit): its hex, reservations and block links are freed at once. Returns false if there is no such unit.
    /// </summary>
    public bool RemoveFromMap(UnitId id)
    {
        if (!_units.TryGetValue(id, out var unit))
        {
            return false;
        }

        Detach(unit);
        OccupancyVersion++;
        return true;
    }

    private void Detach(UnitState unit)
    {
        _units.Remove(unit.Id);
        if (_occupancy.TryGetValue(unit.Cell, out var holder) && holder == unit.Id)
        {
            _occupancy.Remove(unit.Cell);
        }

        foreach (var cell in new List<HexCoord>(_reservations.Keys))
        {
            if (_reservations[cell] == unit.Id)
            {
                _reservations.Remove(cell);
            }
        }

        unit.Orders.Clear();
        unit.Orders.SetAttackTarget(null);
        unit.Orders.SetGroup(null);
        unit.Motion.PathCells.Clear();
        unit.Motion.StepFrom = null;
        unit.Motion.StepTo = null;
        unit.Motion.Plan = null;
    }

    /// <summary>Gives the unit its skill set.</summary>
    public void SetSkills(UnitId id, IReadOnlyList<string> skills) => GetUnit(id).Skills = skills;

    /// <summary>Creates a planned skill action with a new stable id (spec part 3 §6.5).</summary>
    public HexTactics.Core.Skills.SkillAction NewSkillAction(string skillId, HexDirection direction, int? originWaypointId = null) =>
        new(_nextSkillActionId++, skillId, direction, originWaypointId);

    /// <summary>Plans (or clears) the unit's immediate skill use.</summary>
    public void SetPendingSkill(UnitId id, HexTactics.Core.Skills.SkillAction? action)
    {
        var unit = GetUnit(id);
        if (unit.PendingSkill is { Status: HexTactics.Core.Skills.SkillActionStatus.Planned } old && !ReferenceEquals(old, action))
        {
            old.Status = HexTactics.Core.Skills.SkillActionStatus.Cancelled;
        }

        unit.PendingSkill = action;
    }

    /// <summary>Attaches (null: removes) the skill use planned at one of the unit's waypoints.</summary>
    public bool SetWaypointSkill(UnitId id, int waypointId, HexTactics.Core.Skills.SkillAction? action) =>
        GetUnit(id).Orders.SetSkill(waypointId, action);

    /// <summary>Marks a skill action as executed at <paramref name="tick"/> (it never fires again).</summary>
    public void MarkExecuted(HexTactics.Core.Skills.SkillAction action, long tick)
    {
        action.Status = HexTactics.Core.Skills.SkillActionStatus.Executed;
        action.ResolvedTick = tick;
    }

    /// <summary>Sets the attack stage of the unit's order (null: no attack).</summary>
    public void SetAttackTarget(UnitId id, UnitId? target) => GetUnit(id).Orders.SetAttackTarget(target);

    /// <summary>Drops the detour steps at the front of the unit's queue.</summary>
    public void RemoveLeadingDetours(UnitId id) => GetUnit(id).Orders.RemoveLeadingDetours();

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

    /// <summary>Gives a pending unit its first facing and makes it active.</summary>
    public void Activate(UnitId id, HexDirection facing)
    {
        var unit = GetUnit(id);
        unit.Facing = facing;
        unit.Lifecycle = UnitLifecycle.Active;
    }

    public void SetRole(UnitId id, UnitRole role) => GetUnit(id).Role = role;

    /// <summary>The unit is driven by the simple AI of <paramref name="profile"/>; its home is where it stands now.</summary>
    public void SetAi(UnitId id, HexTactics.Core.Scenarios.AiProfile? profile)
    {
        var unit = GetUnit(id);
        unit.Ai = profile is null || profile.Preset == HexTactics.Core.Scenarios.AiPreset.None ? null : new AiState(profile, unit.Cell);
    }

    /// <summary>The unit follows <paramref name="route"/> from the visit <paramref name="index"/> (where it must stand).</summary>
    public void AssignRoute(UnitId id, HexTactics.Core.Map.EnemyRoute route, int index = 0)
    {
        var unit = GetUnit(id);
        if (index < 0 || index >= route.Cells.Count || route.Cells[index] != unit.Cell)
        {
            throw new System.ArgumentException("The unit must stand on the route visit it starts from.", nameof(index));
        }

        unit.Route = new RouteProgress(route, index);
    }

    /// <summary>The unit stops following its route.</summary>
    public void ClearRoute(UnitId id) => GetUnit(id).Route = null;

    /// <summary>The unit reached the next visit of its route.</summary>
    public void AdvanceRoute(UnitId id)
    {
        if (GetUnit(id).Route is { Finished: false } route)
        {
            route.Index++;
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

    public void SetFinalFacing(UnitId id, HexDirection? facing) => GetUnit(id).Orders.SetFinalFacing(facing);

    public void TruncateWaypoints(UnitId id, int index) => GetUnit(id).Orders.TruncateFrom(index);

    /// <summary>Puts temporary detour steps in front of the unit's queue (stepping aside for a teammate).</summary>
    public IReadOnlyList<Waypoint> InsertDetours(UnitId id, IReadOnlyList<HexCoord> cells) =>
        GetUnit(id).Orders.InsertDetours(() => _nextWaypointId++, cells);

    /// <summary>Id the next accepted command order will get (used to preview its seeded companion facings).</summary>
    public int NextGroupId => _nextGroupId;

    // ----- Command orders (spec part 2 §6.4) -----

    /// <summary>Accepted command orders that still have at least one member executing them (ordered by id).</summary>
    public IEnumerable<GroupOrderState> Groups => _groups.Values;

    public GroupOrderState? FindGroup(int id) => _groups.TryGetValue(id, out var g) ? g : null;

    /// <summary>The command order a unit currently executes, if any.</summary>
    public GroupOrderState? GroupOf(UnitId unit) => FindUnit(unit)?.Orders.GroupId is { } id ? FindGroup(id) : null;

    /// <summary>Registers an accepted command order; the members' queues are filled by the caller.</summary>
    public GroupOrderState AddGroup(
        UnitId captain,
        IReadOnlyList<UnitId> members,
        IReadOnlyList<HexCoord> waypoints,
        IReadOnlyList<IReadOnlyList<GroupSlotAssignment>> stages,
        IReadOnlyDictionary<UnitId, HexDirection> finalFacings)
    {
        var group = new GroupOrderState(_nextGroupId++, captain, members, waypoints, stages, finalFacings);
        _groups.Add(group.Id, group);
        return group;
    }

    /// <summary>Marks a unit's queue as part of a command order (or personal with null).</summary>
    public void SetQueueGroup(UnitId id, int? groupId) => GetUnit(id).Orders.SetGroup(groupId);

    /// <summary>Drops command orders no member executes any more; returns the removed ids.</summary>
    public IReadOnlyList<int> PruneGroups()
    {
        List<int>? removed = null;
        foreach (var (id, group) in _groups)
        {
            var active = false;
            foreach (var member in group.Members)
            {
                if (FindUnit(member)?.Orders.GroupId == id)
                {
                    active = true;
                    break;
                }
            }

            if (!active)
            {
                (removed ??= new List<int>()).Add(id);
            }
        }

        if (removed is null)
        {
            return Array.Empty<int>();
        }

        foreach (var id in removed)
        {
            _groups.Remove(id);
        }

        return removed;
    }
}
