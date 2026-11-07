using System;
using System.Collections.Generic;
using HexTactics.Core.Combat;
using HexTactics.Core.Units;

namespace HexTactics.Core.Effects;

/// <summary>Kind of a timed status on a unit.</summary>
public enum StatusKind
{
    /// <summary>Changes a characteristic (buff / debuff).</summary>
    Modifier = 0,

    /// <summary>Damage or healing per game second.</summary>
    Periodic = 1,
}

/// <summary>What happens when the same effect is applied again to a unit that already has it (spec part 4 §6.4).</summary>
public enum StackingPolicy
{
    /// <summary>Default: the existing status keeps its value and gets its full duration again.</summary>
    Refresh = 0,

    /// <summary>A second instance is added; values add up.</summary>
    Stack = 1,

    /// <summary>The existing status is replaced by the new one (new value, new duration).</summary>
    Replace = 2,

    /// <summary>The new application is ignored while the status lasts.</summary>
    Ignore = 3,
}

/// <summary>Characteristics a modifier can change (only existing ones, spec part 4 §6.3 p.3–4).</summary>
public static class Stats
{
    public const string MoveSpeed = "moveSpeed";
    public const string AttackPower = "attackPower";
    public const string AttackRate = "attackRate";

    /// <summary>
    /// A resistance in percentage points (spec part 5 §4.6: «R_temporary»); <see cref="StatusEffect.ResistanceKey"/> names
    /// the layer — <c>global</c>, a family or a subtype id.
    /// </summary>
    public const string Resistance = "resistance";

    /// <summary>Safe lower limits of debuffed values: a unit never stops completely or attacks infinitely slowly.</summary>
    public const double MinMoveSpeed = 0.1;
    public const double MinAttackRate = 0.1;
    public const double MinAttackPower = 0;
}

/// <summary>
/// A timed status on a unit (spec part 4 §6.3): a characteristic modifier or a periodic effect, with its own timer in game
/// time (frozen by the pause). Statuses outlive the action that applied them.
/// </summary>
public sealed class StatusEffect
{
    /// <summary>Identity for the stacking policy: the source skill, the effect type and the characteristic.</summary>
    public required string Key { get; init; }

    public required StatusKind Kind { get; init; }

    /// <summary>Modified characteristic (<see cref="Stats"/>), for modifiers.</summary>
    public string Stat { get; init; } = string.Empty;

    /// <summary>Signed absolute change of the characteristic.</summary>
    public double Delta { get; init; }

    /// <summary>Signed relative change (0.5 = +50 %).</summary>
    public double Percent { get; init; }

    /// <summary>HP per game second of a periodic status.</summary>
    public double AmountPerSecond { get; init; }

    /// <summary>True: a periodic status heals; false: it damages.</summary>
    public bool Heals { get; init; }

    /// <summary>Damage type of a damaging periodic status (spec part 5 §4.6: DoT goes through the damage resolver).</summary>
    public string DamageTypeId { get; init; } = HexTactics.Core.Combat.DamageTypes.Untyped;

    /// <summary>Resistance layer changed by a <see cref="Stats.Resistance"/> modifier: <c>global</c>, a family or a type id.</summary>
    public string ResistanceKey { get; init; } = HexTactics.Core.Combat.ResistanceSet.Global;

    public required double Duration { get; init; }

    public double Remaining { get; internal set; }

    public UnitId? Source { get; init; }

    public StatusEffect Copy() => new()
    {
        Key = Key, Kind = Kind, Stat = Stat, Delta = Delta, Percent = Percent, AmountPerSecond = AmountPerSecond, Heals = Heals,
        Duration = Duration, Remaining = Remaining, Source = Source, DamageTypeId = DamageTypeId, ResistanceKey = ResistanceKey,
    };
}

/// <summary>Effective characteristics = base characteristics changed by the active modifiers, within safe limits.</summary>
public static class StatModifiers
{
    public static UnitStats Effective(UnitStats baseStats, IEnumerable<StatusEffect> statuses)
    {
        double speedP = 0, speedD = 0, powerP = 0, powerD = 0, rateP = 0, rateD = 0;
        var any = false;
        foreach (var s in statuses)
        {
            if (s.Kind != StatusKind.Modifier)
            {
                continue;
            }

            any = true;
            switch (s.Stat)
            {
                case Stats.MoveSpeed:
                    speedP += s.Percent;
                    speedD += s.Delta;
                    break;
                case Stats.AttackPower:
                    powerP += s.Percent;
                    powerD += s.Delta;
                    break;
                case Stats.AttackRate:
                    rateP += s.Percent;
                    rateD += s.Delta;
                    break;
            }
        }

        if (!any)
        {
            return baseStats;
        }

        return baseStats with
        {
            MoveSpeedHexPerSecond = Math.Max(Stats.MinMoveSpeed, (baseStats.MoveSpeedHexPerSecond * (1 + speedP)) + speedD),
            AttackPower = Math.Max(Stats.MinAttackPower, (baseStats.AttackPower * (1 + powerP)) + powerD),
            AttackRatePerSecond = Math.Max(Stats.MinAttackRate, (baseStats.AttackRatePerSecond * (1 + rateP)) + rateD),
        };
    }
}
