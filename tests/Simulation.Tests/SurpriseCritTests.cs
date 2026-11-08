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

/// <summary>Surprise by the side of the defender and critical tiers in the one pipeline (spec part 6 §6, §7; P6-33…P6-46).</summary>
public class SurpriseCritTests
{
    private static HexCoord C(int col, int row) => HexCoord.FromOffset(col, row);

    private static readonly CombatRules On = new() { SurpriseEnabled = true, CritEnabled = true };

    private static (GameSimulation Sim, UnitId Target) Arena(CombatRules rules, HexDirection facing = HexDirection.North, UnitStats? target = null)
    {
        var sim = new GameSimulation(TestMaps.Empty(), new SimulationSettings { CombatRules = rules });
        var t = sim.Spawn(SpawnRequest.AtCell(C(5, 5), Side.Enemy, facing) with
        {
            Profile = new UnitProfile("t", "Цель", target ?? new UnitStats { MaxHealth = 100000 }, System.Array.Empty<string>()),
        }).Unit!.Value;
        return (sim, t);
    }

    private static UnitId Attacker(GameSimulation sim, HexCoord cell, double critChance = 0, IReadOnlyList<DamageModifier>? mods = null) =>
        sim.Spawn(SpawnRequest.AtCell(cell, Side.Player, HexDirection.North) with
        {
            Profile = new UnitProfile("a", "Атакующий", new UnitStats { CritChance = critChance, DamageModifiers = mods ?? System.Array.Empty<DamageModifier>() }, System.Array.Empty<string>()),
        }).Unit!.Value;

    private static DamageResult Hit(GameSimulation sim, UnitId? source, UnitId target, double amount, string key, DamageOrigin origin = DamageOrigin.NormalAttack,
        bool? surprise = null, CritTier? crit = null) =>
        sim.Damage.Resolve(sim.Damage.Create(source, target, DamageTypes.Blunt, amount, origin, canCrit: true, canSurprise: true, rollKey: key)
            with { ForcedSurprise = surprise, ForcedCrit = crit }, sim.Tick);

    [Fact]
    public void P6_33_front_0_front_flanks_25_rear_flanks_75_rear_100()
    {
        var (sim, target) = Arena(On);
        var expected = new[] { 0.0, 25, 75, 100, 75, 25 };
        for (var d = 0; d < 6; d++)
        {
            var a = Attacker(sim, C(5, 5).Neighbor(HexDirections.All[d]));
            var r = Hit(sim, a, target, 10, $"side{d}");
            Assert.Equal((d, expected[d]), (r.SurpriseSector!.Value, r.SurpriseChance));
        }

        var behind = Hit(sim, sim.State.UnitAt(C(5, 5).Neighbor(HexDirection.South))!.Id, target, 10, "rear");
        Assert.True(behind.Surprised); // 100 %: always
        Assert.Equal(15, behind.HpLoss, 9);
        Assert.False(Hit(sim, sim.State.UnitAt(C(5, 5).Neighbor(HexDirection.North))!.Id, target, 10, "front").Surprised);
    }

    [Fact]
    public void P6_34_P6_35_turning_the_defender_by_one_side_changes_the_zone_and_its_logical_facing_counts()
    {
        var (sim, target) = Arena(On, HexDirection.North);
        var a = Attacker(sim, C(5, 5).Neighbor(HexDirection.South));
        Assert.Equal(3, Hit(sim, a, target, 10, "k1").SurpriseSector);
        Assert.True(sim.ApplyOrder(new HexTactics.Core.Orders.IndividualOrder(target, System.Array.Empty<HexCoord>(), HexDirection.SouthEast)).Success);
        for (var i = 0; i < 60 && sim.State.GetUnit(target).Facing != HexDirection.SouthEast; i++)
        {
            sim.Step();
        }

        Assert.Equal(HexDirection.SouthEast, sim.State.GetUnit(target).Facing);
        Assert.Equal(1, Hit(sim, a, target, 10, "k2").SurpriseSector); // the same attacker is now on the front flank
    }

    [Fact]
    public void P6_36_P6_37_the_side_is_hex_geometry_and_hits_without_a_direction_get_no_surprise()
    {
        Assert.Equal(3, SurpriseMath.Sector(C(5, 5), HexDirection.North, C(5, 8)));   // far behind: the nearest of six directions
        Assert.Null(SurpriseMath.Sector(C(5, 5), HexDirection.North, C(5, 5)));       // the same hex
        Assert.Null(SurpriseMath.Sector(C(5, 5), null, C(5, 6)));                     // no facing
        var (sim, target) = Arena(On);
        var a = Attacker(sim, C(5, 5).Neighbor(HexDirection.South), critChance: 1000);
        var surface = Hit(sim, a, target, 10, "s", DamageOrigin.Surface);
        Assert.Equal((null, false, CritTier.Normal), (surface.SurpriseSector, surface.Surprised, surface.Crit));
        Assert.False(Hit(sim, null, target, 10, "none").Surprised); // no source
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(35, 0.35, 0)]
    [InlineData(99, 0.99, 0)]
    [InlineData(100, 1, 0)]
    [InlineData(125, 0.75, 0.25)]
    [InlineData(175, 0.25, 0.75)]
    [InlineData(200, 0, 1)]
    [InlineData(250, 0, 1)]
    public void P6_38_to_P6_41_the_crit_table_and_the_rolls_follow_it(double c, double critOnly, double super)
    {
        Assert.Equal(critOnly, CritMath.CritOnlyChance(c), 12);
        Assert.Equal(super, CritMath.SuperChance(c), 12);
        var tiers = Enumerable.Range(0, 20000).Select(i => CritMath.Tier(c, DeterministicRoll.Of(7, $"k{i}", "crit"))).ToList();
        Assert.Equal(critOnly, tiers.Count(t => t == CritTier.Crit) / 20000.0, 2);
        Assert.Equal(super, tiers.Count(t => t == CritTier.SuperCrit) / 20000.0, 2);
    }

    [Fact]
    public void P6_42_outgoing_20_surprise_and_supercrit_give_D0_67_5_before_any_defense()
    {
        var (sim, target) = Arena(On, target: new UnitStats { MaxHealth = 100000, Resistances = new ResistanceSet(new Dictionary<string, double> { [DamageTypes.Physical] = 50 }) });
        var a = Attacker(sim, C(5, 5).Neighbor(HexDirection.South), mods: new[] { new DamageModifier(DamageModifierKind.Outgoing, null, 100, "outgoing", "×2") });
        var r = Hit(sim, a, target, 10, "x", surprise: true, crit: CritTier.SuperCrit); // 10 → passives 20 → ×1.5 × ×2.25
        Assert.Equal((10.0, 67.5), (r.BaseAmount, r.D0));
        Assert.Equal(33.75, r.HpLoss, 9); // then R 50 %
    }

    [Fact]
    public void P6_43_an_area_skill_rolls_per_target_by_default_and_once_with_a_shared_result()
    {
        var (sim, _) = Arena(On);
        var caster = Attacker(sim, C(1, 1), critChance: 50);
        var targets = new[] { C(3, 3), C(4, 3), C(5, 3) }.Select(c => sim.Spawn(SpawnRequest.AtCell(c, Side.Enemy, HexDirection.North) with
        {
            Profile = new UnitProfile("t", "Т", new UnitStats { MaxHealth = 100000 }, System.Array.Empty<string>()),
        }).Unit!.Value).ToList();
        CritTier[] Tiers(string critTargets, long action)
        {
            var spec = new EffectSpec("damage", new Dictionary<string, object> { ["amount"] = 10.0, ["damageType"] = DamageTypes.Blunt, ["targets"] = "enemies", ["canCrit"] = true, ["critTargets"] = critTargets });
            return targets.Select(t => sim.Effects.DealDamage(sim.State.GetUnit(t), sim.State.GetUnit(caster), 10, sim.Tick, DamageOrigin.Skill, DamageTypes.Blunt, canCrit: true,
                rollKey: $"skill:{action}:0:{(critTargets == "shared" ? "all" : t.Value.ToString())}").Crit).ToArray();
        }

        Assert.All(Enumerable.Range(1, 50), i => Assert.Single(Tiers("shared", i).Distinct()));
        Assert.Contains(Enumerable.Range(1, 50), i => Tiers("per_target", i).Distinct().Count() > 1);
    }

    [Fact]
    public void P6_44_a_hit_rolls_once_by_its_key_pause_and_redraws_roll_nothing()
    {
        var (sim, target) = Arena(On);
        var a = Attacker(sim, C(5, 5).Neighbor(HexDirection.SouthEast), critChance: 60);
        var first = Hit(sim, a, target, 10, "same");
        sim.SetPaused(true);
        sim.Advance(10);
        sim.SetPaused(false);
        var again = Hit(sim, a, target, 10, "same");
        Assert.Equal((first.CritRoll, first.SurpriseRoll, first.Crit, first.Surprised), (again.CritRoll, again.SurpriseRoll, again.Crit, again.Surprised));

        // Normal attacks: three hits a second at a 100 % rear surprise, each hit its own key, same totals on every run.
        double Run()
        {
            var (s, t) = Arena(On);
            var striker = s.Spawn(SpawnRequest.AtCell(C(5, 5).Neighbor(HexDirection.South), Side.Player, HexDirection.North) with
            {
                Profile = new UnitProfile("a", "А", new UnitStats { AttackPower = 10, CritChance = 50 }, System.Array.Empty<string>()),
            }).Unit!.Value;
            Assert.True(s.ApplyOrder(new HexTactics.Core.Orders.IndividualOrder(striker, System.Array.Empty<HexCoord>(), HexDirection.North, t)).Success);
            for (var i = 0; i < 90; i++)
            {
                s.Step();
            }

            return s.State.GetUnit(t).CurrentHealth;
        }

        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void Melee_only_rules_need_a_neighbour_and_the_attackers_bonus_adds_to_the_side_up_to_100()
    {
        var (sim, target) = Arena(On with { SurpriseMeleeOnly = true });
        var near = Attacker(sim, C(5, 5).Neighbor(HexDirection.South));
        var far = Attacker(sim, C(5, 8));
        Assert.Equal(3, Hit(sim, near, target, 10, "n").SurpriseSector);
        Assert.Equal((null, false), (Hit(sim, far, target, 10, "f").SurpriseSector, Hit(sim, far, target, 10, "f2").Surprised));

        var (open, t2) = Arena(On);
        UnitId Scout(HexCoord cell, double bonus) => open.Spawn(SpawnRequest.AtCell(cell, Side.Player, HexDirection.North) with
        {
            Profile = new UnitProfile("s", "Разведчик", new UnitStats { SurpriseChanceBonus = bonus }, System.Array.Empty<string>()),
        }).Unit!.Value;
        Assert.Equal(90, Hit(open, Scout(C(5, 5).Neighbor(HexDirection.SouthEast), 15), t2, 10, "b1").SurpriseChance); // 75 + 15
        Assert.Equal(15, Hit(open, Scout(C(5, 5).Neighbor(HexDirection.North), 15), t2, 10, "b2").SurpriseChance);    // 0 + 15
        Assert.Equal(100, Hit(open, Scout(C(5, 5).Neighbor(HexDirection.South), 40), t2, 10, "b3").SurpriseChance);   // 100 + 40 → 100
    }

    [Fact]
    public void P6_51_the_damage_event_tells_the_view_the_tier_and_the_surprise_only()
    {
        var (sim, target) = Arena(On);
        var a = Attacker(sim, C(5, 5).Neighbor(HexDirection.South), critChance: 200);
        sim.DrainEvents();
        Hit(sim, a, target, 10, "e");
        var applied = sim.DrainEvents().OfType<DamageApplied>().Single();
        Assert.Equal((true, CritTier.SuperCrit), (applied.Surprised, applied.Crit));
        var (legacy, t2) = Arena(CombatRules.Default);
        var b = Attacker(legacy, C(5, 5).Neighbor(HexDirection.South), critChance: 200);
        legacy.DrainEvents();
        Hit(legacy, b, t2, 10, "e");
        Assert.Equal((false, CritTier.Normal), legacy.DrainEvents().OfType<DamageApplied>().Select(d => (d.Surprised, d.Crit)).Single());
    }

    [Fact]
    public void P6_46_the_compatible_rules_keep_old_results_and_immunities_switch_off_their_bonus()
    {
        var (legacy, t1) = Arena(CombatRules.Default);
        var a = Attacker(legacy, C(5, 5).Neighbor(HexDirection.South), critChance: 1000);
        var r = Hit(legacy, a, t1, 10, "k");
        Assert.Equal((null, CritTier.Normal, 10.0), (r.SurpriseSector, r.Crit, r.HpLoss));

        var (sim, t2) = Arena(On, target: new UnitStats { MaxHealth = 100000, CritImmune = true, SurpriseImmune = true });
        var b = Attacker(sim, C(5, 5).Neighbor(HexDirection.South), critChance: 1000);
        var immune = Hit(sim, b, t2, 10, "k");
        Assert.Equal((false, CritTier.Normal, 10.0), (immune.Surprised, immune.Crit, immune.HpLoss));
    }
}
