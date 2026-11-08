using System.Linq;
using HexTactics.Core.Hex;
using HexTactics.Core.Orders;
using HexTactics.Core.Spawning;
using HexTactics.Core.Units;
using HexTactics.Simulation;
using HexTactics.Tests.Shared;
using Xunit;

namespace HexTactics.Simulation.Tests;

public class PauseTests
{
    private static HexCoord C(int col, int row) => HexCoord.FromOffset(col, row);

    private static (GameSimulation Sim, UnitId Unit) MovingUnit()
    {
        var sim = new GameSimulation(TestMaps.Empty());
        var id = sim.Spawn(SpawnRequest.AtCell(C(2, 5))).Unit!.Value;
        sim.IssueMoveOrder(id, C(9, 5), OrderMode.Replace);
        for (var i = 0; i < 40; i++) // 1 1/3 steps at 1 hex/s: mid-step, before the cell switch
        {
            sim.Advance(1.0 / 30);
        }

        return (sim, id);
    }

    [Fact]
    public void Pause_freezes_ticks_game_time_and_movement()
    {
        var (sim, id) = MovingUnit();
        var u = sim.State.GetUnit(id);
        var tick = sim.Tick;
        var cell = u.Cell;
        var progress = u.Motion.Progress;
        sim.SetPaused(true);
        sim.DrainEvents();

        for (var i = 0; i < 300; i++)
        {
            Assert.Equal(0, sim.Advance(1.0 / 60));
        }

        Assert.Equal(tick, sim.Tick);
        Assert.Equal(tick * sim.Settings.TickDuration, sim.GameTime, 9);
        Assert.Equal(cell, u.Cell);
        Assert.Equal(progress, u.Motion.Progress);
        Assert.Empty(sim.DrainEvents());
    }

    [Fact]
    public void Resume_continues_from_saved_state_without_jump()
    {
        var (sim, id) = MovingUnit();
        var u = sim.State.GetUnit(id);
        sim.SetPaused(true);
        var progress = u.Motion.Progress;
        var stepTo = u.Motion.StepTo;
        sim.Advance(10.0); // a long pause: this time must not be caught up
        sim.SetPaused(false);

        Assert.Equal(1, sim.Advance(1.0 / 30));
        Assert.Equal(stepTo, u.Motion.StepTo);
        Assert.Equal(progress + (u.SpeedHexesPerSecond / 30.0), u.Motion.Progress, 9);
    }

    [Fact]
    public void Orders_can_be_changed_during_pause_and_apply_after_resume()
    {
        var (sim, id) = MovingUnit();
        var u = sim.State.GetUnit(id);
        sim.SetPaused(true);
        var stepTo = u.Motion.StepTo!.Value;

        Assert.True(sim.IssueMoveOrder(id, C(4, 9), OrderMode.Replace).Success);
        Assert.True(sim.IssueMoveOrder(id, C(1, 9), OrderMode.Append).Success);
        Assert.Equal(2, u.Orders.Count);
        Assert.True(u.Motion.IsStepping); // frozen mid-step

        sim.SetPaused(false);
        sim.DrainEvents();
        var reached = new System.Collections.Generic.List<HexCoord>();
        HexCoord? firstEntered = null;
        for (var i = 0; i < 2000 && !u.Orders.IsEmpty; i++)
        {
            sim.Advance(1.0 / 30);
            foreach (var e in sim.DrainEvents())
            {
                if (e is UnitEnteredCell entered)
                {
                    firstEntered ??= entered.To;
                }

                if (e is WaypointReached r)
                {
                    reached.Add(r.Waypoint.Cell);
                }
            }
        }

        Assert.Equal(stepTo, firstEntered);
        Assert.Equal(new[] { C(4, 9), C(1, 9) }, reached);
    }

    [Fact]
    public void Pause_events_and_toggle()
    {
        var sim = new GameSimulation(TestMaps.Empty());
        sim.TogglePause();
        sim.SetPaused(true); // no duplicate event
        sim.TogglePause();
        var events = sim.DrainEvents().OfType<PauseChanged>().Select(e => e.Paused).ToArray();
        Assert.Equal(new[] { true, false }, events);
        Assert.False(sim.IsPaused);
    }

    [Fact]
    public void Spawning_works_during_pause()
    {
        var sim = new GameSimulation(TestMaps.LoadPrototype());
        sim.SetPaused(true);
        Assert.True(sim.Spawn(SpawnRequest.AtBase()).Success);
        Assert.True(sim.Spawn(SpawnRequest.AtZone(1)).Success);
    }

    [Fact]
    public void Registered_systems_tick_in_order_and_only_when_running()
    {
        var sim = new GameSimulation(TestMaps.Empty());
        var counter = new CountingSystem();
        sim.AddSystem(counter);
        Assert.Equal(new[] { "Packs", "Movement", "Skills", "Attack", "Tiles", "Statuses", "Counting" }, sim.Systems.Select(s => s.Name)); // spec part 3 §7 order; packs first (part 6 §5.5)

        sim.Advance(0.1); // 3 ticks
        sim.SetPaused(true);
        sim.Advance(1.0);
        Assert.Equal(3, counter.Ticks);
        Assert.Equal(3, counter.LastTick);
    }

    private sealed class CountingSystem : ISimulationSystem
    {
        public int Ticks { get; private set; }

        public long LastTick { get; private set; }

        public string Name => "Counting";

        public void Tick(SimulationContext context)
        {
            Ticks++;
            LastTick = context.Tick;
            Assert.Equal(1.0 / 30, context.DeltaSeconds, 12);
        }
    }
}
