using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Hex;
using HexTactics.Core.Map;
using HexTactics.Core.Orders;
using HexTactics.Core.Spawning;
using HexTactics.Core.Units;
using HexTactics.Interaction;
using HexTactics.Simulation;
using HexTactics.Tests.Shared;
using Xunit;

namespace HexTactics.Interaction.Tests;

/// <summary>Continuous order drafts (spec part 2 §4, §7, §8, §9) on the flat-top prototype map.</summary>
public class DraftTests
{
    private readonly HexLayout _layout = new(1.0);
    private readonly GameSimulation _sim = new(TestMaps.LoadPrototype());
    private readonly InputStateMachine _fsm;
    private readonly List<InteractionFeedback> _feedback = new();
    private readonly UnitId _unit;

    public DraftTests()
    {
        _fsm = new InputStateMachine(_sim, _layout);
        _fsm.Feedback += _feedback.Add;
        _unit = _sim.Spawn(SpawnRequest.AtCell(C(2, 4), facing: HexDirection.North)).Unit!.Value;
    }

    private static HexCoord C(int col, int row) => HexCoord.FromOffset(col, row);

    private UnitState Unit => _sim.State.GetUnit(_unit);

    private PointerSample AtPlane(PlaneVector p, int? wp = null) =>
        new(new ScreenPoint(p.X * 100, p.Z * 100), p, _layout.PlaneToHex(p), _sim.State.UnitAt(_layout.PlaneToHex(p))?.Id, wp);

    private PointerSample AtHex(HexCoord h, int? wp = null) => AtPlane(_layout.HexToPlane(h), wp);

    private PointerSample Sector(HexCoord h, HexDirection dir) => AtPlane(_layout.HexToPlane(h) + (HexLayout.DirectionVector(dir) * 0.6));

    private void Click(PointerSample s, PointerButton b = PointerButton.Primary)
    {
        _fsm.PointerMove(s);
        _fsm.PointerDown(b, s);
        _fsm.PointerUp(b, s);
    }

    private void DragFrom(PointerSample from, PointerSample to)
    {
        _fsm.PointerMove(from);
        _fsm.PointerDown(PointerButton.Primary, from);
        _fsm.PointerMove(to);
        _fsm.PointerUp(PointerButton.Primary, to);
    }

    /// <summary>ТД1 by dragging from the unit, then further waypoints by plain clicks.</summary>
    private void Chain(params HexCoord[] waypoints)
    {
        DragFrom(AtHex(Unit.Cell), AtHex(waypoints[0]));
        foreach (var w in waypoints.Skip(1))
        {
            Click(AtHex(w));
        }
    }

    private void RunUntilIdle()
    {
        for (var i = 0; i < 6000 && !(Unit.Orders.IsEmpty && !Unit.Motion.IsStepping && Unit.Orders.FinalFacing is null); i++)
        {
            _sim.Advance(1.0 / 60);
        }
    }

    [Fact]
    public void Drag_from_unit_places_TD1_and_the_draft_stays_open_with_a_live_arrow()
    {
        DragFrom(AtHex(C(2, 4)), AtHex(C(3, 1)));
        Assert.Equal(InputMode.SingleDraft, _fsm.Mode);
        Assert.Equal(new[] { C(3, 1) }, _fsm.Draft!.Waypoints);
        Assert.False(_fsm.Draft.Held);
        Assert.True(Unit.Orders.IsEmpty); // nothing is applied before the facing is confirmed

        // No button held: the live arrow follows the cursor from ТД1.
        _fsm.PointerMove(AtHex(C(4, 8)));
        Assert.Equal(C(3, 1), _fsm.Draft.Source);
        Assert.Equal(_layout.HexToPlane(C(3, 1)), _fsm.Draft.SourceCenter);
        Assert.Equal(DraftTargetStatus.Valid, _fsm.Draft.Status);
        Assert.Equal(C(4, 8), _fsm.Draft.Path![^1]);
    }

    [Fact]
    public void Single_clicks_add_TD2_and_TD3_without_regrabbing()
    {
        Chain(C(3, 1), C(1, 8), C(4, 8));
        Assert.Equal(new[] { C(3, 1), C(1, 8), C(4, 8) }, _fsm.Draft!.Waypoints);
        Assert.Equal(C(4, 8), _fsm.Draft.Source);
        Assert.Contains(_feedback, f => f is DraftWaypointAdded { Number: 3 });
    }

    [Fact]
    public void Sector_click_on_last_TD_confirms_final_facing_and_applies_the_chain_at_once()
    {
        Chain(C(3, 1), C(1, 8), C(4, 8));
        _fsm.PointerMove(Sector(C(4, 8), HexDirection.NorthWest));
        Assert.Equal(DraftTargetStatus.FacingSector, _fsm.Draft!.Status);
        Assert.Equal(HexDirection.NorthWest, _fsm.Draft.SectorFacing);
        Assert.True(Unit.Orders.IsEmpty);

        Click(Sector(C(4, 8), HexDirection.NorthWest));
        Assert.Null(_fsm.Draft);
        Assert.Equal(InputMode.UnitSelected, _fsm.Mode);
        Assert.Equal(new[] { C(3, 1), C(1, 8), C(4, 8) }, Unit.Orders.Items.Select(w => w.Cell));
        Assert.Equal(HexDirection.NorthWest, Unit.Orders.FinalFacing);

        RunUntilIdle();
        Assert.Equal(C(4, 8), Unit.Cell);
        Assert.Equal(HexDirection.NorthWest, Unit.Facing);
    }

    [Theory]
    [InlineData(HexDirection.North)]
    [InlineData(HexDirection.NorthEast)]
    [InlineData(HexDirection.SouthEast)]
    [InlineData(HexDirection.South)]
    [InlineData(HexDirection.SouthWest)]
    [InlineData(HexDirection.NorthWest)]
    public void All_six_facings_are_available_at_the_last_TD_and_at_the_unit(HexDirection dir)
    {
        Chain(C(3, 1));
        Click(Sector(C(3, 1), dir));
        Assert.Equal(dir, Unit.Orders.FinalFacing);

        RunUntilIdle();
        Click(AtHex(Unit.Cell)); // select (already selected; centre = neutral, no turn)
        var target = dir.Opposite();
        Click(Sector(Unit.Cell, target));
        _sim.Step();
        Assert.Equal(target, Unit.Facing);
        Assert.Equal(C(3, 1), Unit.Cell);
    }

    [Fact]
    public void Neutral_centre_click_never_confirms()
    {
        Chain(C(3, 1));
        Click(AtHex(C(3, 1)));
        Assert.NotNull(_fsm.Draft);
        Assert.True(Unit.Orders.IsEmpty);
        Assert.Equal(DraftTargetStatus.Neutral, _fsm.Draft!.Status);
    }

    [Fact]
    public void Turn_in_place_of_selected_unit_without_any_waypoint()
    {
        Click(AtHex(C(2, 4)));
        Assert.Equal(_unit, _fsm.SelectedUnit);
        _fsm.PointerMove(Sector(C(2, 4), HexDirection.SouthEast));
        Assert.Equal(HexDirection.SouthEast, _fsm.HoverTurnFacing);
        Assert.Equal(HexDirection.North, Unit.Facing); // preview only

        Click(Sector(C(2, 4), HexDirection.SouthEast));
        _sim.Step();
        Assert.Equal(HexDirection.SouthEast, Unit.Facing);
        Assert.Equal(C(2, 4), Unit.Cell);
        Assert.True(Unit.Orders.IsEmpty);
    }

    [Fact]
    public void Releasing_the_first_drag_in_a_sector_of_the_own_hex_turns_in_place()
    {
        DragFrom(AtHex(C(2, 4)), Sector(C(2, 4), HexDirection.SouthWest));
        Assert.Null(_fsm.Draft);
        _sim.Step();
        Assert.Equal(HexDirection.SouthWest, Unit.Facing);
        Assert.Equal(C(2, 4), Unit.Cell);
    }

    [Fact]
    public void Draft_without_confirmed_facing_is_never_applied()
    {
        Chain(C(3, 1), C(1, 8));
        for (var i = 0; i < 120; i++)
        {
            _sim.Advance(1.0 / 30);
            _fsm.Refresh();
        }

        Assert.True(Unit.Orders.IsEmpty);
        Assert.Equal(C(2, 4), Unit.Cell);
        Assert.NotNull(_fsm.Draft);
    }

    [Fact]
    public void Right_click_cancels_the_whole_draft_but_keeps_previously_confirmed_orders()
    {
        Chain(C(3, 1));
        Click(Sector(C(3, 1), HexDirection.South));
        var confirmed = Unit.Orders.Items.ToList();

        _fsm.Select(_unit);
        DragFrom(AtHex(C(2, 4)), AtHex(C(1, 8)));
        Click(AtHex(C(4, 8)));
        Click(AtHex(C(0, 0)), PointerButton.Secondary);

        Assert.Null(_fsm.Draft);
        Assert.Equal(confirmed, Unit.Orders.Items);
        Assert.Contains(_feedback, f => f is DraftCancelled { Reason: DraftCancelReason.ByUser });
    }

    [Fact]
    public void Escape_cancels_like_right_click()
    {
        Chain(C(3, 1), C(1, 8));
        _fsm.Cancel();
        Assert.Null(_fsm.Draft);
        Assert.True(Unit.Orders.IsEmpty);
        Assert.Equal(InputMode.UnitSelected, _fsm.Mode);
    }

    [Fact]
    public void A_tile_type_without_waypoints_is_red_for_a_waypoint_but_a_route_may_cross_it()
    {
        _sim.State.Grid.SetTile(C(3, 2), TileType.Walkable, "test.tile.ford", 0, allowsWaypoints: false);
        Chain(C(3, 1));
        _fsm.PointerMove(AtHex(C(3, 2)));
        Assert.Equal(DraftTargetStatus.WaypointsForbidden, _fsm.Draft!.Status);
        _fsm.PointerMove(AtHex(C(3, 3)));
        Assert.Equal(DraftTargetStatus.Valid, _fsm.Draft!.Status);
    }

    [Fact]
    public void Hovering_a_red_hex_only_shows_it_but_clicking_it_cancels_the_draft()
    {
        Chain(C(3, 1));
        _fsm.PointerMove(AtHex(C(5, 4))); // wall
        Assert.Equal(DraftTargetStatus.NotWalkable, _fsm.Draft!.Status);
        _fsm.PointerMove(AtHex(C(8, 3))); // isolated pocket
        Assert.Equal(DraftTargetStatus.Unreachable, _fsm.Draft!.Status);
        _fsm.PointerMove(AtHex(C(1, 8)));
        Assert.NotNull(_fsm.Draft); // still drafting

        Click(AtHex(C(8, 3)));
        Assert.Null(_fsm.Draft);
        Assert.True(Unit.Orders.IsEmpty);
        Assert.Contains(_feedback, f => f is DraftCancelled { Reason: DraftCancelReason.InvalidTarget, Status: DraftTargetStatus.Unreachable });
    }

    [Fact]
    public void Releasing_the_first_drag_on_a_red_hex_cancels()
    {
        DragFrom(AtHex(C(2, 4)), AtHex(C(5, 3)));
        Assert.Null(_fsm.Draft);
        Assert.True(Unit.Orders.IsEmpty);
    }

    [Fact]
    public void Right_click_on_a_draft_TD_removes_it_and_all_following()
    {
        Chain(C(3, 1), C(1, 8), C(4, 8));
        Click(AtHex(C(1, 8)), PointerButton.Secondary);
        Assert.Equal(new[] { C(3, 1) }, _fsm.Draft!.Waypoints);
        Assert.Equal(C(3, 1), _fsm.Draft.Source);
        Assert.Contains(_feedback, f => f is DraftTruncated { RemovedFromNumber: 2, Remaining: 1 });
    }

    [Fact]
    public void Right_click_on_a_confirmed_TD_removes_it_and_following()
    {
        Chain(C(3, 1), C(1, 8), C(4, 8));
        Click(Sector(C(4, 8), HexDirection.North));
        var second = Unit.Orders.Items[1];
        Click(AtHex(C(1, 8), wp: second.Id), PointerButton.Secondary);
        Assert.Equal(new[] { C(3, 1) }, Unit.Orders.Items.Select(w => w.Cell));
        Assert.Equal(HexDirection.North, Unit.Orders.FinalFacing);
        Assert.Contains(_feedback, f => f is ConfirmedWaypointsRemoved);
    }

    [Fact]
    public void Dragging_from_the_last_confirmed_TD_continues_the_route()
    {
        Chain(C(3, 1), C(1, 8));
        Click(Sector(C(1, 8), HexDirection.North));
        var last = Unit.Orders.Last!;

        DragFrom(AtHex(C(1, 8), wp: last.Id), AtHex(C(4, 8)));
        Assert.True(_fsm.Draft!.IsEdit);
        Assert.Equal(new[] { C(3, 1), C(1, 8), C(4, 8) }, _fsm.Draft.Waypoints);
        Click(Sector(C(4, 8), HexDirection.SouthEast));
        Assert.Equal(new[] { C(3, 1), C(1, 8), C(4, 8) }, Unit.Orders.Items.Select(w => w.Cell));
        Assert.Equal(HexDirection.SouthEast, Unit.Orders.FinalFacing);
    }

    [Fact]
    public void Dragging_from_a_middle_confirmed_TD_is_refused()
    {
        Chain(C(3, 1), C(1, 8));
        Click(Sector(C(1, 8), HexDirection.North));
        var first = Unit.Orders.Items[0];
        DragFrom(AtHex(C(3, 1), wp: first.Id), AtHex(C(4, 8)));
        Assert.Null(_fsm.Draft);
        Assert.Contains(_feedback, f => f is ChainContinueRefused);
    }

    [Fact]
    public void Enemy_under_the_arrow_becomes_the_attack_target_after_the_waypoints_spec_part3()
    {
        // Spec part 3 §4.1 replaces the part 2 reservation: the click creates an AttackTargetOrder instead of a waypoint.
        var enemy = _sim.Spawn(SpawnRequest.AtCell(C(3, 6), Side.Enemy)).Unit!.Value;
        Chain(C(3, 1));
        _fsm.PointerMove(AtHex(C(3, 6)));
        Assert.Equal(DraftTargetStatus.AttackTarget, _fsm.Draft!.Status);
        Assert.Equal(enemy, _fsm.Draft.AttackTarget);

        Click(AtHex(C(3, 6)));
        Assert.Null(_fsm.Draft); // committed at once: no facing step
        var committed = Assert.IsType<DraftCommitted>(_feedback.Last());
        Assert.Equal(OrderKind.AttackTarget, committed.Order.Kind);
        Assert.Equal(new[] { C(3, 1) }, Unit.Orders.Items.Select(w => w.Cell));
        Assert.Equal(enemy, Unit.Orders.AttackTarget);
        Assert.Null(Unit.Orders.FinalFacing);
    }

    [Fact]
    public void Allied_unit_hex_is_red_in_a_single_draft_and_clicking_it_cancels()
    {
        _sim.Spawn(SpawnRequest.AtCell(C(3, 6)));
        Chain(C(3, 1));
        _fsm.PointerMove(AtHex(C(3, 6)));
        Assert.Equal(DraftTargetStatus.Occupied, _fsm.Draft!.Status);
        Click(AtHex(C(3, 6)));
        Assert.Null(_fsm.Draft);
    }

    [Fact]
    public void Commit_fails_if_the_route_became_impossible_and_old_route_is_kept()
    {
        Chain(C(3, 1));
        Click(Sector(C(3, 1), HexDirection.North));
        var confirmed = Unit.Orders.Items.ToList();

        Chain(C(1, 8));
        _sim.SetObstacle(C(1, 8), true); // the game changes before the facing is confirmed
        Click(Sector(C(1, 8), HexDirection.South));
        Assert.NotNull(_fsm.Draft);
        Assert.Contains(_feedback, f => f is DraftCommitFailed);
        Assert.Equal(confirmed, Unit.Orders.Items);
    }

    [Fact]
    public void Facing_preview_follows_first_step_of_shortest_path_around_the_wall()
    {
        var u = _sim.Spawn(SpawnRequest.AtCell(C(4, 4), facing: HexDirection.SouthWest)).Unit!.Value;
        _fsm.PointerMove(AtHex(C(4, 4)));
        _fsm.PointerDown(PointerButton.Primary, AtHex(C(4, 4)));
        _fsm.PointerMove(AtHex(C(6, 4)));
        var view = _fsm.Draft!;
        Assert.Equal(DraftTargetStatus.Valid, view.Status);
        var straight = HexLine.FirstStepDirection(C(4, 4), C(6, 4))!.Value;
        Assert.False(_sim.State.Grid.IsWalkable(C(4, 4).Neighbor(straight)));
        Assert.Equal(C(4, 4).DirectionTo(view.Path![1]), view.UnitFacingPreview);
        Assert.Equal(HexDirection.SouthWest, _sim.State.GetUnit(u).Facing);
    }

    [Fact]
    public void Reachable_set_is_recomputed_when_the_state_changes()
    {
        Chain(C(3, 1));
        _fsm.PointerMove(AtHex(C(3, 6)));
        Assert.Equal(DraftTargetStatus.Valid, _fsm.Draft!.Status);
        _sim.Spawn(SpawnRequest.AtCell(C(3, 6)));
        _fsm.Refresh();
        Assert.Equal(DraftTargetStatus.Occupied, _fsm.Draft!.Status);
        Assert.DoesNotContain(C(3, 6), _fsm.Draft.Reachable);
    }

    [Fact]
    public void Micro_movement_is_a_click_not_a_drag()
    {
        var press = AtHex(C(2, 4));
        var jitter = new PointerSample(new ScreenPoint(press.Screen.X + 5, press.Screen.Y + 4), press.Plane, press.Hex, _unit);
        _fsm.PointerDown(PointerButton.Primary, press);
        _fsm.PointerMove(jitter);
        _fsm.PointerUp(PointerButton.Primary, jitter);
        Assert.Null(_fsm.Draft);
        Assert.Equal(_unit, _fsm.SelectedUnit);
    }

    [Fact]
    public void Moving_unit_on_pause_is_selectable_on_its_hex_and_gets_a_new_route_without_teleport()
    {
        Chain(C(2, 8));
        Click(Sector(C(2, 8), HexDirection.South));
        for (var i = 0; i < 20; i++)
        {
            _sim.Advance(1.0 / 30);
        }

        _sim.SetPaused(true);
        Assert.True(Unit.Motion.IsStepping);
        var logical = Unit.Cell;
        var stepTo = Unit.Motion.StepTo!.Value;
        var progress = Unit.Motion.Progress;
        var oldRoute = Unit.Orders.Items.ToList();

        _fsm.Select(null);
        Click(new PointerSample(new ScreenPoint(1, 1), _layout.HexToPlane(logical), logical)); // its current hex
        Assert.Equal(_unit, _fsm.SelectedUnit);

        DragFrom(AtHex(logical), AtHex(C(0, 8)));
        Assert.Equal(stepTo, _fsm.Draft!.Anchor); // the chain starts at the nearest safe hex centre
        Click(Sector(C(0, 8), HexDirection.North));
        Assert.Equal(new[] { C(0, 8) }, Unit.Orders.Items.Select(w => w.Cell));
        Assert.NotEqual(oldRoute, Unit.Orders.Items);
        Assert.Equal(progress, Unit.Motion.Progress); // nothing moved during the pause

        _sim.SetPaused(false);
        _sim.DrainEvents();
        var entered = new List<HexCoord>();
        for (var i = 0; i < 600 && !(Unit.Orders.IsEmpty && !Unit.Motion.IsStepping); i++)
        {
            _sim.Advance(1.0 / 30);
            entered.AddRange(_sim.DrainEvents().OfType<UnitEnteredCell>().Select(e => e.To));
        }

        if (!Unit.Motion.CellSwitched && logical != stepTo && entered.Count > 0)
        {
            Assert.Equal(stepTo, entered[0]);
        }

        Assert.Equal(C(0, 8), Unit.Cell);
        Assert.DoesNotContain(C(2, 8), entered);
    }

    [Fact]
    public void Cancelling_an_edit_on_pause_leaves_the_running_route_unchanged()
    {
        Chain(C(2, 8));
        Click(Sector(C(2, 8), HexDirection.South));
        _sim.Advance(0.2);
        _sim.SetPaused(true);
        var before = Unit.Orders.Items.ToList();
        DragFrom(AtHex(Unit.Cell), AtHex(C(0, 8)));
        Click(AtHex(C(1, 1)));
        Click(AtHex(C(5, 9)), PointerButton.Secondary);
        Assert.Equal(before, Unit.Orders.Items);
        Assert.Equal(HexDirection.South, Unit.Orders.FinalFacing);
    }

    [Fact]
    public void Facing_sector_next_to_a_raised_wall_uses_the_ground_point_single_draft()
    {
        Chain(C(7, 1));
        var ground = _layout.HexToPlane(C(7, 1)) + (HexLayout.DirectionVector(HexDirection.South) * 0.6);
        var sample = new PointerSample(new ScreenPoint(1, 1), _layout.HexToPlane(C(7, 2)), C(7, 2), null, null, ground);
        _fsm.PointerMove(sample);
        Assert.Equal(DraftTargetStatus.FacingSector, _fsm.Draft!.Status);
        _fsm.PointerDown(PointerButton.Primary, sample);
        _fsm.PointerUp(PointerButton.Primary, sample);
        Assert.Null(_fsm.Draft);
        Assert.Equal(HexDirection.South, Unit.Orders.FinalFacing);
    }
}
