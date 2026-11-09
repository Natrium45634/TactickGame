using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Combat;
using HexTactics.Core.Hex;
using HexTactics.Core.Orders;
using HexTactics.Core.Skills;
using HexTactics.Core.Spawning;
using HexTactics.Core.Units;
using HexTactics.Tests.Shared;
using Xunit;

namespace HexTactics.Simulation.Tests;

/// <summary>The automatic base attack in its own area (spec part 7 §8; acceptance M7-43…M7-49).</summary>
public class BasicAttackTests
{
    private static HexCoord C(int col, int row) => HexCoord.FromOffset(col, row);

    private static readonly BasicAttackDefinition Spear = new()
    {
        Id = "spear", Name = "Копьё", Cells = new[] { new HexCoord(0, -1), new HexCoord(0, -2) },
    };

    private static GameSimulation Sim(bool autoAttacks = true, CombatRules? rules = null) =>
        new(TestMaps.Empty(), new SimulationSettings { AutoAttacks = autoAttacks, CombatRules = rules ?? CombatRules.Default });

    private static UnitId Fighter(GameSimulation sim, HexCoord cell, BasicAttackDefinition? basic = null, Side side = Side.Player, HexDirection facing = HexDirection.North,
        double rate = 3, bool auto = true, double power = 10) =>
        sim.Spawn(SpawnRequest.AtCell(cell, side, facing) with
        {
            Profile = new UnitProfile("f", "Боец", new UnitStats
            {
                AttackPower = power, AttackRatePerSecond = rate, BasicAttack = basic ?? BasicAttackDefinition.Front, AutoAttackEnabled = auto,
            }, System.Array.Empty<string>()),
        }).Unit!.Value;

    private static UnitId Dummy(GameSimulation sim, HexCoord cell, int hp = 1000, Side side = Side.Enemy) =>
        sim.Spawn(SpawnRequest.AtCell(cell, side, HexDirection.North) with
        {
            Profile = new UnitProfile("d", "Мишень", new UnitStats { MaxHealth = hp }, System.Array.Empty<string>()),
        }).Unit!.Value;

    private static double Hp(GameSimulation sim, UnitId id) => sim.State.GetUnit(id).CurrentHealth;

    private static void Run(GameSimulation sim, int ticks)
    {
        for (var i = 0; i < ticks; i++)
        {
            sim.Step();
        }
    }

    [Fact]
    public void M7_44_three_hits_per_game_second_on_a_target_in_front_and_none_without_the_match_switch()
    {
        var sim = Sim();
        Fighter(sim, C(5, 5));
        var target = Dummy(sim, C(5, 4));
        Run(sim, 30); // one game second at 1/30 s
        Assert.Equal(1000 - 30, Hp(sim, target), 9);

        var off = Sim(autoAttacks: false);
        Fighter(off, C(5, 5));
        var t2 = Dummy(off, C(5, 4));
        Run(off, 60);
        Assert.Equal(1000, Hp(off, t2), 9); // old maps: no automatic attacks
    }

    [Fact]
    public void M7_45_M7_46_only_its_own_area_no_turning_and_a_turn_of_60_degrees_moves_the_area_by_one_side()
    {
        var sim = Sim();
        var spear = Fighter(sim, C(5, 5), Spear);
        var far = Dummy(sim, C(5, 3));          // two hexes ahead: in the area
        var behind = Dummy(sim, C(5, 6));       // behind: never searched
        var side = Dummy(sim, C(5, 5).Neighbor(HexDirection.NorthEast));
        Run(sim, 30);
        Assert.Equal((970.0, 1000.0, 1000.0), (Hp(sim, far), Hp(sim, behind), Hp(sim, side)));
        Assert.Equal(HexDirection.North, sim.State.GetUnit(spear).Facing); // never turns by itself
        Assert.Equal(C(5, 5), sim.State.GetUnit(spear).Cell);               // never chases

        Assert.True(sim.ApplyOrder(new IndividualOrder(spear, System.Array.Empty<HexCoord>(), HexDirection.NorthEast)).Success);
        Run(sim, 60);
        Assert.Equal(HexDirection.NorthEast, sim.State.GetUnit(spear).Facing);
        Assert.Equal(1, BasicAttackDefinition.Front.Area(C(5, 5), HexDirection.North)[0].DistanceTo(C(5, 5)));
        Assert.True(Hp(sim, side) < 1000);           // the area turned by one side
        Assert.Equal(970, Hp(sim, far), 9);           // the old front is no longer attacked
    }

    [Fact]
    public void M7_43_the_base_attack_area_is_independent_of_skills()
    {
        var sim = Sim();
        var unit = Fighter(sim, C(5, 5), Spear);
        var skill = SkillCatalog.LinearStrike();
        Assert.NotEqual(skill.Area(C(5, 5), HexDirection.North).Count, BasicAttackDefinition.Front.Area(C(5, 5), HexDirection.North).Count);
        Assert.Equal(Spear.Area(C(5, 5), HexDirection.North), new[] { C(5, 4), C(5, 3) });
        Assert.Same(Spear, sim.State.GetUnit(unit).Stats.BasicAttack); // the unit keeps its own definition, not the skill's cells
    }

    [Fact]
    public void M7_47_an_explicit_order_has_priority_without_a_free_extra_hit()
    {
        var sim = Sim();
        var unit = Fighter(sim, C(5, 5), Spear, rate: 1);
        var near = Dummy(sim, C(5, 4));
        var chosen = Dummy(sim, C(5, 3));
        Run(sim, 20); // two thirds of an interval towards the nearest
        Assert.True(sim.ApplyOrder(new IndividualOrder(unit, System.Array.Empty<HexCoord>(), HexDirection.North, chosen)).Success);
        Run(sim, 31);
        Assert.Equal((1000.0, 1000.0), (Hp(sim, near), Hp(sim, chosen))); // explicit hits come after a full interval of its own
        sim.ApplyOrder(new IndividualOrder(unit, System.Array.Empty<HexCoord>(), HexDirection.North, chosen));
        Run(sim, 120);
        Assert.Equal(1000, Hp(sim, near), 9);                               // the order's target, not the nearest
        Assert.True(Hp(sim, chosen) < 1000);
    }

    [Fact]
    public void M7_48_the_hit_goes_through_the_one_pipeline_with_crits_surprise_and_defense()
    {
        var sim = Sim(rules: new CombatRules { SurpriseEnabled = true, CritEnabled = true });
        sim.Spawn(SpawnRequest.AtCell(C(5, 5), Side.Player, HexDirection.North) with
        {
            Profile = new UnitProfile("a", "Убийца", new UnitStats { AttackPower = 10, AttackRatePerSecond = 1, CritChance = 200, BasicAttack = BasicAttackDefinition.Front, AutoAttackEnabled = true }, System.Array.Empty<string>()),
        });
        var target = sim.Spawn(SpawnRequest.AtCell(C(5, 4), Side.Enemy, HexDirection.North) with
        {
            Profile = new UnitProfile("t", "Цель", new UnitStats { MaxHealth = 1000, Armor = new ArmorSet(new Dictionary<string, double> { [ArmorSet.Global] = 100 }) }, System.Array.Empty<string>()),
        }).Unit!.Value;
        sim.DrainEvents();
        Run(sim, 31);
        var hit = sim.DrainEvents().OfType<DamageApplied>().Single();
        Assert.Equal(CritTier.SuperCrit, hit.Crit);       // crits of part 6
        Assert.True(hit.Surprised);                        // the attacker stands behind the target (it looks north)
        Assert.Equal(10 * 2.25 * 1.5 * 0.5, hit.Amount, 9); // defense A 100 halves it
        Assert.Equal(1000 - 16.875, Hp(sim, target), 9);
    }

    [Fact]
    public void Pause_freezes_the_timer_without_a_catch_up_burst_and_no_hits_while_walking_if_forbidden()
    {
        var sim = Sim();
        Fighter(sim, C(5, 5), rate: 1);
        var target = Dummy(sim, C(5, 4));
        Run(sim, 20);
        sim.SetPaused(true);
        sim.Advance(10); // ten real seconds of pause: the game clock stands
        sim.SetPaused(false);
        Assert.Equal(1000, Hp(sim, target), 9);
        Run(sim, 11);
        Assert.Equal(990, Hp(sim, target), 9); // the hit comes after the remaining third, once

        var still = Sim();
        var walker = Fighter(still, C(5, 7), BasicAttackDefinition.Front with { WhileMoving = false }, rate: 3);
        var t2 = Dummy(still, C(6, 5)); // to the right of the path
        Assert.True(still.ApplyOrder(new IndividualOrder(walker, new[] { C(5, 4) }, HexDirection.NorthEast)).Success);
        var hpWhileWalking = new List<double>();
        for (var i = 0; i < 200; i++)
        {
            still.Step();
            if (still.State.GetUnit(walker).Motion.IsStepping)
            {
                hpWhileWalking.Add(Hp(still, t2));
            }
        }

        Assert.All(hpWhileWalking, hp => Assert.Equal(1000, hp, 9));
    }

    [Fact]
    public void M7_49_a_skill_hits_once_and_the_base_attack_goes_on_after_it()
    {
        var skill = SkillCatalog.LinearStrike(20);
        var sim = new GameSimulation(TestMaps.Empty(), new SimulationSettings
        {
            AutoAttacks = true, Skills = new Dictionary<string, SkillDefinition> { [skill.Id] = skill }, DefaultSkillSet = new[] { skill.Id },
        });
        var unit = sim.Spawn(SpawnRequest.AtCell(C(5, 5), Side.Player, HexDirection.North) with
        {
            Profile = new UnitProfile("f", "Боец", new UnitStats { AttackPower = 10, AttackRatePerSecond = 1, BasicAttack = BasicAttackDefinition.Front, AutoAttackEnabled = true }, new[] { skill.Id }),
        }).Unit!.Value;
        var target = Dummy(sim, C(5, 4));
        sim.DrainEvents();
        Assert.True(sim.UseSkill(unit, skill.Id, HexDirection.North).Success);
        var skillHits = 0;
        var autoHits = 0;
        for (var i = 0; i < 120; i++)
        {
            sim.Step();
            foreach (var d in sim.DrainEvents().OfType<DamageApplied>().Where(d => d.Target == target))
            {
                if (d.Kind == DamageKind.Skill)
                {
                    skillHits++;
                }
                else
                {
                    autoHits++;
                }
            }
        }

        Assert.Equal(1, skillHits);       // the skill acts once
        Assert.InRange(autoHits, 2, 4);   // and the base attack goes on (1/s over four seconds, none during the skill action)
    }

    [Fact]
    public void Several_targets_by_priority_and_a_dead_target_takes_no_hit()
    {
        var sim = Sim();
        var ring = new BasicAttackDefinition { Id = "ring", Cells = HexDirections.All.Select(d => default(HexCoord).Neighbor(d)).ToArray(), MaxTargets = 2, Priority = TargetPriority.LowestHealth };
        Fighter(sim, C(5, 5), ring, rate: 1);
        var a = Dummy(sim, C(5, 5).Neighbor(HexDirection.North), hp: 50);
        var b = Dummy(sim, C(5, 5).Neighbor(HexDirection.South), hp: 30);
        var c = Dummy(sim, C(5, 5).Neighbor(HexDirection.SouthWest), hp: 40);
        Run(sim, 31);
        Assert.Equal((50.0, 20.0, 30.0), (Hp(sim, a), Hp(sim, b), Hp(sim, c))); // the two weakest

        var lone = Sim();
        Fighter(lone, C(5, 5), rate: 3);
        var weak = Dummy(lone, C(5, 4), hp: 5);
        var hits = 0;
        for (var i = 0; i < 60; i++)
        {
            lone.Step();
            hits += lone.DrainEvents().OfType<DamageApplied>().Count(d => d.Target == weak);
        }

        Assert.False(lone.State.FindUnit(weak)?.IsAlive ?? false);
        Assert.Equal(1, hits); // no ghost hits after the death
    }
}
