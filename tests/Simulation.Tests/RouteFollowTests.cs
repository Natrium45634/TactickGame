using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Combat;
using HexTactics.Core.Hex;
using HexTactics.Core.Map;
using HexTactics.Core.Orders;
using HexTactics.Core.Spawning;
using HexTactics.Core.Units;
using HexTactics.Tests.Shared;
using Xunit;

namespace HexTactics.Simulation.Tests;

/// <summary>Walking an enemy route exactly (spec part 7 §4.4, §11.2; acceptance M7-09, M7-10, M7-16).</summary>
public class RouteFollowTests
{
    private static HexCoord C(int col, int row) => HexCoord.FromOffset(col, row);

    /// <summary>An open 12×10 map with the given routes; bases are the first and last visits of each route.</summary>
    private static GameSimulation Sim(params EnemyRoute[] routes)
    {
        var open = TestMaps.Empty();
        var bases = new List<MapBase>();
        foreach (var r in routes)
        {
            bases.Add(new MapBase(BaseSide.Enemy, r.SourceBase, "Враги", new[] { r.Cells[0] }));
            bases.Add(new MapBase(BaseSide.Player, r.TargetBase, "База", new[] { r.Cells[^1] }));
        }

        var map = new MapDefinition(open.Name, open.Grid, open.SpawnZones, open.PlayerBase) { Bases = bases, Routes = routes };
        return new GameSimulation(map);
    }

    /// <summary>A path from an offset cell by hex steps (N, NE, SE, S, SW, NW): every visit is next to the previous one.</summary>
    private static EnemyRoute Route(string id, int source, int target, (int Col, int Row) start, params HexDirection[] steps)
    {
        var cells = new List<HexCoord> { C(start.Col, start.Row) };
        foreach (var d in steps)
        {
            cells.Add(cells[^1].Neighbor(d));
        }

        return new EnemyRoute(id, id, source, target, cells);
    }

    private const HexDirection N = HexDirection.North, NE = HexDirection.NorthEast, SE = HexDirection.SouthEast,
        S = HexDirection.South, SW = HexDirection.SouthWest, NW = HexDirection.NorthWest;

    /// <summary>Runs the simulation, records the hexes each unit entered and checks one-unit-per-hex every tick.</summary>
    private static Dictionary<UnitId, List<HexCoord>> Run(GameSimulation sim, int ticks, System.Action<int>? each = null)
    {
        var visits = new Dictionary<UnitId, List<HexCoord>>();
        for (var i = 0; i < ticks; i++)
        {
            sim.Step();
            each?.Invoke(i);
            foreach (var e in sim.DrainEvents().OfType<UnitEnteredCell>())
            {
                if (!visits.TryGetValue(e.Unit, out var list))
                {
                    visits[e.Unit] = list = new List<HexCoord>();
                }

                list.Add(e.To);
            }

            var cells = sim.State.Units.Where(u => u.IsAlive).Select(u => u.Cell).ToList();
            Assert.Equal(cells.Count, cells.Distinct().Count());
        }

        return visits;
    }

    [Fact]
    public void M7_10_a_repeated_hex_keeps_both_visits_and_the_route_ends_on_its_base()
    {
        // Out, a loop around (5,4) through (5,3) twice, and down to the base.
        var loop = Route("r1", 1, 1, (5, 1), S, S, SE, S, SW, NW, N, NE, S, S, S, S); // (5,3) and the hex below it are visited twice
        var sim = Sim(loop);
        var walker = sim.SpawnOnRoute(loop).Unit!.Value;
        Assert.Equal(HexDirection.South, sim.State.GetUnit(walker).Facing); // looks at its second visit
        var finished = new List<RouteFinished>();
        var visits = Run(sim, 600);
        Assert.Equal(2, loop.Cells.Count(c => c == loop.Cells[2]));
        Assert.Equal(loop.Cells.Skip(1), visits[walker]); // both visits, in order
        Assert.Equal(loop.Cells[^1], sim.State.GetUnit(walker).Cell);
        Assert.True(sim.State.GetUnit(walker).Route!.Finished);
        Assert.Equal(MovementStatus.Idle, sim.State.GetUnit(walker).Motion.Status);
    }

    [Fact]
    public void M7_09_crossing_routes_never_swap_lines()
    {
        var down = Route("down", 1, 1, (5, 0), S, S, S, S, S, S);
        var across = Route("across", 2, 2, (2, 3), SE, NE, SE, NE, SE, NE, SE); // crosses the column of «down»
        Assert.NotEmpty(down.Cells.Intersect(across.Cells));
        var sim = Sim(down, across);
        var a = sim.SpawnOnRoute(down).Unit!.Value;
        var b = sim.SpawnOnRoute(across).Unit!.Value;
        var visits = Run(sim, 900);
        Assert.Equal(down.Cells.Skip(1), visits[a]);
        Assert.Equal(across.Cells.Skip(1), visits[b]); // both went through (5,3), each stayed on its own line
    }

    [Fact]
    public void A_taken_next_visit_makes_the_walker_wait_without_a_detour_and_go_on_from_the_same_index()
    {
        var line = Route("r1", 1, 1, (3, 4), S, S, S, S);
        var sim = Sim(line);
        var stone = sim.Spawn(SpawnRequest.AtCell(line.Cells[2], Side.Player, HexDirection.North)).Unit!.Value; // stands on visit 2
        var walker = sim.SpawnOnRoute(line).Unit!.Value;
        Run(sim, 120);
        var w = sim.State.GetUnit(walker);
        Assert.Equal(line.Cells[1], w.Cell);
        Assert.Equal((MovementStatus.Waiting, WaitReason.RouteAhead, 1), (w.Motion.Status, w.Motion.WaitReason, w.Route!.Index)); // no way round

        sim.State.ApplyDamage(stone, 100000); // the visit becomes free
        var visits = Run(sim, 300);
        Assert.Equal(line.Cells.Skip(2), visits[walker]); // on from the same index, nothing skipped or repeated
    }

    [Fact]
    public void A_route_walker_is_held_by_a_block_and_goes_on_along_its_route_after_release()
    {
        var line = Route("r1", 1, 1, (2, 1), S, S, S, S, S, S);
        var sim = Sim(line);
        var guardCell = line.Cells[3].Neighbor(HexDirection.NorthEast);
        var guard = sim.Spawn(SpawnRequest.AtCell(guardCell, Side.Player, HexDirection.South) with
        {
            Profile = new UnitProfile("g", "Страж", new UnitStats { MaxHealth = 1000, BlockCapacity = 1 }, System.Array.Empty<string>()),
        }).Unit!.Value;
        var walker = sim.SpawnOnRoute(line).Unit!.Value;
        Run(sim, 200);
        Assert.Single(sim.State.BlockLinks); // a route walker means to move, so it is held
        var held = sim.State.GetUnit(walker);
        Assert.Equal(1, held.Cell.DistanceTo(guardCell));
        var index = held.Route!.Index;

        Assert.True(sim.ApplyOrder(new IndividualOrder(guard, new[] { C(9, 1) }, HexDirection.North)).Success); // the guard leaves
        var visits = Run(sim, 400);
        Assert.Empty(sim.State.BlockLinks);
        Assert.Equal(line.Cells.Skip(index + 1), visits[walker]);
        Assert.Equal(line.Cells[^1], sim.State.GetUnit(walker).Cell);
    }

    [Fact]
    public void M7_16_the_smooth_position_never_leaves_the_hexes_of_the_route()
    {
        var bend = Route("bend", 1, 1, (2, 2), SE, NE, SE, S, SW, S, SE, NE, SE, S);
        var sim = Sim(bend);
        var walker = sim.SpawnOnRoute(bend).Unit!.Value;
        var allowed = bend.Cells.ToHashSet();
        Run(sim, 700, _ =>
        {
            if (sim.State.Units.FirstOrDefault(u => u.Id == walker)?.Motion.Position is { } p)
            {
                Assert.Contains(sim.Layout.PlaneToHex(p), allowed); // the drawn body is always over a hex of the route
            }
        });
        Assert.Equal(bend.Cells[^1], sim.State.GetUnit(walker).Cell);
    }

    [Fact]
    public void An_explicit_order_takes_the_unit_off_its_route()
    {
        var line = Route("r1", 1, 1, (3, 4), SE, SE, SE);
        var sim = Sim(line);
        var walker = sim.SpawnOnRoute(line).Unit!.Value;
        Assert.True(sim.ApplyOrder(new IndividualOrder(walker, new[] { C(3, 7) }, HexDirection.South)).Success);
        Run(sim, 200);
        Assert.Equal(C(3, 7), sim.State.GetUnit(walker).Cell);
        Assert.Null(sim.State.GetUnit(walker).Route); // it does not jump back onto the line
    }
}
