using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Hex;
using HexTactics.Core.Orders;
using HexTactics.Core.Pathfinding;
using HexTactics.Core.Roster;
using HexTactics.Core.Spawning;
using HexTactics.Core.Units;
using HexTactics.Simulation;
using HexTactics.Tests.Shared;
using Xunit;

namespace HexTactics.Simulation.Tests;

/// <summary>Command orders: formation planning around КТД and their application (spec part 2 §6, acceptance 11–14).</summary>
public class GroupOrderTests
{
    // Prototype map (flat-top odd-q): wall in column 5 rows 2–7, a closed pocket (8,3), rocks (8,7) (9,7).
    private readonly GameSimulation _sim = new(TestMaps.LoadPrototype(), roster: RosterState.Prototype());

    private static HexCoord C(int col, int row) => HexCoord.FromOffset(col, row);

    private UnitId Spawn(int col, int row, HexDirection facing = HexDirection.North, Side side = Side.Player) =>
        _sim.Spawn(SpawnRequest.AtCell(C(col, row), side, facing)).Unit!.Value;

    private UnitState U(UnitId id) => _sim.State.GetUnit(id);

    private GroupStagePlan Evaluate(IReadOnlyList<UnitId> members, HexCoord target) =>
        new GroupStageEvaluator(_sim.State, members, members.Select(m => U(m).Cell).ToArray(), 0).Evaluate(target);

    private void RunUntilIdle(IEnumerable<UnitId> units, double maxSeconds = 30)
    {
        var list = units.ToList();
        for (var t = 0.0; t < maxSeconds; t += 1.0 / 60)
        {
            if (list.All(id => U(id).Orders.IsEmpty && !U(id).Motion.IsStepping && U(id).Orders.FinalFacing is null))
            {
                return;
            }

            _sim.Advance(1.0 / 60);
        }
    }

    [Fact]
    public void Captain_takes_the_KTD_and_companions_distinct_neighbouring_hexes()
    {
        var a = Spawn(2, 6);
        var b = Spawn(3, 6);
        var c = Spawn(1, 7);
        var plan = Evaluate(new[] { a, b, c }, C(3, 2));

        Assert.True(plan.IsValid);
        Assert.Equal(C(3, 2), plan.DestinationOf(a));
        var slots = new[] { plan.DestinationOf(b)!.Value, plan.DestinationOf(c)!.Value };
        Assert.Equal(2, slots.Distinct().Count());
        Assert.All(slots, s => Assert.Equal(1, s.DistanceTo(C(3, 2))));
        Assert.DoesNotContain(C(3, 2), slots);
    }

    [Fact]
    public void Assignment_minimises_the_total_path_length_and_is_deterministic()
    {
        var members = new[] { Spawn(1, 8), Spawn(4, 8), Spawn(0, 2), Spawn(4, 1) };
        var target = C(2, 4);
        var plan = Evaluate(members, target);
        var again = Evaluate(members, target);

        Assert.True(plan.IsValid);
        Assert.Equal(plan.Assignments, again.Assignments);

        // Brute force over all slot permutations gives the same minimal cost.
        var obstacles = GroupRoutePlanner.ObstaclesFor(_sim.State, members.ToHashSet());
        var fields = members.Select(m => Pathfinder.DistanceField(_sim.State.Grid, U(m).Cell, obstacles)).ToArray();
        var slots = HexDirections.All.Select(d => target.Neighbor(d)).Where(h => _sim.State.Grid.Contains(h) && _sim.State.Grid[h].IsWalkable).ToList();
        var best = int.MaxValue;
        foreach (var perm in Permutations(slots, members.Length - 1))
        {
            var cost = 0;
            var ok = true;
            for (var k = 0; k < perm.Count; k++)
            {
                ok &= fields[k + 1].TryGetValue(perm[k], out var d);
                cost += d;
            }

            if (ok)
            {
                best = Math.Min(best, cost);
            }
        }

        Assert.Equal(fields[0][target] + best, plan.TotalCost);
    }

    [Fact]
    public void Kтд_in_an_isolated_pocket_is_unreachable_for_the_captain()
    {
        var plan = Evaluate(new[] { Spawn(6, 3), Spawn(6, 4) }, C(8, 3));
        Assert.Equal(GroupPlanError.CaptainUnreachable, plan.Error);
    }

    [Fact]
    public void A_corner_hex_fits_two_companions_but_not_three()
    {
        var a = Spawn(3, 3);
        var b = Spawn(3, 4);
        var c = Spawn(2, 3);
        var d = Spawn(4, 3);
        Assert.True(Evaluate(new[] { a, b, c }, C(0, 0)).IsValid);
        Assert.Equal(GroupPlanError.NotEnoughSlots, Evaluate(new[] { a, b, c, d }, C(0, 0)).Error);
    }

    [Fact]
    public void Units_outside_the_team_block_the_KTD_and_its_slots_members_do_not()
    {
        var a = Spawn(3, 3);
        var b = Spawn(1, 1); // member standing on a slot of (0,0) keeps it
        var stranger = Spawn(0, 1);
        Assert.Equal(GroupPlanError.Occupied, Evaluate(new[] { a, b }, C(0, 1)).Error);

        var plan = Evaluate(new[] { a, b }, C(0, 0));
        Assert.True(plan.IsValid);
        Assert.Equal(C(1, 0), plan.DestinationOf(b)); // (0,1) is taken by the stranger, (1,0) is the only slot left
        _ = stranger;
    }

    [Fact]
    public void Member_list_is_validated()
    {
        var a = Spawn(2, 2);
        var enemy = Spawn(3, 3, side: Side.Enemy);
        Assert.Equal(GroupPlanError.DuplicateMember, GroupRoutePlanner.ValidateMembers(_sim.State, new[] { a, a }));
        Assert.Equal(GroupPlanError.NotAlly, GroupRoutePlanner.ValidateMembers(_sim.State, new[] { a, enemy }));
        Assert.Equal(GroupPlanError.EmptyGroup, GroupRoutePlanner.ValidateMembers(_sim.State, Array.Empty<UnitId>()));
        var many = Enumerable.Range(0, 8).Select(i => Spawn(i % 4, 8 + (i / 4))).ToArray();
        Assert.Equal(GroupPlanError.TooManyMembers, GroupRoutePlanner.ValidateMembers(_sim.State, many));
    }

    [Fact]
    public void Applied_command_order_fills_every_queue_and_the_team_ends_in_formation()
    {
        var a = Spawn(2, 6);
        var b = Spawn(3, 6);
        var c = Spawn(1, 7);
        var members = new[] { a, b, c };
        var waypoints = new[] { C(3, 2), C(6, 0) };

        var result = _sim.ApplyGroupOrder(new GroupOrder(a, members, waypoints, HexDirection.SouthEast));

        Assert.True(result.Success);
        var group = _sim.State.FindGroup(result.GroupId!.Value)!;
        Assert.Equal(a, group.Captain);
        Assert.Equal(members, group.Members);
        Assert.Equal(new[] { C(3, 2), C(6, 0) }, U(a).Orders.Items.Select(w => w.Cell));
        foreach (var m in members)
        {
            Assert.Equal(2, U(m).Orders.Count);
            Assert.Equal(group.Id, U(m).Orders.GroupId);
            Assert.Equal(HexDirection.SouthEast, U(m).Orders.FinalFacing);
        }

        Assert.Contains(_sim.DrainEvents(), e => e is GroupOrderApplied g && g.GroupId == group.Id);

        RunUntilIdle(members);
        Assert.Equal(C(6, 0), U(a).Cell);
        Assert.All(new[] { b, c }, m => Assert.Equal(1, U(m).Cell.DistanceTo(C(6, 0))));
        Assert.NotEqual(U(b).Cell, U(c).Cell);
        Assert.All(members, m => Assert.Equal(HexDirection.SouthEast, U(m).Facing));
        Assert.All(members, m => Assert.Null(U(m).Orders.GroupId));
        Assert.Empty(_sim.State.Groups);
        Assert.Contains(_sim.DrainEvents(), e => e is GroupOrderFinished);
    }

    [Fact]
    public void Impossible_KTD_rejects_the_whole_command_order_and_keeps_old_routes()
    {
        var a = Spawn(2, 6);
        var b = Spawn(3, 6);
        Assert.True(_sim.ApplyOrder(new IndividualOrder(b, new[] { C(3, 8) }, HexDirection.North)).Success);

        var result = _sim.ApplyGroupOrder(new GroupOrder(a, new[] { a, b }, new[] { C(3, 2), C(8, 3) }, HexDirection.North));

        Assert.False(result.Success);
        Assert.Equal(1, result.FailedIndex);
        Assert.Equal(GroupPlanError.CaptainUnreachable, result.Error);
        Assert.True(U(a).Orders.IsEmpty);
        Assert.Equal(C(3, 8), U(b).Orders.Items.Single().Cell);
        Assert.Empty(_sim.State.Groups);
        Assert.Contains(_sim.DrainEvents(), e => e is GroupOrderRejected);
    }

    [Fact]
    public void Captain_must_be_the_first_member()
    {
        var a = Spawn(2, 6);
        var b = Spawn(3, 6);
        var result = _sim.ApplyGroupOrder(new GroupOrder(a, new[] { b, a }, new[] { C(3, 2) }, HexDirection.North));
        Assert.Equal(GroupPlanError.CaptainNotFirst, result.Error);
    }

    [Fact]
    public void A_personal_order_takes_a_member_out_of_the_command_order()
    {
        var a = Spawn(2, 6);
        var b = Spawn(3, 6);
        var id = _sim.ApplyGroupOrder(new GroupOrder(a, new[] { a, b }, new[] { C(3, 2) }, HexDirection.North)).GroupId!.Value;

        _sim.ApplyOrder(new IndividualOrder(b, new[] { C(2, 8) }, HexDirection.South));
        Assert.Null(U(b).Orders.GroupId);
        Assert.NotNull(_sim.State.FindGroup(id));

        _sim.ApplyOrder(new IndividualOrder(a, Array.Empty<HexCoord>(), HexDirection.South));
        Assert.Null(_sim.State.FindGroup(id));
    }

    [Fact]
    public void Members_placed_from_the_roster_become_active_with_the_command_order()
    {
        var a = Spawn(3, 4);
        var pending = _sim.PlaceFromRoster(1, C(1, 4)).Unit!.Value;
        Assert.False(U(pending).IsActive);

        var result = _sim.ApplyGroupOrder(new GroupOrder(a, new[] { a, pending }, new[] { C(3, 2) }, HexDirection.NorthEast));

        Assert.True(result.Success);
        Assert.True(U(pending).IsActive);
        Assert.NotNull(U(pending).Facing);
        RunUntilIdle(new[] { a, pending });
        Assert.Equal(1, U(pending).Cell.DistanceTo(C(3, 2)));
        Assert.Equal(HexDirection.NorthEast, U(pending).Facing);
    }

    [Fact]
    public void Five_member_team_reaches_its_formation_without_blocking_forever()
    {
        var members = new[] { Spawn(1, 8), Spawn(2, 8), Spawn(0, 8), Spawn(1, 9), Spawn(2, 9) };
        var result = _sim.ApplyGroupOrder(new GroupOrder(members[0], members, new[] { C(3, 3), C(7, 1) }, HexDirection.SouthEast));

        Assert.True(result.Success);
        RunUntilIdle(members, 60);
        Assert.Equal(C(7, 1), U(members[0]).Cell);
        Assert.All(members.Skip(1), m => Assert.Equal(1, U(m).Cell.DistanceTo(C(7, 1))));
        Assert.Equal(members.Length, members.Select(m => U(m).Cell).Distinct().Count());
        Assert.All(members, m => Assert.True(U(m).Orders.IsEmpty));
    }

    [Fact]
    public void Teammates_passing_through_a_slot_make_the_member_wait_quietly_not_block()
    {
        var a = Spawn(2, 6);
        var b = Spawn(3, 6);
        var c = Spawn(1, 7);
        var members = new[] { a, b, c };
        _sim.DrainEvents();
        Assert.True(_sim.ApplyGroupOrder(new GroupOrder(a, members, new[] { C(3, 2), C(7, 1) }, HexDirection.SouthEast)).Success);

        var events = new List<SimulationEvent>();
        for (var t = 0.0; t < 30; t += 1.0 / 60)
        {
            _sim.Advance(1.0 / 60);
            events.AddRange(_sim.DrainEvents());
        }

        Assert.DoesNotContain(events, e => e is UnitBlocked);
        Assert.Equal(C(7, 1), U(a).Cell);
        Assert.All(new[] { b, c }, m => Assert.Equal(1, U(m).Cell.DistanceTo(C(7, 1))));
        Assert.All(members, m => Assert.Equal(MovementStatus.Idle, U(m).Motion.Status));
    }

    private static IEnumerable<IReadOnlyList<T>> Permutations<T>(IReadOnlyList<T> items, int length)
    {
        if (length == 0)
        {
            yield return Array.Empty<T>();
            yield break;
        }

        for (var i = 0; i < items.Count; i++)
        {
            var rest = items.Where((_, j) => j != i).ToList();
            foreach (var tail in Permutations(rest, length - 1))
            {
                yield return new[] { items[i] }.Concat(tail).ToList();
            }
        }
    }
}
