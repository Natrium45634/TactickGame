using System;

namespace HexTactics.Core.Units;

/// <summary>Unique, never reused identifier of a unit within one game.</summary>
public readonly record struct UnitId(int Value) : IComparable<UnitId>
{
    public int CompareTo(UnitId other) => Value.CompareTo(other.Value);

    public override string ToString() => $"#{Value}";
}

/// <summary>Allegiance of a unit. Only the player side is used in stage 1.</summary>
public enum Side
{
    Player = 0,
    Enemy = 1,
}
