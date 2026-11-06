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
        Stats = stats;
        CurrentHealth = stats.MaxHealth;
        Lifecycle = lifecycle;
        RosterEntryId = rosterEntry;
    }

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

    /// <summary>Characteristics of the unit (spec part 3 §3).</summary>
    public UnitStats Stats { get; }

    /// <summary>Movement speed in hex transitions per second (spec part 3 §2.4: 1).</summary>
    public double SpeedHexesPerSecond => Stats.MoveSpeedHexPerSecond;

    public int MaxHealth => Stats.MaxHealth;

    /// <summary>Current health, 0 … <see cref="MaxHealth"/>; damage lowers it, never below 0.</summary>
    public int CurrentHealth { get; internal set; }

    public LifeState LifeState { get; internal set; }

    public bool IsAlive => LifeState == LifeState.Alive;

    /// <summary>State of the normal attack (target from <see cref="OrderQueue.AttackTarget"/>).</summary>
    public CombatState Combat { get; } = new();

    /// <summary>Pending waypoints (ТД) in execution order.</summary>
    public OrderQueue Orders { get; } = new();

    /// <summary>Current movement between hex centres (driven by the simulation).</summary>
    public UnitMotion Motion { get; } = new();

    public override string ToString() => $"Unit {Id} {Side} at {Cell} facing {Facing?.ShortName() ?? "-"} {Lifecycle} {CurrentHealth}/{MaxHealth} HP";
}
