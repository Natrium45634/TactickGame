using System.Collections.Generic;
using HexTactics.Core.Hex;
using HexTactics.Core.Spawning;
using HexTactics.Core.Units;
using HexTactics.Interaction;
using HexTactics.Simulation;
using HexTactics.Tests.Shared;
using Xunit;

namespace HexTactics.Interaction.Tests;

public class SelectionAndPlacementTests
{
    private readonly GameSimulation _sim = new(TestMaps.LoadSmall());
    private readonly InputStateMachine _fsm;
    private readonly List<InteractionFeedback> _feedback = new();

    public SelectionAndPlacementTests()
    {
        _fsm = new InputStateMachine(_sim, new HexLayout(1.0));
        _fsm.Feedback += f => _feedback.Add(f);
    }

    private static PointerSample At(int col, int row, UnitId? unit = null, double sx = 100, double sy = 100) =>
        new(new ScreenPoint(sx, sy), new PlaneVector(0, 0), HexCoord.FromOffset(col, row), unit);

    private void Click(PointerSample s, PointerButton b = PointerButton.Primary)
    {
        _fsm.PointerDown(b, s);
        _fsm.PointerUp(b, s);
    }

    [Fact]
    public void Click_on_unit_selects_it_and_click_on_empty_cell_deselects()
    {
        var id = _sim.Spawn(SpawnRequest.AtCell(HexCoord.FromOffset(3, 2))).Unit!.Value;
        Click(At(3, 2, id));
        Assert.Equal(InputMode.UnitSelected, _fsm.Mode);
        Assert.Equal(id, _fsm.SelectedUnit);

        Click(At(5, 3));
        Assert.Equal(InputMode.Idle, _fsm.Mode);
        Assert.Null(_fsm.SelectedUnit);
    }

    [Fact]
    public void Click_on_occupied_hex_without_body_hit_selects_occupant()
    {
        var id = _sim.Spawn(SpawnRequest.AtCell(HexCoord.FromOffset(3, 2))).Unit!.Value;
        Click(At(3, 2));
        Assert.Equal(id, _fsm.SelectedUnit);
    }

    [Fact]
    public void Small_hand_jitter_is_still_a_click()
    {
        var id = _sim.Spawn(SpawnRequest.AtCell(HexCoord.FromOffset(3, 2))).Unit!.Value;
        _fsm.PointerDown(PointerButton.Primary, At(3, 2, id, 100, 100));
        _fsm.PointerUp(PointerButton.Primary, At(3, 2, id, 104, 103));
        Assert.Equal(id, _fsm.SelectedUnit);
    }

    [Fact]
    public void Click_outside_map_deselects_without_errors()
    {
        var id = _sim.Spawn(SpawnRequest.AtCell(HexCoord.FromOffset(3, 2))).Unit!.Value;
        _fsm.Select(id);
        Click(At(40, -7));
        Assert.Null(_fsm.SelectedUnit);
    }

    [Fact]
    public void Placement_mode_places_unit_on_clicked_valid_cell_and_selects_it()
    {
        _fsm.EnterPlacementMode();
        Assert.Equal(InputMode.Placement, _fsm.Mode);
        Click(At(4, 3));
        Assert.Equal(1, _sim.State.UnitCount);
        var unit = _sim.State.UnitAt(HexCoord.FromOffset(4, 3));
        Assert.NotNull(unit);
        Assert.Equal(unit!.Id, _fsm.SelectedUnit);
        Assert.Equal(InputMode.UnitSelected, _fsm.Mode);
        Assert.Contains(_feedback, f => f is SpawnAttempted { Result.Success: true });
    }

    [Fact]
    public void Placement_on_invalid_cell_reports_error_and_stays_in_mode()
    {
        _sim.Spawn(SpawnRequest.AtCell(HexCoord.FromOffset(4, 3)));
        _fsm.EnterPlacementMode();
        Click(At(4, 3));  // occupied
        Click(At(2, 1));  // blocked
        Click(At(30, 30)); // outside
        Assert.Equal(InputMode.Placement, _fsm.Mode);
        Assert.Equal(1, _sim.State.UnitCount);
        var errors = _feedback.FindAll(f => f is SpawnAttempted).ConvertAll(f => ((SpawnAttempted)f).Result.Error);
        Assert.Equal(new[] { SpawnError.Occupied, SpawnError.NotWalkable, SpawnError.OutOfBounds }, errors);
    }

    [Fact]
    public void Escape_or_right_click_leaves_placement_mode_without_placing()
    {
        _fsm.EnterPlacementMode();
        _fsm.Cancel();
        Assert.Equal(InputMode.Idle, _fsm.Mode);

        _fsm.EnterPlacementMode();
        Click(At(4, 3), PointerButton.Secondary);
        Assert.Equal(InputMode.Idle, _fsm.Mode);
        Assert.Equal(0, _sim.State.UnitCount);
    }

    [Fact]
    public void Leaving_placement_mode_keeps_previous_selection()
    {
        var id = _sim.Spawn(SpawnRequest.AtCell(HexCoord.FromOffset(3, 2))).Unit!.Value;
        _fsm.Select(id);
        _fsm.EnterPlacementMode();
        _fsm.Cancel();
        Assert.Equal(InputMode.UnitSelected, _fsm.Mode);
        Assert.Equal(id, _fsm.SelectedUnit);
    }
}
