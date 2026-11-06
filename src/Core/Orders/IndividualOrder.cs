using System;
using System.Collections.Generic;
using HexTactics.Core.Hex;
using HexTactics.Core.Units;

namespace HexTactics.Core.Orders;

/// <summary>
/// A complete, confirmed order of one unit (spec part 2 §10): the ordered waypoints (possibly none) and the
/// mandatory final facing. An order without waypoints is a turn in place. Applied atomically by the simulation.
/// </summary>
public sealed record IndividualOrder(UnitId Unit, IReadOnlyList<HexCoord> Waypoints, HexDirection FinalFacing)
{
    public OrderKind Kind => OrderKind.MoveToTile;

    public bool IsTurnInPlace => Waypoints.Count == 0;

    public static IndividualOrder TurnInPlace(UnitId unit, HexDirection facing) => new(unit, Array.Empty<HexCoord>(), facing);
}
