using System;
using System.Linq;
using HexTactics.Content.Kinds;
using HexTactics.Content.Library;
using HexTactics.Content.Registry;
using HexTactics.Content.Runtime;
using HexTactics.Core.Hex;
using HexTactics.Core.Map;
using HexTactics.Editor.Maps;
using HexTactics.Tests.Support;
using Xunit;

namespace HexTactics.Editor.Tests;

/// <summary>«Маршруты врагов» in the map editor (spec part 7 §4.2–4.3; acceptance M7-06…M7-08, M7-10…M7-13).</summary>
public class RouteEditorTests
{
    private static HexCoord C(int col, int row) => HexCoord.FromOffset(col, row);

    /// <summary>
    /// A 10×8 field of ground: enemy bases 1 at (2,0) and 2 at (7,0), player bases 1 at (2,7) and 2 at (7,7), an obstacle at
    /// (4,3) and no hex at (5,3).
    /// </summary>
    private static MapEditorModel Field(ContentLibrary library)
    {
        var item = library.Create(ContentTypeRegistry.Map, "Поле маршрутов");
        var map = (MapData)item.Data;
        map.Columns = 10;
        map.Rows = 8;
        map.Tiles.Clear();
        for (var c = 0; c < 10; c++)
        {
            for (var r = 0; r < 8; r++)
            {
                var tile = (c, r) switch
                {
                    (2, 0) or (7, 0) => "core.tile.enemy_base",
                    (2, 7) or (7, 7) => "core.tile.player_base",
                    (4, 3) => "core.tile.obstacle",
                    (5, 3) => null,
                    _ => "core.tile.ground",
                };
                if (tile is not null)
                {
                    map.Tiles.Add(new MapTileData { Col = c, Row = r, Tile = tile, Base = c == 7 && r is 0 or 7 ? 2 : null });
                }
            }
        }

        Assert.True(library.Save(item).Ok);
        var model = new MapEditorModel(library, library.Find(item.Ref)!);
        model.SetRouteMode(true);
        return model;
    }

    /// <summary>Hovers over a column of hexes, one neighbour at a time.</summary>
    private static bool Walk(MapEditorModel model, int col, int fromRow, int toRow)
    {
        var done = false;
        for (var r = fromRow; r <= toRow && !done; r++)
        {
            done = model.RouteHover(C(col, r));
        }

        return done;
    }

    [Fact]
    public void M7_06_moving_the_cursor_over_neighbouring_hexes_draws_the_route_and_a_player_base_finishes_it_in_one_step()
    {
        using var dir = new TempDirectory();
        var model = Field(ContentTestKit.Library(dir.Path));
        Assert.Equal(RouteClick.DraftStarted, model.RouteClickAt(C(2, 0)));
        Assert.False(Walk(model, 2, 1, 6)); // no click per hex: the cursor only moves
        Assert.Equal(7, model.RouteDraft.Count);
        Assert.True(model.RouteHover(C(2, 7))); // the player base finishes the route
        Assert.False(model.IsDrafting);
        var route = Assert.Single(model.Map.Routes);
        Assert.Equal(("route1", "Маршрут 1", 1, 1), (route.Id, route.Name, route.Source, route.Target));
        Assert.Equal(Enumerable.Range(0, 8).Select(r => (2, r)), route.Cells);
        Assert.Equal("route1", model.SelectedRoute);
        Assert.Equal("Новый маршрут «Маршрут 1»", model.History.UndoDescription);
        Assert.Empty(model.RouteProblems("route1"));
        Assert.True(model.Undo());
        Assert.Empty(model.Map.Routes);
    }

    [Fact]
    public void M7_07_a_route_starts_only_on_an_enemy_base_and_ends_only_on_a_player_base()
    {
        using var dir = new TempDirectory();
        var model = Field(ContentTestKit.Library(dir.Path));
        Assert.Equal(RouteClick.NotABase, model.RouteClickAt(C(3, 3)));    // plain ground
        Assert.Equal(RouteClick.NotABase, model.RouteClickAt(C(2, 7)));    // a player base is an end, not a start
        Assert.False(model.IsDrafting);

        Assert.Equal(RouteClick.DraftStarted, model.RouteClickAt(C(7, 0)));
        Walk(model, 7, 1, 7);
        var route = Assert.Single(model.Map.Routes);
        Assert.Equal((2, 2), (route.Source, route.Target));

        // Data that does not start or end on its bases is reported with the hex.
        var map = RuntimeFactory.BuildMap(model.Item, model.Library, new HexTactics.Content.Model.IssueList());
        var wrong = new EnemyRoute("x", "x", 1, 1, new[] { C(3, 1), C(3, 2) });
        var problems = RouteCheck.Check(map, wrong).Select(p => p.Kind).ToList();
        Assert.Contains(RouteProblemKind.StartNotEnemyBase, problems);
        Assert.Contains(RouteProblemKind.EndNotPlayerBase, problems);
    }

    [Fact]
    public void M7_08_two_enemy_bases_and_two_player_bases_give_four_valid_routes_and_they_are_saved()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var model = Field(library);
        foreach (var (from, to) in new[] { (2, 2), (2, 7), (7, 2), (7, 7) })
        {
            Assert.Equal(RouteClick.DraftStarted, model.RouteClickAt(C(from, 0)));
            Walk(model, from, 1, 5);
            Assert.True(model.RouteHover(C(to, 7))); // a far move fills the straight line, one neighbour at a time
        }

        Assert.Equal(4, model.Map.Routes.Count);
        Assert.Equal(new[] { (1, 1), (1, 2), (2, 1), (2, 2) }, model.Map.Routes.Select(r => (r.Source, r.Target)));
        Assert.All(model.CheckRoutes(), line => Assert.StartsWith("✓", line, StringComparison.Ordinal));
        Assert.True(model.Save().Ok);
        var saved = (MapData)ContentTestKit.Library(dir.Path).Find(model.Item.Ref)!.Data;
        Assert.Equal(4, saved.Routes.Count);
        Assert.All(saved.Routes, r => Assert.All(r.Cells.Zip(r.Cells.Skip(1)), p =>
            Assert.Equal(1, C(p.First.Col, p.First.Row).DistanceTo(C(p.Second.Col, p.Second.Row)))));
    }

    [Fact]
    public void M7_10_going_back_and_forth_keeps_every_visit_in_order()
    {
        using var dir = new TempDirectory();
        var model = Field(ContentTestKit.Library(dir.Path));
        model.RouteClickAt(C(2, 0));
        foreach (var r in new[] { 1, 2, 1, 2, 3 })
        {
            model.RouteHover(C(2, r));
        }

        Assert.Equal(new[] { C(2, 0), C(2, 1), C(2, 2), C(2, 1), C(2, 2), C(2, 3) }, model.RouteDraft);
        Walk(model, 2, 4, 7);
        var route = Assert.Single(model.Map.Routes);
        Assert.Equal(2, route.Cells.Count(c => c == (2, 1)));
        Assert.Contains("повторных посещений: 2", model.RouteSummary(route), StringComparison.Ordinal);
        Assert.Empty(model.RouteProblems(route.Id)); // repeats are not an error
    }

    [Fact]
    public void M7_11_M7_12_a_closed_or_missing_hex_stops_the_draft_with_its_name_and_a_far_jump_adds_only_neighbours()
    {
        using var dir = new TempDirectory();
        var model = Field(ContentTestKit.Library(dir.Path));
        model.RouteClickAt(C(2, 0));
        model.RouteHover(C(6, 5)); // a far jump: the straight line, one neighbour at a time
        Assert.All(model.RouteDraft.Zip(model.RouteDraft.Skip(1)), p => Assert.Equal(1, p.First.DistanceTo(p.Second)));
        Assert.True(model.RouteDraft.Count > 2);

        model.CancelRouteDraft();
        model.RouteClickAt(C(2, 0));
        Walk(model, 2, 1, 3);
        Assert.False(model.RouteHover(C(4, 3)) || model.RouteHover(C(4, 3)));
        Assert.Equal(C(4, 3), model.DraftErrorCell);
        Assert.Contains("гекс (4,3) непроходим", model.DraftError, StringComparison.Ordinal);
        Assert.DoesNotContain(C(4, 3), model.RouteDraft);
        Assert.Empty(model.Map.Routes); // nothing committed

        model.CancelRouteDraft();
        model.RouteClickAt(C(7, 0));
        Walk(model, 7, 1, 2);
        model.RouteHover(C(6, 3)); // odd-q: the south-west neighbour of (7,2)
        model.RouteHover(C(5, 3)); // no hex there
        Assert.Contains("на (5,3) нет гекса", model.DraftError, StringComparison.Ordinal);
        Assert.Equal(C(6, 3), model.RouteDraft[^1]); // the last correct part stays
    }

    [Fact]
    public void Backspace_and_escape_change_only_the_draft_and_redraw_keeps_id_and_name()
    {
        using var dir = new TempDirectory();
        var model = Field(ContentTestKit.Library(dir.Path));
        model.RouteClickAt(C(2, 0));
        Walk(model, 2, 1, 7);
        model.RenameRoute("route1", "Левая");
        model.RouteClickAt(C(7, 0));
        Walk(model, 7, 1, 3);
        Assert.True(model.RemoveLastVisit());
        Assert.Equal(C(7, 2), model.RouteDraft[^1]);
        model.CancelRouteDraft();
        Assert.False(model.IsDrafting);
        Assert.Single(model.Map.Routes); // the saved line stays

        model.RedrawRoute("route1");
        Assert.Equal(RouteClick.DraftStarted, model.RouteClickAt(C(2, 0)));
        Walk(model, 2, 1, 2);
        model.RouteHover(C(3, 2));
        Walk(model, 3, 3, 6);
        model.RouteHover(C(2, 7));
        var route = Assert.Single(model.Map.Routes);
        Assert.Equal(("route1", "Левая"), (route.Id, route.Name));
        Assert.Contains((3, 4), route.Cells);
        Assert.Equal("Перерисовать маршрут «Левая»", model.History.UndoDescription);
    }

    [Fact]
    public void M7_13_deleting_a_hex_under_a_route_keeps_the_route_and_the_check_names_the_hex()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var model = Field(library);
        model.RouteClickAt(C(2, 0));
        Walk(model, 2, 1, 7);
        model.SetRouteMode(false);
        Assert.False(model.PressDeleteButton()); // the red tool is on
        model.Click(C(2, 4));
        Assert.True(model.PressDeleteButton()); // marked: ready to delete
        Assert.Contains(model.DeleteImpactNow().Links, l => l.Contains("маршрут «Маршрут 1» идёт через (2,4)", StringComparison.Ordinal));
        model.ExecuteDelete();
        Assert.Single(model.Map.Routes); // not removed silently
        Assert.Contains(model.RouteProblems("route1"), p => p.Kind == RouteProblemKind.MissingHex && p.Cell == C(2, 4));
        Assert.Contains(model.Validate(), i => !i.IsError && i.Message.Contains("маршрут «Маршрут 1» требует проверки: шаг 4: гекса (2,4) нет на карте", StringComparison.Ordinal));
        Assert.True(model.Save().Ok); // the map stays usable for free play; scenarios with the route will refuse to start
    }
}
