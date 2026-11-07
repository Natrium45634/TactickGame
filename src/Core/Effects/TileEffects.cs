namespace HexTactics.Core.Effects;

/// <summary>When an effect of a tile type acts on a unit (spec part 4 §7.1: «при входе, во время нахождения, при выходе»).</summary>
public enum TileTrigger
{
    /// <summary>
    /// While the unit's logical hex is this tile: a continuous effect, <c>amount per second × game time</c> on every tick
    /// (spec part 4 §7.2). Only periodic damage/healing supports it.
    /// </summary>
    Stay = 0,

    /// <summary>Once, on the tick the unit's logical hex becomes this tile (also when it appears on it).</summary>
    Enter = 1,

    /// <summary>Once, on the tick the unit's logical hex stops being this tile (not on death).</summary>
    Exit = 2,
}

/// <summary>One configured effect of a tile type with its trigger; executed by the simulation's effect resolver.</summary>
public sealed record TileEffect(TileTrigger Trigger, EffectSpec Effect);

/// <summary>Side filter of tile effects (a tile has no source unit, so «enemies/allies» do not apply).</summary>
public static class TileSides
{
    public const string All = "all";
    public const string Player = "player";
    public const string Enemy = "enemy";
}
