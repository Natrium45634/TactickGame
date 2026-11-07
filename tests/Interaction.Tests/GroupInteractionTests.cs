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

/// <summary>The "Команда" tool (spec part 2 §6, §8, §11; acceptance P10, P11, P12, P14, P15, P16).</summary>
public class GroupInteractionTests
{
    private readonly HexLayout _layout = new(1.0);
    private readonly GameSimulation _sim = new(TestMaps.LoadPrototype());
    private readonly InputStateMachine _fsm;
    private readonly List<InteractionFeedback> _feedback = new();
    private readonly UnitId _a;
    private readonly UnitId _b;
    private readonly UnitId _c;

    public GroupInteractionTests()
    {
        _fsm = new InputStateMachine(_sim, _layout);
        _fsm.Feedback += _feedback.Add;
        _a = Spawn(2, 6);
        _b = Spawn(3, 6);
        _c = Spawn(1, 7);
    }

    private static HexCoord C(int col, int row) => HexCoord.FromOffset(col, row);

    private UnitId Spawn(int col, int row, Side side = Side.Player) =>
        _sim.Spawn(SpawnRequest.AtCell(C(col, row), side, HexDirection.North)).Unit!.Value;

    private UnitState U(UnitId id) => _sim.State.GetUnit(id);

    private PointerSample AtPlane(PlaneVector p) =>
        new(new ScreenPoint(p.X * 100, p.Z * 100), p, _layout.PlaneToHex(p), _sim.State.UnitAt(_layout.PlaneToHex(p))?.Id);

    private PointerSample AtHex(HexCoord h) => AtPlane(_layout.HexToPlane(h));

    private PointerSample AtUnit(UnitId id) => AtHex(U(id).Cell);

    private PointerSample Sector(HexCoord h, HexDirection dir) => AtPlane(_layout.HexToPlane(h) + (HexLayout.DirectionVector(dir) * 0.6));

    private void Click(PointerSample s, PointerButton b = PointerButton.Primary)
    {
        _fsm.PointerMove(s);
        _fsm.PointerDown(b, s);
        _fsm.PointerUp(b, s);
    }

    private void Collect(params UnitId[] units)
    {
        _fsm.ToggleGroupMode();
        foreach (var u in units)
        {
            Click(AtUnit(u));
        }
    }

    [Fact]
    public void Command_mode_is_an_explicit_tool_selecting_units_does_not_start_it()
    {
        Click(AtUnit(_a));
        Click(AtUnit(_b));
        Assert.Equal(InputMode.UnitSelected, _fsm.Mode);
        Assert.Null(_fsm.Group);

        _fsm.ToggleGroupMode();
        Assert.Equal(InputMode.GroupCollect, _fsm.Mode);
        Assert.Null(_fsm.SelectedUnit);
        Assert.Contains(_feedback, f => f is GroupModeChanged { Active: true });

        _fsm.PointerMove(AtHex(C(4, 1)));
        Assert.Equal(GroupTargetStatus.NeedCaptain, _fsm.Group!.Status);
        Click(AtHex(C(4, 1)));
        Assert.Equal(InputMode.GroupCollect, _fsm.Mode);
        Assert.IsType<GroupNeedsCaptain>(_feedback.Last());
    }

    [Fact]
    public void P10_first_clicked_ally_is_the_captain_and_every_member_gets_an_arrow()
    {
        Collect(_a);
        Assert.Equal(new[] { _a }, _fsm.Group!.Members);
        Assert.Equal(_a, _fsm.Group.Captain);
        Assert.Contains(_feedback, f => f is GroupMemberAdded { IsCaptain: true });

        _fsm.PointerMove(AtUnit(_b));
        Assert.Equal(GroupTargetStatus.AddMember, _fsm.Group!.Status);
        Click(AtUnit(_b));
        Click(AtUnit(_c));
        Assert.Equal(new[] { _a, _b, _c }, _fsm.Group!.Members);
        Assert.Equal(3, _fsm.Group.Anchors.Count);
        Assert.Equal(3, _fsm.Group.Sources.Count);

        // No duplicates, the captain never changes.
        Click(AtUnit(_a));
        Click(AtUnit(_c));
        Assert.Equal(new[] { _a, _b, _c }, _fsm.Group!.Members);
        Assert.Equal(_a, _fsm.Group.Captain);
        Assert.Contains(_feedback, f => f is GroupMemberRefused { Reason: GroupTargetStatus.AlreadyMember });
    }

    [Fact]
    public void P11_P12_click_on_free_hex_becomes_KTD1_and_the_chain_continues_without_regrab()
    {
        Collect(_a, _b, _c);
        _fsm.PointerMove(AtHex(C(3, 2)));
        var preview = _fsm.Group!.Preview!;
        Assert.Equal(GroupTargetStatus.Valid, _fsm.Group.Status);
        Assert.Equal(C(3, 2), preview.DestinationOf(_a));
        Assert.NotEqual(preview.DestinationOf(_b), preview.DestinationOf(_c));

        Click(AtHex(C(3, 2)));
        Assert.Equal(InputMode.GroupDraft, _fsm.Mode);
        Assert.Equal(new[] { C(3, 2) }, _fsm.Group!.Waypoints);
        Assert.Single(_fsm.Group.Stages);
        Assert.Equal(_fsm.Group.Stages[0].DestinationOf(_b), _fsm.Group.Sources[1]); // next stage starts from the slot

        Click(AtHex(C(6, 0)));
        Assert.Equal(new[] { C(3, 2), C(6, 0) }, _fsm.Group!.Waypoints);

        _fsm.PointerMove(Sector(C(6, 0), HexDirection.SouthEast));
        Assert.Equal(GroupTargetStatus.FacingSector, _fsm.Group!.Status);
        Click(Sector(C(6, 0), HexDirection.SouthEast));

        Assert.Null(_fsm.Group);
        Assert.Equal(InputMode.UnitSelected, _fsm.Mode);
        Assert.Equal(_a, _fsm.SelectedUnit);
        var committed = _feedback.OfType<GroupCommitted>().Single();
        Assert.Equal(new[] { _a, _b, _c }, committed.Order.Members);
        var group = _sim.State.FindGroup(committed.GroupId)!;
        Assert.Equal(new[] { C(3, 2), C(6, 0) }, U(_a).Orders.Items.Select(w => w.Cell));
        Assert.All(group.Members, m => Assert.Equal(2, U(m).Orders.Count));
        Assert.Equal(HexDirection.SouthEast, U(_a).Orders.FinalFacing);
    }

    [Fact]
    public void P13_facing_preview_shows_exactly_the_companion_facings_that_get_stored()
    {
        Collect(_a, _b, _c);
        Click(AtHex(C(3, 2)));
        _fsm.PointerMove(Sector(C(3, 2), HexDirection.North));
        var preview = _fsm.Group!.FacingPreview!;
        Assert.Equal(HexDirection.North, preview[_a]);
        _fsm.PointerMove(Sector(C(3, 2), HexDirection.South));
        _fsm.PointerMove(Sector(C(3, 2), HexDirection.North));
        Assert.Equal(preview, _fsm.Group!.FacingPreview); // hovering does not re-roll

        Click(Sector(C(3, 2), HexDirection.North));
        var committed = _feedback.OfType<GroupCommitted>().Single();
        var stored = _sim.State.FindGroup(committed.GroupId)!.FinalFacings;
        Assert.Equal(preview.OrderBy(p => p.Key.Value), stored.OrderBy(p => p.Key.Value));
        Assert.All(new[] { _b, _c }, m => Assert.Contains(stored[m], new[] { HexDirection.NorthWest, HexDirection.North, HexDirection.NorthEast }));
    }

    [Fact]
    public void Facing_sector_next_to_a_raised_wall_is_chosen_on_the_ground()
    {
        // (7,2) is a raised wall south of (7,1): on screen it covers the south edge of (7,1). The pick hits the wall top,
        // the ground point lies in (7,1) — the click must choose the south facing, not cancel on the wall.
        Collect(_a, _b);
        Click(AtHex(C(3, 2)));
        Click(AtHex(C(7, 1)));
        var ground = _layout.HexToPlane(C(7, 1)) + (HexLayout.DirectionVector(HexDirection.South) * 0.6);
        var wallTop = _layout.HexToPlane(C(7, 2));
        var sample = new PointerSample(new ScreenPoint(1, 1), wallTop, C(7, 2), null, null, ground);

        _fsm.PointerMove(sample);
        Assert.Equal(GroupTargetStatus.FacingSector, _fsm.Group!.Status);
        Assert.Equal(HexDirection.South, _fsm.Group.SectorFacing);
        _fsm.PointerDown(PointerButton.Primary, sample);
        _fsm.PointerUp(PointerButton.Primary, sample);
        Assert.Single(_feedback.OfType<GroupCommitted>());
        Assert.Equal(HexDirection.South, U(_a).Orders.FinalFacing);
    }

    [Fact]
    public void Centre_of_the_last_KTD_never_confirms()
    {
        Collect(_a, _b);
        Click(AtHex(C(3, 2)));
        Click(AtHex(C(3, 2)));
        Assert.Equal(InputMode.GroupDraft, _fsm.Mode);
        Assert.True(U(_a).Orders.IsEmpty);
    }

    [Fact]
    public void P14_KTD_is_red_when_the_team_does_not_fit_and_a_click_there_cancels()
    {
        var d = Spawn(3, 3);
        var e = Spawn(4, 3);
        Collect(_a, _b, _c, d);
        _fsm.PointerMove(AtHex(C(0, 0))); // corner: only two slots for three companions
        Assert.Equal(GroupTargetStatus.NotEnoughSlots, _fsm.Group!.Status);
        Assert.Equal(GroupPlanError.NotEnoughSlots, _fsm.Group.Preview!.Error);

        // Hovering red does not cancel (P16).
        _fsm.PointerMove(AtHex(C(3, 2)));
        Assert.Equal(GroupTargetStatus.Valid, _fsm.Group!.Status);

        Click(AtHex(C(0, 0)));
        Assert.Null(_fsm.Group);
        Assert.Equal(InputMode.Idle, _fsm.Mode);
        Assert.Contains(_feedback, f => f is GroupCancelled { Reason: DraftCancelReason.InvalidTarget, Status: GroupTargetStatus.NotEnoughSlots });
        Assert.All(new[] { _a, _b, _c, d }, m => Assert.True(U(m).Orders.IsEmpty));
        _ = e;
    }

    [Fact]
    public void A_tile_type_without_waypoints_is_red_for_a_KTD()
    {
        _sim.State.Grid.SetTile(C(3, 2), HexTactics.Core.Map.TileType.Walkable, "test.tile.ford", 0, allowsWaypoints: false);
        Collect(_a, _b);
        _fsm.PointerMove(AtHex(C(3, 2)));
        Assert.Equal(GroupTargetStatus.WaypointsForbidden, _fsm.Group!.Status);
    }

    [Fact]
    public void Unreachable_or_walled_KTD_is_red()
    {
        Collect(_a, _b);
        _fsm.PointerMove(AtHex(C(8, 3)));
        Assert.Equal(GroupTargetStatus.CaptainUnreachable, _fsm.Group!.Status);
        _fsm.PointerMove(AtHex(C(5, 4)));
        Assert.Equal(GroupTargetStatus.NotWalkable, _fsm.Group!.Status);
    }

    [Fact]
    public void P15_RMB_and_Esc_discard_the_command_draft_but_not_earlier_orders()
    {
        Assert.True(_sim.ApplyOrder(new IndividualOrder(_c, new[] { C(1, 9) }, HexDirection.South)).Success);
        Collect(_a, _b);
        Click(AtHex(C(3, 2)));
        _fsm.PointerDown(PointerButton.Secondary, AtHex(C(8, 8)));

        Assert.Null(_fsm.Group);
        Assert.Contains(_feedback, f => f is GroupCancelled { Reason: DraftCancelReason.ByUser });
        Assert.Contains(_feedback, f => f is GroupModeChanged { Active: false });
        Assert.True(U(_a).Orders.IsEmpty);
        Assert.Equal(C(1, 9), U(_c).Orders.Items.Single().Cell);

        Collect(_a);
        _fsm.Cancel();
        Assert.Null(_fsm.Group);
        Assert.Equal(InputMode.Idle, _fsm.Mode);

        Collect(_a);
        _fsm.ToggleGroupMode(); // the button again closes the mode
        Assert.Null(_fsm.Group);
    }

    [Fact]
    public void RMB_on_a_draft_KTD_removes_it_and_the_following_ones()
    {
        Collect(_a, _b);
        Click(AtHex(C(3, 2)));
        Click(AtHex(C(6, 0)));
        Click(AtHex(C(9, 1)));

        _fsm.PointerDown(PointerButton.Secondary, AtHex(C(6, 0)));
        Assert.Equal(new[] { C(3, 2) }, _fsm.Group!.Waypoints);
        Assert.Contains(_feedback, f => f is GroupDraftTruncated { RemovedFromNumber: 2, Remaining: 1 });

        _fsm.PointerDown(PointerButton.Secondary, AtHex(C(3, 2)));
        Assert.Equal(InputMode.GroupCollect, _fsm.Mode);
        Assert.Empty(_fsm.Group!.Waypoints);
        Assert.Equal(new[] { _a, _b }, _fsm.Group.Members); // the team is kept
    }

    [Fact]
    public void Enemy_under_the_cursor_is_a_reserved_attack_target_and_does_not_cancel()
    {
        var enemy = Spawn(4, 4, Side.Enemy);
        Collect(_a);
        _fsm.PointerMove(AtUnit(enemy));
        Assert.Equal(GroupTargetStatus.AttackTarget, _fsm.Group!.Status);
        Click(AtUnit(enemy));
        Assert.Equal(InputMode.GroupCollect, _fsm.Mode);
        Assert.Contains(_feedback, f => f is AttackTargetReserved r && r.Target == enemy);
    }

    [Fact]
    public void Team_size_is_limited_to_seven()
    {
        var extra = new[] { Spawn(0, 8), Spawn(1, 8), Spawn(2, 8), Spawn(3, 8), Spawn(4, 8) };
        Collect(new[] { _a, _b, _c }.Concat(extra.Take(4)).ToArray());
        Assert.Equal(GroupRoutePlanner.MaxMembers, _fsm.Group!.Members.Count);

        Click(AtUnit(extra[4]));
        Assert.Equal(GroupRoutePlanner.MaxMembers, _fsm.Group!.Members.Count);
        Assert.Contains(_feedback, f => f is GroupMemberRefused { Reason: GroupTargetStatus.TeamFull });
    }

    [Fact]
    public void Other_tools_close_the_command_mode()
    {
        Collect(_a, _b);
        _fsm.StartDraft(_c);
        Assert.Null(_fsm.Group);
        Assert.Equal(InputMode.SingleDraft, _fsm.Mode);

        _fsm.Cancel();
        Collect(_a);
        _fsm.TogglePlacementMode();
        Assert.Null(_fsm.Group);
        Assert.Equal(InputMode.Placement, _fsm.Mode);
    }
}
