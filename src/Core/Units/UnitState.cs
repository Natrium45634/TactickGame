using HexTactics.Core.Hex;
using HexTactics.Core.Orders;

namespace HexTactics.Core.Units;

/// <summary>
/// Authoritative state of one unit. A unit occupies exactly one hex. Facing is one of the six
/// discrete <see cref="HexDirection"/>s and is part of the model, not just a visual rotation.
/// Mutation goes through <see cref="GameState"/> so occupancy always stays consistent.
/// </summary>
public sealed class UnitState
{
    internal UnitState(UnitId id, Side side, HexCoord cell, HexDirection facing, double speedHexesPerSecond)
    {
        Id = id;
        Side = side;
        Cell = cell;
        Facing = facing;
        SpeedHexesPerSecond = speedHexesPerSecond;
    }

    public UnitId Id { get; }

    public Side Side { get; }

    /// <summary>The single logical hex the unit occupies.</summary>
    public HexCoord Cell { get; internal set; }

    public HexDirection Facing { get; internal set; }

    /// <summary>Movement speed in hex transitions per second (prototype default: 2).</summary>
    public double SpeedHexesPerSecond { get; }

    /// <summary>Pending waypoints (ТД) in execution order.</summary>
    public OrderQueue Orders { get; } = new();

    public override string ToString() => $"Unit {Id} {Side} at {Cell} facing {Facing.ShortName()}";
}
