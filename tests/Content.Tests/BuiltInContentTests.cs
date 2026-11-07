using System.IO;
using System.Linq;
using HexTactics.Content.Kinds;
using HexTactics.Content.Library;
using HexTactics.Content.Model;
using HexTactics.Content.Registry;
using HexTactics.Content.Runtime;
using HexTactics.Core.Hex;
using HexTactics.Core.Map;
using HexTactics.Core.Skills;
using HexTactics.Tests.Shared;
using HexTactics.Tests.Support;
using Xunit;

namespace HexTactics.Content.Tests;

/// <summary>The game's own content is ordinary data of the library (spec part 4 §1, §6.6, §9.1).</summary>
public class BuiltInContentTests
{
    private readonly ContentLibrary _library = ContentTestKit.Library(null);

    [Fact]
    public void The_index_lists_exactly_the_files_of_the_content_folder()
    {
        var onDisk = Directory.GetFiles(ContentTestKit.BuiltInRoot, "*.json", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(ContentTestKit.BuiltInRoot, f).Replace('\\', '/'))
            .Where(f => f != "index.json")
            .OrderBy(f => f, System.StringComparer.Ordinal);
        Assert.Equal(onDisk, ContentTestKit.BuiltInFiles().Select(f => f.Path).OrderBy(f => f, System.StringComparer.Ordinal));
    }

    [Fact]
    public void Every_built_in_object_loads_and_validates_without_issues_and_is_read_only()
    {
        Assert.Empty(_library.Problems);
        foreach (var kind in _library.Registry.All)
        {
            foreach (var item in _library.Items(kind.Id))
            {
                Assert.True(item.IsBuiltIn);
                Assert.StartsWith("core.", item.Id);
                Assert.Empty(item.Issues);
                Assert.Empty(_library.Validate(item));
            }
        }

        Assert.Equal(7, _library.Items(ContentTypeRegistry.Tile).Count);
        Assert.Single(_library.Items(ContentTypeRegistry.Unit));
        Assert.Single(_library.Items(ContentTypeRegistry.Skill));
        Assert.Single(_library.Items(ContentTypeRegistry.Map));
    }

    [Fact]
    public void The_prototype_map_from_content_equals_the_part_3_hexmap_cell_by_cell()
    {
        var legacy = MapParser.Parse(File.ReadAllText(TestPaths.PrototypeMap));
        var game = RuntimeFactory.BuildGame(_library);
        Assert.Empty(game.Issues);
        var map = game.Map;
        Assert.Equal(legacy.Grid.Columns, map.Grid.Columns);
        Assert.Equal(legacy.Grid.Rows, map.Grid.Rows);
        foreach (var c in legacy.Grid.Coords)
        {
            Assert.Equal(legacy.Grid[c].Type, map.Grid[c].Type);
            Assert.Equal(legacy.Grid[c].ZoneId, map.Grid[c].ZoneId);
            Assert.NotNull(map.Grid[c].TileId);
        }

        Assert.Equal(legacy.SpawnZones.Select(z => (z.Id, z.Name, string.Join(";", z.Cells))), map.SpawnZones.Select(z => (z.Id, z.Name, string.Join(";", z.Cells))));
        Assert.Equal(legacy.PlayerBase!.Cells, map.PlayerBase!.Cells);
        Assert.Equal(legacy.PlayerBase.Name, map.PlayerBase.Name);
        Assert.Equal(legacy.DeployCells, map.DeployCells);
    }

    [Fact]
    public void Linear_strike_is_an_ordinary_skill_definition_with_the_part_3_behaviour()
    {
        var game = RuntimeFactory.BuildGame(_library);
        var skill = game.Skills[SkillCatalog.LinearStrikeId];
        var reference = SkillCatalog.LinearStrike();
        foreach (var d in HexDirections.All)
        {
            Assert.Equal(reference.Area(C(5, 5), d), skill.Area(C(5, 5), d));
        }

        Assert.All(skill.Cells, c =>
        {
            var e = Assert.Single(c.Effects);
            Assert.Equal("damage", e.Type);
            Assert.Equal(20, e.Number("amount"));
            Assert.Equal(HexTactics.Core.Effects.EffectTargets.Enemies, e.Targets);
        });
    }

    [Fact]
    public void The_default_unit_carries_the_part_3_characteristics_and_the_skill()
    {
        var unit = RuntimeFactory.BuildGame(_library).DefaultUnit;
        Assert.Equal(100, unit.Stats.MaxHealth);
        Assert.Equal(1.0, unit.Stats.MoveSpeedHexPerSecond);
        Assert.Equal(3.0, unit.Stats.AttackRatePerSecond);
        Assert.Equal(10, unit.Stats.AttackPower);
        Assert.Equal(1, unit.Stats.NormalAttackRangeHex);
        Assert.Equal(new[] { SkillCatalog.LinearStrikeId }, unit.Skills);
    }

    [Fact]
    public void Every_built_in_file_round_trips_byte_for_byte_through_the_serializer()
    {
        foreach (var (path, text) in ContentTestKit.BuiltInFiles())
        {
            var read = ContentSerializer.Read(text, _library.Registry, ContentOrigin.BuiltIn);
            Assert.NotNull(read.Item);
            var again = ContentSerializer.Read(ContentSerializer.Write(read.Item!), _library.Registry, ContentOrigin.BuiltIn).Item!;
            Assert.Equal(ContentSerializer.Write(read.Item!), ContentSerializer.Write(again));
            Assert.Equal(read.Item!.Data.ToJson().ToJsonString(), again.Data.ToJson().ToJsonString());
            Assert.True(path.Length > 0);
        }
    }

    [Fact]
    public void Tile_types_map_to_the_rule_roles_of_the_core()
    {
        TileType Type(string id) => RuntimeFactory.TypeOf((TileData)_library.Find(ContentTypeRegistry.Tile, id)!.Data);
        Assert.Equal(TileType.Walkable, Type("core.tile.ground"));
        Assert.Equal(TileType.Walkable, Type("core.tile.high"));
        Assert.Equal(TileType.Blocked, Type("core.tile.obstacle"));
        Assert.Equal(TileType.PlayerBase, Type("core.tile.player_base"));
        Assert.Equal(TileType.EnemyBase, Type("core.tile.enemy_base"));
        Assert.Equal(TileType.DeployZone, Type("core.tile.deploy"));
        Assert.Equal(TileType.SpawnZone, Type("core.tile.spawn"));
    }

    [Fact]
    public void A_skill_test_run_puts_the_tested_skill_first_for_roster_cards_and_debug_spawns()
    {
        var skill = _library.Find(ContentTypeRegistry.Skill, SkillCatalog.LinearStrikeId)!.CopyAs("test.skill", ContentOrigin.User, "Тест");
        var game = RuntimeFactory.BuildGame(_library, _library.Find(ContentTypeRegistry.Map, RuntimeFactory.DefaultMapId)!, testSkill: skill);
        Assert.True(game.Skills.ContainsKey("test.skill"));
        Assert.Equal("test.skill", game.DefaultUnit.Skills[0]);
        Assert.Contains(SkillCatalog.LinearStrikeId, game.DefaultUnit.Skills);
        Assert.All(game.Squad, s => Assert.Equal("test.skill", s.Profile.Skills[0]));
    }


    private static HexCoord C(int col, int row) => HexCoord.FromOffset(col, row);
}
