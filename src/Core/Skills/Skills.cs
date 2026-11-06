using System;
using System.Collections.Generic;
using HexTactics.Core.Hex;
using HexTactics.Core.Map;

namespace HexTactics.Core.Skills;

/// <summary>
/// Data of a skill (spec part 3 §5, §8 <c>SkillDefinition</c>): the hexes it hits relative to the caster for a direction,
/// its damage and hit rules. Cost, cooldown and cast time are not defined by the author yet — fields exist so they can be
/// configured later without code changes; null means "none" (§5.1).
/// </summary>
public sealed record SkillDefinition
{
    public required string Id { get; init; }

    /// <summary>Display name (prototype name, may change).</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Hit hexes for the direction <see cref="HexDirection.North"/> as steps along directions relative to the facing
    /// (0 = forward). Linear Strike: forward ×1 and forward ×2.
    /// </summary>
    public required IReadOnlyList<IReadOnlyList<int>> RelativeSteps { get; init; }

    public int Damage { get; init; }

    /// <summary>Spec part 3 §5.1 temporary rule: allies are not hurt.</summary>
    public bool FriendlyFire { get; init; }

    /// <summary>Spec part 3 §5.1 temporary rule: obstacles do not stop the skill.</summary>
    public bool BlockedByObstacles { get; init; }

    public int? Cost { get; init; }

    public double? CooldownSeconds { get; init; }

    public double? CastSeconds { get; init; }

    /// <summary>
    /// Hexes hit when the skill is used from <paramref name="origin"/> towards <paramref name="direction"/>; hexes outside
    /// the map are dropped (no error, spec part 3 §5.1). Pure hex arithmetic — no screen pixels or camera angles.
    /// </summary>
    public IReadOnlyList<HexCoord> Area(HexCoord origin, HexDirection direction, HexGrid? grid = null)
    {
        var cells = new List<HexCoord>(RelativeSteps.Count);
        foreach (var path in RelativeSteps)
        {
            var cell = origin;
            foreach (var step in path)
            {
                cell = cell.Neighbor(direction.RotateClockwise(step));
            }

            if (grid is null || grid.Contains(cell))
            {
                cells.Add(cell);
            }
        }

        return cells;
    }
}

/// <summary>Skills of the prototype.</summary>
public static class SkillCatalog
{
    public const string LinearStrikeId = "linear-strike";

    /// <summary>
    /// «Линейный удар» (prototype name, spec part 3 §5): exactly the two hexes in front of the caster — at distance 1 and 2
    /// along the chosen direction; 20 damage once to every living enemy standing there at activation.
    /// </summary>
    public static SkillDefinition LinearStrike(int damage = 20) => new()
    {
        Id = LinearStrikeId,
        Name = "Линейный удар",
        RelativeSteps = new IReadOnlyList<int>[] { new[] { 0 }, new[] { 0, 0 } },
        Damage = damage,
        FriendlyFire = false,
        BlockedByObstacles = false,
    };
}

public enum SkillActionStatus
{
    Planned = 0,
    Executed = 1,
    Cancelled = 2,
}

/// <summary>
/// One planned use of a skill (spec part 3 §6.5, §8 <c>WaypointSkillAction</c>): stable id, direction and an execution
/// flag, so it fires exactly once — never again on re-entering a hex, re-planning, FPS changes or pause/resume.
/// </summary>
public sealed class SkillAction
{
    internal SkillAction(int id, string skillId, HexDirection direction, int? originWaypointId)
    {
        Id = id;
        SkillId = skillId;
        Direction = direction;
        OriginWaypointId = originWaypointId;
    }

    public int Id { get; }

    public string SkillId { get; }

    public HexDirection Direction { get; }

    /// <summary>The waypoint (ТД) the action belongs to, or null for an immediate use from the unit's hex.</summary>
    public int? OriginWaypointId { get; }

    public SkillActionStatus Status { get; internal set; }

    /// <summary>Tick of execution (or cancellation).</summary>
    public long? ResolvedTick { get; internal set; }
}
