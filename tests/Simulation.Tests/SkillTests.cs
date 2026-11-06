using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Hex;
using HexTactics.Core.Orders;
using HexTactics.Core.Skills;
using HexTactics.Core.Spawning;
using HexTactics.Core.Units;
using HexTactics.Simulation;
using HexTactics.Tests.Shared;
using Xunit;

namespace HexTactics.Simulation.Tests;

/// <summary>«Линейный удар» (spec part 3 §5; acceptance 12–14, 21).</summary>
public class SkillTests
{
    private readonly GameSimulation _sim = new(TestMaps.Empty());

    private static HexCoord C(int col, int row) => HexCoord.FromOffset(col, row);

    private UnitId Spawn(int col, int row, Side side = Side.Player) =>
        _sim.Spawn(SpawnRequest.AtCell(C(col, row), side, HexDirection.North)).Unit!.Value;

    private UnitState U(UnitId id) => _sim.State.GetUnit(id);

    private List<SimulationEvent> RunTicks(int ticks)
    {
        var events = new List<SimulationEvent>();
        for (var i = 0; i < ticks; i++)
        {
            _sim.Step();
            events.AddRange(_sim.DrainEvents());
        }

        return events;
    }

    [Fact]
    public void P12_area_is_exactly_the_two_next_hexes_in_each_of_six_directions()
    {
        var skill = SkillCatalog.LinearStrike();
        var origin = C(5, 5);
        foreach (var d in HexDirections.All)
        {
            var area = skill.Area(origin, d, _sim.State.Grid);
            Assert.Equal(new[] { origin.Neighbor(d), origin.Neighbor(d).Neighbor(d) }, area);
            Assert.Equal(new[] { 1, 2 }, area.Select(c => c.DistanceTo(origin)));
        }
    }

    [Fact]
    public void P12_at_the_map_edge_only_existing_hexes_remain()
    {
        var skill = SkillCatalog.LinearStrike();
        Assert.Empty(skill.Area(C(0, 0), HexDirection.North, _sim.State.Grid));
        Assert.Single(skill.Area(C(5, 1), HexDirection.North, _sim.State.Grid));
        Assert.Equal(2, skill.Area(C(5, 2), HexDirection.North, _sim.State.Grid).Count);
        Assert.Single(skill.Area(C(10, 5), HexDirection.SouthEast, _sim.State.Grid));
    }

    [Fact]
    public void P13_two_enemies_in_the_area_take_20_each_an_ally_takes_nothing()
    {
        var caster = Spawn(5, 6);
        var enemyNear = Spawn(5, 5, Side.Enemy);
        var enemyFar = Spawn(5, 4, Side.Enemy);
        var ally = Spawn(6, 5); // beside the line: not hit
        var allyInLine = Spawn(4, 6);
        Assert.True(_sim.UseSkill(caster, SkillCatalog.LinearStrikeId, HexDirection.North).Success);

        var events = RunTicks(1);
        Assert.Equal(80, U(enemyNear).CurrentHealth);
        Assert.Equal(80, U(enemyFar).CurrentHealth);
        Assert.Equal(100, U(ally).CurrentHealth);
        var activated = Assert.Single(events.OfType<SkillActivated>());
        Assert.Equal(new[] { enemyNear, enemyFar }, activated.Hit);
        Assert.Equal(HexDirection.North, U(caster).Facing);

        // Friendly fire is off: an ally inside the area is not hurt either.
        Assert.True(_sim.UseSkill(allyInLine, SkillCatalog.LinearStrikeId, HexDirection.NorthEast).Success); // (5,5) enemy, (6,5) ally
        RunTicks(1);
        Assert.Equal(60, U(enemyNear).CurrentHealth);
        Assert.Equal(100, U(ally).CurrentHealth);
    }

    [Fact]
    public void Empty_area_still_counts_as_used_and_the_action_fires_exactly_once()
    {
        var caster = Spawn(5, 6);
        Assert.True(_sim.UseSkill(caster, SkillCatalog.LinearStrikeId, HexDirection.South).Success);
        var action = U(caster).PendingSkill!;
        var events = RunTicks(1);
        Assert.Equal(SkillActionStatus.Executed, action.Status);
        Assert.Empty(Assert.Single(events.OfType<SkillActivated>()).Hit);
        Assert.Null(U(caster).PendingSkill);

        var enemy = Spawn(5, 7, Side.Enemy); // walks into the old area afterwards: nothing happens any more
        _sim.SetPaused(true);
        _sim.SetPaused(false);
        events = RunTicks(90);
        Assert.DoesNotContain(events, e => e is SkillActivated);
        Assert.Equal(100, U(enemy).CurrentHealth);
    }

    [Fact]
    public void P14_damage_hits_who_stands_there_at_activation_not_who_stood_there_when_planned()
    {
        var caster = Spawn(5, 8);
        var leaver = Spawn(5, 6, Side.Enemy);   // in the area when planned, leaves before the strike
        var comer = Spawn(6, 6, Side.Enemy);    // outside when planned, steps into the area
        Assert.True(_sim.ApplyOrder(new IndividualOrder(caster, new[] { C(5, 7) }, HexDirection.North)).Success);
        _sim.Step(); // the caster starts its step to (5,7): the strike will come from there, after the step

        _sim.SetPaused(true);
        Assert.True(_sim.UseSkill(caster, SkillCatalog.LinearStrikeId, HexDirection.North).Success); // (5,6), (5,5)
        Assert.True(_sim.ApplyOrder(new IndividualOrder(leaver, new[] { C(4, 6) }, HexDirection.North)).Success);
        Assert.True(_sim.ApplyOrder(new IndividualOrder(comer, new[] { C(5, 5) }, HexDirection.North)).Success);
        Assert.Equal(100, U(leaver).CurrentHealth); // nothing happens on pause
        _sim.SetPaused(false);

        var events = new List<SimulationEvent>();
        for (var i = 0; i < 60 && !events.OfType<SkillActivated>().Any(); i++)
        {
            _sim.Advance(1.0 / 30);
            events.AddRange(_sim.DrainEvents());
        }

        var activated = Assert.Single(events.OfType<SkillActivated>());
        Assert.Equal(C(5, 7), activated.Origin);
        Assert.Equal(100, U(leaver).CurrentHealth);
        Assert.Equal(80, U(comer).CurrentHealth);
        Assert.Equal(new[] { comer }, activated.Hit);
    }

    [Fact]
    public void Planning_needs_a_living_active_unit_with_the_skill_and_each_plan_has_its_own_id()
    {
        var caster = Spawn(5, 6);
        Assert.False(_sim.UseSkill(caster, "no-such-skill", HexDirection.North).Success);
        Assert.False(_sim.UseSkill(new UnitId(999), SkillCatalog.LinearStrikeId, HexDirection.North).Success);

        Assert.True(_sim.UseSkill(caster, SkillCatalog.LinearStrikeId, HexDirection.North).Success);
        var first = U(caster).PendingSkill!;
        Assert.True(_sim.UseSkill(caster, SkillCatalog.LinearStrikeId, HexDirection.South).Success); // re-aimed
        var second = U(caster).PendingSkill!;
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(SkillActionStatus.Cancelled, first.Status);

        _sim.CancelPendingSkill(caster);
        Assert.Null(U(caster).PendingSkill);
        Assert.Equal(SkillActionStatus.Cancelled, second.Status);
        Assert.DoesNotContain(RunTicks(5), e => e is SkillActivated);
    }

    [Fact]
    public void A_skill_killing_a_target_removes_it_at_the_end_of_the_tick()
    {
        var caster = Spawn(5, 6);
        var enemy = Spawn(5, 5, Side.Enemy);
        _sim.State.ApplyDamage(enemy, 85);
        Assert.True(_sim.UseSkill(caster, SkillCatalog.LinearStrikeId, HexDirection.North).Success);
        var events = RunTicks(1);
        Assert.Contains(events, e => e is DamageApplied { Kind: DamageKind.Skill, Amount: 15, HealthAfter: 0 });
        Assert.Contains(events, e => e is UnitDied d && d.Unit == enemy);
        Assert.Null(_sim.State.FindUnit(enemy));
    }
}
