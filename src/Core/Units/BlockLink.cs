using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Effects;

namespace HexTactics.Core.Units;

/// <summary>
/// A combat contact (spec part 7 §7.2 <c>BlockLink</c>): <paramref name="Blocker"/> holds <paramref name="Enemy"/> on the
/// enemy's own hex — two units, two hexes, never one. An enemy has at most one blocker.
/// </summary>
public sealed record BlockLink(UnitId Blocker, UnitId Enemy, long CreatedAtTick);

/// <summary>Why a block ended (events and tests).</summary>
public enum BlockReleaseReason
{
    BlockerGone = 0,
    EnemyGone = 1,
    BlockerMoved = 2,
    NoLongerAdjacent = 3,
    CapacityReduced = 4,
    EnemyStopped = 5,
    SideChanged = 6,
}

/// <summary>The one block capacity rule (spec part 7 §7.1–7.2).</summary>
public static class BlockMath
{
    /// <summary>The capacity now: the characteristic plus block buffs and debuffs, whole slots, never below zero.</summary>
    public static int Capacity(UnitState unit) =>
        Math.Max(0, unit.Stats.BlockCapacity + (int)Math.Floor(unit.Statuses.Where(s => s.Kind == StatusKind.Modifier && s.Stat == Stats.Block).Sum(s => s.Delta) + 1e-9));
}
