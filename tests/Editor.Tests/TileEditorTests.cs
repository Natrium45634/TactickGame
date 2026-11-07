using System.Linq;
using HexTactics.Content.Kinds;
using HexTactics.Content.Library;
using HexTactics.Content.Registry;
using HexTactics.Content.Runtime;
using HexTactics.Core.Effects;
using HexTactics.Core.Hex;
using HexTactics.Core.Orders;
using HexTactics.Core.Spawning;
using HexTactics.Editor.Maps;
using HexTactics.Editor.Tiles;
using HexTactics.Simulation;
using HexTactics.Tests.Support;
using Xunit;

namespace HexTactics.Editor.Tests;

/// <summary>The tile type editor (spec part 4 §7; acceptance §12.3 p.21–23).</summary>
public class TileEditorTests
{
    private static HexCoord C(int col, int row) => HexCoord.FromOffset(col, row);

    private static HexTactics.Content.Schema.FieldSchema F(string key) => TileData.Fields.Single(f => f.Key == key);

    /// <summary>«Жгучая поверхность» / «Целебная поверхность» made only through the editor model (as the UI does).</summary>
    private static TileEditorModel Surface(ContentLibrary library, string name, string kind)
    {
        var editor = new TileEditorModel(library, library.Create(ContentTypeRegistry.Tile, name));
        Assert.Null(editor.TrySetField(F("category"), TileData.Special));
        Assert.Null(editor.TrySetField(F("color"), kind == "heal" ? "#8FD19E" : "#E0805A"));
        Assert.Null(editor.TrySetField(F("icon"), kind == "heal" ? "+" : "~"));
        Assert.Null(editor.AddEffect(EffectTypes.Periodic));
        Assert.Null(editor.SetEffectField(0, "kind", kind));
        Assert.Null(editor.SetEffectField(0, "perSecond", "5"));
        return editor;
    }

    private static GameSimulation Play(GameContent game) => new(game.Map, new SimulationSettings
    {
        DefaultStats = game.DefaultUnit.Stats, Skills = game.Skills, DefaultSkillSet = game.DefaultUnit.Skills, TileEffects = game.TileEffects,
    });

    [Fact]
    public void A_periodic_effect_on_a_tile_defaults_to_5_HP_per_second_while_on_the_hex_and_is_described_in_numbers()
    {
        var editor = Surface(ContentTestKit.Library(null), "Жгучая поверхность", "damage");
        var effect = editor.Tile.Effects.Single();
        Assert.Equal("stay", effect.Get("trigger"));
        Assert.Equal("all", effect.Get("sides"));
        Assert.Null(effect.Get("targets")); // skill-only fields are not on tiles
        var preview = editor.Preview();
        Assert.Contains("Пока юнит на гексе: урон 5 HP за игровую секунду нахождения (за 2 с — 10 HP) — все юниты.", preview);
        Assert.DoesNotContain(editor.Validate(), i => i.IsError);
    }

    [Fact]
    public void Invalid_values_are_refused_and_change_nothing()
    {
        var editor = Surface(ContentTestKit.Library(null), "Жгучая поверхность", "damage");
        Assert.NotNull(editor.SetEffectField(0, "perSecond", "-5"));
        Assert.NotNull(editor.SetEffectField(0, "perSecond", "много"));
        Assert.NotNull(editor.SetEffectField(0, "trigger", "always"));
        Assert.NotNull(editor.TrySetField(F("icon"), "ABCDE"));
        Assert.Equal(5.0, editor.Tile.Effects[0].Get("perSecond"));
        Assert.Equal("~", editor.Tile.Icon);
        Assert.NotNull(editor.AddEffect("teleport"));
        Assert.Null(editor.AddEffect(EffectTypes.Damage));
        Assert.NotNull(editor.SetEffectField(1, "trigger", "stay")); // instant effects act on entering/leaving only
        Assert.True(editor.Undo());
        Assert.Single(editor.Tile.Effects);
    }

    [Fact]
    public void P21_P23_a_burning_surface_saved_in_the_editor_is_placed_by_reference_in_the_map_editor_and_takes_10_HP_in_2_seconds()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var burn = Surface(library, "Жгучая поверхность", "damage");
        Assert.True(burn.Save().Ok);

        var restarted = ContentTestKit.Library(dir.Path);
        Assert.Contains(restarted.Items(ContentTypeRegistry.Tile), t => t.Id == burn.Item.Id && t.Name == "Жгучая поверхность");
        var map = new MapEditorModel(restarted, restarted.Create(ContentTypeRegistry.Map, "Поверхности"));
        map.Click(C(2, 4));
        map.SetTileType(C(2, 4), "core.tile.player_base");
        map.Brush = burn.Item.Id;
        map.Click(C(3, 4));
        map.Brush = "core.tile.ground";
        map.Click(C(4, 4));
        var stored = ((MapData)map.Item.Data).Tiles.Single(t => t.Coord == C(3, 4));
        Assert.Equal(burn.Item.Id, stored.Tile); // a reference to the library type, no copy of its logic
        Assert.DoesNotContain("perSecond", HexTactics.Content.Library.ContentSerializer.Write(map.Item));

        var sim = Play(RuntimeFactory.BuildGame(restarted, map.Item));
        var unit = sim.Spawn(SpawnRequest.AtCell(C(3, 4), facing: HexDirection.North)).Unit!.Value;
        for (var i = 0; i < 60; i++)
        {
            sim.Step();
        }

        Assert.Equal(90, sim.State.GetUnit(unit).CurrentHealth, 9);
    }

    [Fact]
    public void P22_a_healing_surface_heals_up_to_the_maximum_and_editing_the_type_changes_every_map_after_saving()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var heal = Surface(library, "Целебная поверхность", "heal");
        Assert.True(heal.Save().Ok);
        var map = new MapEditorModel(library, library.Create(ContentTypeRegistry.Map, "Лазарет"));
        map.Brush = heal.Item.Id;
        map.Click(C(5, 5));

        var sim = Play(RuntimeFactory.BuildGame(library, map.Item));
        var unit = sim.Spawn(SpawnRequest.AtCell(C(5, 5), facing: HexDirection.North)).Unit!.Value;
        sim.State.ApplyDamage(unit, 20);
        for (var i = 0; i < 300; i++)
        {
            sim.Step();
        }

        Assert.Equal(100, sim.State.GetUnit(unit).CurrentHealth, 9);

        // The library type is the one source of truth: after saving it, maps that use it get the new rule.
        Assert.Null(heal.SetEffectField(0, "perSecond", "10"));
        Assert.True(heal.Save().Ok);
        var game = RuntimeFactory.BuildGame(library, map.Item);
        Assert.Equal(10.0, game.TileEffects[heal.Item.Id].Single().Effect.Number("perSecond"));
    }

    [Fact]
    public void Built_in_tile_types_are_saved_as_a_copy()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var editor = new TileEditorModel(library, library.Find(ContentTypeRegistry.Tile, "core.tile.ground")!);
        Assert.Null(editor.TrySetField(F("waypoints"), false));
        var result = editor.Save();
        Assert.True(result.Ok);
        Assert.NotEqual("core.tile.ground", result.Item!.Id);
        Assert.True(((TileData)library.Find(ContentTypeRegistry.Tile, "core.tile.ground")!.Data).Waypoints);
    }
}
