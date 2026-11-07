using System.Collections.Generic;
namespace HexTactics.Simulation;

/// <summary>Technical parameters of the simulation (spec §6.3, §7). Not balance values.</summary>
public sealed record SimulationSettings
{
    /// <summary>Fixed simulation rate; rules never depend on render FPS.</summary>
    public int TicksPerSecond { get; init; } = 30;

    /// <summary>
    /// Base unit speed in hex transitions per second (spec part 3 §2.4: 1 hex/s, replaces the 2 hex/s test value of
    /// part 1). Time of a route = transitions / speed, independent of how much the curve shortens it.
    /// </summary>
    public double DefaultUnitSpeed => DefaultStats.MoveSpeedHexPerSecond;

    /// <summary>Skills by id (spec part 3 §5); the client loads the damage from balance data.</summary>
    public IReadOnlyDictionary<string, HexTactics.Core.Skills.SkillDefinition> Skills { get; init; } =
        new Dictionary<string, HexTactics.Core.Skills.SkillDefinition>
        {
            [HexTactics.Core.Skills.SkillCatalog.LinearStrikeId] = HexTactics.Core.Skills.SkillCatalog.LinearStrike(),
        };

    /// <summary>Skill set of new units (prototype: every unit has «Линейный удар»).</summary>
    public IReadOnlyList<string> DefaultSkillSet { get; init; } = new[] { HexTactics.Core.Skills.SkillCatalog.LinearStrikeId };

    /// <summary>Unit definition id of units created without a profile (enemy targets, debug spawns).</summary>
    public string? DefaultUnitId { get; init; }

    /// <summary>Characteristics of new units (spec part 3 §3); the client loads them from balance data.</summary>
    public HexTactics.Core.Combat.UnitStats DefaultStats { get; init; } = HexTactics.Core.Combat.UnitStats.Default;

    /// <summary>
    /// Seed of the few deliberately random decisions (companion facings of command orders, spec part 2 §6.3, §10).
    /// Same state + same commands + same seed = same result.
    /// </summary>
    public int Seed { get; init; } = 20261106;

    /// <summary>A team member blocked only by teammates asks one of them to step aside after this time.</summary>
    public double TeammateYieldDelaySeconds { get; init; } = 0.8;

    /// <summary>If the way stays closed by teammates (nobody can step aside) the unit reports a blocked route after this time.</summary>
    public double TeammateWaitLimitSeconds { get; init; } = 6.0;

    public double TickDuration => 1.0 / TicksPerSecond;

    public static SimulationSettings Default { get; } = new();
}
