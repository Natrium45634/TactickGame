using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Combat;
using HexTactics.Core.Effects;
using HexTactics.Core.Hex;
using HexTactics.Core.Map;
using HexTactics.Core.Skills;
using HexTactics.Core.Spawning;
using HexTactics.Core.Units;
using HexTactics.Tests.Shared;
using Xunit;

namespace HexTactics.Simulation.Tests;

/// <summary>Defense A as its own stage of the one damage pipeline (spec part 6 §3, §8; acceptance §12.2 P6-09…P6-19).</summary>
public class ArmorTests
{
    private const string Fire = "test.damage.fire";

    private static readonly IReadOnlyDictionary<string, DamageTypeDefinition> Types = new Dictionary<string, DamageTypeDefinition>(DamageTypes.BuiltIn)
    {
        [Fire] = new(Fire, "Огонь", DamageTypes.Magical, DamageStrategy.ResistanceAffected, new[] { "fire" }),
    };

    private static HexCoord C(int col, int row) => HexCoord.FromOffset(col, row);

    private static ResistanceSet R(params (string Key, double Value)[] values) => new(values.ToDictionary(v => v.Key, v => v.Value));

    private static ArmorSet A(params (string Key, double Value)[] values) => new(values.ToDictionary(v => v.Key, v => v.Value));

    private static EffectSpec E(string type, params (string Key, object Value)[] p) => new(type, p.ToDictionary(x => x.Key, x => x.Value));

    private static (GameSimulation Sim, UnitId Target) Target(ResistanceSet? r = null, ArmorSet? a = null, CombatRules? rules = null,
        IReadOnlyList<DefenseRule>? defense = null, IReadOnlyList<ShieldDefinition>? shields = null, int hp = 1000)
    {
        var sim = new GameSimulation(TestMaps.Empty(), new SimulationSettings { DamageTypes = Types, CombatRules = rules ?? CombatRules.Default });
        var stats = new UnitStats
        {
            MaxHealth = hp,
            Resistances = r ?? ResistanceSet.Empty,
            Armor = a ?? ArmorSet.Empty,
            DefenseRules = defense ?? System.Array.Empty<DefenseRule>(),
            Shields = shields ?? System.Array.Empty<ShieldDefinition>(),
        };
        var target = sim.Spawn(SpawnRequest.AtCell(C(5, 5), Side.Player, HexDirection.North) with
        {
            Profile = new UnitProfile("test.unit", "Цель", stats, new[] { SkillCatalog.LinearStrikeId }),
        }).Unit!.Value;
        return (sim, target);
    }

    private static DamageResult Hit(GameSimulation sim, UnitId target, string type, double amount, double penetration = 0, double armorFlat = 0, double armorPercent = 0) =>
        sim.Damage.Resolve(sim.Damage.Create(null, target, type, amount, DamageOrigin.Debug, penetration, armorPenetration: armorFlat, armorPenetrationPercent: armorPercent), sim.Tick);

    [Theory]
    [InlineData(0, 0, 100)]      // spec part 6 §3.3, the reference table (K = 100)
    [InlineData(0, 100, 50)]     // P6-09
    [InlineData(50, 100, 25)]    // P6-10
    [InlineData(-50, 100, 75)]   // P6-11
    [InlineData(100, 100, 0)]
    [InlineData(300, 100, 0)]    // R > 100 % alone never heals, A does not change that
    public void P6_09_to_P6_11_R_then_A_are_two_multipliers_in_sequence(double r, double a, double expected)
    {
        var (sim, target) = Target(R((DamageTypes.Slashing, r)), A((DamageTypes.Physical, a)));
        var result = Hit(sim, target, DamageTypes.Slashing, 100);
        Assert.Equal(expected, result.AfterArmor, 9);
        Assert.Equal(expected, result.HpLoss, 9);
        Assert.Equal(1000 - expected, sim.State.GetUnit(target).CurrentHealth, 9);
    }

    [Fact]
    public void P6_12_armor_penetration_in_percent_is_exact_and_not_rounded_early()
    {
        var (sim, target) = Target(R((DamageTypes.Physical, 50)), A((DamageTypes.Physical, 100)));
        var result = Hit(sim, target, DamageTypes.Piercing, 100, armorPercent: 50);
        Assert.Equal(50, result.AfterResistance, 9);
        Assert.Equal(50, result.Armor!.Value, 9);
        Assert.Equal(50.0 * 100 / 150, result.AfterArmor, 12); // 33,333… — rounding only at HP/UI
        Assert.Equal(50.0 * 100 / 150, result.HpLoss, 12);
    }

    [Fact]
    public void P6_13_true_damage_ignores_R_A_and_their_penetrations()
    {
        var (sim, target) = Target(R((ResistanceSet.Global, 75), (DamageTypes.True, 75)), A((ArmorSet.Global, 500), (DamageTypes.True, 500)));
        var result = Hit(sim, target, DamageTypes.True, 100, armorFlat: 10);
        Assert.Null(result.Resistance);
        Assert.Null(result.Armor);
        Assert.Equal(100, result.HpLoss, 9);
        Assert.Null(sim.Damage.EffectiveArmor(sim.State.GetUnit(target), DamageTypes.True));
    }

    [Fact]
    public void P6_14_layers_global_family_type_add_up_once()
    {
        var (sim, target) = Target(a: A((ArmorSet.Global, 10), (DamageTypes.Magical, 20), (Fire, 30)));
        var unit = sim.State.GetUnit(target);
        Assert.Equal(60, sim.Damage.EffectiveArmor(unit, Fire)!.Value.Raw);           // the subtype: all three layers
        Assert.Equal(30, sim.Damage.EffectiveArmor(unit, DamageTypes.Magical)!.Value.Raw); // the family itself: global + family
        Assert.Equal(10, sim.Damage.EffectiveArmor(unit, DamageTypes.Blunt)!.Value.Raw);   // another family: global only
        Assert.Equal(100 * 100 / 160.0, Hit(sim, target, Fire, 100).HpLoss, 9);
    }

    [Fact]
    public void P6_15_a_negative_total_after_a_debuff_counts_as_zero_and_never_adds_damage()
    {
        var (sim, target) = Target(a: A((DamageTypes.Physical, 100)));
        var unit = sim.State.GetUnit(target);
        sim.Effects.Apply(new EffectInvocation(
            E("debuff", ("stat", Stats.Armor), ("armorOf", DamageTypes.Physical), ("delta", 300.0), ("duration", 5.0), ("stacking", "refresh"), ("targets", "all")),
            null, unit, 0, EffectSource.Skill, "test"));
        var armor = sim.Damage.EffectiveArmor(unit, DamageTypes.Slashing)!.Value;
        Assert.Equal((-200.0, 0.0), (armor.Raw, armor.Effective));
        var result = Hit(sim, target, DamageTypes.Slashing, 40);
        Assert.Equal(40, result.HpLoss, 9); // ×1, not more
        Assert.Equal(-200, result.ArmorRaw!.Value, 9);

        // A buff adds to the layer for its game time only (spec part 6 §3.2 «A_temporary»).
        var (buffed, t2) = Target();
        var u2 = buffed.State.GetUnit(t2);
        buffed.Effects.Apply(new EffectInvocation(
            E("buff", ("stat", Stats.Armor), ("armorOf", ArmorSet.Global), ("delta", 100.0), ("duration", 1.0), ("stacking", "refresh"), ("targets", "all")),
            null, u2, 0, EffectSource.Skill, "test"));
        Assert.Equal(20, Hit(buffed, t2, DamageTypes.Magical, 40).HpLoss, 9);
        buffed.SetPaused(true);
        buffed.Advance(5);
        Assert.Equal(100, buffed.Damage.EffectiveArmor(u2, DamageTypes.Magical)!.Value.Raw); // frozen on the pause
        buffed.SetPaused(false);
        for (var i = 0; i < 31; i++)
        {
            buffed.Step();
        }

        Assert.Equal(0, buffed.Damage.EffectiveArmor(u2, DamageTypes.Magical)!.Value.Raw);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(7)]
    [InlineData(1_000_000)]
    public void P6_16_without_A_every_old_number_stays_whatever_K_is(double k)
    {
        var rules = new CombatRules { DefenseK = k };
        foreach (var (r, expected) in new[] { (-500.0, 240.0), (-50.0, 60.0), (0.0, 40.0), (25.0, 30.0), (75.0, 10.0), (300.0, 0.0) })
        {
            var (sim, target) = Target(R((DamageTypes.Slashing, r)), rules: rules);
            var result = Hit(sim, target, DamageTypes.Slashing, 40);
            Assert.Equal(expected, result.HpLoss, 9);
            Assert.Equal(result.AfterResistance, result.AfterArmor, 12);
        }
    }

    private static DefenseRule FireRule() => new()
    {
        Id = "test.rule.fire",
        Trigger = DefenseTrigger.OnHit,
        DamageType = Fire,
        MinEffectiveResistance = 300,
        Priority = 100,
        Actions = new[]
        {
            new DefenseAction { Kind = DefenseActionKind.ConvertToHealing, Fraction = 0.25 },
            new DefenseAction { Kind = DefenseActionKind.RedirectAsDamage, Fraction = 0.75, Targeting = ReactionTargeting.Area, Radius = 1, Filter = ReactionFilter.EnemiesOfDefender },
        },
    };

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void P6_17_the_part_5_fire_case_holds_with_and_without_A(double armor)
    {
        var (sim, defender) = Target(R((Fire, 300)), A((DamageTypes.Magical, armor)), defense: new[] { FireRule() }, hp: 100);
        var enemy = sim.Spawn(SpawnRequest.AtCell(C(6, 5), Side.Enemy, HexDirection.North)).Unit!.Value;
        sim.State.ApplyDamage(defender, 40);
        var result = Hit(sim, defender, Fire, 40);
        Assert.Equal((40.0, 10.0, 0.0), (result.Converted, result.Healed, result.HpLoss)); // conversions take D0 before R and A
        Assert.Equal(70, sim.State.GetUnit(defender).CurrentHealth, 9);
        Assert.Equal(sim.State.GetUnit(enemy).MaxHealth - 30, sim.State.GetUnit(enemy).CurrentHealth, 9);
    }

    [Fact]
    public void P6_18_resistance_and_armor_penetrations_act_only_on_their_own_stage()
    {
        var (sim, target) = Target(R((DamageTypes.Physical, 50)), A((DamageTypes.Physical, 100)));
        var rOnly = Hit(sim, target, DamageTypes.Slashing, 100, penetration: 30);
        Assert.Equal((20.0, 100.0, 100.0), (rOnly.Resistance!.Value, rOnly.ArmorRaw!.Value, rOnly.Armor!.Value));
        Assert.Equal(80 * 0.5, rOnly.AfterArmor, 9);

        var aOnly = Hit(sim, target, DamageTypes.Slashing, 100, armorFlat: 60);
        Assert.Equal((50.0, 100.0, 40.0), (aOnly.Resistance!.Value, aOnly.ArmorRaw!.Value, aOnly.Armor!.Value));
        Assert.Equal(50 * 100 / 140.0, aOnly.AfterArmor, 9);

        var both = Hit(sim, target, DamageTypes.Slashing, 100, penetration: 30, armorFlat: 60, armorPercent: 50);
        Assert.Equal((20.0, 20.0), (both.Resistance!.Value, both.Armor!.Value)); // (100 − 60) × 50 %
        Assert.Equal(80 * 100 / 120.0, both.AfterArmor, 9);
    }

    [Fact]
    public void P6_19_flat_reduction_and_shields_come_after_A_and_only_the_rest_reaches_health()
    {
        var guard = new DefenseRule
        {
            Id = "test.rule.flat", Trigger = DefenseTrigger.OnHit,
            Actions = new[] { new DefenseAction { Kind = DefenseActionKind.FlatReduction, Amount = 10 } },
        };
        var (sim, target) = Target(a: A((ArmorSet.Global, 100)), defense: new[] { guard }, shields: new[] { new ShieldDefinition("Барьер", 30, null, 0) });
        var result = Hit(sim, target, DamageTypes.Blunt, 100);
        Assert.Equal(50, result.AfterArmor, 9);           // 100 × 100 / 200
        Assert.Equal((30.0, 10.0), (result.ShieldLoss, result.HpLoss)); // 50 − 10 flat = 40: shield 30, health 10
        Assert.Equal(990, sim.State.GetUnit(target).CurrentHealth, 9);
    }

    [Fact]
    public void K_of_the_ruleset_and_K_of_a_family_change_only_their_types()
    {
        var rules = new CombatRules { DefenseK = 100, DefenseKByFamily = new Dictionary<string, double> { [DamageTypes.Magical] = 50 } };
        var (sim, target) = Target(a: A((ArmorSet.Global, 50)), rules: rules);
        var magic = Hit(sim, target, Fire, 90);
        Assert.Equal((50.0, 45.0), (magic.DefenseK, magic.HpLoss));            // K 50: ×0.5
        var physical = Hit(sim, target, DamageTypes.Slashing, 90);
        Assert.Equal((100.0, 60.0), (physical.DefenseK, physical.HpLoss));     // K 100: ×2/3
    }

    [Fact]
    public void Normal_attacks_and_skills_carry_their_armor_penetration_into_the_one_resolver()
    {
        var sim = new GameSimulation(TestMaps.Empty(), new SimulationSettings { DamageTypes = Types });
        var attackerStats = new UnitStats { AttackDamageTypeId = DamageTypes.Slashing, AttackPower = 30, AttackArmorPenetration = 50 };
        var attacker = sim.Spawn(SpawnRequest.AtCell(C(5, 5), Side.Player, HexDirection.South) with { Profile = new UnitProfile("a", "А", attackerStats, System.Array.Empty<string>()) }).Unit!.Value;
        var target = sim.Spawn(SpawnRequest.AtCell(C(5, 6), Side.Enemy, HexDirection.North) with
        {
            Profile = new UnitProfile("t", "Т", new UnitStats { MaxHealth = 1000, Armor = A((ArmorSet.Global, 150)) }, System.Array.Empty<string>()),
        }).Unit!.Value;
        Assert.True(sim.ApplyOrder(new HexTactics.Core.Orders.IndividualOrder(attacker, System.Array.Empty<HexCoord>(), HexDirection.South, target)).Success);
        for (var i = 0; i < 30 && sim.Damage.Last is null; i++)
        {
            sim.Step();
        }

        var hit = sim.Damage.Last!;
        Assert.Equal((150.0, 100.0), (hit.ArmorRaw!.Value, hit.Armor!.Value));
        Assert.Equal(15, hit.HpLoss, 9); // 30 × 100 / 200

        var unit = sim.State.GetUnit(target);
        var skillHit = sim.Effects.DealDamage(unit, null, 40, sim.Tick, DamageOrigin.Skill, DamageTypes.Blunt, armorPenetrationPercent: 100);
        Assert.Equal(40, skillHit.HpLoss, 9); // 100 % of A ignored
    }
}
