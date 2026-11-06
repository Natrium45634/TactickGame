using System;
using System.Collections.Generic;
using HexTactics.Core.Hex;
using HexTactics.Core.Units;

namespace HexTactics.Core.Orders;

/// <summary>
/// Final facings of a command order's companions (spec part 2 §6.3, §14.5): each companion looks the captain's way or
/// one of the two neighbouring directions (±60°), never behind the captain. The choice is random-looking but fixed: it is
/// a hash of the simulation seed, the command order id and the unit id, made once when the order is confirmed and stored
/// in the order — it does not change per frame, with hovering or with the frame rate (spec part 2 §10, determinism).
/// </summary>
public static class CompanionFacingRule
{
    /// <summary>Rotation of a companion relative to the captain: −1 (60° counter-clockwise), 0 or +1 (60° clockwise).</summary>
    public static int Offset(int seed, int groupId, UnitId member)
    {
        unchecked
        {
            var x = ((ulong)(uint)seed * 0x9E3779B97F4A7C15UL)
                ^ ((ulong)(uint)groupId * 0xBF58476D1CE4E5B9UL)
                ^ ((ulong)(uint)member.Value * 0x94D049BB133111EBUL);

            // SplitMix64 finaliser: well-distributed and identical on every platform.
            x += 0x9E3779B97F4A7C15UL;
            x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
            x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
            x ^= x >> 31;
            return (int)(x % 3UL) - 1;
        }
    }

    /// <summary>Facings of all members: the captain's is the player's choice, the companions' follow the rule.</summary>
    public static IReadOnlyDictionary<UnitId, HexDirection> Assign(IReadOnlyList<UnitId> members, HexDirection captainFacing, int seed, int groupId)
    {
        ArgumentNullException.ThrowIfNull(members);
        var facings = new Dictionary<UnitId, HexDirection>(members.Count);
        for (var i = 0; i < members.Count; i++)
        {
            facings[members[i]] = i == 0 ? captainFacing : captainFacing.RotateClockwise(Offset(seed, groupId, members[i]));
        }

        return facings;
    }
}
