using System;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Combat;

/// <summary>Outcome tier of a critical check (spec part 6 §7.1): one normal and one super tier in this edition.</summary>
public enum CritTier
{
    Normal = 0,
    Crit = 1,
    SuperCrit = 2,
}

/// <summary>
/// Reproducible random numbers of combat (spec part 6 §7.2, D-162): a roll is a pure function of the match seed, the key of
/// the hit and the purpose — the same hit always gets the same number, a pause, a redraw or a preview never rolls again.
/// </summary>
public static class DeterministicRoll
{
    /// <summary>A number in [0, 1) for this seed, key and purpose (FNV-1a 64 over the text, then the top 53 bits).</summary>
    public static double Of(int seed, string key, string purpose)
    {
        unchecked
        {
            var h = 14695981039346656037UL;
            foreach (var c in $"{seed}|{purpose}|{key}")
            {
                h ^= c;
                h *= 1099511628211UL;
            }

            h ^= h >> 33;
            h *= 0xff51afd7ed558ccdUL;
            h ^= h >> 33;
            return (h >> 11) * (1.0 / (1UL << 53));
        }
    }
}

/// <summary>The one critical formula (spec part 6 §7.1).</summary>
public static class CritMath
{
    /// <summary>P(supercrit) = clamp((C − 100) / 100, 0, 1).</summary>
    public static double SuperChance(double c) => Math.Clamp((Math.Max(0, c) - 100) / 100, 0, 1);

    /// <summary>P(crit only) = clamp(C / 100, 0, 1) − P(supercrit).</summary>
    public static double CritOnlyChance(double c) => Math.Clamp(Math.Max(0, c) / 100, 0, 1) - SuperChance(c);

    /// <summary>The tier for a roll u ∈ [0, 1): u &lt; P(super) — supercrit, u &lt; P(crit or more) — crit, else normal.</summary>
    public static CritTier Tier(double c, double u) =>
        u < SuperChance(c) ? CritTier.SuperCrit : u < Math.Clamp(Math.Max(0, c) / 100, 0, 1) ? CritTier.Crit : CritTier.Normal;

    public static double Multiplier(CritTier tier, CombatRules rules) => tier switch
    {
        CritTier.SuperCrit => rules.SuperCritMultiplier,
        CritTier.Crit => rules.CritMultiplier,
        _ => 1,
    };
}

/// <summary>The side of a hit relative to the defender's logical facing (spec part 6 §6.1–6.2).</summary>
public static class SurpriseMath
{
    public static readonly string[] SectorNames = { "спереди", "передний правый фланг", "задний правый фланг", "сзади", "задний левый фланг", "передний левый фланг" };

    /// <summary>
    /// 0…5 clockwise from the front: the nearest of the six directions from the defender towards the source cell, relative to
    /// the defender's facing; null if there is no facing or no direction (the same hex).
    /// </summary>
    public static int? Sector(HexCoord defender, HexDirection? facing, HexCoord source)
    {
        if (facing is not { } f || HexDirections.Toward(defender, source) is not { } toward)
        {
            return null;
        }

        return (((int)toward - (int)f) % 6 + 6) % 6;
    }
}
