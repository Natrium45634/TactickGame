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
/// Regression suite for the stage-1 acceptance list (spec §8) on the real prototype map, driven through
/// the same path as the mouse: pointer samples → state machine → simulation. Items that are purely
/// visual (camera angle, line thickness, colours) are checked by the screenshot scenarios instead.
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

    private PointerSample At(PlaneVector p, UnitId? unit = null, int? wp = null) =>
        new(new ScreenPoint(p.X * 100, p.Z * 100), p, _layout.PlaneToHex(p), unit, wp);

    private PointerSample AtHex(HexCoord h, UnitId? unit = null, int? wp = null) => At(_layout.HexToPlane(h), unit, wp);

    private void Drag(PointerSample from, PointerSample to)
    {
        _fsm.PointerDown(PointerButton.Primary, from);
        _fsm.PointerMove(to);
        _fsm.PointerUp(PointerButton.Primary, to);
    }

    private UnitId Spawn(HexCoord cell) => _sim.Spawn(SpawnRequest.AtCell(cell)).Unit!.Value;

    private List<SimulationEvent> RunUntilIdle(UnitId id, int maxSeconds = 120)
    {
        var events = new List<SimulationEvent>();
        var u = _sim.State.GetUnit(id);
        for (var i = 0; i < maxSeconds * 60 && !(u.Orders.IsEmpty && !u.Motion.IsStepping); i++)
        {
            _sim.Advance(1.0 / 60);
            events.AddRange(_sim.DrainEvents());
        }

        return events;
    }

    [Fact]
    public void A01_map_has_exactly_120_regular_hexes_12_by_10()
    {
        Assert.Equal((12, 10, 120), (_map.Grid.Columns, _map.Grid.Rows, _map.Grid.CellCount));
        foreach (var c in _map.Grid.Coords)
        {
            foreach (var n in _map.Grid.NeighborsInBounds(c))
            {
                Assert.Equal(_layout.HexWidth, _layout.HexToPlane(c).DistanceTo(_layout.HexToPlane(n)), 9);
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
                var p = PlaneVector.Lerp(_layout.HexToPlane(edge), _layout.Corner(edge, i), 0.9);
                Assert.Equal(edge, _layout.PlaneToHex(p));
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
        Assert.False(_sim.Spawn(SpawnRequest.AtCell(C(4, 4))).Success);
        Assert.False(_sim.Spawn(SpawnRequest.AtCell(C(20, 20))).Success);
        Assert.Equal(before, _sim.State.Units.Select(u => (u.Id, u.Cell)).ToList());
    }

    [Fact]
    public void A05_six_facings_and_short_gesture_rotates_without_moving()
    {
        var id = Spawn(C(6, 6));
        foreach (var dir in HexDirections.All)
        {
            Drag(AtHex(C(6, 6), id), At(_layout.HexToPlane(C(6, 6)) + (HexLayout.DirectionVector(dir) * 0.55)));
            Assert.Equal(dir, _sim.State.GetUnit(id).Facing);
            Assert.Equal(C(6, 6), _sim.State.GetUnit(id).Cell);
        }
    }

    [Fact]
    public void A06_hold_and_release_on_reachable_hex_creates_TD1()
    {
        var id = Spawn(C(1, 1));
        _fsm.PointerDown(PointerButton.Primary, AtHex(C(1, 1), id));
        _fsm.PointerMove(AtHex(C(3, 1)));
        Assert.Equal(InputMode.DraggingFromUnit, _fsm.Mode);
        Assert.Equal(_layout.HexToPlane(C(1, 1)), _fsm.Drag!.SourceCenter);
        _fsm.PointerUp(PointerButton.Primary, AtHex(C(3, 1)));
        Assert.Equal(C(3, 1), Assert.Single(_sim.State.GetUnit(id).Orders.Items).Cell);
    }

    [Fact]
    public void A07_TD1_TD2_TD3_are_visited_strictly_in_order()
    {
        var id = Spawn(C(1, 8));
        Drag(AtHex(C(1, 8), id), AtHex(C(2, 1)));
        Drag(AtHex(C(2, 1), wp: _sim.State.GetUnit(id).Orders.Last!.Id), AtHex(C(10, 1)));
        Drag(AtHex(C(10, 1), wp: _sim.State.GetUnit(id).Orders.Last!.Id), AtHex(C(10, 8)));
        var reached = RunUntilIdle(id).OfType<WaypointReached>().Select(e => (e.Waypoint.Sequence, e.Waypoint.Cell)).ToList();
        Assert.Equal(new[] { (1, C(2, 1)), (2, C(10, 1)), (3, C(10, 8)) }, reached);
    }

    [Fact]
    public void A08_obstacle_between_two_TDs_is_bypassed_by_a_shortest_path()
    {
        var id = Spawn(C(2, 6));
        Drag(AtHex(C(2, 6), id), AtHex(C(2, 4)));
        Drag(AtHex(C(2, 4), wp: _sim.State.GetUnit(id).Orders.Last!.Id), AtHex(C(6, 4))); // wall in between
        var events = RunUntilIdle(id);
        var visited = events.OfType<UnitEnteredCell>().Select(e => e.To).ToList();
        var segment = visited.SkipWhile(c => c != C(2, 4)).Skip(1).ToList();
        Assert.Equal(Pathfinder.FindPath(_map.Grid, C(2, 4), C(6, 4))!.Count - 1, segment.Count);
        Assert.DoesNotContain(segment, c => !_map.Grid.IsWalkable(c));
        Assert.Equal(C(6, 4), _sim.State.GetUnit(id).Cell);
    }

    [Fact]
    public void A09_free_but_unreachable_hex_is_red_and_cannot_take_a_TD()
    {
        var id = Spawn(C(6, 6));
        _fsm.PointerDown(PointerButton.Primary, AtHex(C(6, 6), id));
        _fsm.PointerMove(AtHex(C(8, 3)));
        Assert.True(_map.Grid.IsWalkable(C(8, 3)));
        Assert.Equal(DragTargetStatus.Unreachable, _fsm.Drag!.Status);
        Assert.NotEqual(WaypointError.None, WaypointRules.ValidateTarget(_sim.State, id, C(6, 6), C(8, 3), _fsm.Drag.Reachable));
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
        foreach (var target in new[] { C(-5, 3), C(30, 30), C(7, 6), C(4, 4) })
        {
            Drag(AtHex(C(6, 6), id), AtHex(target));
        }

        Assert.True(_sim.State.GetUnit(id).Orders.IsEmpty);
        Assert.Equal(C(7, 6), _sim.State.GetUnit(other).Cell);
    }

    [Fact]
    public void A11_cancel_keeps_previous_TDs_and_clears_the_preview()
    {
        var id = Spawn(C(1, 1));
        Drag(AtHex(C(1, 1), id), AtHex(C(3, 1)));
        var before = _sim.State.GetUnit(id).Orders.Items.ToList();
        _fsm.PointerDown(PointerButton.Primary, AtHex(C(3, 1), wp: before[0].Id));
        _fsm.PointerMove(AtHex(C(6, 1)));
        Assert.NotNull(_fsm.Drag);
        _fsm.Cancel();
        Assert.Null(_fsm.Drag);
        Assert.Equal(before, _sim.State.GetUnit(id).Orders.Items);
    }

    [Fact]
    public void A12_pause_freezes_simulation_allows_order_changes_and_resumes_without_jump()
    {
        var id = Spawn(C(1, 8));
        Drag(AtHex(C(1, 8), id), AtHex(C(9, 8)));
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

        // While paused: build a new chain from the unit (it starts at the hex the unit is stepping into).
        Drag(AtHex(u.Cell, id), AtHex(C(2, 1)));
        Assert.Equal(C(2, 1), Assert.Single(u.Orders.Items).Cell);
        Assert.Equal(frozen, (u.Cell, u.Motion.StepTo, u.Motion.Progress, _sim.Tick));

        _sim.SetPaused(false);
        _sim.Advance(1.0 / 30);
        Assert.Equal(frozen.StepTo, u.Motion.StepTo);
        Assert.Equal(frozen.Progress + (u.SpeedHexesPerSecond / 30.0), u.Motion.Progress, 9);
        RunUntilIdle(id);
        Assert.Equal(C(2, 1), u.Cell);
    }
}
