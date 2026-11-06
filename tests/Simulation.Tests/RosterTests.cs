using System;
using System.Linq;
using HexTactics.Core.Hex;
using HexTactics.Core.Orders;
using HexTactics.Core.Roster;
using HexTactics.Core.Spawning;
using HexTactics.Core.Units;
using HexTactics.Simulation;
using HexTactics.Tests.Shared;
using Xunit;

namespace HexTactics.Simulation.Tests;

/// <summary>Roster placement and the unit lifecycle (spec part 2 §5, acceptance 7–9).</summary>
public class RosterTests
{
    // Prototype map: base (0,4) (1,4) (0,5); deploy cells (1,5) (0,6); edge zones on row 0 and column 11.
    private readonly GameSimulation _sim = new(TestMaps.LoadPrototype(), roster: RosterState.Prototype());

    private static HexCoord C(int col, int row) => HexCoord.FromOffset(col, row);

    private RosterEntry Entry(int id) => _sim.State.Roster.Find(id)!;

    private void Run(double seconds)
    {
        for (var t = 0.0; t < seconds; t += 1.0 / 60)
        {
            _sim.Advance(1.0 / 60);
        }
    }

    [Fact]
    public void Prototype_roster_has_five_available_operatives()
    {
        Assert.Equal(5, _sim.State.Roster.Entries.Count);
        Assert.All(_sim.State.Roster.Entries, e => Assert.Equal(RosterEntryState.AvailableInRoster, e.State));
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, _sim.State.Roster.Entries.Select(e => e.Id));
    }

    [Fact]
    public void Allowed_cells_are_the_base_and_deploy_cells_only()
    {
        var allowed = PlacementRules.AllowedCells(_sim.State.Map).ToHashSet();
        Assert.Equal(new[] { C(0, 4), C(1, 4), C(0, 5), C(1, 5), C(0, 6) }.ToHashSet(), allowed);
    }

    [Fact]
    public void Placing_on_the_base_creates_a_unit_without_facing_pending_activation()
    {
        Assert.True(_sim.BeginRosterPlacement(1).Success);
        Assert.Equal(RosterEntryState.PlacementPreview, Entry(1).State);

        var result = _sim.PlaceFromRoster(1, C(1, 4));

        Assert.True(result.Success);
        var unit = _sim.State.GetUnit(result.Unit!.Value);
        Assert.Equal(C(1, 4), unit.Cell);
        Assert.Null(unit.Facing);
        Assert.Equal(UnitLifecycle.PlacedPendingFacing, unit.Lifecycle);
        Assert.False(unit.IsActive);
        Assert.Equal(1, unit.RosterEntryId);
        Assert.Equal(RosterEntryState.PlacedPendingFacing, Entry(1).State);
        Assert.Equal(unit.Id, Entry(1).Unit);
        Assert.Contains(_sim.DrainEvents(), e => e is UnitPlacedFromRoster { RosterEntry: 1 });
    }

    [Theory]
    [InlineData(3, 4, PlacementError.NotAllowedHere)] // ordinary walkable hex
    [InlineData(7, 0, PlacementError.NotAllowedHere)] // edge spawn zone is not automatically allowed (§5.6)
    [InlineData(11, 5, PlacementError.NotAllowedHere)]
    [InlineData(5, 4, PlacementError.NotAllowedHere)] // wall
    [InlineData(-1, 4, PlacementError.OutOfBounds)]
    public void Forbidden_hexes_reject_placement_and_the_card_returns(int col, int row, PlacementError expected)
    {
        _sim.BeginRosterPlacement(2);
        var result = _sim.PlaceFromRoster(2, C(col, row));

        Assert.False(result.Success);
        Assert.Equal(expected, result.Error);
        Assert.Equal(0, _sim.State.UnitCount);
        Assert.Equal(RosterEntryState.AvailableInRoster, Entry(2).State);
        Assert.Null(Entry(2).Unit);
        Assert.Contains(_sim.DrainEvents(), e => e is PlacementRejected { RosterEntry: 2 });
    }

    [Fact]
    public void Occupied_allowed_hex_rejects_placement_without_moving_anyone()
    {
        var first = _sim.PlaceFromRoster(1, C(0, 5)).Unit!.Value;
        var result = _sim.PlaceFromRoster(2, C(0, 5));

        Assert.Equal(PlacementError.Occupied, result.Error);
        Assert.Equal(1, _sim.State.UnitCount);
        Assert.Equal(C(0, 5), _sim.State.GetUnit(first).Cell);
        Assert.Equal(RosterEntryState.AvailableInRoster, Entry(2).State);
    }

    [Fact]
    public void A_placed_operative_cannot_be_placed_again()
    {
        _sim.PlaceFromRoster(1, C(0, 4));
        Assert.False(_sim.BeginRosterPlacement(1).Success);
        var again = _sim.PlaceFromRoster(1, C(1, 4));
        Assert.Equal(PlacementError.EntryNotAvailable, again.Error);
        Assert.Equal(1, _sim.State.UnitCount);
        Assert.Equal(RosterEntryState.PlacedPendingFacing, Entry(1).State);
    }

    [Fact]
    public void Cancelled_drag_returns_the_card()
    {
        _sim.BeginRosterPlacement(3);
        _sim.CancelRosterPlacement(3);
        Assert.Equal(RosterEntryState.AvailableInRoster, Entry(3).State);
        Assert.Equal(0, _sim.State.UnitCount);
    }

    [Fact]
    public void Pending_unit_does_not_move_or_act_until_it_gets_a_facing()
    {
        var id = _sim.PlaceFromRoster(1, C(1, 4)).Unit!.Value;
        _sim.IssueMoveOrder(id, C(3, 4), OrderMode.Replace); // legacy single-waypoint API: queued, but not executed
        Run(3);

        var unit = _sim.State.GetUnit(id);
        Assert.Equal(C(1, 4), unit.Cell);
        Assert.False(unit.Motion.IsStepping);
        Assert.Null(unit.Facing);
        Assert.False(unit.IsActive);
    }

    [Fact]
    public void Turning_a_pending_unit_activates_it()
    {
        var id = _sim.PlaceFromRoster(4, C(0, 6)).Unit!.Value;
        Assert.True(_sim.SetFacing(id, HexDirection.SouthEast).Success);

        var unit = _sim.State.GetUnit(id);
        Assert.True(unit.IsActive);
        Assert.Equal(HexDirection.SouthEast, unit.Facing);
        Assert.Equal(RosterEntryState.Active, Entry(4).State);
        Assert.Contains(_sim.DrainEvents(), e => e is UnitActivated { Facing: HexDirection.SouthEast });
    }

    [Fact]
    public void First_order_with_waypoints_activates_the_unit_and_ends_with_its_final_facing()
    {
        var id = _sim.PlaceFromRoster(1, C(1, 4)).Unit!.Value;
        var applied = _sim.ApplyOrder(new IndividualOrder(id, new[] { C(3, 4) }, HexDirection.NorthEast));

        Assert.True(applied.Success);
        var unit = _sim.State.GetUnit(id);
        Assert.True(unit.IsActive);
        Assert.NotNull(unit.Facing);
        Assert.Equal(RosterEntryState.Active, Entry(1).State);

        Run(4);
        Assert.Equal(C(3, 4), unit.Cell);
        Assert.Equal(HexDirection.NorthEast, unit.Facing);
    }

    [Fact]
    public void Rejected_first_order_keeps_the_unit_pending()
    {
        var id = _sim.PlaceFromRoster(1, C(1, 4)).Unit!.Value;
        var applied = _sim.ApplyOrder(new IndividualOrder(id, new[] { C(5, 4) }, HexDirection.North)); // wall

        Assert.False(applied.Success);
        var unit = _sim.State.GetUnit(id);
        Assert.False(unit.IsActive);
        Assert.Null(unit.Facing);
        Assert.Equal(RosterEntryState.PlacedPendingFacing, Entry(1).State);
    }

    [Fact]
    public void Pending_units_block_their_hex_for_others()
    {
        _sim.PlaceFromRoster(1, C(1, 4));
        var spawned = _sim.Spawn(SpawnRequest.AtCell(C(1, 4)));
        Assert.Equal(SpawnError.Occupied, spawned.Error);
    }

    [Fact]
    public void Active_units_cannot_be_created_without_facing()
    {
        Assert.Throws<InvalidOperationException>(() => _sim.State.CreateUnit(Side.Player, C(3, 3), null, 2.0));
    }

    [Fact]
    public void Roster_version_changes_with_every_state_change()
    {
        var v0 = _sim.State.Roster.Version;
        _sim.BeginRosterPlacement(5);
        var v1 = _sim.State.Roster.Version;
        _sim.PlaceFromRoster(5, C(0, 4));
        var v2 = _sim.State.Roster.Version;
        Assert.True(v1 > v0);
        Assert.True(v2 > v1);
    }
}
