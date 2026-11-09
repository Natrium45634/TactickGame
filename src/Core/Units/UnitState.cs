using System.Collections.Generic;
using HexTactics.Core.Combat;
using HexTactics.Core.Hex;
using HexTactics.Core.Orders;

namespace HexTactics.Core.Units;

/// <summary>Whether a unit on the map is already active (spec part 2 §5.7).</summary>
public enum UnitLifecycle
{
    /// <summary>Has a facing and executes orders.</summary>
    Active = 0,

    /// <summary>Placed from the roster, waiting for the mandatory facing: does not move or act.</summary>
    PlacedPendingFacing = 1,
}

/// <summary>
/// Authoritative state of one unit. A unit occupies exactly one hex. Facing is one of the six
/// discrete <see cref="HexDirection"/>s and is part of the model, not just a visual rotation.
/// Mutation goes through <see cref="GameState"/> so occupancy always stays consistent.
/// </summary>
public sealed class UnitState
{
    internal UnitState(UnitId id, Side side, HexCoord cell, HexDirection? facing, UnitStats stats, UnitLifecycle lifecycle, int? rosterEntry)
    {
        Id = id;
        Side = side;
        Cell = cell;
        Facing = facing;
        BaseStats = stats;
        Stats = stats;
        CurrentHealth = stats.MaxHealth;
        Lifecycle = lifecycle;
        RosterEntryId = rosterEntry;
        var shields = new List<HexTactics.Core.Combat.ShieldState>();
        foreach (var definition in stats.Shields)
        {
            shields.Add(new HexTactics.Core.Combat.ShieldState(definition));
        }

        shields.Sort((a, b) => a.Definition.Order != b.Definition.Order ? a.Definition.Order.CompareTo(b.Definition.Order) : string.CompareOrdinal(a.Definition.Name, b.Definition.Name));
        Shields = shields;
    }

    /// <summary>Live shield points (spec part 5 §4.5), in the order they take damage.</summary>
    public IReadOnlyList<HexTactics.Core.Combat.ShieldState> Shields { get; }

    public UnitId Id { get; }

    public Side Side { get; }

    /// <summary>The single logical hex the unit occupies.</summary>
    public HexCoord Cell { get; internal set; }

    /// <summary>
    /// One of six directions. Null only while the unit was placed from the roster and is waiting for its first facing
    /// (<see cref="UnitLifecycle.PlacedPendingFacing"/>); an active unit always has a facing (spec part 2 §3.2).
    /// </summary>
    public HexDirection? Facing { get; internal set; }

    public UnitLifecycle Lifecycle { get; internal set; }

    public bool IsActive => Lifecycle == UnitLifecycle.Active;

    /// <summary>The roster card the unit was placed from, if any.</summary>
    public int? RosterEntryId { get; }

    /// <summary>
    /// Effective characteristics (spec part 3 §3): <see cref="BaseStats"/> changed by the active buffs and debuffs
    /// (spec part 4 §6.3). Movement, attacks and «% от Attack Power» effects read these.
    /// </summary>
    public UnitStats Stats { get; internal set; }

    /// <summary>Characteristics of the unit's definition, without temporary modifiers.</summary>
    public UnitStats BaseStats { get; }

    /// <summary>
    /// Current «per matching ally» bonuses by characteristic in percentage points (spec part 6 §5.5), set by the pack system;
    /// part of <see cref="Stats"/>.
    /// </summary>
    public IReadOnlyDictionary<string, double> PackPercent { get; internal set; } = new Dictionary<string, double>();

    /// <summary>Active timed statuses (modifiers, periodic effects), in application order.</summary>
    public IReadOnlyList<HexTactics.Core.Effects.StatusEffect> Statuses => _statuses;

    internal readonly List<HexTactics.Core.Effects.StatusEffect> _statuses = new();

    /// <summary>A skill action being executed (the unit waits until its last hit, spec part 4 §6.5).</summary>
    public HexTactics.Core.Skills.SkillExecution? ActiveSkill { get; internal set; }

    /// <summary>Movement speed in hex transitions per second (spec part 3 §2.4: 1).</summary>
    public double SpeedHexesPerSecond => Stats.MoveSpeedHexPerSecond;

    public int MaxHealth => Stats.MaxHealth;

    /// <summary>
    /// Current health, 0 … <see cref="MaxHealth"/>; damage lowers it, never below 0. Fractional values are kept exactly
    /// (e.g. 75 % of an attack power of 10 = 7.5) — the UI rounds only for display.
    /// </summary>
    public double CurrentHealth { get; internal set; }

    /// <summary>Unit definition (content id) the unit was created from, if any (spec part 4 §5, §8.4).</summary>
    public string? DefinitionId { get; internal set; }

    public LifeState LifeState { get; internal set; }

    public bool IsAlive => LifeState == LifeState.Alive;

    /// <summary>State of the normal attack (target from <see cref="OrderQueue.AttackTarget"/>).</summary>
    public CombatState Combat { get; } = new();

    /// <summary>Skills the unit can use (skill ids, spec part 3 §3 "набор навыков").</summary>
    public IReadOnlyList<string> Skills { get; internal set; } = System.Array.Empty<string>();

    /// <summary>An immediate skill use waiting for the unit to stand on a hex centre (executed once, then cleared).</summary>
    public HexTactics.Core.Skills.SkillAction? PendingSkill { get; internal set; }

    /// <summary>Pending waypoints (ТД) in execution order.</summary>
    public OrderQueue Orders { get; } = new();

    /// <summary>Current movement between hex centres (driven by the simulation).</summary>
    public UnitMotion Motion { get; } = new();

    /// <summary>A fighter, or a defended mission object (spec part 7 §5.1) that never moves, attacks or takes orders.</summary>
    public UnitRole Role { get; internal set; }

    /// <summary>The player may give it orders (fighters only).</summary>
    public bool IsCommandable => Role == UnitRole.Combatant;

    /// <summary>The simple AI of the unit (spec part 7 §5.2), or null.</summary>
    public AiState? Ai { get; internal set; }

    /// <summary>The enemy route the unit follows (spec part 7 §4.4), or null; explicit orders go first.</summary>
    public RouteProgress? Route { get; internal set; }

    /// <summary>The unit means to move on: it has waypoints or an unfinished route (blocks hold only such units).</summary>
    public bool WantsToMove => Orders.OrderCount > 0 || Route is { Finished: false };

    public override string ToString() => $"Unit {Id} {Side} at {Cell} facing {Facing?.ShortName() ?? "-"} {Lifecycle} {CurrentHealth}/{MaxHealth} HP";
}
