using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Hex;
using HexTactics.Core.Map;
using HexTactics.Core.Orders;
using HexTactics.Core.Spawning;
using HexTactics.Core.Units;
using HexTactics.Simulation;
using HexTactics.Tests.Shared;
using Xunit;

namespace HexTactics.Simulation.Tests;

/// <summary>
/// v0.10.0: synchronised КТД stages, companion facings, collision handling inside a team and five units at once
/// (spec part 2 §6.2, §6.3, §10, acceptance 12, 13, 19).
/// </summary>
public class GroupSyncTests
{
    /// <summary>Corridor (row 1) with a dead-end niche at (3,2) that touches only (3,1).</summary>
    private const string CorridorWithNiche = """
        @layout flat-top odd-q
        @name Corridor
        @size 7 3
        # # # # # # #
        . . . . . . .
        # # # . # # #
        """;

    private static HexCoord C(int col, int row) => HexCoord.FromOffset(col, row);

    private static UnitId Spawn(GameSimulation sim, int col, int row, HexDirection facing = HexDirection.North) =>
        sim.Spawn(SpawnRequest.AtCell(C(col, row), Side.Player, facing)).Unit!.Value;

    private static void Run(GameSimulation sim, double seconds, Action? eachTick = null)
    {
        for (var t = 0.0; t < seconds; t += 1.0 / 30)
        {
            sim.Step();
            eachTick?.Invoke();
        }
    }

    private static bool AllIdle(GameSimulation sim, IEnumerable<UnitId> units) =>
        units.All(id => sim.State.GetUnit(id) is { } u && u.Orders.IsEmpty && !u.Motion.IsStepping && u.Orders.FinalFacing is null);

    private static void RunUntilIdle(GameSimulation sim, IReadOnlyCollection<UnitId> units, double maxSeconds, Action? eachTick = null)
    {
        for (var t = 0.0; t < maxSeconds && !AllIdle(sim, units); t += 1.0 / 30)
        {
            sim.Step();
            eachTick?.Invoke();
        }
    }

    // ------------------------------------------------------------------ companion facings (§6.3)

    [Fact]
    public void P13_companions_look_the_captains_way_or_60_degrees_aside_never_behind()
    {
        var sim = new GameSimulation(TestMaps.LoadPrototype());
        var members = new[] { Spawn(sim, 2, 8), Spawn(sim, 1, 8), Spawn(sim, 3, 8), Spawn(sim, 0, 8), Spawn(sim, 1, 9), Spawn(sim, 2, 9), Spawn(sim, 3, 9) };
        var result = sim.ApplyGroupOrder(new GroupOrder(members[0], members, new[] { C(3, 4) }, HexDirection.North));

        Assert.True(result.Success);
        var group = sim.State.FindGroup(result.GroupId!.Value)!;
        Assert.Equal(HexDirection.North, group.FinalFacings[members[0]]);
        var allowed = new[] { HexDirection.NorthWest, HexDirection.North, HexDirection.NorthEast };
        Assert.All(members.Skip(1), m => Assert.Contains(group.FinalFacings[m], allowed));
        Assert.True(members.Skip(1).Select(m => group.FinalFacings[m]).Distinct().Count() > 1, "facings should vary");

        RunUntilIdle(sim, members, 30);
        Assert.All(members, m => Assert.Equal(group.FinalFacings[m], sim.State.GetUnit(m).Facing));
    }

    [Fact]
    public void Companion_facings_are_fixed_at_confirmation_previewed_exactly_and_reproducible()
    {
        IReadOnlyDictionary<UnitId, HexDirection> Play(out IReadOnlyDictionary<UnitId, HexDirection> preview)
        {
            var sim = new GameSimulation(TestMaps.LoadPrototype());
            var members = new[] { Spawn(sim, 2, 6), Spawn(sim, 3, 6), Spawn(sim, 1, 7), Spawn(sim, 3, 7) };
            preview = sim.PreviewCompanionFacings(members, HexDirection.SouthEast);
            Assert.Equal(preview, sim.PreviewCompanionFacings(members, HexDirection.SouthEast)); // no re-roll on hover
            var id = sim.ApplyGroupOrder(new GroupOrder(members[0], members, new[] { C(3, 2) }, HexDirection.SouthEast)).GroupId!.Value;
            var stored = sim.State.FindGroup(id)!.FinalFacings.ToDictionary(p => p.Key, p => p.Value);
            Run(sim, 2);
            Assert.Equal(stored, sim.State.FindGroup(id)?.FinalFacings.ToDictionary(p => p.Key, p => p.Value) ?? stored);
            return stored;
        }

        var first = Play(out var preview1);
        var second = Play(out _);
        Assert.Equal(preview1, first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Facing_rule_depends_only_on_seed_order_and_unit()
    {
        var a = CompanionFacingRule.Offset(7, 3, new UnitId(5));
        Assert.Equal(a, CompanionFacingRule.Offset(7, 3, new UnitId(5)));
        var offsets = Enumerable.Range(1, 60).Select(i => CompanionFacingRule.Offset(20261106, 1 + (i / 7), new UnitId(i))).ToList();
        Assert.All(offsets, o => Assert.InRange(o, -1, 1));
        Assert.Equal(3, offsets.Distinct().Count());
    }

    // ------------------------------------------------------------------ synchronised stages (§6.2)

    [Fact]
    public void P12_team_visits_the_KTD_in_order_nobody_heads_for_the_next_one_before_all_arrived()
    {
        var sim = new GameSimulation(TestMaps.LoadPrototype());
        var members = new[] { Spawn(sim, 2, 6), Spawn(sim, 0, 8), Spawn(sim, 4, 8) }; // the companions start far apart
        var waypoints = new[] { C(3, 3), C(6, 0), C(10, 2) };
        var id = sim.ApplyGroupOrder(new GroupOrder(members[0], members, waypoints, HexDirection.South)).GroupId!.Value;
        var group = sim.State.FindGroup(id)!;
        var sawStageWait = false;

        RunUntilIdle(sim, members, 60, () =>
        {
            if (sim.State.FindGroup(id) is null)
            {
                return;
            }

            var progress = members.Select(m => group.Waypoints.Count - sim.State.GetUnit(m).Orders.OrderCount).ToArray();
            Assert.True(progress.Max() - progress.Min() <= 1, $"stages drifted apart: {string.Join(',', progress)}");
            foreach (var m in members)
            {
                var u = sim.State.GetUnit(m);
                sawStageWait |= u.Motion.WaitReason == WaitReason.TeamStage;
                if (u.Motion.IsStepping && u.Orders.Peek() is { IsDetour: false })
                {
                    Assert.Equal(progress.Min(), group.Waypoints.Count - u.Orders.OrderCount);
                }
            }
        });

        Assert.True(sawStageWait, "someone must have waited for the team at a КТД");
        Assert.Equal(C(10, 2), sim.State.GetUnit(members[0]).Cell);
        Assert.All(members.Skip(1), m => Assert.Equal(1, sim.State.GetUnit(m).Cell.DistanceTo(C(10, 2))));
        Assert.Empty(sim.State.Groups);
    }

    [Fact]
    public void A_member_leaving_the_command_order_does_not_hold_the_team_back()
    {
        var sim = new GameSimulation(TestMaps.LoadPrototype());
        var members = new[] { Spawn(sim, 2, 6), Spawn(sim, 3, 6), Spawn(sim, 1, 7) };
        var id = sim.ApplyGroupOrder(new GroupOrder(members[0], members, new[] { C(3, 3), C(6, 0) }, HexDirection.South)).GroupId!.Value;

        // RMB on a confirmed waypoint of one member: its route becomes personal.
        var straggler = sim.State.GetUnit(members[2]);
        Assert.True(sim.TruncateOrders(members[2], straggler.Orders.Items[0].Id).Success);
        Assert.Null(straggler.Orders.GroupId);
        Assert.NotNull(sim.State.FindGroup(id));

        RunUntilIdle(sim, members.Take(2).ToArray(), 40);
        Assert.Equal(C(6, 0), sim.State.GetUnit(members[0]).Cell);
    }

    // ------------------------------------------------------------------ no mutual blocking (§6.2)

    [Fact]
    public void Formation_search_returns_the_lexicographic_optimum_of_conflicts_then_length()
    {
        var sim = new GameSimulation(TestMaps.LoadPrototype());
        var members = new[] { Spawn(sim, 1, 8), Spawn(sim, 4, 8), Spawn(sim, 0, 2), Spawn(sim, 4, 1) };
        var evaluator = new GroupStageEvaluator(sim.State, members, members.Select(m => sim.State.GetUnit(m).Cell).ToArray(), 0);
        foreach (var target in new[] { C(2, 4), C(6, 1), C(4, 0), C(3, 8) })
        {
            var plan = evaluator.Evaluate(target);
            Assert.True(plan.IsValid);
            var slots = evaluator.CandidateSlots(target);
            var best = Permutations(slots, members.Length - 1)
                .Select(p => evaluator.Score(target, p))
                .Where(s => s is not null)
                .Select(s => s!.Value)
                .OrderBy(s => s.Conflicts).ThenBy(s => s.Length)
                .First();
            var chosen = evaluator.Score(target, members.Skip(1).Select(m => plan.DestinationOf(m)!.Value).ToArray())!.Value;
            Assert.Equal(best, chosen);
            Assert.Equal(best.Conflicts, plan.Conflicts);
        }
    }

    [Fact]
    public void Companion_on_the_next_KTD_never_takes_the_captains_hex_no_swap_deadlock()
    {
        var sim = new GameSimulation(TestMaps.LoadPrototype());
        var members = new[] { Spawn(sim, 2, 6), Spawn(sim, 3, 6), Spawn(sim, 1, 7) };
        var anchors = members.Select(m => sim.State.GetUnit(m).Cell).ToArray();
        var stage1 = new GroupStageEvaluator(sim.State, members, anchors, 0).Evaluate(C(3, 3));
        var companion = members[1];
        var ktd2 = stage1.DestinationOf(companion)!.Value; // КТД2 on the companion's first place

        var chain = GroupRoutePlanner.PlanChain(sim.State, members, anchors, new[] { C(3, 3), ktd2 });
        Assert.True(chain.IsValid);
        Assert.NotEqual(C(3, 3), chain.Stages[1].DestinationOf(companion));

        var events = new List<SimulationEvent>();
        Assert.True(sim.ApplyGroupOrder(new GroupOrder(members[0], members, new[] { C(3, 3), ktd2 }, HexDirection.North)).Success);
        RunUntilIdle(sim, members, 30, () => events.AddRange(sim.DrainEvents()));
        Assert.True(AllIdle(sim, members));
        Assert.Equal(ktd2, sim.State.GetUnit(members[0]).Cell);
        Assert.DoesNotContain(events, e => e is UnitBlocked);
    }

    [Fact]
    public void Corridor_formation_keeps_places_off_the_captains_way()
    {
        var sim = new GameSimulation(MapParser.Parse(CorridorWithNiche));
        var captain = Spawn(sim, 2, 1);
        var companion = Spawn(sim, 1, 1);
        var plan = new GroupStageEvaluator(sim.State, new[] { captain, companion }, new[] { C(2, 1), C(1, 1) }, 0).Evaluate(C(5, 1));

        // Places around (5,1) in the corridor: (4,1) on the captain's way, (6,1) behind the КТД (way through it).
        Assert.True(plan.IsValid);
        Assert.Equal(1, plan.Conflicts);
        var events = new List<SimulationEvent>();
        Assert.True(sim.ApplyGroupOrder(new GroupOrder(captain, new[] { captain, companion }, new[] { C(5, 1) }, HexDirection.North)).Success);
        RunUntilIdle(sim, new[] { captain, companion }, 20, () => events.AddRange(sim.DrainEvents()));
        Assert.Equal(C(5, 1), sim.State.GetUnit(captain).Cell);
        Assert.Equal(1, sim.State.GetUnit(companion).Cell.DistanceTo(C(5, 1)));
        Assert.DoesNotContain(events, e => e is UnitBlocked);
    }

    [Fact]
    public void A_teammate_standing_in_the_way_steps_aside_and_returns_to_its_place()
    {
        var sim = new GameSimulation(MapParser.Parse(CorridorWithNiche));
        var runner = Spawn(sim, 0, 1, HexDirection.SouthEast);
        var sitter = Spawn(sim, 3, 1, HexDirection.South);

        // A command order in which the sitter has already finished on (3,1) and the runner must pass it to (6,1).
        var state = sim.State;
        var members = new[] { runner, sitter };
        var group = state.AddGroup(
            runner,
            members,
            new[] { C(6, 1) },
            new IReadOnlyList<GroupSlotAssignment>[] { new[] { new GroupSlotAssignment(0, runner, C(6, 1)), new GroupSlotAssignment(0, sitter, C(3, 1)) } },
            new Dictionary<UnitId, HexDirection> { [runner] = HexDirection.North, [sitter] = HexDirection.South });
        state.AppendWaypoint(runner, C(6, 1));
        state.SetFinalFacing(runner, HexDirection.North);
        state.SetQueueGroup(runner, group.Id);
        state.SetQueueGroup(sitter, group.Id);

        var events = new List<SimulationEvent>();
        var sawTeammateWait = false;
        var sawHoldAside = false;
        RunUntilIdle(sim, members, 20, () =>
        {
            events.AddRange(sim.DrainEvents());
            sawTeammateWait |= state.GetUnit(runner).Motion.WaitReason == WaitReason.Teammate;
            sawHoldAside |= state.GetUnit(sitter).Motion.WaitReason == WaitReason.Yielding;
        });

        Assert.True(sawTeammateWait);
        Assert.True(sawHoldAside); // it waited aside until the runner had passed
        var yielded = Assert.Single(events.OfType<TeammateYielded>());
        Assert.Equal(sitter, yielded.Unit);
        Assert.Equal(C(3, 2), yielded.Aside);
        Assert.Equal(C(6, 1), state.GetUnit(runner).Cell);
        Assert.Equal(C(3, 1), state.GetUnit(sitter).Cell); // back on its place
        Assert.Equal(HexDirection.South, state.GetUnit(sitter).Facing);
        Assert.DoesNotContain(events, e => e is UnitBlocked);
        Assert.Empty(state.Groups);
    }

    [Fact]
    public void When_nobody_can_step_aside_the_unit_reports_a_blocked_route_instead_of_waiting_forever()
    {
        var sim = new GameSimulation(MapParser.Parse(CorridorWithNiche));
        var runner = Spawn(sim, 0, 1);
        var sitter = Spawn(sim, 4, 1); // (4,1) has no free neighbour off the runner's way
        var state = sim.State;
        var members = new[] { runner, sitter };
        var group = state.AddGroup(
            runner,
            members,
            new[] { C(6, 1) },
            new IReadOnlyList<GroupSlotAssignment>[] { new[] { new GroupSlotAssignment(0, runner, C(6, 1)), new GroupSlotAssignment(0, sitter, C(4, 1)) } },
            new Dictionary<UnitId, HexDirection> { [runner] = HexDirection.North, [sitter] = HexDirection.North });
        state.AppendWaypoint(runner, C(6, 1));
        state.SetFinalFacing(runner, HexDirection.North);
        state.SetQueueGroup(runner, group.Id);
        state.SetQueueGroup(sitter, group.Id);

        var events = new List<SimulationEvent>();
        Run(sim, 10, () => events.AddRange(sim.DrainEvents()));

        var blocked = Assert.Single(events.OfType<UnitBlocked>());
        Assert.Equal(BlockReason.BlockedByTeammates, blocked.Reason);
        Assert.Equal(MovementStatus.Blocked, state.GetUnit(runner).Motion.Status);
    }

    // ------------------------------------------------------------------ five units at once (§12.19)

    [Fact]
    public void P19_five_units_move_at_once_without_sharing_a_hex_and_all_arrive()
    {
        var sim = new GameSimulation(TestMaps.LoadPrototype());
        var starts = new[] { (0, 0), (0, 9), (11, 0), (11, 9), (6, 9) };
        var targets = new[] { (11, 8), (11, 1), (0, 8), (0, 1), (6, 1) }; // crossing routes around the wall
        var units = starts.Select(s => Spawn(sim, s.Item1, s.Item2)).ToArray();
        for (var i = 0; i < units.Length; i++)
        {
            Assert.True(sim.ApplyOrder(new IndividualOrder(units[i], new[] { C(targets[i].Item1, targets[i].Item2) }, HexDirection.South)).Success);
        }

        RunUntilIdle(sim, units, 60, () =>
        {
            var cells = units.Select(u => sim.State.GetUnit(u).Cell).ToArray();
            Assert.Equal(cells.Length, cells.Distinct().Count());
            foreach (var u in units)
            {
                Assert.Equal(u, sim.State.UnitAt(sim.State.GetUnit(u).Cell)!.Id);
            }
        });

        for (var i = 0; i < units.Length; i++)
        {
            Assert.Equal(C(targets[i].Item1, targets[i].Item2), sim.State.GetUnit(units[i]).Cell);
        }
    }

    [Fact]
    public void Five_member_team_with_a_KTD_chain_and_a_personal_unit_crossing_all_finish()
    {
        var sim = new GameSimulation(TestMaps.LoadPrototype());
        var team = new[] { Spawn(sim, 1, 8), Spawn(sim, 2, 8), Spawn(sim, 0, 8), Spawn(sim, 1, 9), Spawn(sim, 2, 9) };
        var crosser = Spawn(sim, 11, 1);
        Assert.True(sim.ApplyGroupOrder(new GroupOrder(team[0], team, new[] { C(3, 3), C(6, 1), C(10, 3) }, HexDirection.South)).Success);
        Assert.True(sim.ApplyOrder(new IndividualOrder(crosser, new[] { C(0, 0) }, HexDirection.SouthWest)).Success);

        var all = team.Append(crosser).ToArray();
        RunUntilIdle(sim, all, 90, () =>
        {
            var cells = all.Select(u => sim.State.GetUnit(u).Cell).ToArray();
            Assert.Equal(cells.Length, cells.Distinct().Count());
        });

        Assert.True(AllIdle(sim, all));
        Assert.Equal(C(10, 3), sim.State.GetUnit(team[0]).Cell);
        Assert.All(team.Skip(1), m => Assert.Equal(1, sim.State.GetUnit(m).Cell.DistanceTo(C(10, 3))));
        Assert.Equal(C(0, 0), sim.State.GetUnit(crosser).Cell);
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
