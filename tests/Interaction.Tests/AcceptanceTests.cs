using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Hex;
using HexTactics.Core.Map;
using HexTactics.Core.Orders;
using HexTactics.Core.Pathfinding;
using HexTactics.Core.Spawning;
using HexTactics.Core.Units;
using HexTactics.Interaction;
using HexTactics.Simulation;
using HexTactics.Tests.Shared;
using Xunit;

namespace HexTactics.Interaction.Tests;

/// <summary>
/// Regression suite for the acceptance lists: part 1 §8 (A01–A12) and part 2 §12 (P01…P20, the items that belong to
/// the current version), on the flat-top prototype map, driven through the same path as the mouse:
/// pointer samples → state machine → simulation. Purely visual items are covered by the screenshot scenarios.
/// Part 2 overrides part 1 where they conflict (continuous drafts, mandatory final facing).
/// </summary>
public class AcceptanceTests
{
    private readonly HexLayout _layout = new(1.0);
    private readonly MapDefinition _map = TestMaps.LoadPrototype();
    private readonly GameSimulation _sim;
    private readonly InputStateMachine _fsm;
    private readonly List<InteractionFeedback> _feedback = new();

    public AcceptanceTests()
    {
        _sim = new GameSimulation(_map);
        _fsm = new InputStateMachine(_sim, _layout);
        _fsm.Feedback += _feedback.Add;
    }

    private static HexCoord C(int col, int row) => HexCoord.FromOffset(col, row);

    private PointerSample At(PlaneVector p, int? wp = null) =>
        new(new ScreenPoint(p.X * 100, p.Z * 100), p, _layout.PlaneToHex(p), _sim.State.UnitAt(_layout.PlaneToHex(p))?.Id, wp);

    private PointerSample AtHex(HexCoord h, int? wp = null) => At(_layout.HexToPlane(h), wp);

    private PointerSample Sector(HexCoord h, HexDirection d) => At(_layout.HexToPlane(h) + (HexLayout.DirectionVector(d) * 0.6));

    private void Click(PointerSample s, PointerButton b = PointerButton.Primary)
    {
        _fsm.PointerMove(s);
        _fsm.PointerDown(b, s);
        _fsm.PointerUp(b, s);
    }

    private void Drag(PointerSample from, PointerSample to)
    {
        _fsm.PointerMove(from);
        _fsm.PointerDown(PointerButton.Primary, from);
        _fsm.PointerMove(to);
        _fsm.PointerUp(PointerButton.Primary, to);
    }

    /// <summary>Full order through the UI: drag to the first ТД, click the others, click a facing sector of the last.</summary>
    private void Order(UnitId unit, HexDirection facing, params HexCoord[] waypoints)
    {
        var cell = _sim.State.GetUnit(unit).Cell;
        Drag(AtHex(cell), AtHex(waypoints[0]));
        foreach (var w in waypoints.Skip(1))
        {
            Click(AtHex(w));
        }

        Click(Sector(waypoints[^1], facing));
    }

    private UnitId Spawn(HexCoord cell, HexDirection facing = HexDirection.North) => _sim.Spawn(SpawnRequest.AtCell(cell, facing: facing)).Unit!.Value;

    private List<SimulationEvent> RunUntilIdle(UnitId id, int maxSeconds = 120)
    {
        var events = new List<SimulationEvent>();
        var u = _sim.State.GetUnit(id);
        for (var i = 0; i < maxSeconds * 60 && !(u.Orders.IsEmpty && !u.Motion.IsStepping && u.Orders.FinalFacing is null); i++)
        {
            _sim.Advance(1.0 / 60);
            events.AddRange(_sim.DrainEvents());
        }

        return events;
    }

    // ------------------------------------------------------------------ part 1 §8

    [Fact]
    public void A01_map_has_exactly_120_regular_flat_top_hexes_12_by_10()
    {
        Assert.Equal((12, 10, 120), (_map.Grid.Columns, _map.Grid.Rows, _map.Grid.CellCount));
        foreach (var c in _map.Grid.Coords)
        {
            foreach (var n in _map.Grid.NeighborsInBounds(c))
            {
                Assert.Equal(_layout.NeighborDistance, _layout.HexToPlane(c).DistanceTo(_layout.HexToPlane(n)), 9);
            }
        }
    }

    [Fact]
    public void A02_edge_cells_are_resolved_from_plane_points_near_their_corners()
    {
        foreach (var edge in new[] { C(0, 0), C(11, 0), C(0, 9), C(11, 9), C(0, 5), C(11, 4) })
        {
            for (var i = 0; i < 6; i++)
            {
                Assert.Equal(edge, _layout.PlaneToHex(PlaneVector.Lerp(_layout.HexToPlane(edge), _layout.Corner(edge, i), 0.9)));
            }
        }
    }

    [Fact]
    public void A04_three_spawn_methods_and_safe_rejection()
    {
        Assert.True(_sim.Spawn(SpawnRequest.AtBase()).Success);
        Assert.True(_sim.Spawn(SpawnRequest.AtZone(1)).Success);
        Assert.True(_sim.Spawn(SpawnRequest.AtCell(C(6, 6))).Success);
        var before = _sim.State.Units.Select(u => (u.Id, u.Cell)).ToList();
        Assert.False(_sim.Spawn(SpawnRequest.AtCell(C(6, 6))).Success);
        Assert.False(_sim.Spawn(SpawnRequest.AtCell(C(5, 4))).Success);
        Assert.False(_sim.Spawn(SpawnRequest.AtCell(C(20, 20))).Success);
        Assert.Equal(before, _sim.State.Units.Select(u => (u.Id, u.Cell)).ToList());
    }

    [Fact]
    public void A05_six_facings_and_turn_without_moving()
    {
        var id = Spawn(C(6, 6));
        Click(AtHex(C(6, 6)));
        foreach (var dir in HexDirections.All)
        {
            Click(Sector(C(6, 6), dir));
            _sim.Step();
            Assert.Equal(dir, _sim.State.GetUnit(id).Facing);
            Assert.Equal(C(6, 6), _sim.State.GetUnit(id).Cell);
        }
    }

    [Fact]
    public void A06_hold_and_release_on_reachable_hex_creates_TD1()
    {
        var id = Spawn(C(1, 1));
        _fsm.PointerDown(PointerButton.Primary, AtHex(C(1, 1)));
        _fsm.PointerMove(AtHex(C(3, 1)));
        Assert.True(_fsm.Draft!.Held);
        Assert.Equal(_layout.HexToPlane(C(1, 1)), _fsm.Draft.SourceCenter);
        _fsm.PointerUp(PointerButton.Primary, AtHex(C(3, 1)));
        Assert.Equal(new[] { C(3, 1) }, _fsm.Draft!.Waypoints);
        Assert.True(_sim.State.GetUnit(id).Orders.IsEmpty); // applied only with the final facing (part 2)
    }

    [Fact]
    public void A07_TD1_TD2_TD3_are_visited_strictly_in_order()
    {
        var id = Spawn(C(1, 8));
        Order(id, HexDirection.South, C(2, 1), C(10, 1), C(10, 8));
        var reached = RunUntilIdle(id).OfType<WaypointReached>().Select(e => (e.Waypoint.Sequence, e.Waypoint.Cell)).ToList();
        Assert.Equal(new[] { (1, C(2, 1)), (2, C(10, 1)), (3, C(10, 8)) }, reached);
    }

    [Fact]
    public void A08_obstacle_between_two_TDs_is_bypassed_by_a_shortest_path()
    {
        var id = Spawn(C(2, 6));
        Order(id, HexDirection.North, C(4, 4), C(6, 4)); // straight wall at column 5 in between
        var events = RunUntilIdle(id);
        var visited = events.OfType<UnitEnteredCell>().Select(e => e.To).ToList();
        var segment = visited.SkipWhile(c => c != C(4, 4)).Skip(1).ToList();
        Assert.Equal(Pathfinder.FindPath(_map.Grid, C(4, 4), C(6, 4))!.Count - 1, segment.Count);
        Assert.DoesNotContain(segment, c => !_map.Grid.IsWalkable(c));
        Assert.Equal(C(6, 4), _sim.State.GetUnit(id).Cell);
    }

    [Fact]
    public void A09_free_but_unreachable_hex_is_red_and_cannot_take_a_TD()
    {
        var id = Spawn(C(6, 6));
        _fsm.PointerDown(PointerButton.Primary, AtHex(C(6, 6)));
        _fsm.PointerMove(AtHex(C(8, 3)));
        Assert.True(_map.Grid.IsWalkable(C(8, 3)));
        Assert.Equal(DraftTargetStatus.Unreachable, _fsm.Draft!.Status);
        Assert.NotEqual(WaypointError.None, WaypointRules.ValidateTarget(_sim.State, id, C(6, 6), C(8, 3), _fsm.Draft.Reachable));
        _fsm.PointerUp(PointerButton.Primary, AtHex(C(8, 3)));
        Assert.True(_sim.State.GetUnit(id).Orders.IsEmpty);
    }

    [Fact]
    public void A10_equal_paths_are_repeatable_and_bad_targets_do_not_throw()
    {
        var first = Pathfinder.FindPath(_map.Grid, C(0, 9), C(11, 0));
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(first, Pathfinder.FindPath(_map.Grid, C(0, 9), C(11, 0)));
        }

        var id = Spawn(C(6, 6));
        var other = Spawn(C(7, 6));
        foreach (var target in new[] { C(-5, 3), C(30, 30), C(7, 6), C(5, 4) })
        {
            Drag(AtHex(C(6, 6)), AtHex(target));
        }

        Assert.True(_sim.State.GetUnit(id).Orders.IsEmpty);
        Assert.Equal(C(7, 6), _sim.State.GetUnit(other).Cell);
    }

    [Fact]
    public void A11_cancel_keeps_previous_TDs_and_clears_the_preview()
    {
        var id = Spawn(C(1, 1));
        Order(id, HexDirection.North, C(3, 1));
        var before = _sim.State.GetUnit(id).Orders.Items.ToList();
        Drag(AtHex(C(1, 1)), AtHex(C(1, 8)));
        Assert.NotNull(_fsm.Draft);
        _fsm.Cancel();
        Assert.Null(_fsm.Draft);
        Assert.Equal(before, _sim.State.GetUnit(id).Orders.Items);
    }

    [Fact]
    public void A12_pause_freezes_simulation_allows_order_changes_and_resumes_without_jump()
    {
        var id = Spawn(C(1, 8));
        Order(id, HexDirection.North, C(9, 8));
        for (var i = 0; i < 25; i++)
        {
            _sim.Advance(1.0 / 60);
        }

        var u = _sim.State.GetUnit(id);
        _sim.SetPaused(true);
        var frozen = (u.Cell, u.Motion.StepTo, u.Motion.Progress, _sim.Tick);
        for (var i = 0; i < 120; i++)
        {
            _sim.Advance(1.0 / 60);
        }

        Assert.Equal(frozen, (u.Cell, u.Motion.StepTo, u.Motion.Progress, _sim.Tick));
        Order(id, HexDirection.South, C(2, 1));
        Assert.Equal(C(2, 1), Assert.Single(u.Orders.Items).Cell);
        Assert.Equal(frozen, (u.Cell, u.Motion.StepTo, u.Motion.Progress, _sim.Tick));

        _sim.SetPaused(false);
        _sim.Advance(1.0 / 30);
        Assert.Equal(frozen.StepTo, u.Motion.StepTo);
        Assert.Equal(frozen.Progress + (u.SpeedHexesPerSecond / 30.0), u.Motion.Progress, 9);
        RunUntilIdle(id);
        Assert.Equal(C(2, 1), u.Cell);
        Assert.Equal(HexDirection.South, u.Facing);
    }

    // ------------------------------------------------------------------ part 2 §12

    [Fact]
    public void P01_flat_top_neighbors_click_and_facing_use_the_map_plane()
    {
        var hex = C(6, 5);
        Assert.Equal(C(6, 4), hex.Neighbor(HexDirection.North));
        Assert.Equal(6, hex.Neighbors().Distinct().Count());
        foreach (var dir in HexDirections.All)
        {
            var p = _layout.HexToPlane(hex) + (HexLayout.DirectionVector(dir) * 0.6);
            Assert.Equal(hex, _layout.PlaneToHex(p));
            Assert.Equal(dir, HexDirections.FromPlaneVector((p - _layout.HexToPlane(hex)).X, (p - _layout.HexToPlane(hex)).Z));
        }
    }

    [Fact]
    public void P04_TD2_and_TD3_are_placed_by_single_clicks_without_regrabbing_TD1()
    {
        var id = Spawn(C(2, 4));
        Drag(AtHex(C(2, 4)), AtHex(C(3, 1)));
        _fsm.PointerMove(AtHex(C(1, 8))); // live preview A → B without any button
        Assert.Equal(C(3, 1), _fsm.Draft!.Source);
        Click(AtHex(C(1, 8)));
        Click(AtHex(C(4, 8)));
        Assert.Equal(new[] { C(3, 1), C(1, 8), C(4, 8) }, _fsm.Draft!.Waypoints);
        Assert.True(_sim.State.GetUnit(id).Orders.IsEmpty);
    }

    [Fact]
    public void P05_sector_of_TD3_previews_and_confirms_the_final_facing_reached_on_arrival()
    {
        var id = Spawn(C(2, 4));
        Drag(AtHex(C(2, 4)), AtHex(C(3, 1)));
        Click(AtHex(C(1, 8)));
        Click(AtHex(C(4, 8)));
        _fsm.PointerMove(Sector(C(4, 8), HexDirection.NorthEast));
        Assert.Equal(HexDirection.NorthEast, _fsm.Draft!.SectorFacing);
        Click(Sector(C(4, 8), HexDirection.NorthEast));
        RunUntilIdle(id);
        Assert.Equal((C(4, 8), HexDirection.NorthEast), (_sim.State.GetUnit(id).Cell, _sim.State.GetUnit(id).Facing));
    }

    [Fact]
    public void P06_turn_in_place_without_TD_keeps_the_logical_hex()
    {
        var id = Spawn(C(6, 6));
        Click(AtHex(C(6, 6)));
        Click(Sector(C(6, 6), HexDirection.South));
        _sim.Step();
        Assert.Equal((C(6, 6), HexDirection.South), (_sim.State.GetUnit(id).Cell, _sim.State.GetUnit(id).Facing));
    }

    [Fact]
    public void P07_draft_without_final_facing_is_not_complete()
    {
        var id = Spawn(C(2, 4));
        Drag(AtHex(C(2, 4)), AtHex(C(3, 1)));
        Click(AtHex(C(1, 8)));
        Click(AtHex(C(1, 8))); // neutral centre of the source: no confirmation
        for (var i = 0; i < 60; i++)
        {
            _sim.Advance(1.0 / 30);
        }

        Assert.NotNull(_fsm.Draft);
        Assert.True(_sim.State.GetUnit(id).Orders.IsEmpty);
        Assert.Equal(C(2, 4), _sim.State.GetUnit(id).Cell);
    }

    [Fact]
    public void P15_right_click_removes_the_current_draft_but_not_confirmed_orders_of_other_units()
    {
        var a = Spawn(C(1, 1));
        var b = Spawn(C(1, 8));
        Order(a, HexDirection.North, C(3, 1));
        var aOrders = _sim.State.GetUnit(a).Orders.Items.ToList();
        Drag(AtHex(C(1, 8)), AtHex(C(4, 8)));
        Click(AtHex(C(3, 8)));
        Click(AtHex(C(9, 9)), PointerButton.Secondary);
        Assert.Null(_fsm.Draft);
        Assert.Equal(aOrders, _sim.State.GetUnit(a).Orders.Items);
        Assert.True(_sim.State.GetUnit(b).Orders.IsEmpty);
    }

    [Fact]
    public void P16_hovering_red_does_not_erase_the_draft_but_confirming_there_does()
    {
        Spawn(C(2, 4));
        Drag(AtHex(C(2, 4)), AtHex(C(3, 1)));
        _fsm.PointerMove(AtHex(C(5, 4)));
        _fsm.PointerMove(AtHex(C(8, 3)));
        Assert.NotNull(_fsm.Draft);
        Click(AtHex(C(5, 4)));
        Assert.Null(_fsm.Draft);
    }

    [Fact]
    public void P17_on_pause_a_moving_unit_gets_a_new_route_from_its_hex_without_teleport()
    {
        var id = Spawn(C(2, 4));
        Order(id, HexDirection.South, C(2, 8));
        _sim.Advance(0.6);
        _sim.SetPaused(true);
        var u = _sim.State.GetUnit(id);
        var stepTo = u.Motion.StepTo;
        Click(AtHex(u.Cell));
        Drag(AtHex(u.Cell), AtHex(C(0, 2)));
        Click(Sector(C(0, 2), HexDirection.North));
        Assert.Equal(new[] { C(0, 2) }, u.Orders.Items.Select(w => w.Cell));
        _sim.SetPaused(false);
        _sim.Advance(1.0 / 30);
        Assert.Equal(stepTo, u.Motion.StepTo); // the running step continues smoothly
        var reached = RunUntilIdle(id).OfType<WaypointReached>().Select(e => e.Waypoint.Cell).ToList();
        Assert.Equal(new[] { C(0, 2) }, reached);
    }

    [Fact]
    public void P18_cancelled_edit_on_pause_keeps_the_previous_route()
    {
        var id = Spawn(C(2, 4));
        Order(id, HexDirection.South, C(2, 8));
        _sim.Advance(0.6);
        _sim.SetPaused(true);
        var u = _sim.State.GetUnit(id);
        var before = u.Orders.Items.ToList();
        Drag(AtHex(u.Cell), AtHex(C(0, 2)));
        _fsm.Cancel();
        Assert.Equal(before, u.Orders.Items);
        _sim.SetPaused(false);
        Assert.Contains(C(2, 8), RunUntilIdle(id).OfType<WaypointReached>().Select(e => e.Waypoint.Cell));
    }

    [Fact]
    public void P20_click_on_enemy_routes_to_reserved_attack_target_without_fake_combat()
    {
        var id = Spawn(C(2, 4));
        var enemy = _sim.Spawn(SpawnRequest.AtCell(C(4, 4), Side.Enemy)).Unit!.Value;
        Drag(AtHex(C(2, 4)), AtHex(C(3, 1)));
        Click(AtHex(C(4, 4)));
        Assert.Contains(_feedback, f => f is AttackTargetReserved r && r.Target == enemy);
        Assert.Equal(new[] { C(3, 1) }, _fsm.Draft!.Waypoints);
        Assert.True(System.Enum.IsDefined(OrderKind.AttackTarget));
        Assert.Equal(C(4, 4), _sim.State.GetUnit(enemy).Cell);
        Assert.True(_sim.State.GetUnit(id).Orders.IsEmpty);
    }
}
