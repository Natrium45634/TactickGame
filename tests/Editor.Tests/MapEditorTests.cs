using System.Linq;
using HexTactics.Content.Kinds;
using HexTactics.Content.Library;
using HexTactics.Content.Registry;
using HexTactics.Content.Runtime;
using HexTactics.Core.Hex;
using HexTactics.Core.Map;
using HexTactics.Core.Orders;
using HexTactics.Core.Spawning;
using HexTactics.Core.Units;
using HexTactics.Editor.Maps;
using HexTactics.Simulation;
using HexTactics.Tests.Support;
using Xunit;

namespace HexTactics.Editor.Tests;

/// <summary>The map editor (spec part 4 §4; acceptance §12.1 p.3–9).</summary>
public class MapEditorTests
{
    private static HexCoord C(int col, int row) => HexCoord.FromOffset(col, row);

    private static MapEditorModel NewMap(ContentLibrary library) => new(library, library.Create(ContentTypeRegistry.Map, "Остров"));

    [Fact]
    public void P3_a_new_map_is_an_empty_12x10_workspace_without_a_single_logical_hex()
    {
        var library = ContentTestKit.Library(null);
        var editor = NewMap(library);
        Assert.Equal((12, 10), (editor.Map.Columns, editor.Map.Rows));
        Assert.Empty(editor.Map.Tiles);
        Assert.True(editor.IsDirty);
        var grid = RuntimeFactory.BuildMap(editor.Item, library, new HexTactics.Content.Model.IssueList()).Grid;
        Assert.All(grid.Coords, c => Assert.False(grid.Exists(c)));
    }

    [Fact]
    public void P4_a_click_creates_exactly_one_hex_and_selects_it_a_click_on_it_selects_it_empty_places_stay_impassable()
    {
        var library = ContentTestKit.Library(null);
        var editor = NewMap(library);
        Assert.Equal(MapClick.Created, editor.Click(C(3, 3)));
        Assert.Single(editor.Map.Tiles);
        Assert.Equal(C(3, 3), editor.Selected);
        Assert.Equal("core.tile.ground", editor.TileAt(C(3, 3))!.Tile);

        Assert.Equal(MapClick.Created, editor.Click(C(5, 2)));
        Assert.Equal(C(5, 2), editor.Selected);
        Assert.Equal(MapClick.Selected, editor.Click(C(3, 3))); // no second tile on the same place
        Assert.Equal(2, editor.Map.Tiles.Count);
        Assert.Equal(C(3, 3), editor.Selected);
        Assert.Equal(MapClick.Outside, editor.Click(C(30, 3)));

        var grid = RuntimeFactory.BuildMap(editor.Item, library, new HexTactics.Content.Model.IssueList()).Grid;
        Assert.True(grid.IsWalkable(C(3, 3)));
        Assert.False(grid.IsWalkable(C(4, 3)));
        Assert.Equal(2, grid.Coords.Count(grid.Exists));
    }

    [Fact]
    public void P5_types_and_coordinates_survive_saving_and_a_restart()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var editor = NewMap(library);
        editor.Click(C(1, 1));
        editor.SetTileType(C(1, 1), "core.tile.player_base");
        editor.Click(C(2, 1));
        editor.SetTileType(C(2, 1), "core.tile.high");
        editor.Click(C(3, 1));
        editor.SetTileType(C(3, 1), "core.tile.enemy_base");
        editor.SetTileNote(C(3, 1), "лагерь");
        var saved = editor.Save();
        Assert.True(saved.Ok, saved.Message);
        Assert.False(editor.IsDirty);

        var restarted = ContentTestKit.Library(dir.Path);
        var map = (MapData)restarted.Find(ContentTypeRegistry.Map, editor.Item.Id)!.Data;
        Assert.Equal("core.tile.player_base", map.TileAt(1, 1)!.Tile);
        Assert.Equal("core.tile.high", map.TileAt(2, 1)!.Tile);
        Assert.Equal("core.tile.enemy_base", map.TileAt(3, 1)!.Tile);
        Assert.Equal("лагерь", map.TileAt(3, 1)!.Note);
        Assert.Equal(3, map.Tiles.Count);
    }

    [Fact]
    public void P6_the_red_button_marks_on_the_first_press_deletes_on_the_second_and_ctrl_z_restores_both()
    {
        var library = ContentTestKit.Library(null);
        var editor = NewMap(library);
        foreach (var (c, r) in new[] { (1, 1), (2, 1), (3, 1) })
        {
            editor.Click(C(c, r));
        }

        editor.SetTileType(C(2, 1), "core.tile.high");
        Assert.False(editor.PressDeleteButton()); // first press: mode on, nothing deleted
        Assert.True(editor.DeleteMode);
        Assert.Equal(MapClick.ToggledForDeletion, editor.Click(C(1, 1)));
        editor.Click(C(2, 1));
        editor.Click(C(3, 1));
        editor.Click(C(3, 1)); // unmark again
        Assert.Equal(3, editor.Map.Tiles.Count); // still there before the second press
        Assert.Equal(2, editor.DeleteSelection.Count);

        Assert.True(editor.PressDeleteButton());
        editor.ExecuteDelete();
        Assert.False(editor.DeleteMode);
        Assert.Equal(new[] { C(3, 1) }, editor.Map.Tiles.Select(t => t.Coord));

        Assert.True(editor.Undo()); // one step restores both, with their types
        Assert.Equal(3, editor.Map.Tiles.Count);
        Assert.Equal("core.tile.high", editor.TileAt(C(2, 1))!.Tile);
        Assert.True(editor.Redo());
        Assert.Single(editor.Map.Tiles);
    }

    [Fact]
    public void P7_Esc_or_an_empty_selection_never_delete_anything()
    {
        var library = ContentTestKit.Library(null);
        var editor = NewMap(library);
        editor.Click(C(1, 1));
        editor.Click(C(2, 1));
        var steps = editor.History.UndoCount;

        editor.PressDeleteButton();
        editor.Click(C(1, 1));
        editor.CancelDelete(); // Esc
        Assert.False(editor.DeleteMode);
        Assert.Equal(2, editor.Map.Tiles.Count);

        editor.PressDeleteButton();
        Assert.False(editor.PressDeleteButton()); // empty selection: the second press only leaves the mode
        Assert.False(editor.DeleteMode);
        Assert.Equal(2, editor.Map.Tiles.Count);
        Assert.Equal(steps, editor.History.UndoCount);
    }

    [Fact]
    public void P8_deleting_a_hex_with_a_test_point_lists_the_link_and_leaves_no_dangling_point()
    {
        var library = ContentTestKit.Library(null);
        var editor = NewMap(library);
        editor.Click(C(1, 1));
        editor.Click(C(2, 1));
        editor.SetTestPoint(C(1, 1), true);
        editor.SetTestPoint(C(2, 1), true);
        Assert.Equal(new[] { "A", "B" }, editor.Map.TestPoints.Select(p => p.Label));

        editor.PressDeleteButton();
        editor.Click(C(1, 1));
        var impact = editor.DeleteImpactNow();
        Assert.True(impact.NeedsConfirmation);
        Assert.Contains("«A»", Assert.Single(impact.Links));
        editor.PressDeleteButton();
        editor.ExecuteDelete();
        Assert.Equal(new[] { "B" }, editor.Map.TestPoints.Select(p => p.Label));
        Assert.DoesNotContain(editor.Validate(), i => i.IsError);
        editor.Undo();
        Assert.Equal(2, editor.Map.TestPoints.Count);
    }

    [Fact]
    public void P9_a_saved_editor_map_plays_in_the_same_simulation_and_refuses_waypoints_on_empty_places()
    {
        var library = ContentTestKit.Library(null);
        var editor = NewMap(library);
        for (var c = 1; c <= 5; c++)
        {
            editor.Click(C(c, 4));
        }

        editor.SetTileType(C(1, 4), "core.tile.player_base");
        var game = RuntimeFactory.BuildGame(library, editor.Item);
        var sim = new GameSimulation(game.Map, new SimulationSettings { DefaultStats = game.DefaultUnit.Stats, Skills = game.Skills, DefaultSkillSet = game.DefaultUnit.Skills });
        var unit = sim.Spawn(SpawnRequest.AtBase()).Unit!.Value;
        Assert.Equal(C(1, 4), sim.State.GetUnit(unit).Cell);
        Assert.Equal(WaypointError.NotWalkable, WaypointRules.ValidateTarget(sim.State, unit, C(1, 4), C(3, 2)));
        Assert.True(sim.ApplyOrder(new IndividualOrder(unit, new[] { C(5, 4) }, HexDirection.North)).Success);
        for (var i = 0; i < 200; i++)
        {
            sim.Step();
        }

        Assert.Equal(C(5, 4), sim.State.GetUnit(unit).Cell);
        Assert.False(sim.ApplyOrder(new IndividualOrder(unit, new[] { C(6, 4) }, HexDirection.North)).Success); // empty place
    }

    [Fact]
    public void Resizing_removes_outside_tiles_in_one_undo_step_and_reports_them_first()
    {
        var library = ContentTestKit.Library(null);
        var editor = NewMap(library);
        editor.Click(C(1, 1));
        editor.Click(C(10, 8));
        editor.SetTestPoint(C(10, 8), true);
        var impact = editor.ResizeImpact(8, 6);
        Assert.Equal(1, impact.Tiles);
        Assert.Single(impact.Links);
        editor.Resize(8, 6);
        Assert.Equal((8, 6), (editor.Map.Columns, editor.Map.Rows));
        Assert.Single(editor.Map.Tiles);
        Assert.Empty(editor.Map.TestPoints);
        editor.Undo();
        Assert.Equal((12, 10), (editor.Map.Columns, editor.Map.Rows));
        Assert.Equal(2, editor.Map.Tiles.Count);
    }

    [Fact]
    public void Test_points_without_a_passable_route_are_reported()
    {
        var library = ContentTestKit.Library(null);
        var editor = NewMap(library);
        editor.Click(C(1, 1));
        editor.Click(C(2, 1));
        editor.Click(C(3, 1));
        editor.SetTestPoint(C(1, 1), true);
        editor.SetTestPoint(C(3, 1), true);
        Assert.DoesNotContain(editor.Validate(), i => i.Message.Contains("маршрута"));
        editor.SetTileType(C(2, 1), "core.tile.obstacle");
        Assert.Contains(editor.Validate(), i => !i.IsError && i.Message.Contains("нет проходимого маршрута"));
    }

    [Fact]
    public void Built_in_maps_are_saved_as_a_copy_and_the_library_original_is_unchanged()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var editor = new MapEditorModel(library, library.Find(ContentTypeRegistry.Map, "core.map.prototype")!);
        editor.PressDeleteButton();
        editor.Click(C(0, 0));
        editor.PressDeleteButton();
        editor.ExecuteDelete();
        var result = editor.Save();
        Assert.True(result.Ok, result.Message);
        Assert.NotEqual("core.map.prototype", editor.Item.Id);
        Assert.Equal("Прототип 12×10 (копия)", editor.Item.Name);
        Assert.Equal(119, ((MapData)editor.Item.Data).Tiles.Count);
        Assert.Equal(120, ((MapData)library.Find(ContentTypeRegistry.Map, "core.map.prototype")!.Data).Tiles.Count);
    }

    [Fact]
    public void Dirty_follows_saves_and_undo_and_no_op_edits_are_not_recorded()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var editor = NewMap(library);
        editor.Click(C(1, 1));
        editor.SetTileType(C(1, 1), "core.tile.player_base");
        Assert.True(editor.Save().Ok);
        Assert.False(editor.IsDirty);
        var steps = editor.History.UndoCount;
        editor.SetTileType(C(1, 1), "core.tile.player_base"); // same value
        Assert.Equal(steps, editor.History.UndoCount);
        editor.SetName("Остров 2");
        Assert.True(editor.IsDirty);
        editor.Undo();
        Assert.False(editor.IsDirty);
        Assert.Equal("Остров", editor.Item.Name);
    }
}
