using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Hex;
using HexTactics.Core.Orders;
using HexTactics.Core.Skills;
using HexTactics.Core.Spawning;
using HexTactics.Core.Units;
using HexTactics.Interaction;
using HexTactics.Simulation;
using HexTactics.Tests.Shared;
using Xunit;

namespace HexTactics.Interaction.Tests;

/// <summary>Planning a skill at a waypoint with clicks (spec part 3 §6; acceptance 15, 16, 19, 21).</summary>
public class WaypointSkillInputTests
{
    private readonly HexLayout _layout = new(1.0);
    private readonly GameSimulation _sim = new(TestMaps.Empty());
    private readonly InputStateMachine _fsm;
    private readonly List<InteractionFeedback> _feedback = new();
    private readonly UnitId _unit;
    private readonly UnitId _other;

    public WaypointSkillInputTests()
    {
        _fsm = new InputStateMachine(_sim, _layout);
        _fsm.Feedback += _feedback.Add;
        _unit = _sim.Spawn(SpawnRequest.AtCell(C(1, 5), Side.Player, HexDirection.North)).Unit!.Value;
        _other = _sim.Spawn(SpawnRequest.AtCell(C(8, 8), Side.Player, HexDirection.North)).Unit!.Value;
        Assert.True(_sim.ApplyOrder(new IndividualOrder(_other, new[] { C(9, 8) }, HexDirection.North)).Success);
    }

    private static HexCoord C(int col, int row) => HexCoord.FromOffset(col, row);

    private UnitState U => _sim.State.GetUnit(_unit);

    private PointerSample AtPlane(PlaneVector p, int? waypoint = null) =>
        new(new ScreenPoint(p.X * 100, p.Z * 100), p, _layout.PlaneToHex(p), _sim.State.UnitAt(_layout.PlaneToHex(p))?.Id, waypoint, p);

    private PointerSample AtHex(HexCoord h, int? waypoint = null) => AtPlane(_layout.HexToPlane(h), waypoint);

    private PointerSample Off(HexCoord h, HexDirection d, double distance) => AtPlane(_layout.HexToPlane(h) + (HexLayout.DirectionVector(d) * distance));

    private void Click(PointerSample s, PointerButton b = PointerButton.Primary)
    {
        _fsm.PointerMove(s);
        _fsm.PointerDown(b, s);
        _fsm.PointerUp(b, s);
    }

    private void DraftOfThree()
    {
        _fsm.PointerMove(AtHex(C(1, 5)));
        _fsm.PointerDown(PointerButton.Primary, AtHex(C(1, 5)));
        _fsm.PointerMove(AtHex(C(3, 3)));
        _fsm.PointerUp(PointerButton.Primary, AtHex(C(3, 3)));
        Click(AtHex(C(5, 1)));
        Click(AtHex(C(7, 1))); // continuous laying: ТД2, ТД3 without re-grabbing (P16)
        Assert.Equal(new[] { C(3, 3), C(5, 1), C(7, 1) }, _fsm.Draft!.Waypoints);
    }

    [Fact]
    public void P15_short_click_on_a_draft_waypoint_marker_opens_its_menu_the_icon_aims_from_that_waypoint()
    {
        DraftOfThree();
        Click(AtHex(C(5, 1))); // short click on the central marker of ТД2

        Assert.Equal(3, _fsm.Draft!.Waypoints.Count); // P16: no new waypoint
        var menu = _fsm.WaypointMenu!;
        Assert.Equal(2, menu.Number);
        Assert.Equal(1, menu.DraftIndex);

        Assert.True(_fsm.BeginWaypointSkillAim(SkillCatalog.LinearStrikeId)); // the icon in the menu
        Assert.Null(_fsm.WaypointMenu);
        Assert.Equal(InputMode.SkillAim, _fsm.Mode);
        _fsm.PointerMove(Off(C(5, 1), HexDirection.NorthWest, 1.6));
        var aim = _fsm.SkillAim!;
        Assert.True(aim.FromWaypoint);
        Assert.Equal(C(5, 1), aim.Origin); // the preview is computed from the waypoint, not from the unit
        Assert.Equal(new[] { C(5, 1).Neighbor(HexDirection.NorthWest), C(5, 1).Neighbor(HexDirection.NorthWest).Neighbor(HexDirection.NorthWest) }, aim.Area);

        Click(Off(C(5, 1), HexDirection.NorthWest, 1.6));
        Assert.Equal(InputMode.SingleDraft, _fsm.Mode); // back to the draft
        Assert.Equal(HexDirection.NorthWest, _fsm.Draft!.Skills![1].Direction);
        Assert.Contains(_feedback, f => f is WaypointSkillPlanned { Number: 2, InDraft: true });
        Assert.True(U.Orders.IsEmpty); // nothing applied yet: part of the draft transaction

        // The edge sector of the last waypoint still sets the final facing — and applies the skill with the order.
        Click(Off(C(7, 1), HexDirection.South, 0.6));
        Assert.Null(_fsm.Draft);
        Assert.Equal(3, U.Orders.Count);
        Assert.Equal(HexDirection.NorthWest, U.Orders.Items[1].Skill!.Direction);
        Assert.Equal(HexDirection.South, U.Orders.FinalFacing);
    }

    [Fact]
    public void P16_a_drag_from_a_draft_waypoint_lays_a_waypoint_at_the_release_and_opens_no_menu()
    {
        DraftOfThree();
        _fsm.PointerMove(AtHex(C(5, 1)));
        _fsm.PointerDown(PointerButton.Primary, AtHex(C(5, 1)));
        _fsm.PointerMove(AtHex(C(8, 4)));
        _fsm.PointerUp(PointerButton.Primary, AtHex(C(8, 4)));
        Assert.Null(_fsm.WaypointMenu);
        Assert.Equal(C(8, 4), _fsm.Draft!.Waypoints[^1]);
    }

    [Fact]
    public void P19_RMB_in_the_red_arrow_mode_cancels_only_the_skill_preview_the_next_RMB_the_draft()
    {
        DraftOfThree();
        Click(AtHex(C(3, 3)));
        _fsm.BeginWaypointSkillAim(SkillCatalog.LinearStrikeId);
        _fsm.PointerMove(Off(C(3, 3), HexDirection.North, 1.5));
        _fsm.PointerDown(PointerButton.Secondary, Off(C(3, 3), HexDirection.North, 1.5));

        Assert.Equal(InputMode.SingleDraft, _fsm.Mode);
        Assert.Equal(3, _fsm.Draft!.Waypoints.Count);
        Assert.Empty(_fsm.Draft.Skills!);

        _fsm.PointerDown(PointerButton.Secondary, AtHex(C(10, 8)));
        Assert.Null(_fsm.Draft);
        Assert.Equal(C(9, 8), _sim.State.GetUnit(_other).Orders.Items.Single().Cell); // other units untouched
    }

    [Fact]
    public void Removing_a_draft_waypoint_removes_its_skill_skills_on_earlier_waypoints_stay()
    {
        DraftOfThree();
        foreach (var (cell, index) in new[] { (C(3, 3), 0), (C(7, 1), 2) })
        {
            Click(AtHex(cell));
            _fsm.BeginWaypointSkillAim(SkillCatalog.LinearStrikeId);
            Click(Off(cell, HexDirection.South, 1.5));
            Assert.True(_fsm.Draft!.Skills!.ContainsKey(index));
        }

        _fsm.PointerDown(PointerButton.Secondary, AtHex(C(7, 1))); // RMB on ТД3: remove it (and its skill)
        Assert.Equal(2, _fsm.Draft!.Waypoints.Count);
        Assert.Equal(new[] { 0 }, _fsm.Draft.Skills!.Keys);
    }

    [Fact]
    public void Open_menu_clicks_elsewhere_close_it_without_a_waypoint_RMB_and_Esc_close_only_the_menu()
    {
        DraftOfThree();
        Click(AtHex(C(5, 1)));
        Click(AtHex(C(9, 3))); // a green hex: only closes the menu
        Assert.Null(_fsm.WaypointMenu);
        Assert.Equal(3, _fsm.Draft!.Waypoints.Count);

        Click(AtHex(C(5, 1)));
        _fsm.PointerDown(PointerButton.Secondary, AtHex(C(9, 3)));
        Assert.Null(_fsm.WaypointMenu);
        Assert.NotNull(_fsm.Draft);

        Click(AtHex(C(5, 1)));
        _fsm.Cancel();
        Assert.Null(_fsm.WaypointMenu);
        Assert.NotNull(_fsm.Draft);
    }

    [Fact]
    public void Confirmed_route_waypoint_menu_assigns_on_confirmation_only_and_can_clear_the_skill()
    {
        Assert.True(_sim.ApplyOrder(new IndividualOrder(_unit, new[] { C(3, 5), C(5, 5) }, HexDirection.North)).Success);
        _fsm.Select(_unit);
        var wp = U.Orders.Items[0];
        Click(AtHex(C(3, 5), wp.Id)); // short click on the confirmed ТД1 marker
        Assert.Equal(wp.Id, _fsm.WaypointMenu!.WaypointId);

        // Cancelling the aiming leaves the route as it was (transactional edit, spec part 3 §6.2).
        _fsm.BeginWaypointSkillAim(SkillCatalog.LinearStrikeId);
        _fsm.Cancel();
        Assert.Null(U.Orders.Items[0].Skill);
        Assert.Equal(2, U.Orders.Count);

        Click(AtHex(C(3, 5), wp.Id));
        _fsm.BeginWaypointSkillAim(SkillCatalog.LinearStrikeId);
        Click(Off(C(3, 5), HexDirection.North, 1.5));
        Assert.Equal(HexDirection.North, U.Orders.Items[0].Skill!.Direction);
        Assert.Contains(_feedback, f => f is WaypointSkillPlanned { Number: 1, InDraft: false });

        Click(AtHex(C(3, 5), wp.Id));
        Assert.NotNull(_fsm.WaypointMenu!.Skill);
        _fsm.ClearWaypointSkillFromMenu();
        Assert.Null(U.Orders.Items[0].Skill);
        Assert.Equal(2, U.Orders.Count);
    }

    [Fact]
    public void A_short_click_on_a_command_order_waypoint_opens_no_skill_menu()
    {
        Assert.True(_sim.ApplyGroupOrder(new GroupOrder(_unit, new[] { _unit }, new[] { C(4, 5) }, HexDirection.North)).Success);
        _fsm.Select(_unit);
        var wp = U.Orders.Items[^1];
        Click(AtHex(wp.Cell, wp.Id));
        Assert.Null(_fsm.WaypointMenu);
        Assert.Equal(wp.Id, _fsm.SelectedWaypointId); // still selects the waypoint as before
    }

    [Fact]
    public void P21_assigning_a_skill_on_pause_deals_no_damage_until_resume()
    {
        var enemy = _sim.Spawn(SpawnRequest.AtCell(C(3, 4), Side.Enemy, HexDirection.South)).Unit!.Value;
        _sim.SetPaused(true);
        Assert.True(_sim.ApplyOrder(new IndividualOrder(_unit, new[] { C(3, 5) }, HexDirection.North)).Success);
        _fsm.Select(_unit);
        Click(AtHex(C(3, 5), U.Orders.Items[0].Id));
        _fsm.BeginWaypointSkillAim(SkillCatalog.LinearStrikeId);
        Click(Off(C(3, 5), HexDirection.North, 1.5));
        _sim.Advance(5);
        Assert.Equal(100, _sim.State.GetUnit(enemy).CurrentHealth);

        _sim.SetPaused(false);
        for (var i = 0; i < 150; i++)
        {
            _sim.Advance(1.0 / 30);
        }

        Assert.Equal(80, _sim.State.GetUnit(enemy).CurrentHealth);
    }
}
