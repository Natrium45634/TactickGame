using System.Linq;
using HexTactics.Content.Kinds;
using HexTactics.Content.Library;
using HexTactics.Content.Model;
using HexTactics.Content.Registry;
using HexTactics.Tests.Support;
using Xunit;

namespace HexTactics.Content.Tests;

/// <summary>The common envelope and the checks done before anything is saved (spec part 4 §5.1, §8.2, §12.2 p.11, §12.3 p.28).</summary>
public class SerializerAndValidationTests
{
    private readonly ContentTypeRegistry _registry = ContentTypeRegistry.CreateDefault();

    private ContentReadResult Read(string json) => ContentSerializer.Read(json, _registry, ContentOrigin.User);

    private const string Unit = """
        {"schemaVersion":1,"id":"unit_test01","kind":"unit","name":"Тест","description":"","revision":3,
         "references":[],"data":{"maxHealth":175,"moveSpeed":1.5,"attackRate":2,"attackPower":12,"skills":[]}}
        """;

    [Fact]
    public void The_envelope_has_every_required_field_in_a_fixed_order()
    {
        var item = Read(Unit).Item!;
        var text = ContentSerializer.Write(item);
        var keys = System.Text.Json.Nodes.JsonNode.Parse(text)!.AsObject().Select(p => p.Key).ToArray();
        Assert.Equal(new[] { "schemaVersion", "id", "kind", "name", "description", "revision", "tags", "references", "data" }, keys);
        Assert.Contains("\"name\": \"Тест\"", text); // readable UTF-8, no \u escapes
        var unit = (UnitData)item.Data;
        Assert.Equal(175, unit.MaxHealth);
        Assert.Equal(1.5, unit.MoveSpeed);
        Assert.Equal(2, unit.AttackRate);
        Assert.Equal(3, item.Revision);
    }

    [Theory]
    [InlineData("{ not json", "повреждённый JSON")]
    [InlineData("[1,2]", "ожидается JSON-объект")]
    [InlineData("""{"schemaVersion":1,"id":"x_1234","kind":"spaceship","name":"a","data":{}}""", "неизвестный вид")]
    [InlineData("""{"schemaVersion":99,"id":"unit_1234","kind":"unit","name":"a","data":{}}""", "новее поддерживаемой")]
    [InlineData("""{"id":"unit_1234","kind":"unit","name":"a","data":{}}""", "нет версии схемы")]
    [InlineData("""{"schemaVersion":1,"id":"unit_1234","kind":"unit","name":"a"}""", "нет содержимого data")]
    [InlineData("""{"schemaVersion":1,"id":"../evil","kind":"unit","name":"a","data":{}}""", "неверный идентификатор")]
    public void Broken_files_are_refused_with_a_readable_reason(string json, string reason)
    {
        var result = Read(json);
        Assert.Null(result.Item);
        Assert.Contains(result.Issues, i => i.IsError && i.Message.Contains(reason));
    }

    [Fact]
    public void Too_large_files_are_refused_before_parsing()
    {
        var result = ContentSerializer.Read(new string(' ', 200) + Unit, _registry, ContentOrigin.User, new ContentLimits { MaxFileBytes = 100 });
        Assert.Null(result.Item);
        Assert.Contains("КБ", result.Issues.Single().Message);
    }

    [Fact]
    public void Wrong_value_types_are_reported_and_never_turn_into_an_empty_object()
    {
        var result = Read(Unit.Replace("\"maxHealth\":175", "\"maxHealth\":\"много\""));
        Assert.NotNull(result.Item);
        Assert.Contains(result.Issues, i => i.IsError && i.Path == "data.maxHealth");
        Assert.True(result.HasErrors);
    }

    [Theory]
    [InlineData("maxHealth", -5.0)]
    [InlineData("maxHealth", 0.0)]
    [InlineData("moveSpeed", 0.0)]
    [InlineData("moveSpeed", -1.0)]
    [InlineData("attackRate", 0.0)]
    [InlineData("attackPower", -1.0)]
    [InlineData("moveSpeed", double.NaN)]
    [InlineData("attackRate", double.PositiveInfinity)]
    public void Invalid_unit_values_are_errors(string field, double value)
    {
        var library = ContentTestKit.Library(null);
        var item = library.Create(ContentTypeRegistry.Unit, "Тест");
        UnitData.Fields.Single(f => f.Key == field).Set!(item.Data, value);
        Assert.Contains(library.Validate(item), i => i.IsError && i.Path == "data." + field);
    }

    [Fact]
    public void A_reference_to_a_missing_skill_is_an_error()
    {
        var library = ContentTestKit.Library(null);
        var item = library.Create(ContentTypeRegistry.Unit, "Тест");
        ((UnitData)item.Data).Skills.Add("skill_missing1");
        Assert.Contains(library.Validate(item), i => i.IsError && i.Message.Contains("skill_missing1"));
    }

    [Fact]
    public void Unknown_effect_types_and_out_of_range_effect_values_are_errors()
    {
        var library = ContentTestKit.Library(null);
        var item = library.Create(ContentTypeRegistry.Skill, "Тест");
        var skill = (SkillData)item.Data;
        skill.Cells.Add(new SkillCellData { Q = 0, R = -1 });
        skill.Cells[0].Effects.Add(new EffectData("teleport"));
        skill.Cells.Add(new SkillCellData { Q = 0, R = -2 });
        skill.Cells[1].Effects.Add(new EffectData("damage", new System.Collections.Generic.Dictionary<string, object> { ["amount"] = -3.0 }));
        var issues = library.Validate(item);
        Assert.Contains(issues, i => i.IsError && i.Message.Contains("неизвестный тип эффекта «teleport»"));
        Assert.Contains(issues, i => i.IsError && i.Path == "data.cells[1].effects[0].amount");
    }

    [Fact]
    public void Skill_area_limits_duplicates_and_radius_are_checked()
    {
        var library = ContentTestKit.Library(null);
        var item = library.Create(ContentTypeRegistry.Skill, "Тест");
        var skill = (SkillData)item.Data;
        skill.BaseEffects.Add(_registry.Effects.Find("damage")!.CreateDefault());
        skill.Cells.Add(new SkillCellData { Q = 0, R = -1 });
        skill.Cells.Add(new SkillCellData { Q = 0, R = -1 });
        skill.Cells.Add(new SkillCellData { Q = 0, R = -9 });
        var issues = library.Validate(item);
        Assert.Contains(issues, i => i.IsError && i.Message.Contains("указана дважды"));
        Assert.Contains(issues, i => i.IsError && i.Message.Contains("дальше 6"));
    }

    [Fact]
    public void Map_checks_duplicates_bounds_missing_types_edges_and_deploy_places()
    {
        var library = ContentTestKit.Library(null);
        var item = library.Create(ContentTypeRegistry.Map, "Тест");
        var map = (MapData)item.Data;
        Assert.Contains(library.Validate(item), i => !i.IsError && i.Message.Contains("нет ни одного гекса"));
        for (var c = 0; c < 3; c++)
        {
            for (var r = 0; r < 3; r++)
            {
                map.Tiles.Add(new MapTileData { Col = c, Row = r, Tile = "core.tile.ground" });
            }
        }

        Assert.Contains(library.Validate(item), i => !i.IsError && i.Message.Contains("нет мест выставления"));
        map.TileAt(1, 1)!.Tile = "core.tile.spawn";
        map.TileAt(0, 0)!.Tile = "core.tile.player_base";
        Assert.Contains(library.Validate(item), i => !i.IsError && i.Message.Contains("не на краю"));
        map.Tiles.Add(new MapTileData { Col = 1, Row = 1, Tile = "core.tile.ground" });
        map.Tiles.Add(new MapTileData { Col = 40, Row = 1, Tile = "core.tile.ground" });
        map.Tiles.Add(new MapTileData { Col = 2, Row = 5, Tile = "tile_absent1" });
        var issues = library.Validate(item);
        Assert.Contains(issues, i => i.IsError && i.Message.Contains("два гекса на одной координате"));
        Assert.Contains(issues, i => i.IsError && i.Message.Contains("вне рабочей области"));
        Assert.Contains(issues, i => i.IsError && i.Message.Contains("tile_absent1"));
    }

    [Fact]
    public void Obstacles_cannot_be_passable_and_zones_cannot_be_blocked()
    {
        var library = ContentTestKit.Library(null);
        var item = library.Create(ContentTypeRegistry.Tile, "Тест");
        var tile = (TileData)item.Data;
        tile.Category = TileData.Obstacle;
        Assert.Contains(library.Validate(item), i => i.IsError && i.Path == "data.passable");
        tile.Category = TileData.Deploy;
        tile.Passable = false;
        Assert.Contains(library.Validate(item), i => i.IsError && i.Path == "data.passable");
    }

    [Fact]
    public void Stable_ids_are_safe_file_names_and_core_is_reserved()
    {
        Assert.True(ContentIds.IsValid("skill_5f2c9a1b"));
        Assert.True(ContentIds.IsValid("core.tile.ground"));
        foreach (var bad in new[] { "", "ab", "Skill", "a b", "../x", "x/y", "x\\y", "con.", "-x", new string('a', 65) })
        {
            Assert.False(ContentIds.IsValid(bad), bad);
        }

        var id = ContentIds.New("skill");
        Assert.Matches("^skill_[0-9a-f]{8}$", id);
        Assert.Equal(id + ".json", ContentIds.FileName(id));
        Assert.True(ContentIds.IsBuiltIn("core.skill.linear_strike"));
        Assert.False(ContentIds.IsBuiltIn(id));
    }
}
