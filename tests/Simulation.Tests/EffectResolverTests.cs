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

/// <summary>Unit definitions in the simulation (spec part 4 §5): profiles, fractional attack power and health.</summary>
public class UnitProfileTests
{
    private static HexCoord C(int col, int row) => HexCoord.FromOffset(col, row);

    [Fact]
    public void A_profile_gives_the_unit_its_characteristics_and_a_fractional_attack_power_is_dealt_exactly()
    {
        var profile = new UnitProfile("unit_test0001", "Тест", new HexTactics.Core.Combat.UnitStats { MaxHealth = 175, MoveSpeedHexPerSecond = 1.5, AttackRatePerSecond = 2, AttackPower = 12.5 }, System.Array.Empty<string>());
        var sim = new GameSimulation(TestMaps.Empty(), new SimulationSettings { DefaultUnitId = "core.unit.operative" });
        var a = sim.Spawn(SpawnRequest.AtCell(C(5, 5), Side.Player, HexDirection.North) with { Profile = profile }).Unit!.Value;
        var e = sim.Spawn(SpawnRequest.AtCell(C(5, 4), Side.Enemy, HexDirection.South)).Unit!.Value;
        var attacker = sim.State.GetUnit(a);
        Assert.Equal(175, attacker.CurrentHealth);
        Assert.Equal(1.5, attacker.SpeedHexesPerSecond);
        Assert.Equal("unit_test0001", attacker.DefinitionId);
        Assert.Equal("core.unit.operative", sim.State.GetUnit(e).DefinitionId);

        Assert.True(sim.ApplyOrder(HexTactics.Core.Orders.IndividualOrder.Attack(a, System.Array.Empty<HexCoord>(), e, HexDirection.North)).Success);
        for (var i = 0; i < 15; i++)
        {
            sim.Step(); // 0.5 s at 2 attacks/s: exactly one hit
        }

        Assert.Equal(87.5, sim.State.GetUnit(e).CurrentHealth);
        Assert.Equal(new[] { 12.5 }, sim.DrainEvents().OfType<DamageApplied>().Select(d => d.Amount));
    }

    [Fact]
    public void Health_never_drops_below_zero_or_rises_above_the_maximum()
    {
        var sim = new GameSimulation(TestMaps.Empty());
        var u = sim.Spawn(SpawnRequest.AtCell(C(5, 5), Side.Player, HexDirection.North)).Unit!.Value;
        Assert.Equal(30.25, sim.State.ApplyDamage(u, 30.25));
        Assert.Equal(69.75, sim.State.GetUnit(u).CurrentHealth);
        Assert.Equal(30.25, sim.State.Heal(u, 50));
        Assert.Equal(100, sim.State.GetUnit(u).CurrentHealth);
        Assert.Equal(0, sim.State.ApplyDamage(u, double.NaN));
        Assert.Equal(100, sim.State.ApplyDamage(u, 1000));
        Assert.False(sim.State.GetUnit(u).IsAlive);
        Assert.Equal(0, sim.State.Heal(u, 10)); // the dead are not healed
    }
}
