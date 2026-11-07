using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Combat;
using HexTactics.Core.Effects;
using HexTactics.Core.Hex;
using HexTactics.Core.Map;
using HexTactics.Core.Orders;
using HexTactics.Core.Skills;
using HexTactics.Core.Spawning;
using HexTactics.Core.Units;
using HexTactics.Tests.Shared;
using Xunit;

namespace HexTactics.Simulation.Tests;

/// <summary>Typed damage through one resolver (spec part 5 §2–§5; acceptance §11.1–§11.4).</summary>
public class DamageResolverTests
{
    private static HexCoord C(int col, int row) => HexCoord.FromOffset(col, row);

    private static EffectSpec E(string type, params (string Key, object Value)[] p) => new(type, p.ToDictionary(x => x.Key, x => x.Value));

    private static ResistanceSet R(params (string Key, double Value)[] values) => new(values.ToDictionary(v => v.Key, v => v.Value));

    private static UnitProfile Profile(ResistanceSet resistances, double maxHealth = 1000) =>
        new("test.unit", "Цель", new UnitStats { MaxHealth = (int)maxHealth, Resistances = resistances }, new[] { SkillCatalog.LinearStrikeId });

    private static (GameSimulation Sim, UnitId Target) Target(ResistanceSet resistances, double maxHealth = 1000)
    {
        var sim = new GameSimulation(TestMaps.Empty());
        var target = sim.Spawn(SpawnRequest.AtCell(C(5, 5), Side.Enemy, HexDirection.South) with { Profile = Profile(resistances, maxHealth) }).Unit!.Value;
        return (sim, target);
    }

    private static double Hit(GameSimulation sim, UnitId target, string type, double amount, double penetration = 0) =>
        sim.Damage.Resolve(sim.Damage.Create(null, target, type, amount, DamageOrigin.Debug, penetration), sim.Tick).HpLoss;

    [Theory]
    [InlineData(-500, 240)]
    [InlineData(-100, 80)]
    [InlineData(-50, 60)]
    [InlineData(0, 40)]
    [InlineData(25, 30)]
    [InlineData(50, 20)]
    [InlineData(75, 10)]
    [InlineData(100, 0)]
    [InlineData(300, 0)]
    [InlineData(500, 0)]
    public void P8_the_one_formula_for_40_damage(double r, double expected)
    {
        var (sim, target) = Target(R((DamageTypes.Slashing, r)));
        Assert.Equal(expected, Hit(sim, target, DamageTypes.Slashing, 40), 9);
        Assert.Equal(1000 - expected, sim.State.GetUnit(target).CurrentHealth, 9); // R > 100 % never heals
    }

    [Fact]
    public void P11_layers_add_up_global_family_subtype_and_clamp_at_500()
    {
        var (sim, target) = Target(R((ResistanceSet.Global, 10), (DamageTypes.Physical, 20), (DamageTypes.Piercing, -15)));
        Assert.Equal(15, sim.Damage.EffectiveResistance(sim.State.GetUnit(target), DamageTypes.Piercing));
        Assert.Equal(34, Hit(sim, target, DamageTypes.Piercing, 40), 9);
        Assert.Equal(28, Hit(sim, target, DamageTypes.Slashing, 40), 9); // 10 + 20
        Assert.Equal(36, Hit(sim, target, DamageTypes.Magical, 40), 9); // global only

        var (big, t2) = Target(R((ResistanceSet.Global, -500), (DamageTypes.Physical, -500)));
        Assert.Equal(-500, big.Damage.EffectiveResistance(big.State.GetUnit(t2), DamageTypes.Blunt));
        Assert.Equal(240, Hit(big, t2, DamageTypes.Blunt, 40), 9); // ×6 at most
    }

    [Fact]
    public void P13_penetration_lowers_only_a_positive_resistance()
    {
        var (sim, target) = Target(R((DamageTypes.Slashing, 50), (DamageTypes.Piercing, -50), (DamageTypes.Blunt, 20)));
        var unit = sim.State.GetUnit(target);
        Assert.Equal(20, sim.Damage.EffectiveResistance(unit, DamageTypes.Slashing, 30));
        Assert.Equal(-50, sim.Damage.EffectiveResistance(unit, DamageTypes.Piercing, 30));
        Assert.Equal(0, sim.Damage.EffectiveResistance(unit, DamageTypes.Blunt, 30));
        Assert.Equal(32, Hit(sim, target, DamageTypes.Slashing, 40, 30), 9);
        Assert.Equal(60, Hit(sim, target, DamageTypes.Piercing, 40, 30), 9);
    }

    [Fact]
    public void P14_true_damage_ignores_numeric_resistance()
    {
        var (sim, target) = Target(R((ResistanceSet.Global, 500), (DamageTypes.True, 500)));
        Assert.Null(sim.Damage.EffectiveResistance(sim.State.GetUnit(target), DamageTypes.True));
        Assert.Equal(40, Hit(sim, target, DamageTypes.True, 40), 9);
        Assert.Equal(0, Hit(sim, target, DamageTypes.Slashing, 40), 9);
    }

    [Fact]
    public void P5_a_mixed_hit_applies_each_component_with_its_own_defense()
    {
        var mixed = new SkillDefinition
        {
            Id = "test.mixed",
            Name = "Смешанный",
            Cells = new[]
            {
                new SkillCell(new HexCoord(0, -1), new[]
                {
                    E("damage", ("amount", 12.0), ("damageType", DamageTypes.Slashing)),
                    E("damage", ("amount", 8.0), ("damageType", DamageTypes.Magical)),
                }),
            },
        };
        var sim = new GameSimulation(TestMaps.Empty(), new SimulationSettings { Skills = new Dictionary<string, SkillDefinition> { [mixed.Id] = mixed }, DefaultSkillSet = new[] { mixed.Id } });
        var caster = sim.Spawn(SpawnRequest.AtCell(C(5, 7), Side.Player, HexDirection.North)).Unit!.Value;
        var target = sim.Spawn(SpawnRequest.AtCell(C(5, 6), Side.Enemy, HexDirection.South) with { Profile = Profile(R((DamageTypes.Physical, 50)), 100) }).Unit!.Value;
        sim.DrainEvents();
        Assert.True(sim.UseSkill(caster, mixed.Id, HexDirection.North).Success);
        sim.Step();
        Assert.Equal(100 - 6 - 8, sim.State.GetUnit(target).CurrentHealth, 9);
        var hits = sim.DrainEvents().OfType<DamageApplied>().ToList();
        Assert.Equal(new[] { DamageTypes.Slashing, DamageTypes.Magical }, hits.Select(h => h.DamageTypeId));
    }

    private static (GameSimulation Sim, UnitId Attacker, List<UnitId> Enemies) AreaAttack(int radius, bool split, params HexCoord[] enemies)
    {
        var sim = new GameSimulation(TestMaps.Empty(), new SimulationSettings
        {
            DefaultStats = new UnitStats { AttackDamageTypeId = DamageTypes.Piercing, AttackAreaRadius = radius, AttackAreaSplit = split },
        });
        var attacker = sim.Spawn(SpawnRequest.AtCell(C(3, 5), Side.Player, HexDirection.SouthEast)).Unit!.Value;
        var ids = enemies.Select(c => sim.Spawn(SpawnRequest.AtCell(c, Side.Enemy, HexDirection.South) with { Profile = Profile(R((DamageTypes.Piercing, -100)), 1000) }).Unit!.Value).ToList();
        Assert.True(sim.ApplyOrder(IndividualOrder.Attack(attacker, System.Array.Empty<HexCoord>(), ids[0], HexDirection.SouthEast)).Success);
        return (sim, attacker, ids);
    }

    [Fact]
    public void P4_single_and_area_normal_attacks_use_the_same_formula_and_type()
    {
        // The first enemy is adjacent; two more stand next to it. Piercing −100 % doubles each hit of 10.
        var (single, _, singleIds) = AreaAttack(0, false, C(4, 5), C(5, 5), C(4, 6));
        var (area, _, areaIds) = AreaAttack(1, false, C(4, 5), C(5, 5), C(4, 6));
        var (split, _, splitIds) = AreaAttack(1, true, C(4, 5), C(5, 5), C(4, 6));
        foreach (var sim in new[] { single, area, split })
        {
            for (var i = 0; i < 10; i++)
            {
                sim.Step(); // the first hit comes after one attack interval (1/3 s = 10 ticks)
            }
        }

        Assert.Equal(new[] { 980.0, 1000, 1000 }, singleIds.Select(id => single.State.GetUnit(id).CurrentHealth));
        Assert.Equal(new[] { 980.0, 980, 980 }, areaIds.Select(id => area.State.GetUnit(id).CurrentHealth));
        Assert.All(splitIds, id => Assert.Equal(1000 - (20.0 / 3), split.State.GetUnit(id).CurrentHealth, 9));
    }

    [Fact]
    public void A_split_skill_effect_shares_its_strength_among_the_targets_it_hits_in_one_wave()
    {
        var wave = new SkillDefinition
        {
            Id = "test.wave",
            Name = "Волна",
            Cells = new[] { new HexCoord(-1, 0), new HexCoord(0, -1), new HexCoord(1, -1) }
                .Select(c => new SkillCell(c, new[] { E("damage", ("amount", 30.0), ("damageType", DamageTypes.Blunt), ("area", "split"), ("group", "base.0")) }))
                .ToArray(),
        };

        double[] Run(int enemies)
        {
            var sim = new GameSimulation(TestMaps.Empty(), new SimulationSettings { Skills = new Dictionary<string, SkillDefinition> { [wave.Id] = wave }, DefaultSkillSet = new[] { wave.Id } });
            var caster = sim.Spawn(SpawnRequest.AtCell(C(5, 7), Side.Player, HexDirection.North)).Unit!.Value;
            var cells = wave.Area(C(5, 7), HexDirection.North).Take(enemies).ToList();
            var ids = cells.Select(c => sim.Spawn(SpawnRequest.AtCell(c, Side.Enemy, HexDirection.South)).Unit!.Value).ToList();
            sim.UseSkill(caster, wave.Id, HexDirection.North);
            sim.Step();
            return ids.Select(id => 100 - sim.State.GetUnit(id).CurrentHealth).ToArray();
        }

        Assert.Equal(new[] { 30.0 }, Run(1));
        Assert.Equal(new[] { 15.0, 15.0 }, Run(2));
        Assert.All(Run(3), d => Assert.Equal(10, d, 9));
    }

    [Fact]
    public void P15_a_resistance_debuff_changes_R_for_its_game_time_only_and_freezes_on_pause()
    {
        var (sim, target) = Target(R((DamageTypes.Physical, 50)));
        var unit = sim.State.GetUnit(target);
        sim.Effects.Apply(new EffectInvocation(
            E("debuff", ("stat", Stats.Resistance), ("resistanceOf", DamageTypes.Physical), ("delta", 100.0), ("duration", 1.0), ("stacking", "refresh"), ("targets", "all")),
            null, unit, 0, EffectSource.Skill, "test"));
        Assert.Equal(-50, sim.Damage.EffectiveResistance(unit, DamageTypes.Slashing));
        Assert.Equal(60, Hit(sim, target, DamageTypes.Slashing, 40), 9);
        sim.SetPaused(true);
        sim.Advance(5);
        Assert.Equal(-50, sim.Damage.EffectiveResistance(unit, DamageTypes.Slashing));
        sim.SetPaused(false);
        for (var i = 0; i < 31; i++)
        {
            sim.Step();
        }

        Assert.Equal(50, sim.Damage.EffectiveResistance(unit, DamageTypes.Slashing));
        Assert.Equal(20, Hit(sim, target, DamageTypes.Slashing, 40), 9);
    }

    [Theory]
    [InlineData(DamageTypes.Physical, 0, 10)]
    [InlineData(DamageTypes.Physical, 50, 5)]
    [InlineData(DamageTypes.Direct, 50, 10)] // the legacy «direct HP» surface keeps its HP for every target
    public void P31_a_typed_surface_of_5_HP_per_second_for_2_seconds(string type, double resistance, double expected)
    {
        var map = TestMaps.Empty();
        map.Grid.SetTile(C(5, 5), TileType.Walkable, "test.tile.burn", 0);
        var sim = new GameSimulation(map, new SimulationSettings
        {
            TileEffects = new Dictionary<string, IReadOnlyList<TileEffect>>
            {
                ["test.tile.burn"] = new[] { new TileEffect(TileTrigger.Stay, E("periodic", ("kind", "damage"), ("perSecond", 5.0), ("damageType", type))) },
            },
        });
        var unit = sim.Spawn(SpawnRequest.AtCell(C(5, 5), Side.Player, HexDirection.North) with { Profile = Profile(R((DamageTypes.Physical, resistance)), 100) }).Unit!.Value;
        for (var i = 0; i < 60; i++)
        {
            sim.Step();
        }

        Assert.Equal(100 - expected, sim.State.GetUnit(unit).CurrentHealth, 9);
    }

    [Fact]
    public void A_periodic_status_deals_its_own_damage_type()
    {
        var (sim, target) = Target(R((DamageTypes.Magical, 100)), 100);
        var unit = sim.State.GetUnit(target);
        sim.Effects.Apply(new EffectInvocation(E("periodic", ("kind", "damage"), ("perSecond", 5.0), ("duration", 2.0), ("damageType", DamageTypes.Magical), ("targets", "all")), null, unit, 0, EffectSource.Skill, "test"));
        sim.Effects.Apply(new EffectInvocation(E("periodic", ("kind", "damage"), ("perSecond", 5.0), ("duration", 2.0), ("damageType", DamageTypes.Blunt), ("targets", "all")), null, unit, 0, EffectSource.Skill, "test2"));
        for (var i = 0; i < 90; i++)
        {
            sim.Step();
        }

        Assert.Equal(90, unit.CurrentHealth, 9); // only the blunt 5 HP/s × 2 s
    }

    [Theory]
    [InlineData(1.0 / 30)]
    [InlineData(1.0 / 60)]
    [InlineData(1.0 / 144)]
    public void P26_P28_results_do_not_depend_on_the_frame_rate(double frame)
    {
        var (sim, attacker, ids) = AreaAttack(1, true, C(4, 5), C(5, 5));
        for (var t = 0.0; t < 2.0; t += frame)
        {
            sim.Advance(frame);
        }

        Assert.Equal(60, sim.Tick);
        _ = attacker;
        Assert.All(ids, id => Assert.Equal(1000 - (6 * 10.0), sim.State.GetUnit(id).CurrentHealth, 9)); // 6 hits × 20/2
    }

    [Fact]
    public void Three_hits_of_7_5_stay_22_5()
    {
        var (sim, target) = Target(ResistanceSet.Empty, 100);
        for (var i = 0; i < 3; i++)
        {
            Hit(sim, target, DamageTypes.Physical, 7.5);
        }

        Assert.Equal(77.5, sim.State.GetUnit(target).CurrentHealth, 9);
    }
}
