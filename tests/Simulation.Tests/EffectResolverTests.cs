using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Effects;
using HexTactics.Core.Hex;
using HexTactics.Core.Skills;
using HexTactics.Core.Spawning;
using HexTactics.Core.Units;
using HexTactics.Tests.Shared;
using Xunit;

namespace HexTactics.Simulation.Tests;

/// <summary>The single effect executor (spec part 4 §6.3, §9.2): target rules and unknown types.</summary>
public class EffectResolverTests
{
    private static HexCoord C(int col, int row) => HexCoord.FromOffset(col, row);

    private static GameSimulation Sim(SkillDefinition skill) => new(TestMaps.Empty(), new SimulationSettings
    {
        Skills = new Dictionary<string, SkillDefinition> { [skill.Id] = skill },
        DefaultSkillSet = new[] { skill.Id },
    });

    private static SkillDefinition Ring(string targets, string type = EffectTypes.Damage) => new()
    {
        Id = "test.ring",
        Name = "Кольцо",
        Cells = new[] { new HexCoord(0, 0) }.Concat(HexDirections.All.Select(HexCoord.DirectionOffset))
            .Select(o => new SkillCell(o, new[] { new EffectSpec(type, new Dictionary<string, object> { ["amount"] = 5.0, ["targets"] = targets }) }))
            .ToArray(),
    };

    [Theory]
    [InlineData("enemies", 100, 95, 100)]
    [InlineData("allies", 95, 100, 95)]
    [InlineData("self", 95, 100, 100)]
    [InlineData("all", 95, 95, 95)]
    public void Targets_select_who_is_affected(string targets, int caster, int enemy, int ally)
    {
        var sim = Sim(Ring(targets));
        var c = sim.Spawn(SpawnRequest.AtCell(C(5, 5), Side.Player, HexDirection.North)).Unit!.Value;
        var e = sim.Spawn(SpawnRequest.AtCell(C(5, 4), Side.Enemy, HexDirection.South)).Unit!.Value;
        var a = sim.Spawn(SpawnRequest.AtCell(C(5, 6), Side.Player, HexDirection.North)).Unit!.Value;
        Assert.True(sim.UseSkill(c, "test.ring", HexDirection.North).Success);
        sim.Step();
        Assert.Equal(caster, sim.State.GetUnit(c).CurrentHealth);
        Assert.Equal(enemy, sim.State.GetUnit(e).CurrentHealth);
        Assert.Equal(ally, sim.State.GetUnit(a).CurrentHealth);
    }

    [Fact]
    public void Unknown_effect_types_are_never_executed()
    {
        var sim = Sim(Ring("all", "teleport"));
        var c = sim.Spawn(SpawnRequest.AtCell(C(5, 5), Side.Player, HexDirection.North)).Unit!.Value;
        var e = sim.Spawn(SpawnRequest.AtCell(C(5, 4), Side.Enemy, HexDirection.South)).Unit!.Value;
        Assert.False(sim.Effects.CanExecute("teleport"));
        sim.UseSkill(c, "test.ring", HexDirection.North);
        sim.Step();
        Assert.Equal(100, sim.State.GetUnit(e).CurrentHealth);
        Assert.Empty(sim.DrainEvents().OfType<SkillActivated>().Single().Hit);
    }
}
