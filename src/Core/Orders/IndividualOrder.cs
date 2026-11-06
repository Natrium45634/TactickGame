using System;
using System.Collections.Generic;
using HexTactics.Core.Hex;
using HexTactics.Core.Units;

namespace HexTactics.Core.Orders;

/// <summary>
/// A complete, confirmed order of one unit (spec part 2 §10, part 3 §4): the ordered waypoints (possibly none) and either
/// the mandatory final facing (<c>MoveOrder</c>) or, as the final stage, an enemy unit to attack (<c>AttackTargetOrder</c>)
/// — then the facing is not chosen by the player but follows the target. An order without waypoints and without a target
/// is a turn in place. Applied atomically by the simulation.
/// </summary>
/// <param name="FinalFacing">Final facing of a move order; for an attack order the facing towards the target at confirmation.</param>
public sealed record IndividualOrder(UnitId Unit, IReadOnlyList<HexCoord> Waypoints, HexDirection FinalFacing, UnitId? AttackTarget = null)
{
    public OrderKind Kind => AttackTarget is null ? OrderKind.MoveToTile : OrderKind.AttackTarget;

    public bool IsAttack => AttackTarget is not null;

    public bool IsTurnInPlace => Waypoints.Count == 0 && AttackTarget is null;

    public static IndividualOrder Attack(UnitId unit, IReadOnlyList<HexCoord> waypoints, UnitId target, HexDirection facing) =>
        new(unit, waypoints, facing, target);

    public static IndividualOrder TurnInPlace(UnitId unit, HexDirection facing) => new(unit, Array.Empty<HexCoord>(), facing);
}
