using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Hex;
using HexTactics.Core.Orders;
using HexTactics.Core.Spawning;
using HexTactics.Core.Units;
using HexTactics.Interaction;
using HexTactics.Simulation;
using HexTactics.Tests.Shared;
using Xunit;

namespace HexTactics.Interaction.Tests;

/// <summary>Mouse scenarios of spec §5 driven through the pure input state machine.</summary>
public class OrderDragTests
{
    private readonly HexLayout _layout = new(1.0);
    private readonly GameSimulation _sim = new(TestMaps.Empty());
    private readonly InputStateMachine _fsm;
    private readonly List<InteractionFeedback> _feedback = new();
    private readonly UnitId _unit;

    public OrderDragTests()
    {
        _fsm = new InputStateMachine(_sim, _layout);
        _fsm.Feedback += f => _feedback.Add(f);
        _unit = _sim.Spawn(SpawnRequest.AtCell(C(3, 4), facing: HexDirection.East)).Unit!.Value;
    }

    private static HexCoord C(int col, int row) => HexCoord.FromOffset(col, row);

    private UnitState Unit => _sim.State.GetUnit(_unit);

    /// <summary>Sample at a plane point; screen = plane × 100 px so threshold checks are meaningful.</summary>
    private PointerSample AtPlane(PlaneVector p, UnitId? unit = null, int? wp = null) =>
        new(new ScreenPoint(p.X * 100, p.Z * 100), p, _layout.PlaneToHex(p), unit, wp);

    private PointerSample AtHex(HexCoord h, UnitId? unit = null, int? wp = null) => AtPlane(_layout.HexToPlane(h), unit, wp);

    private PointerSample AtHexOffset(HexCoord h, HexDirection dir, double distance) =>
        AtPlane(_layout.HexToPlane(h) + (HexLayout.DirectionVector(dir) * distance));

    /// <summary>Press on <paramref name="from"/>, move through intermediate points, release on <paramref name="to"/>.</summary>
    private void Drag(PointerSample from, PointerSample to, params PointerSample[] via)
    {
        _fsm.PointerDown(PointerButton.Primary, from);
        foreach (var v in via)
        {
            _fsm.PointerMove(v);
        }

        _fsm.PointerMove(to);
        _fsm.PointerUp(PointerButton.Primary, to);
    }

    [Fact]
    public void Hold_and_drag_from_unit_to_reachable_hex_creates_TD1()
    {
        _fsm.PointerDown(PointerButton.Primary, AtHex(C(3, 4), _unit));
        _fsm.PointerMove(AtHex(C(6, 2)));
        Assert.Equal(InputMode.DraggingFromUnit, _fsm.Mode);
        Assert.NotNull(_fsm.Drag);
        Assert.Equal(DragTargetStatus.Valid, _fsm.Drag!.Status);
        Assert.Equal(C(3, 4), _fsm.Drag.SourceCell);

        _fsm.PointerUp(PointerButton.Primary, AtHex(C(6, 2)));
        var wp = Assert.Single(Unit.Orders.Items);
        Assert.Equal(C(6, 2), wp.Cell);
        Assert.Equal(1, wp.Sequence);
        Assert.Equal(InputMode.UnitSelected, _fsm.Mode);
        Assert.Null(_fsm.Drag);
        Assert.Equal(C(3, 4), Unit.Cell); // no movement in v0.3.0
    }

    [Fact]
    public void Drag_from_last_waypoint_builds_TD2_and_TD3_in_order()
    {
        Drag(AtHex(C(3, 4), _unit), AtHex(C(6, 2)));
        var wp1 = Unit.Orders.Items[0];
        Drag(AtHex(C(6, 2), wp: wp1.Id), AtHex(C(8, 6)));
        var wp2 = Unit.Orders.Items[1];
        Drag(AtHex(C(8, 6), wp: wp2.Id), AtHex(C(2, 8)));

        Assert.Equal(new[] { C(6, 2), C(8, 6), C(2, 8) }, Unit.Orders.Items.Select(w => w.Cell));
        Assert.Equal(new[] { 1, 2, 3 }, Unit.Orders.Items.Select(w => w.Sequence));
    }

    [Fact]
    public void Drag_from_waypoint_starts_at_that_waypoint()
    {
        Drag(AtHex(C(3, 4), _unit), AtHex(C(6, 2)));
        var wp1 = Unit.Orders.Items[0];
        _fsm.PointerDown(PointerButton.Primary, AtHex(C(6, 2), wp: wp1.Id));
        _fsm.PointerMove(AtHex(C(8, 2)));
        Assert.Equal(InputMode.DraggingFromWaypoint, _fsm.Mode);
        Assert.Equal(C(6, 2), _fsm.Drag!.SourceCell);
        Assert.Equal(OrderMode.Append, _fsm.Drag.Mode);
        Assert.Null(_fsm.Drag.FacingPreview);
    }

    [Fact]
    public void New_drag_from_unit_replaces_unfinished_queue()
    {
        Drag(AtHex(C(3, 4), _unit), AtHex(C(6, 2)));
        Drag(AtHex(C(6, 2), wp: Unit.Orders.Items[0].Id), AtHex(C(8, 6)));
        Drag(AtHex(C(3, 4), _unit), AtHex(C(1, 1)));
        var wp = Assert.Single(Unit.Orders.Items);
        Assert.Equal(C(1, 1), wp.Cell);
        Assert.Equal(1, wp.Sequence);
    }

    [Fact]
    public void Drag_from_middle_waypoint_is_refused_and_queue_unchanged()
    {
        Drag(AtHex(C(3, 4), _unit), AtHex(C(6, 2)));
        Drag(AtHex(C(6, 2), wp: Unit.Orders.Items[0].Id), AtHex(C(8, 6)));
        var before = Unit.Orders.Items.ToList();

        _fsm.Select(_unit);
        Drag(AtHex(C(6, 2), wp: before[0].Id), AtHex(C(9, 1)));

        Assert.Equal(before, Unit.Orders.Items);
        Assert.Contains(_feedback, f => f is ChainContinueRefused);
        Assert.Equal(InputMode.UnitSelected, _fsm.Mode);
    }

    [Theory]
    [InlineData(HexDirection.East)]
    [InlineData(HexDirection.NorthEast)]
    [InlineData(HexDirection.NorthWest)]
    [InlineData(HexDirection.West)]
    [InlineData(HexDirection.SouthWest)]
    [InlineData(HexDirection.SouthEast)]
    public void Short_directional_drag_inside_source_hex_rotates_without_moving(HexDirection dir)
    {
        _sim.SetFacing(_unit, dir.Opposite());
        Drag(AtHex(C(3, 4), _unit), AtHexOffset(C(3, 4), dir, 0.6));
        Assert.Equal(dir, Unit.Facing);
        Assert.Equal(C(3, 4), Unit.Cell);
        Assert.True(Unit.Orders.IsEmpty);
        Assert.Contains(_feedback, f => f is RotatedInPlace r && r.Facing == dir);
    }

    [Fact]
    public void Rotation_uses_map_plane_direction_and_snaps_to_nearest_of_six()
    {
        // 20° above east on the plane is still East; 40° is NorthEast.
        var center = _layout.HexToPlane(C(3, 4));
        var e20 = new PlaneVector(System.Math.Cos(0.349), -System.Math.Sin(0.349)) * 0.6;
        var e40 = new PlaneVector(System.Math.Cos(0.698), -System.Math.Sin(0.698)) * 0.6;
        Drag(AtHex(C(3, 4), _unit), AtPlane(center + e20));
        Assert.Equal(HexDirection.East, Unit.Facing);
        Drag(AtHex(C(3, 4), _unit), AtPlane(center + e40));
        Assert.Equal(HexDirection.NorthEast, Unit.Facing);
    }

    [Fact]
    public void Release_on_source_after_leaving_it_still_rotates()
    {
        Drag(AtHex(C(3, 4), _unit), AtHexOffset(C(3, 4), HexDirection.SouthWest, 0.5), AtHex(C(6, 2)), AtHex(C(5, 5)));
        Assert.Equal(HexDirection.SouthWest, Unit.Facing);
        Assert.True(Unit.Orders.IsEmpty);
    }

    [Fact]
    public void Release_near_source_centre_does_nothing()
    {
        // Drag starts (screen distance > threshold) but ends within the dead zone of the source hex.
        Drag(AtHex(C(3, 4), _unit), AtHexOffset(C(3, 4), HexDirection.West, 0.1), AtHex(C(6, 2)));
        Assert.Equal(HexDirection.East, Unit.Facing);
        Assert.True(Unit.Orders.IsEmpty);
        Assert.Contains(_feedback, f => f is DragCancelled { ByUser: false });
    }

    [Fact]
    public void Rotate_only_is_not_applied_when_unit_already_has_waypoints()
    {
        Drag(AtHex(C(3, 4), _unit), AtHex(C(6, 2)));
        _fsm.PointerDown(PointerButton.Primary, AtHex(C(3, 4), _unit));
        _fsm.PointerMove(AtHexOffset(C(3, 4), HexDirection.West, 0.6));
        Assert.Equal(DragTargetStatus.SourceHasOrders, _fsm.Drag!.Status);
        _fsm.PointerUp(PointerButton.Primary, AtHexOffset(C(3, 4), HexDirection.West, 0.6));
        Assert.Equal(HexDirection.East, Unit.Facing);
        Assert.Single(Unit.Orders.Items);
    }

    [Fact]
    public void Micro_movement_is_a_click_not_a_drag()
    {
        var press = AtHex(C(3, 4), _unit);
        var jitter = new PointerSample(new ScreenPoint(press.Screen.X + 5, press.Screen.Y + 4), press.Plane, press.Hex, _unit);
        _fsm.PointerDown(PointerButton.Primary, press);
        _fsm.PointerMove(jitter);
        Assert.False(_fsm.IsDragging);
        _fsm.PointerUp(PointerButton.Primary, jitter);
        Assert.Equal(_unit, _fsm.SelectedUnit);
        Assert.True(Unit.Orders.IsEmpty);
        Assert.Equal(HexDirection.East, Unit.Facing);
    }

    [Theory]
    [InlineData(-3, 4)]
    [InlineData(14, 4)]
    [InlineData(5, -2)]
    public void Release_outside_map_creates_no_waypoint(int col, int row)
    {
        Drag(AtHex(C(3, 4), _unit), AtHex(C(col, row)));
        Assert.True(Unit.Orders.IsEmpty);
        Assert.Contains(_feedback, f => f is OrderNotCreated { Status: DragTargetStatus.OutOfBounds });
    }

    [Fact]
    public void Release_on_occupied_or_blocked_hex_creates_no_waypoint()
    {
        var sim = new GameSimulation(TestMaps.LoadSmall());
        var fsm = new InputStateMachine(sim, _layout);
        var a = sim.Spawn(SpawnRequest.AtCell(C(1, 1))).Unit!.Value;
        sim.Spawn(SpawnRequest.AtCell(C(4, 2)));

        fsm.PointerDown(PointerButton.Primary, AtHex(C(1, 1), a));
        fsm.PointerMove(AtHex(C(4, 2)));
        Assert.Equal(DragTargetStatus.Occupied, fsm.Drag!.Status);
        fsm.PointerMove(AtHex(C(2, 1)));
        Assert.Equal(DragTargetStatus.NotWalkable, fsm.Drag!.Status);
        fsm.PointerUp(PointerButton.Primary, AtHex(C(2, 1)));
        Assert.True(sim.State.GetUnit(a).Orders.IsEmpty);
    }

    [Fact]
    public void Escape_cancels_current_arrow_but_keeps_previous_waypoints()
    {
        Drag(AtHex(C(3, 4), _unit), AtHex(C(6, 2)));
        var before = Unit.Orders.Items.ToList();

        _fsm.PointerDown(PointerButton.Primary, AtHex(C(6, 2), wp: before[0].Id));
        _fsm.PointerMove(AtHex(C(9, 5)));
        Assert.True(_fsm.IsDragging);
        _fsm.Cancel();
        Assert.False(_fsm.IsDragging);
        Assert.Null(_fsm.Drag);
        _fsm.PointerUp(PointerButton.Primary, AtHex(C(9, 5)));

        Assert.Equal(before, Unit.Orders.Items);
        Assert.Equal(InputMode.UnitSelected, _fsm.Mode);
        Assert.Contains(_feedback, f => f is DragCancelled { ByUser: true });
    }

    [Fact]
    public void Right_button_cancels_only_the_unfinished_drag()
    {
        Drag(AtHex(C(3, 4), _unit), AtHex(C(6, 2)));
        var before = Unit.Orders.Items.ToList();
        _fsm.PointerDown(PointerButton.Primary, AtHex(C(3, 4), _unit));
        _fsm.PointerMove(AtHex(C(9, 5)));
        _fsm.PointerDown(PointerButton.Secondary, AtHex(C(9, 5)));
        _fsm.PointerUp(PointerButton.Secondary, AtHex(C(9, 5)));
        _fsm.PointerUp(PointerButton.Primary, AtHex(C(9, 5)));
        Assert.Equal(before, Unit.Orders.Items);
    }

    [Fact]
    public void Click_on_waypoint_selects_it()
    {
        Drag(AtHex(C(3, 4), _unit), AtHex(C(6, 2)));
        var wp = Unit.Orders.Items[0];
        _fsm.Select(null);
        var s = AtHex(C(6, 2), wp: wp.Id);
        _fsm.PointerDown(PointerButton.Primary, s);
        _fsm.PointerUp(PointerButton.Primary, s);
        Assert.Equal(_unit, _fsm.SelectedUnit);
        Assert.Equal(wp.Id, _fsm.SelectedWaypointId);
    }

    [Fact]
    public void Dragging_from_another_unit_selects_it()
    {
        var other = _sim.Spawn(SpawnRequest.AtCell(C(8, 8))).Unit!.Value;
        _fsm.Select(_unit);
        Drag(AtHex(C(8, 8), other), AtHex(C(10, 8)));
        Assert.Equal(other, _fsm.SelectedUnit);
        Assert.Single(_sim.State.GetUnit(other).Orders.Items);
        Assert.True(Unit.Orders.IsEmpty);
    }

    [Fact]
    public void Facing_preview_follows_first_step_towards_target_without_changing_model()
    {
        _fsm.PointerDown(PointerButton.Primary, AtHex(C(3, 4), _unit));
        _fsm.PointerMove(AtHex(C(3, 1)));
        Assert.NotNull(_fsm.Drag!.FacingPreview);
        Assert.Equal(HexLine.FirstStepDirection(C(3, 4), C(3, 1)), _fsm.Drag.FacingPreview);
        Assert.Equal(HexDirection.East, Unit.Facing);
        _fsm.Cancel();
    }
}
