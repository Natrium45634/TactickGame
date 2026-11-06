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

/// <summary>Skills planned at waypoints (spec part 3 §6, §7; acceptance 17, 18).</summary>
public class WaypointSkillTests
{
    private readonly GameSimulation _sim = new(TestMaps.Empty());

    private static HexCoord C(int col, int row) => HexCoord.FromOffset(col, row);

    private UnitId Spawn(int col, int row, Side side = Side.Player) =>
        _sim.Spawn(SpawnRequest.AtCell(C(col, row), side, HexDirection.North)).Unit!.Value;

    private UnitState U(UnitId id) => _sim.State.GetUnit(id);

    private static Dictionary<int, WaypointSkillSpec> Skill(int index, HexDirection direction) =>
        new() { [index] = new WaypointSkillSpec(SkillCatalog.LinearStrikeId, direction) };

    private List<SimulationEvent> RunUntilIdle(UnitId id, double seconds = 30)
    {
        var events = new List<SimulationEvent>();
        var u = U(id);
        for (var t = 0.0; t < seconds && !(u.Orders.IsEmpty && !u.Motion.IsStepping && u.Orders.FinalFacing is null); t += 1.0 / 30)
        {
            _sim.Step();
            events.AddRange(_sim.DrainEvents());
        }

        return events;
    }

    [Fact]
    public void P17_route_TD1_TD2_skill_TD3_reaches_TD2_strikes_once_and_goes_on_by_itself()
    {
        var unit = Spawn(1, 5);
        var enemy = Spawn(4, 1, Side.Enemy); // NW of ТД2 (5,1)… two hexes: (4,1), (3,0)
        var order = new IndividualOrder(unit, new[] { C(3, 3), C(5, 1), C(7, 1) }, HexDirection.South, null, Skill(1, HexDirection.NorthWest));
        Assert.True(_sim.ApplyOrder(order).Success);
        var action = U(unit).Orders.Items[1].Skill!;
        Assert.Equal(SkillActionStatus.Planned, action.Status);

        var events = RunUntilIdle(unit);
        var sequence = events.Where(e => e is WaypointReached { Waypoint.IsDetour: false } || e is SkillActivated)
            .Select(e => e switch
            {
                WaypointReached r => $"reach {r.Waypoint.Cell}",
                SkillActivated s => $"skill {s.Origin} {s.Direction}",
                _ => string.Empty,
            }).ToList();
        Assert.Equal(new[] { $"reach {C(3, 3)}", $"skill {C(5, 1)} NorthWest", $"reach {C(5, 1)}", $"reach {C(7, 1)}" }, sequence);

        Assert.Equal(80, U(enemy).CurrentHealth);
        Assert.Equal(SkillActionStatus.Executed, action.Status);
        Assert.Single(events.OfType<SkillActivated>());
        Assert.Equal(C(7, 1), U(unit).Cell);
        Assert.Equal(HexDirection.South, U(unit).Facing); // the order's final facing at the end

        // The unit is held at ТД2 for exactly the tick of the strike.
        var skillTick = events.OfType<SkillActivated>().Single().Tick;
        var leftTd2 = events.OfType<StepStarted>().First(s => s.From == C(5, 1)).Tick;
        Assert.Equal(skillTick + 1, leftTd2);
    }

    [Fact]
    public void P18_skills_on_different_waypoints_have_their_own_ids_and_each_fires_exactly_once()
    {
        var unit = Spawn(1, 5);
        var skills = new Dictionary<int, WaypointSkillSpec>
        {
            [0] = new(SkillCatalog.LinearStrikeId, HexDirection.North),
            [2] = new(SkillCatalog.LinearStrikeId, HexDirection.South),
        };
        Assert.True(_sim.ApplyOrder(new IndividualOrder(unit, new[] { C(2, 4), C(4, 4), C(2, 4) }, HexDirection.North, null, skills)).Success);
        var ids = U(unit).Orders.Items.Where(w => w.Skill is not null).Select(w => w.Skill!.Id).ToList();
        Assert.Equal(2, ids.Distinct().Count());

        // ТД1 and ТД3 are the same hex: re-entering it does not repeat the first strike.
        var events = RunUntilIdle(unit);
        var fired = events.OfType<SkillActivated>().Select(s => s.ActionId).ToList();
        Assert.Equal(ids, fired);
        Assert.Equal(new[] { HexDirection.North, HexDirection.South }, events.OfType<SkillActivated>().Select(s => s.Direction));
    }

    [Fact]
    public void At_the_final_waypoint_the_unit_strikes_then_turns_back_to_the_order_facing()
    {
        var unit = Spawn(1, 5);
        Assert.True(_sim.ApplyOrder(new IndividualOrder(unit, new[] { C(3, 5) }, HexDirection.North, null, Skill(0, HexDirection.SouthEast))).Success);
        HexDirection? atStrike = null;
        for (var i = 0; i < 200 && (U(unit).Orders.Count > 0 || U(unit).Orders.FinalFacing is not null); i++)
        {
            _sim.Step();
            if (_sim.DrainEvents().OfType<SkillActivated>().Any())
            {
                atStrike = U(unit).Facing;
            }
        }

        Assert.Equal(HexDirection.SouthEast, atStrike);
        Assert.Equal(HexDirection.North, U(unit).Facing);
    }

    [Fact]
    public void Removing_a_waypoint_removes_only_its_skill_and_an_unreachable_waypoint_never_fires_elsewhere()
    {
        var unit = Spawn(1, 5);
        var skills = new Dictionary<int, WaypointSkillSpec>
        {
            [0] = new(SkillCatalog.LinearStrikeId, HexDirection.North),
            [1] = new(SkillCatalog.LinearStrikeId, HexDirection.North),
        };
        Assert.True(_sim.ApplyOrder(new IndividualOrder(unit, new[] { C(1, 3), C(4, 3) }, HexDirection.North, null, skills)).Success);
        var first = U(unit).Orders.Items[0].Skill!;
        var second = U(unit).Orders.Items[1].Skill!;
        Assert.True(_sim.TruncateOrders(unit, U(unit).Orders.Items[1].Id).Success);
        Assert.Equal(SkillActionStatus.Cancelled, second.Status);
        Assert.Equal(SkillActionStatus.Planned, first.Status);

        // Wall the remaining waypoint in: the unit stops, the skill does not fire from another hex.
        Assert.True(_sim.SetObstacle(C(1, 3), true).Success);
        var events = new List<SimulationEvent>();
        for (var i = 0; i < 150; i++)
        {
            _sim.Step();
            events.AddRange(_sim.DrainEvents());
        }

        Assert.DoesNotContain(events, e => e is SkillActivated);
        Assert.Equal(SkillActionStatus.Planned, first.Status);
    }

    [Fact]
    public void Skills_can_be_assigned_to_and_removed_from_a_confirmed_route_and_bad_ones_are_refused()
    {
        var unit = Spawn(1, 5);
        Assert.True(_sim.ApplyOrder(new IndividualOrder(unit, new[] { C(3, 5), C(5, 5) }, HexDirection.North)).Success);
        var wp = U(unit).Orders.Items[1];
        Assert.True(_sim.AssignWaypointSkill(unit, wp.Id, SkillCatalog.LinearStrikeId, HexDirection.North).Success);
        var action = U(unit).Orders.Items[1].Skill!;
        Assert.Equal(wp.Id, action.OriginWaypointId);
        Assert.True(_sim.AssignWaypointSkill(unit, wp.Id, SkillCatalog.LinearStrikeId, HexDirection.South).Success); // re-aimed
        Assert.Equal(SkillActionStatus.Cancelled, action.Status);
        Assert.True(_sim.RemoveWaypointSkill(unit, wp.Id).Success);
        Assert.Null(U(unit).Orders.Items[1].Skill);

        Assert.False(_sim.AssignWaypointSkill(unit, 9999, SkillCatalog.LinearStrikeId, HexDirection.North).Success);
        Assert.False(_sim.AssignWaypointSkill(unit, wp.Id, "nothing", HexDirection.North).Success);
        var bad = new IndividualOrder(unit, new[] { C(2, 5) }, HexDirection.North, null, Skill(3, HexDirection.North));
        Assert.Equal(WaypointError.InvalidSkill, _sim.ApplyOrder(bad).Error);
        Assert.Equal(2, U(unit).Orders.Count); // the old route is kept
    }

    [Fact]
    public void Command_order_waypoints_carry_no_skills()
    {
        var captain = Spawn(2, 5);
        var mate = Spawn(2, 6);
        Assert.True(_sim.ApplyGroupOrder(new GroupOrder(captain, new[] { captain, mate }, new[] { C(6, 5) }, HexDirection.North)).Success);
        foreach (var id in new[] { captain, mate })
        {
            var wp = U(id).Orders.Items[^1];
            Assert.False(_sim.AssignWaypointSkill(id, wp.Id, SkillCatalog.LinearStrikeId, HexDirection.North).Success);
            Assert.Null(U(id).Orders.Items[^1].Skill);
        }
    }

    [Fact]
    public void An_attack_order_after_a_skill_waypoint_keeps_the_skill()
    {
        var unit = Spawn(1, 5);
        var enemy = Spawn(6, 5, Side.Enemy);
        var order = new IndividualOrder(unit, new[] { C(3, 5) }, HexDirection.North, enemy, Skill(0, HexDirection.NorthEast));
        Assert.True(_sim.ApplyOrder(order).Success);
        var events = new List<SimulationEvent>();
        for (var i = 0; i < 400 && U(unit).Combat.Status != HexTactics.Core.Combat.AttackStatus.Attacking; i++)
        {
            _sim.Step();
            events.AddRange(_sim.DrainEvents());
        }

        Assert.Single(events.OfType<SkillActivated>());
        Assert.Equal(HexTactics.Core.Combat.AttackStatus.Attacking, U(unit).Combat.Status);
    }

    [Fact]
    public void A_unit_killed_before_its_skill_waypoint_never_strikes()
    {
        var unit = Spawn(1, 5);
        var enemy = Spawn(4, 4, Side.Enemy);
        Assert.True(_sim.ApplyOrder(new IndividualOrder(unit, new[] { C(4, 5) }, HexDirection.North, null, Skill(0, HexDirection.North))).Success);
        _sim.Step();
        _sim.State.ApplyDamage(unit, 100);
        var events = new List<SimulationEvent>();
        for (var i = 0; i < 150; i++)
        {
            _sim.Step();
            events.AddRange(_sim.DrainEvents());
        }

        Assert.DoesNotContain(events, e => e is SkillActivated);
        Assert.Equal(100, U(enemy).CurrentHealth);
    }
}
