using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Effects;
using HexTactics.Core.Hex;
using HexTactics.Core.Orders;
using HexTactics.Core.Skills;
using HexTactics.Core.Spawning;
using HexTactics.Core.Units;
using HexTactics.Tests.Shared;
using Xunit;

namespace HexTactics.Simulation.Tests;

/// <summary>Per-cell effects with formulas, hits over time, healing, buffs/debuffs, periodic effects (spec part 4 §6; §12.2 p.12–20).</summary>
public class SkillEffectsTests
{
    private static HexCoord C(int col, int row) => HexCoord.FromOffset(col, row);

    private static EffectSpec E(string type, params (string Key, object Value)[] p) =>
        new(type, p.ToDictionary(x => x.Key, x => x.Value));

    private static readonly HexCoord Front = new(0, -1);
    private static readonly HexCoord Front3 = new(0, -3);

    /// <summary>«Тройной рубящий удар»: 3 × 150 % of AP in front, 3 × 75 % two hexes further, 0.2 s apart.</summary>
    private static SkillDefinition TripleSlash() => new()
    {
        Id = "test.triple",
        Name = "Тройной рубящий удар",
        Cells = new[]
        {
            new SkillCell(Front, new[] { E("damage", ("formula", "attack"), ("multiplier", 1.5), ("hits", 3.0), ("interval", 0.2), ("targets", "enemies")) }),
            new SkillCell(Front3, new[] { E("damage", ("formula", "attack"), ("multiplier", 0.75), ("hits", 3.0), ("interval", 0.2), ("targets", "enemies")) }),
        },
    };

    private static GameSimulation Sim(params SkillDefinition[] skills) => new(TestMaps.Empty(), new SimulationSettings
    {
        Skills = skills.ToDictionary(s => s.Id),
        DefaultSkillSet = skills.Select(s => s.Id).ToArray(),
    });

    private static UnitId Spawn(GameSimulation sim, int col, int row, Side side = Side.Player) =>
        sim.Spawn(SpawnRequest.AtCell(C(col, row), side, side == Side.Player ? HexDirection.North : HexDirection.South)).Unit!.Value;

    [Fact]
    public void P12_one_hex_three_hits_of_150_percent_of_attack_power_10_take_45()
    {
        var sim = Sim(TripleSlash());
        var caster = Spawn(sim, 5, 7);
        var enemy = Spawn(sim, 5, 6, Side.Enemy);
        Assert.True(sim.UseSkill(caster, "test.triple", HexDirection.North).Success);
        sim.Step();
        Assert.Equal(85, sim.State.GetUnit(enemy).CurrentHealth, 9); // first hit at once
        for (var i = 0; i < 30; i++)
        {
            sim.Step();
        }

        Assert.Equal(55, sim.State.GetUnit(enemy).CurrentHealth, 9);
        Assert.Null(sim.State.GetUnit(caster).ActiveSkill);
    }

    [Fact]
    public void P13_the_far_hex_takes_three_hits_of_75_percent_22_5_identically_at_any_frame_rate()
    {
        double Run(double frame)
        {
            var sim = Sim(TripleSlash());
            var caster = Spawn(sim, 5, 8);
            var far = Spawn(sim, 5, 5, Side.Enemy); // (0,-3) from the caster
            sim.UseSkill(caster, "test.triple", HexDirection.North);
            for (var t = 0.0; t < 2.0; t += frame)
            {
                sim.Advance(frame);
            }

            return sim.State.GetUnit(far).CurrentHealth;
        }

        Assert.Equal(77.5, Run(1.0 / 60), 9);
        Assert.Equal(Run(1.0 / 60), Run(1.0 / 7), 12);
        Assert.Equal(Run(1.0 / 60), Run(0.3), 12);
    }

    [Fact]
    public void P14_the_area_rotates_with_the_facing_without_mirroring()
    {
        var skill = new SkillDefinition { Id = "test.l", Name = "L", Cells = new[] { new SkillCell(new HexCoord(0, -1), System.Array.Empty<EffectSpec>()), new SkillCell(new HexCoord(1, -2), System.Array.Empty<EffectSpec>()) } };
        var origin = C(5, 5);
        foreach (var d in HexDirections.All)
        {
            var area = skill.Area(origin, d);
            Assert.Equal(origin.Neighbor(d), area[0]); // forward always first
            Assert.Equal(origin + new HexCoord(1, -2).RotateFor(d), area[1]);
            Assert.Equal(2, area[1].DistanceTo(origin));
            Assert.Equal(1, area[1].DistanceTo(area[0]));
        }
    }

    [Fact]
    public void P16_healing_allies_only_never_above_the_maximum_and_never_the_enemy()
    {
        var heal = new SkillDefinition
        {
            Id = "test.heal",
            Name = "Лечебный луч",
            Cells = new[] { Front, new HexCoord(0, -2), new HexCoord(0, -3) }.Select(o => new SkillCell(o, new[] { E("heal", ("formula", "fixed"), ("amount", 10.0), ("targets", "allies")) })).ToArray(),
        };
        var sim = Sim(heal);
        var caster = Spawn(sim, 5, 8);
        var wounded = Spawn(sim, 5, 7);
        var almost = Spawn(sim, 5, 6);
        var enemy = Spawn(sim, 5, 5, Side.Enemy);
        sim.State.ApplyDamage(wounded, 30);
        sim.State.ApplyDamage(almost, 4);
        sim.State.ApplyDamage(enemy, 30);
        sim.UseSkill(caster, "test.heal", HexDirection.North);
        sim.Step();
        Assert.Equal(80, sim.State.GetUnit(wounded).CurrentHealth);
        Assert.Equal(100, sim.State.GetUnit(almost).CurrentHealth);
        Assert.Equal(70, sim.State.GetUnit(enemy).CurrentHealth);
        Assert.Contains(sim.DrainEvents().OfType<HealApplied>(), h => h.Target == almost && h.Amount == 4);
    }

    [Fact]
    public void P17_buff_and_debuff_last_game_time_freeze_on_pause_and_restore_the_value()
    {
        var slow = new SkillDefinition { Id = "test.slow", Name = "Замедление", Cells = new[] { new SkillCell(Front, new[] { E("debuff", ("stat", "moveSpeed"), ("mode", "percent"), ("percent", 0.5), ("duration", 2.0), ("targets", "enemies")) }) } };
        var rage = new SkillDefinition { Id = "test.rage", Name = "Ярость", Cells = new[] { new SkillCell(new HexCoord(0, 0), new[] { E("buff", ("stat", "attackPower"), ("mode", "fixed"), ("delta", 5.0), ("duration", 1.0), ("targets", "self")) }) } };
        var sim = Sim(slow, rage);
        var caster = Spawn(sim, 5, 7);
        var enemy = Spawn(sim, 5, 6, Side.Enemy);
        sim.UseSkill(caster, "test.slow", HexDirection.North);
        sim.Step();
        Assert.Equal(0.5, sim.State.GetUnit(enemy).SpeedHexesPerSecond, 9);
        Assert.Equal(1.0, sim.State.GetUnit(enemy).BaseStats.MoveSpeedHexPerSecond);

        sim.SetPaused(true);
        sim.Advance(10); // no time passes on pause
        Assert.Equal(0.5, sim.State.GetUnit(enemy).SpeedHexesPerSecond, 9);
        sim.SetPaused(false);
        for (var i = 0; i < 61; i++)
        {
            sim.Step();
        }

        Assert.Equal(1.0, sim.State.GetUnit(enemy).SpeedHexesPerSecond, 9);
        Assert.Contains(sim.DrainEvents().OfType<StatusExpired>(), s => s.Target == enemy && s.Stat == "moveSpeed");

        sim.UseSkill(caster, "test.rage", HexDirection.North);
        sim.Step();
        Assert.Equal(15, sim.State.GetUnit(caster).Stats.AttackPower, 9);
    }

    [Theory]
    [InlineData("refresh", 0.5, 1.5)]   // one instance, its timer reset at the second application
    [InlineData("stack", 0.25, 1.5)]    // two instances: 1 − 0.375 − 0.375; the second one runs out 1 s after it was applied
    [InlineData("replace", 0.5, 1.5)]
    [InlineData("ignore", 0.5, 1.0)]    // the second application does nothing: expires 1 s after the first
    public void Stacking_policies_of_a_repeated_debuff(string policy, double speedAfterSecond, double expiresAt)
    {
        var slow = new SkillDefinition { Id = "test.slow", Name = "Замедление", Cells = new[] { new SkillCell(Front, new[] { E("debuff", ("stat", "moveSpeed"), ("mode", "fixed"), ("delta", policy == "stack" ? 0.375 : 0.5), ("duration", 1.0), ("stacking", policy), ("targets", "enemies")) }) } };
        var sim = Sim(slow);
        var caster = Spawn(sim, 5, 7);
        var enemy = Spawn(sim, 5, 6, Side.Enemy);
        sim.UseSkill(caster, "test.slow", HexDirection.North);
        sim.Step();
        for (var i = 0; i < 15; i++)
        {
            sim.Step(); // 0.5 s later the same skill again
        }

        sim.UseSkill(caster, "test.slow", HexDirection.North);
        sim.Step();
        Assert.Equal(speedAfterSecond, sim.State.GetUnit(enemy).SpeedHexesPerSecond, 9);
        var t = 16.0 / 30;
        while (sim.State.GetUnit(enemy).Statuses.Count > 0 && t < 5)
        {
            sim.Step();
            t += 1.0 / 30;
        }

        Assert.Equal(expiresAt, t, 1);
        Assert.Equal(1.0, sim.State.GetUnit(enemy).SpeedHexesPerSecond, 9);
    }

    [Fact]
    public void A_debuff_never_pushes_a_value_below_its_safe_minimum()
    {
        var crush = new SkillDefinition { Id = "test.crush", Name = "Сокрушение", Cells = new[] { new SkillCell(Front, new[] { E("debuff", ("stat", "attackRate"), ("mode", "percent"), ("percent", 5.0), ("duration", 1.0), ("targets", "enemies")), E("debuff", ("stat", "attackPower"), ("mode", "fixed"), ("delta", 500.0), ("duration", 1.0), ("targets", "enemies")) }) } };
        var sim = Sim(crush);
        var caster = Spawn(sim, 5, 7);
        var enemy = Spawn(sim, 5, 6, Side.Enemy);
        sim.UseSkill(caster, "test.crush", HexDirection.North);
        sim.Step();
        Assert.Equal(Stats.MinAttackRate, sim.State.GetUnit(enemy).Stats.AttackRatePerSecond, 9);
        Assert.Equal(0, sim.State.GetUnit(enemy).Stats.AttackPower, 9);
    }

    [Fact]
    public void A_periodic_effect_of_5_HP_per_second_for_2_seconds_takes_exactly_10()
    {
        var burn = new SkillDefinition { Id = "test.burn", Name = "Поджог", Cells = new[] { new SkillCell(Front, new[] { E("periodic", ("kind", "damage"), ("perSecond", 5.0), ("duration", 2.0), ("targets", "enemies")) }) } };
        var sim = Sim(burn);
        var caster = Spawn(sim, 5, 7);
        var enemy = Spawn(sim, 5, 6, Side.Enemy);
        sim.UseSkill(caster, "test.burn", HexDirection.North);
        sim.Step();
        sim.SetPaused(true);
        sim.Advance(5);
        sim.SetPaused(false);
        for (var i = 0; i < 120; i++)
        {
            sim.Step();
        }

        Assert.Equal(90, sim.State.GetUnit(enemy).CurrentHealth, 9);
        Assert.Empty(sim.State.GetUnit(enemy).Statuses);
    }

    [Fact]
    public void P20_a_target_leaving_the_cell_between_hits_is_not_hit_again_and_pause_never_reactivates()
    {
        // Hits 1 s apart; the leaver switches its logical hex half-way through its 1 s step, so only the first hit lands.
        var slowTriple = new SkillDefinition
        {
            Id = "test.triple",
            Name = "Медленный тройной",
            Cells = new[] { new SkillCell(Front, new[] { E("damage", ("formula", "attack"), ("multiplier", 1.5), ("hits", 3.0), ("interval", 1.0), ("targets", "enemies")) }) },
        };
        var sim = Sim(slowTriple);
        var caster = Spawn(sim, 5, 8);
        var leaver = Spawn(sim, 5, 7, Side.Enemy);
        sim.UseSkill(caster, "test.triple", HexDirection.North);
        sim.Step();
        Assert.Equal(85, sim.State.GetUnit(leaver).CurrentHealth, 9);
        Assert.True(sim.ApplyOrder(new IndividualOrder(leaver, new[] { C(4, 7) }, HexDirection.South)).Success);
        sim.DrainEvents();
        sim.SetPaused(true);
        sim.SetPaused(false);
        var events = new List<SimulationEvent>();
        for (var i = 0; i < 90; i++)
        {
            sim.Step();
            events.AddRange(sim.DrainEvents());
        }

        Assert.Equal(85, sim.State.GetUnit(leaver).CurrentHealth, 9); // in (4,7) logically before the second hit (1 s)
        Assert.Single(events.OfType<SkillCompleted>());
        Assert.DoesNotContain(events, e => e is SkillActivated);
    }

    [Fact]
    public void A_waypoint_skill_holds_the_unit_until_its_last_hit_then_the_route_goes_on()
    {
        var sim = Sim(TripleSlash());
        var unit = Spawn(sim, 1, 5);
        var order = new IndividualOrder(unit, new[] { C(2, 5), C(4, 5) }, HexDirection.North, null, new Dictionary<int, WaypointSkillSpec> { [0] = new("test.triple", HexDirection.North) });
        Assert.True(sim.ApplyOrder(order).Success);
        long started = -1, completed = -1, leftWaypoint = -1;
        for (var i = 0; i < 300 && leftWaypoint < 0; i++)
        {
            sim.Step();
            foreach (var e in sim.DrainEvents())
            {
                switch (e)
                {
                    case SkillActivated a:
                        started = a.Tick;
                        break;
                    case SkillCompleted c:
                        completed = c.Tick;
                        break;
                    case StepStarted s when s.From == C(2, 5):
                        leftWaypoint = s.Tick;
                        break;
                }
            }
        }

        Assert.Equal(12, completed - started); // 0.4 s of hits at 30 ticks/s
        Assert.Equal(completed + 1, leftWaypoint); // then on by itself, next tick
    }

    [Fact]
    public void Removing_the_waypoint_stops_the_remaining_hits_and_keeps_the_applied_ones()
    {
        var sim = Sim(TripleSlash());
        var unit = Spawn(sim, 1, 5);
        var enemy = Spawn(sim, 2, 4, Side.Enemy); // in front of (2,5)
        var order = new IndividualOrder(unit, new[] { C(2, 5), C(4, 5) }, HexDirection.North, null, new Dictionary<int, WaypointSkillSpec> { [0] = new("test.triple", HexDirection.North) });
        sim.ApplyOrder(order);
        for (var i = 0; i < 200 && sim.State.GetUnit(unit).ActiveSkill is null; i++)
        {
            sim.Step();
        }

        Assert.Equal(85, sim.State.GetUnit(enemy).CurrentHealth, 9);
        var wp = sim.State.GetUnit(unit).Orders.Items[0];
        Assert.True(sim.TruncateOrders(unit, wp.Id).Success);
        var events = new List<SimulationEvent>();
        for (var i = 0; i < 30; i++)
        {
            sim.Step();
            events.AddRange(sim.DrainEvents());
        }

        Assert.Equal(85, sim.State.GetUnit(enemy).CurrentHealth, 9);
        Assert.Contains(events, e => e is SkillInterrupted { HitsDone: 2, HitsTotal: 6 });
        Assert.Null(sim.State.GetUnit(unit).ActiveSkill);
    }

    [Fact]
    public void Hits_of_one_tick_follow_the_fixed_order_hit_cell_effect()
    {
        var cells = TripleSlash().Resolve(C(5, 8), HexDirection.North);
        var schedule = SkillExecution.Schedule(cells);
        Assert.Equal(6, schedule.Count);
        Assert.Equal(new[] { (0.0, 0, 0), (0.0, 0, 1), (0.2, 1, 0), (0.2, 1, 1), (0.4, 2, 0), (0.4, 2, 1) }, schedule.Select(h => (System.Math.Round(h.Time, 6), h.HitIndex, h.CellIndex)));
    }
}
