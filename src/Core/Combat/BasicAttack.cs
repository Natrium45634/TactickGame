using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Combat;

/// <summary>How the automatic base attack chooses among the enemies in its area (spec part 7 §8.2 <c>targetPriority</c>).</summary>
public enum TargetPriority
{
    /// <summary>The nearest enemy first (hex distance), then the order of the area cells, then the unit id.</summary>
    Nearest = 0,

    /// <summary>The enemy with the least current health first, then the nearest.</summary>
    LowestHealth = 1,

    /// <summary>The order in which the author listed the area cells (an explicit priority), then the unit id.</summary>
    PatternOrder = 2,
}

/// <summary>
/// The automatic base attack of a unit (spec part 7 §8): its own area, relative to the unit and turned with its logical facing,
/// independent of active skills and of the explicit attack order. Cells are axial offsets for a unit facing north (the same
/// convention as skill areas). The strength is a share of the unit's Attack Power; the hit goes through the one damage pipeline.
/// </summary>
public sealed record BasicAttackDefinition
{
    /// <summary>The built-in base attack of new units: the one hex in front, the nearest enemy, one hit per interval.</summary>
    public const string DefaultId = "core.basic_attack.front";

    /// <summary>Safety limit of base attack hits of one unit in one tick (spec part 7 §8.4): never a silent loss, never a flood.</summary>
    public const int MaxHitsPerTick = 4;

    public string Id { get; init; } = DefaultId;

    public string Name { get; init; } = "Передний гекс";

    /// <summary>Area cells, axial offsets for a unit facing north (front = (0, −1)).</summary>
    public IReadOnlyList<HexCoord> Cells { get; init; } = new[] { new HexCoord(0, -1) };

    public TargetPriority Priority { get; init; } = TargetPriority.Nearest;

    /// <summary>Enemies hit by one attack (1…6), each with its own roll.</summary>
    public int MaxTargets { get; init; } = 1;

    /// <summary>Strength of one hit in percent of the unit's Attack Power (PROTOTYPE_DEFAULT 100).</summary>
    public double PowerPercent { get; init; } = 100;

    /// <summary>Damage type of the hit; null — the unit's normal attack type.</summary>
    public string? DamageTypeId { get; init; }

    public bool CanCrit { get; init; } = true;

    public bool CanSurprise { get; init; } = true;

    /// <summary>Spec part 7 §8.4: the base attack also works while the unit walks (it never stops the route).</summary>
    public bool WhileMoving { get; init; } = true;

    public static BasicAttackDefinition Front { get; } = new();

    /// <summary>The hexes of the area for a unit on <paramref name="origin"/> looking <paramref name="facing"/>, in the stored order.</summary>
    public IReadOnlyList<HexCoord> Area(HexCoord origin, HexDirection facing) => Cells.Select(c => origin + c.RotateFor(facing)).ToList();

    /// <summary>
    /// Chooses up to <see cref="MaxTargets"/> of <paramref name="candidates"/> (already in the area) by the priority; ties are
    /// broken by the cell order and the unit id, so the choice is deterministic.
    /// </summary>
    public IReadOnlyList<T> Choose<T>(HexCoord origin, HexDirection facing, IEnumerable<T> candidates, Func<T, HexCoord> cellOf, Func<T, double> healthOf, Func<T, int> idOf)
    {
        var area = Area(origin, facing);
        int Index(T c) => Math.Max(0, IndexOf(area, cellOf(c)));
        var ordered = Priority switch
        {
            TargetPriority.LowestHealth => candidates.OrderBy(healthOf).ThenBy(c => origin.DistanceTo(cellOf(c))).ThenBy(Index).ThenBy(idOf),
            TargetPriority.PatternOrder => candidates.OrderBy(Index).ThenBy(idOf),
            _ => candidates.OrderBy(c => origin.DistanceTo(cellOf(c))).ThenBy(Index).ThenBy(idOf),
        };
        return ordered.Take(Math.Clamp(MaxTargets, 1, 6)).ToList();
    }

    private static int IndexOf(IReadOnlyList<HexCoord> area, HexCoord cell)
    {
        for (var i = 0; i < area.Count; i++)
        {
            if (area[i] == cell)
            {
                return i;
            }
        }

        return -1;
    }
}
