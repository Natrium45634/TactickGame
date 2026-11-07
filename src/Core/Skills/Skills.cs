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

    /// <summary>Short symbol of the skill (ТД menu, skill buttons).</summary>
    public string Icon { get; init; } = "✦";

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

    /// <summary>
    /// Started: its hits are being applied over time (spec part 4 §6.5) — one action with several hit events, never a
    /// new activation. The caster waits at the waypoint until it is done.
    /// </summary>
    Executing = 3,
}

/// <summary>One hit event of a running skill action: when (game seconds after the start), which hit, cell and effect.</summary>
public readonly record struct ScheduledHit(double Time, int HitIndex, int CellIndex, int EffectIndex);

/// <summary>
/// A skill action being executed (spec part 4 §6.5): the cells resolved at the start (origin and direction are fixed),
/// the hit schedule in the fixed order time → hit index → cell → effect, and the game time elapsed. Targets are taken at
/// every hit from whoever stands in the cell then.
/// </summary>
public sealed class SkillExecution
{
    public SkillExecution(SkillAction action, SkillDefinition skill, HexCoord origin, HexDirection direction, IReadOnlyList<(HexCoord Cell, SkillCell Config)> cells, IReadOnlyList<ScheduledHit> hits)
    {
        Action = action;
        Skill = skill;
        Origin = origin;
        Direction = direction;
        Cells = cells;
        Hits = hits;
    }

    public SkillAction Action { get; }

    public SkillDefinition Skill { get; }

    public HexCoord Origin { get; }

    public HexDirection Direction { get; }

    public IReadOnlyList<(HexCoord Cell, SkillCell Config)> Cells { get; }

    public IReadOnlyList<ScheduledHit> Hits { get; }

    /// <summary>Game seconds since the start (pause freezes it: no ticks).</summary>
    public double Elapsed { get; internal set; }

    /// <summary>Index of the next hit to apply.</summary>
    public int Next { get; internal set; }

    public bool Done => Next >= Hits.Count;

    /// <summary>Game time from the first to the last hit.</summary>
    public double Duration => Hits.Count == 0 ? 0 : Hits[^1].Time;

    /// <summary>Builds the schedule from the hit count and interval of every effect (<c>hits</c>, <c>interval</c>).</summary>
    public static IReadOnlyList<ScheduledHit> Schedule(IReadOnlyList<(HexCoord Cell, SkillCell Config)> cells)
    {
        var hits = new List<ScheduledHit>();
        for (var c = 0; c < cells.Count; c++)
        {
            var effects = cells[c].Config.Effects;
            for (var e = 0; e < effects.Count; e++)
            {
                var count = Math.Max(1, (int)Math.Round(effects[e].Number("hits", 1)));
                var interval = Math.Max(0, effects[e].Number("interval", 0));
                for (var h = 0; h < count; h++)
                {
                    hits.Add(new ScheduledHit(h * interval, h, c, e));
                }
            }
        }

        hits.Sort((a, b) =>
        {
            var t = a.Time.CompareTo(b.Time);
            if (t != 0)
            {
                return t;
            }

            var h = a.HitIndex.CompareTo(b.HitIndex);
            return h != 0 ? h : a.CellIndex != b.CellIndex ? a.CellIndex.CompareTo(b.CellIndex) : a.EffectIndex.CompareTo(b.EffectIndex);
        });
        return hits;
    }
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

    /// <summary>Planned or being executed (shown on the waypoint, holds the unit there).</summary>
    public bool IsActive => Status is SkillActionStatus.Planned or SkillActionStatus.Executing;

    /// <summary>Tick of execution (or cancellation).</summary>
    public long? ResolvedTick { get; internal set; }
}
