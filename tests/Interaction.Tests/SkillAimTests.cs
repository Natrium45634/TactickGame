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

/// <summary>Aiming an immediate skill use (spec part 3 §5.2).</summary>
public class SkillAimTests
{
    private readonly HexLayout _layout = new(1.0);
    private readonly GameSimulation _sim = new(TestMaps.Empty());
    private readonly InputStateMachine _fsm;
    private readonly List<InteractionFeedback> _feedback = new();
    private readonly UnitId _unit;

    public SkillAimTests()
    {
        _fsm = new InputStateMachine(_sim, _layout);
        _fsm.Feedback += _feedback.Add;
        _unit = _sim.Spawn(SpawnRequest.AtCell(C(5, 5), Side.Player, HexDirection.North)).Unit!.Value;
    }

    private static HexCoord C(int col, int row) => HexCoord.FromOffset(col, row);

    private PointerSample AtPlane(PlaneVector p) => new(new ScreenPoint(p.X * 100, p.Z * 100), p, _layout.PlaneToHex(p), null, null, p);

    private PointerSample Toward(HexDirection d, double distance) => AtPlane(_layout.HexToPlane(C(5, 5)) + (HexLayout.DirectionVector(d) * distance));

    private void Click(PointerSample s, PointerButton b = PointerButton.Primary)
    {
        _fsm.PointerMove(s);
        _fsm.PointerDown(b, s);
        _fsm.PointerUp(b, s);
    }

    [Fact]
    public void Aiming_needs_a_selected_unit_and_previews_exactly_the_two_hexes_of_the_pointed_direction()
    {
        Assert.False(_fsm.BeginSkillAim(SkillCatalog.LinearStrikeId));
        _fsm.Select(_unit);
        Assert.True(_fsm.BeginSkillAim(SkillCatalog.LinearStrikeId));
        Assert.Equal(InputMode.SkillAim, _fsm.Mode);

        foreach (var d in HexDirections.All)
        {
            _fsm.PointerMove(Toward(d, 2.5)); // anywhere in that direction, not only in the unit's hex
            Assert.Equal(d, _fsm.SkillAim!.Direction);
            Assert.Equal(new[] { C(5, 5).Neighbor(d), C(5, 5).Neighbor(d).Neighbor(d) }, _fsm.SkillAim.Area);
        }

        _fsm.PointerMove(Toward(HexDirection.North, 0.1)); // neutral centre
        Assert.Null(_fsm.SkillAim!.Direction);
        Assert.Empty(_fsm.SkillAim.Area);
        Click(Toward(HexDirection.North, 0.1));
        Assert.Equal(InputMode.SkillAim, _fsm.Mode); // the centre never confirms
    }

    [Fact]
    public void Click_confirms_the_direction_and_plans_the_skill_without_firing_on_pause()
    {
        var enemy = _sim.Spawn(SpawnRequest.AtCell(C(5, 4), Side.Enemy, HexDirection.South)).Unit!.Value;
        _sim.SetPaused(true);
        _fsm.Select(_unit);
        _fsm.BeginSkillAim(SkillCatalog.LinearStrikeId);
        Click(Toward(HexDirection.North, 1.5));

        Assert.Equal(InputMode.UnitSelected, _fsm.Mode);
        Assert.Contains(_feedback, f => f is SkillUsePlanned { Direction: HexDirection.North });
        var pending = _sim.State.GetUnit(_unit).PendingSkill!;
        Assert.Equal(SkillActionStatus.Planned, pending.Status);
        _sim.Advance(3);
        Assert.Equal(100, _sim.State.GetUnit(enemy).CurrentHealth); // P21: planning on pause deals no damage

        _sim.SetPaused(false);
        _sim.Advance(1.0 / 30);
        Assert.Equal(80, _sim.State.GetUnit(enemy).CurrentHealth);
    }

    [Fact]
    public void Rmb_or_Esc_cancel_only_the_aiming_and_keep_the_route()
    {
        Assert.True(_sim.ApplyOrder(new IndividualOrder(_unit, new[] { C(8, 5) }, HexDirection.North)).Success);
        _fsm.Select(_unit);
        _fsm.BeginSkillAim(SkillCatalog.LinearStrikeId);
        _fsm.PointerMove(Toward(HexDirection.South, 2));
        _fsm.PointerDown(PointerButton.Secondary, Toward(HexDirection.South, 2));

        Assert.Equal(InputMode.UnitSelected, _fsm.Mode);
        Assert.Null(_fsm.SkillAim);
        Assert.Contains(_feedback, f => f is SkillAimCancelled);
        Assert.Null(_sim.State.GetUnit(_unit).PendingSkill);
        Assert.Equal(C(8, 5), _sim.State.GetUnit(_unit).Orders.Items.Single().Cell);

        _fsm.BeginSkillAim(SkillCatalog.LinearStrikeId);
        _fsm.Cancel();
        Assert.Equal(InputMode.UnitSelected, _fsm.Mode);
        Assert.Equal(_unit, _fsm.SelectedUnit);
    }

    [Fact]
    public void Other_tools_end_the_aiming()
    {
        _fsm.Select(_unit);
        _fsm.BeginSkillAim(SkillCatalog.LinearStrikeId);
        _fsm.ToggleGroupMode();
        Assert.Null(_fsm.SkillAim);
        Assert.Equal(InputMode.GroupCollect, _fsm.Mode);
    }
}
