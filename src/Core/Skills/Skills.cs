using System;
using System.Collections.Generic;
using HexTactics.Core.Effects;
using HexTactics.Core.Hex;
using HexTactics.Core.Map;

namespace HexTactics.Core.Skills;

/// <summary>
/// One hex of a skill's area (spec part 4 §6.1, §6.4): its offset from the caster for a caster looking
/// <see cref="HexDirection.North"/> (axial; rotated mathematically for the other five facings) and the effects applied to
/// the units standing there.
/// </summary>
public sealed record SkillCell(HexCoord Offset, IReadOnlyList<EffectSpec> Effects);

/// <summary>
/// Runtime data of a skill (spec part 3 §5, part 4 §6): the hexes it affects relative to the caster and the effects of
/// each hex. Built from a content <c>SkillDefinition</c>; the part-3 «Линейный удар» is an ordinary instance (part 4
/// §6.6). Cost, cooldown and cast time are not defined by the author yet — null means "none".
/// </summary>
public sealed record SkillDefinition
{
    public required string Id { get; init; }

    /// <summary>Display name.</summary>
    public required string Name { get; init; }

    /// <summary>Hexes of the area for a caster looking North, in the stored order (also the processing order).</summary>
    public required IReadOnlyList<SkillCell> Cells { get; init; }

    public int? Cost { get; init; }

    public double? CooldownSeconds { get; init; }

    public double? CastSeconds { get; init; }

    /// <summary>
    /// Area cells used from <paramref name="origin"/> towards <paramref name="direction"/>, with their configuration, in
    /// the stored order. Cells outside the map or on empty places are dropped (spec part 3 §5.1, part 4 §4.1). Pure hex
    /// arithmetic — no screen pixels or camera angles.
    /// </summary>
    public IReadOnlyList<(HexCoord Cell, SkillCell Config)> Resolve(HexCoord origin, HexDirection direction, HexGrid? grid = null)
    {
        var cells = new List<(HexCoord, SkillCell)>(Cells.Count);
        foreach (var config in Cells)
        {
            var cell = origin + config.Offset.RotateFor(direction);
            if (grid is null || grid.Exists(cell))
            {
                cells.Add((cell, config));
            }
        }

        return cells;
    }

    /// <summary>Hexes of the area (see <see cref="Resolve"/>).</summary>
    public IReadOnlyList<HexCoord> Area(HexCoord origin, HexDirection direction, HexGrid? grid = null)
    {
        var resolved = Resolve(origin, direction, grid);
        var cells = new List<HexCoord>(resolved.Count);
        foreach (var (cell, _) in resolved)
        {
            cells.Add(cell);
        }

        return cells;
    }
}

/// <summary>Skills of the prototype.</summary>
public static class SkillCatalog
{
    /// <summary>Content id of the built-in «Линейный удар» (<c>data/content/skills</c> in the client).</summary>
    public const string LinearStrikeId = "core.skill.linear_strike";

    /// <summary>
    /// «Линейный удар» (prototype name, spec part 3 §5): exactly the two hexes in front of the caster — at distance 1 and 2
    /// along the chosen direction; fixed damage once to every living enemy standing there at activation. The game loads
    /// the same data from its content library; this factory serves tests.
    /// </summary>
    public static SkillDefinition LinearStrike(int damage = 20) => new()
    {
        Id = LinearStrikeId,
        Name = "Линейный удар",
        Cells = new[]
        {
            new SkillCell(new HexCoord(0, -1), new[] { EffectSpec.FixedDamage(damage) }),
            new SkillCell(new HexCoord(0, -2), new[] { EffectSpec.FixedDamage(damage) }),
        },
    };
}

/// <summary>A skill chosen for a waypoint in a draft or an order (spec part 3 §6): which skill and its direction.</summary>
public sealed record WaypointSkillSpec(string SkillId, HexDirection Direction);

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
