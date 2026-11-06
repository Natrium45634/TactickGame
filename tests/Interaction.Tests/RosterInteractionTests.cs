using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Hex;
using HexTactics.Core.Roster;
using HexTactics.Core.Spawning;
using HexTactics.Core.Units;
using HexTactics.Interaction;
using HexTactics.Simulation;
using HexTactics.Tests.Shared;
using Xunit;

namespace HexTactics.Interaction.Tests;

/// <summary>
/// Dragging roster cards onto the map (spec part 2 §5; acceptance P07–P09). The card press itself happens in the UI:
/// the client calls <see cref="InputStateMachine.BeginRosterDrag"/>, then forwards pointer moves and the release.
/// </summary>
public class RosterInteractionTests
{
    private readonly HexLayout _layout = new(1.0);
    private readonly GameSimulation _sim = new(TestMaps.LoadPrototype(), roster: RosterState.Prototype());
    private readonly InputStateMachine _fsm;
    private readonly List<InteractionFeedback> _feedback = new();

    public RosterInteractionTests()
    {
        _fsm = new InputStateMachine(_sim, _layout);
        _fsm.Feedback += _feedback.Add;
    }

    private static HexCoord C(int col, int row) => HexCoord.FromOffset(col, row);

    private RosterEntry Entry(int id) => _sim.State.Roster.Find(id)!;

    private PointerSample AtPlane(PlaneVector p) =>
        new(new ScreenPoint(p.X * 100, p.Z * 100), p, _layout.PlaneToHex(p), _sim.State.UnitAt(_layout.PlaneToHex(p))?.Id);

    private PointerSample AtHex(HexCoord h) => AtPlane(_layout.HexToPlane(h));

    private PointerSample Sector(HexCoord h, HexDirection dir) => AtPlane(_layout.HexToPlane(h) + (HexLayout.DirectionVector(dir) * 0.6));

    /// <summary>Pointer over a UI panel: no plane point, no hex.</summary>
    private static PointerSample OverUi => new(new ScreenPoint(5, 5), null, null);

    private void Click(PointerSample s, PointerButton b = PointerButton.Primary)
    {
        _fsm.PointerMove(s);
        _fsm.PointerDown(b, s);
        _fsm.PointerUp(b, s);
    }

    /// <summary>Press on card, move over the given hexes, release on the last one.</summary>
    private void DragCard(int entry, params PointerSample[] path)
    {
        _fsm.BeginRosterDrag(entry);
        foreach (var p in path)
        {
            _fsm.PointerMove(p);
        }

        _fsm.PointerUp(PointerButton.Primary, path[^1]);
    }

    private void Run(double seconds)
    {
        for (var t = 0.0; t < seconds; t += 1.0 / 60)
        {
            _sim.Advance(1.0 / 60);
        }
    }

    [Fact]
    public void Dragging_a_card_shows_allowed_hexes_and_validates_the_hex_under_the_cursor()
    {
        _fsm.BeginRosterDrag(1);

        Assert.Equal(InputMode.RosterDrag, _fsm.Mode);
        Assert.Equal(RosterEntryState.PlacementPreview, Entry(1).State);
        Assert.Contains(_feedback, f => f is RosterDragStarted { RosterEntry: 1 });
        var view = _fsm.Placement!;
        Assert.Equal(new[] { C(0, 4), C(1, 4), C(0, 5), C(1, 5), C(0, 6) }.ToHashSet(), view.AllowedCells.ToHashSet());

        _fsm.PointerMove(AtHex(C(3, 4)));
        Assert.False(_fsm.Placement!.CanPlace);
        Assert.Equal(PlacementError.NotAllowedHere, _fsm.Placement.Error);

        _fsm.PointerMove(AtHex(C(1, 4)));
        Assert.True(_fsm.Placement!.CanPlace);
        Assert.Equal(C(1, 4), _fsm.Placement.TargetHex);
        Assert.Equal(0, _sim.State.UnitCount); // nothing is created while hovering
    }

    [Fact]
    public void P08_drop_on_green_base_places_the_unit_and_the_arrow_is_live_without_another_click()
    {
        DragCard(1, AtHex(C(3, 5)), AtHex(C(1, 4)));

        Assert.Equal(1, _sim.State.UnitCount);
        var unit = _sim.State.UnitAt(C(1, 4))!;
        Assert.Equal(UnitLifecycle.PlacedPendingFacing, unit.Lifecycle);
        Assert.Equal(RosterEntryState.PlacedPendingFacing, Entry(1).State);
        Assert.Contains(_feedback, f => f is RosterUnitPlaced { RosterEntry: 1 });
        Assert.Null(_fsm.Placement);

        // The draft of the new unit is open at once: the live arrow follows the cursor.
        Assert.Equal(InputMode.SingleDraft, _fsm.Mode);
        Assert.Equal(unit.Id, _fsm.SelectedUnit);
        Assert.Equal(unit.Id, _fsm.Draft!.Unit);
        Assert.Empty(_fsm.Draft.Waypoints);
        Assert.False(_fsm.Draft.Held);

        _fsm.PointerMove(AtHex(C(3, 3)));
        Assert.Equal(DraftTargetStatus.Valid, _fsm.Draft!.Status);

        // ТД1 by a click, ТД2 by a click, then the final facing in a sector of ТД2.
        Click(AtHex(C(3, 3)));
        Click(AtHex(C(4, 1)));
        Assert.Equal(new[] { C(3, 3), C(4, 1) }, _fsm.Draft!.Waypoints);
        Click(Sector(C(4, 1), HexDirection.SouthEast));

        Assert.Null(_fsm.Draft);
        Assert.True(unit.IsActive);
        Assert.Equal(RosterEntryState.Active, Entry(1).State);
        Assert.Equal(2, unit.Orders.Count);

        Run(6);
        Assert.Equal(C(4, 1), unit.Cell);
        Assert.Equal(HexDirection.SouthEast, unit.Facing);
    }

    [Fact]
    public void Facing_chosen_right_away_in_the_placement_hex_activates_the_unit_in_place()
    {
        DragCard(3, AtHex(C(0, 6)));
        var unit = _sim.State.UnitAt(C(0, 6))!;

        _fsm.PointerMove(Sector(C(0, 6), HexDirection.NorthEast));
        Assert.Equal(DraftTargetStatus.FacingSector, _fsm.Draft!.Status);
        Click(Sector(C(0, 6), HexDirection.NorthEast));

        Assert.True(unit.IsActive);
        Assert.Equal(HexDirection.NorthEast, unit.Facing);
        Assert.Equal(C(0, 6), unit.Cell);
        Assert.True(unit.Orders.IsEmpty);
        Assert.Equal(RosterEntryState.Active, Entry(3).State);
    }

    [Theory]
    [InlineData(3, 4)] // ordinary walkable hex
    [InlineData(5, 4)] // wall
    [InlineData(7, 0)] // edge spawn zone (not a player deploy area)
    public void P09_drop_on_red_hex_returns_the_card_and_creates_nothing(int col, int row)
    {
        DragCard(2, AtHex(C(1, 4)), AtHex(C(col, row)));

        Assert.Equal(0, _sim.State.UnitCount);
        Assert.Equal(RosterEntryState.AvailableInRoster, Entry(2).State);
        Assert.Equal(InputMode.Idle, _fsm.Mode);
        Assert.Null(_fsm.Placement);
        Assert.Null(_fsm.Draft);
        var returned = Assert.IsType<RosterCardReturned>(_feedback.Last());
        Assert.Equal(PlacementError.NotAllowedHere, returned.Error);

        // The card can be dragged again and placed properly.
        DragCard(2, AtHex(C(0, 5)));
        Assert.Equal(1, _sim.State.UnitCount);
    }

    [Fact]
    public void Drop_back_over_the_panel_returns_the_card()
    {
        DragCard(4, AtHex(C(1, 4)), OverUi);

        Assert.Equal(0, _sim.State.UnitCount);
        Assert.Equal(RosterEntryState.AvailableInRoster, Entry(4).State);
        Assert.Equal(PlacementError.OutOfBounds, Assert.IsType<RosterCardReturned>(_feedback.Last()).Error);
    }

    [Fact]
    public void Drop_on_an_occupied_base_hex_returns_the_card_and_nobody_moves()
    {
        DragCard(1, AtHex(C(0, 4)));
        Click(Sector(C(0, 4), HexDirection.North));
        var first = _sim.State.UnitAt(C(0, 4))!;

        DragCard(2, AtHex(C(0, 4)));

        Assert.Equal(1, _sim.State.UnitCount);
        Assert.Equal(C(0, 4), first.Cell);
        Assert.Equal(RosterEntryState.AvailableInRoster, Entry(2).State);
        Assert.Equal(PlacementError.Occupied, Assert.IsType<RosterCardReturned>(_feedback.Last()).Error);
    }

    [Fact]
    public void Rmb_or_esc_while_dragging_returns_the_card()
    {
        _fsm.BeginRosterDrag(1);
        _fsm.PointerMove(AtHex(C(1, 4)));
        _fsm.PointerDown(PointerButton.Secondary, AtHex(C(1, 4)));
        Assert.Equal(RosterEntryState.AvailableInRoster, Entry(1).State);
        Assert.Equal(InputMode.Idle, _fsm.Mode);

        _fsm.BeginRosterDrag(1);
        _fsm.Cancel();
        Assert.Equal(RosterEntryState.AvailableInRoster, Entry(1).State);
        Assert.Equal(0, _sim.State.UnitCount);
    }

    [Fact]
    public void P07_cancelled_draft_leaves_the_unit_placed_but_inactive_until_a_facing_is_given()
    {
        DragCard(1, AtHex(C(1, 4)));
        var unit = _sim.State.UnitAt(C(1, 4))!;
        Click(AtHex(C(3, 3))); // ТД1 in the draft
        _fsm.PointerDown(PointerButton.Secondary, AtHex(C(6, 6))); // RMB: the whole draft is cancelled

        Assert.Null(_fsm.Draft);
        Assert.Equal(UnitLifecycle.PlacedPendingFacing, unit.Lifecycle);
        Assert.Null(unit.Facing);
        Assert.True(unit.Orders.IsEmpty);
        Run(2);
        Assert.Equal(C(1, 4), unit.Cell);

        // Selecting the unit again asks for its facing (the draft opens by itself).
        Click(AtHex(C(1, 4)));
        Assert.Equal(unit.Id, _fsm.Draft?.Unit);
        Click(Sector(C(1, 4), HexDirection.South));
        Assert.True(unit.IsActive);
        Assert.Equal(HexDirection.South, unit.Facing);
    }

    [Fact]
    public void Card_of_a_placed_operative_selects_it_and_reopens_the_draft_while_facing_is_pending()
    {
        DragCard(5, AtHex(C(1, 5)));
        var unit = _sim.State.UnitAt(C(1, 5))!;
        _fsm.Cancel();
        _fsm.Select(null);

        _fsm.BeginRosterDrag(5); // not available any more: nothing happens
        Assert.NotEqual(InputMode.RosterDrag, _fsm.Mode);

        _fsm.SelectRosterEntry(5);
        Assert.Equal(unit.Id, _fsm.SelectedUnit);
        Assert.Equal(unit.Id, _fsm.Draft?.Unit);

        Click(Sector(C(1, 5), HexDirection.North));
        _fsm.Select(null);
        _fsm.SelectRosterEntry(5);
        Assert.Equal(unit.Id, _fsm.SelectedUnit);
        Assert.Null(_fsm.Draft); // active units are just selected
    }

    [Fact]
    public void Starting_a_card_drag_cancels_an_open_draft_without_touching_confirmed_orders()
    {
        DragCard(1, AtHex(C(1, 4)));
        var unit = _sim.State.UnitAt(C(1, 4))!;
        Click(AtHex(C(3, 3)));
        Click(Sector(C(3, 3), HexDirection.North));
        Assert.Equal(1, unit.Orders.Count);

        _fsm.StartDraft(unit.Id);
        Click(AtHex(C(2, 7)));
        _fsm.BeginRosterDrag(2);

        Assert.Equal(InputMode.RosterDrag, _fsm.Mode);
        Assert.Null(_fsm.Draft);
        Assert.Equal(C(3, 3), unit.Orders.Items.Single().Cell);
    }
}
