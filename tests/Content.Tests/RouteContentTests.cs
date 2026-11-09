using System;
using System.Linq;
using HexTactics.Content.Kinds;
using HexTactics.Content.Library;
using HexTactics.Content.Model;
using HexTactics.Content.Registry;
using HexTactics.Content.Runtime;
using HexTactics.Content.Transfer;
using HexTactics.Core.Hex;
using HexTactics.Core.Map;
using HexTactics.Tests.Support;
using Xunit;

namespace HexTactics.Content.Tests;

/// <summary>Routes and numbered bases in the map definition (spec part 7 §4.1, §4.3, §10; acceptance M7-07, M7-13, M7-54).</summary>
public class RouteContentTests
{
    /// <summary>A 6×6 field: enemy bases 1 at (1,0) and 2 at (4,0), player base 1 at (1,5) (base 2 at (4,5)), one route.</summary>
    private static ContentItem Field(ContentLibrary library)
    {
        var item = library.Create(ContentTypeRegistry.Map, "Две линии");
        var map = (MapData)item.Data;
        map.Columns = 6;
        map.Rows = 6;
        map.Tiles.Clear();
        for (var c = 0; c < 6; c++)
        {
            for (var r = 0; r < 6; r++)
            {
                var tile = (c, r) switch
                {
                    (1, 0) or (4, 0) => "core.tile.enemy_base",
                    (1, 5) or (4, 5) => "core.tile.player_base",
                    _ => "core.tile.ground",
                };
                map.Tiles.Add(new MapTileData { Col = c, Row = r, Tile = tile, Base = c == 4 && r is 0 or 5 ? 2 : null });
            }
        }

        var route = new MapRouteData { Id = "route1", Name = "Левая", Source = 1, Target = 1 };
        route.Cells.AddRange(Enumerable.Range(0, 6).Select(r => (1, r)));
        map.Routes.Add(route);
        map.Bases.Add(new MapBaseNameData { Side = MapBaseNameData.Enemy, Id = 2, Name = "Восточное логово" });
        return item;
    }

    [Fact]
    public void Routes_base_numbers_and_names_survive_save_and_reload_and_old_maps_write_none_of_them()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var item = Field(library);
        Assert.True(library.Save(item).Ok);
        var loaded = (MapData)ContentTestKit.Library(dir.Path).Find(item.Ref)!.Data;
        Assert.Equal(((MapData)item.Data).ToJson().ToJsonString(), loaded.ToJson().ToJsonString());
        Assert.Equal(Enumerable.Range(0, 6).Select(r => (1, r)), loaded.Routes[0].Cells);
        Assert.Equal(2, loaded.TileAt(4, 0)!.Base);
        Assert.Equal(("Восточное логово", "База врага 1", "База игрока", "База игрока 2"),
            (loaded.BaseTitle("enemy", 2), loaded.BaseTitle("enemy", 1), loaded.BaseTitle("player", 1), loaded.BaseTitle("player", 2)));

        var prototype = (MapData)library.Find(ContentTypeRegistry.Map, RuntimeFactory.DefaultMapId)!.Data;
        var json = prototype.ToJson();
        Assert.False(json.ContainsKey("routes") || json.ContainsKey("bases")); // maps without routes are written as before
    }

    [Fact]
    public void The_runtime_map_numbers_its_bases_and_carries_the_routes_the_check_accepts()
    {
        var library = ContentTestKit.Library(null);
        var map = RuntimeFactory.BuildMap(Field(library), library, new IssueList());
        Assert.Equal(new[] { (BaseSide.Enemy, 1), (BaseSide.Enemy, 2), (BaseSide.Player, 1), (BaseSide.Player, 2) }, map.Bases.Select(b => (b.Side, b.Number)));
        Assert.Equal("Восточное логово", map.Bases[1].Name);
        var route = Assert.Single(map.Routes);
        Assert.Equal(6, route.Cells.Count);
        Assert.Empty(RouteCheck.Check(map, route));

        // M7-07: the same visits towards base 2 of the player do not end on it.
        Assert.Contains(RouteCheck.Check(map, route with { TargetBase = 2 }), p => p.Kind == RouteProblemKind.EndNotPlayerBase);
    }

    [Fact]
    public void Broken_route_data_is_an_error_and_a_route_over_a_removed_hex_is_a_warning_that_names_it()
    {
        var library = ContentTestKit.Library(null);
        var item = Field(library);
        var map = (MapData)item.Data;
        var twin = map.Routes[0].Clone();
        twin.Cells.Insert(3, (4, 4)); // a jump: not neighbours
        map.Routes.Add(twin);           // and the same id twice
        map.TileAt(0, 0)!.Base = 12;
        var errors = library.Validate(item).Where(i => i.IsError).Select(i => i.Message).ToList();
        Assert.Contains(errors, m => m.Contains("ID маршрута «route1»", StringComparison.Ordinal));
        Assert.Contains(errors, m => m.Contains("не соседние гексы", StringComparison.Ordinal));
        Assert.Contains(errors, m => m.Contains("номер базы — от 1 до 9", StringComparison.Ordinal));

        // M7-13: a hex taken away under a valid route keeps the map saveable but marks the route with the hex.
        var valid = Field(library);
        ((MapData)valid.Data).Tiles.RemoveAll(t => t.Col == 1 && t.Row == 3);
        var issues = library.Validate(valid);
        Assert.DoesNotContain(issues, i => i.IsError);
        Assert.Contains(issues, i => i.Message == "маршрут «Левая» требует проверки: шаг 3: гекса (1,3) нет на карте");
    }

    [Fact]
    public void M7_54_a_package_of_the_map_restores_every_route_with_its_order_of_visits()
    {
        using var source = new TempDirectory();
        using var target = new TempDirectory();
        var library = ContentTestKit.Library(source.Path);
        var item = Field(library);
        var map = (MapData)item.Data;
        var loop = new MapRouteData { Id = "route2", Name = "Петля", Source = 2, Target = 2 };
        loop.Cells.AddRange(new[] { (4, 0), (4, 1), (4, 2), (4, 1), (4, 2), (4, 3), (4, 4), (4, 5) });
        map.Routes.Add(loop);
        Assert.True(library.Save(item).Ok);

        var bytes = ContentExporter.ExportPackage(library, new[] { library.Find(item.Ref)! }, "Линии", string.Empty, "0.35.0", DateTimeOffset.UnixEpoch);
        var clean = ContentTestKit.Library(target.Path);
        Assert.True(ContentImporter.Install(clean, ContentImporter.Inspect(clean, "lines.hxp", bytes)).Ok);
        var back = (MapData)ContentTestKit.Library(target.Path).Find(item.Ref)!.Data;
        Assert.Equal(new[] { "route1", "route2" }, back.Routes.Select(r => r.Id));
        Assert.Equal(loop.Cells, back.Routes[1].Cells); // repeats and order kept
        Assert.Empty(RuntimeFactory.CheckRoutes(RuntimeFactory.BuildMap(ContentTestKit.Library(target.Path).Find(item.Ref)!, clean, new IssueList()), back));
    }
}
