using System.IO;
using System.Linq;
using HexTactics.Content.Kinds;
using HexTactics.Content.Library;
using HexTactics.Content.Registry;
using HexTactics.Content.Runtime;
using HexTactics.Core.Combat;
using HexTactics.Core.Hex;
using HexTactics.Core.Spawning;
using HexTactics.Simulation;
using HexTactics.Tests.Support;
using Xunit;

namespace HexTactics.Content.Tests;

/// <summary>Damage types, resistances and the migration of part 3–4 content (spec part 5 §9; acceptance §11.5 p.41–44).</summary>
public class DamageContentTests
{
    private const string OldSkill = "{\"schemaVersion\":1,\"id\":\"skill_old\",\"kind\":\"skill\",\"name\":\"Старый удар\",\"revision\":4,\"data\":{\"icon\":\"✦\",\"anchor\":\"caster\",\"rotationMode\":\"facing\",\"baseEffects\":[{\"type\":\"damage\",\"amount\":20,\"targets\":\"enemies\"}],\"cells\":[{\"q\":0,\"r\":-1},{\"q\":0,\"r\":-2,\"effects\":[{\"type\":\"periodic\",\"kind\":\"damage\",\"perSecond\":5,\"duration\":2},{\"type\":\"heal\",\"amount\":5}]}]}}";
    private const string OldUnit = "{\"schemaVersion\":1,\"id\":\"unit_old\",\"kind\":\"unit\",\"name\":\"Старый боец\",\"revision\":2,\"data\":{\"maxHealth\":100,\"moveSpeed\":1,\"attackRate\":3,\"attackPower\":10,\"attackRange\":1,\"skills\":[\"skill_old\"],\"side\":\"player\",\"color\":\"#4D80CC\",\"scale\":1}}";
    private const string OldTile = "{\"schemaVersion\":1,\"id\":\"tile_old\",\"kind\":\"tile\",\"name\":\"Старая лава\",\"revision\":1,\"data\":{\"category\":\"special\",\"passable\":true,\"level\":0,\"color\":\"#E0805A\",\"effects\":[{\"type\":\"periodic\",\"trigger\":\"stay\",\"kind\":\"damage\",\"perSecond\":5,\"sides\":\"all\"}]}}";

    private static void Write(string root, string folder, string id, string json)
    {
        Directory.CreateDirectory(Path.Combine(root, "content", folder));
        File.WriteAllText(Path.Combine(root, "content", folder, id + ".json"), json);
    }

    [Fact]
    public void P44_part_3_4_content_migrates_with_backups_to_explicit_legacy_types_and_keeps_its_numbers()
    {
        using var dir = new TempDirectory();
        Write(dir.Path, "skills", "skill_old", OldSkill);
        Write(dir.Path, "units", "unit_old", OldUnit);
        Write(dir.Path, "tiles", "tile_old", OldTile);
        var library = ContentTestKit.Library(dir.Path);
        Assert.Empty(library.Problems);

        var skill = (SkillData)library.Find(ContentTypeRegistry.Skill, "skill_old")!.Data;
        Assert.Equal(Migrations.Untyped, skill.BaseEffects[0].Get("damageType"));
        Assert.Equal(Migrations.Untyped, skill.Cells[1].Effects[0].Get("damageType"));
        Assert.Null(skill.Cells[1].Effects[1].Get("damageType")); // healing has no damage type
        Assert.Equal(Migrations.Untyped, ((UnitData)library.Find(ContentTypeRegistry.Unit, "unit_old")!.Data).AttackDamageType);
        Assert.Empty(((UnitData)library.Find(ContentTypeRegistry.Unit, "unit_old")!.Data).Resistances); // no invisible armour
        Assert.Equal(Migrations.Direct, ((TileData)library.Find(ContentTypeRegistry.Tile, "tile_old")!.Data).Effects[0].Get("damageType"));
        Assert.Equal(4, library.Find(ContentTypeRegistry.Skill, "skill_old")!.Revision);

        foreach (var (folder, id, old, schema) in new[] { ("skills", "skill_old", OldSkill, 2), ("units", "unit_old", OldUnit, 4), ("tiles", "tile_old", OldTile, 2) })
        {
            Assert.Contains($"\"schemaVersion\": {schema}", File.ReadAllText(Path.Combine(dir.Path, "content", folder, id + ".json")), System.StringComparison.Ordinal);
            var backup = Directory.GetFiles(Path.Combine(dir.Path, "backups"), id + ".json", SearchOption.AllDirectories).Single();
            Assert.Equal(old, File.ReadAllText(backup));
        }

        // Same numbers on an unprotected target: the old skill still deals 20 per cell.
        var game = RuntimeFactory.BuildGame(library, squad: new[] { "unit_old" });
        var settings = new SimulationSettings { Skills = game.Skills, DefaultSkillSet = new[] { "skill_old" }, DamageTypes = game.DamageTypes };
        var sim = new GameSimulation(HexTactics.Tests.Shared.TestMaps.Empty(), settings);
        var caster = sim.Spawn(SpawnRequest.AtCell(HexCoord.FromOffset(5, 7), HexTactics.Core.Units.Side.Player, HexDirection.North)).Unit!.Value;
        var enemy = sim.Spawn(SpawnRequest.AtCell(HexCoord.FromOffset(5, 6), HexTactics.Core.Units.Side.Enemy, HexDirection.South)).Unit!.Value;
        sim.UseSkill(caster, "skill_old", HexDirection.North);
        sim.Step();
        Assert.Equal(80, sim.State.GetUnit(enemy).CurrentHealth, 9);
    }

    [Fact]
    public void P10_resistances_outside_500_NaN_or_unknown_types_are_refused()
    {
        var library = ContentTestKit.Library(null);
        var unit = library.Create(ContentTypeRegistry.Unit, "Танк");
        var data = (UnitData)unit.Data;
        data.Resistances["core.damage.physical"] = 50;
        Assert.DoesNotContain(library.Validate(unit), i => i.IsError);
        data.Resistances["core.damage.piercing"] = 501;
        data.Resistances["core.damage.blunt"] = double.NaN;
        data.Resistances["user_damage_nothing"] = 10;
        var issues = library.Validate(unit);
        Assert.Contains(issues, i => i.IsError && i.Path == "data.resistances.core.damage.piercing");
        Assert.Contains(issues, i => i.IsError && i.Path == "data.resistances.core.damage.blunt");
        Assert.Contains(issues, i => i.IsError && i.Message.Contains("user_damage_nothing", System.StringComparison.Ordinal));

        data.Resistances.Remove("core.damage.blunt"); // JSON has no NaN: a file can only hold a wrong number or text
        var json = ContentSerializer.Write(unit).Replace("\"core.damage.physical\": 50", "\"core.damage.physical\": \"много\"", System.StringComparison.Ordinal);
        var read = ContentSerializer.Read(json, library.Registry, ContentOrigin.User);
        Assert.Contains(read.Issues, i => i.IsError && i.Path.Contains("resistances", System.StringComparison.Ordinal));
    }

    [Fact]
    public void A_summed_resistance_beyond_500_is_a_warning_and_is_clamped_in_combat()
    {
        var library = ContentTestKit.Library(null);
        var unit = library.Create(ContentTypeRegistry.Unit, "Неуязвимый");
        var data = (UnitData)unit.Data;
        data.Resistances["global"] = 400;
        data.Resistances["core.damage.physical"] = 300;
        Assert.Contains(library.Validate(unit), i => !i.IsError && i.Message.Contains("ограничен", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Damage_type_definitions_keep_family_and_strategy_consistent()
    {
        var library = ContentTestKit.Library(null);
        var plasma = library.Create(ContentTypeRegistry.DamageType, "Плазменный");
        var d = (DamageTypeData)plasma.Data;
        d.Family = "core.damage.magical";
        Assert.DoesNotContain(library.Validate(plasma), i => i.IsError);
        d.Strategy = DamageTypeData.TrueLike;
        Assert.Contains(library.Validate(plasma), i => i.IsError && i.Path == "data.strategy");
        d.Strategy = DamageTypeData.ResistanceAffected;
        d.Family = "core.damage.piercing";
        Assert.Contains(library.Validate(plasma), i => i.IsError && i.Path == "data.family");
        d.Family = string.Empty;
        d.Strategy = "magic_maybe";
        Assert.Contains(library.Validate(plasma), i => i.IsError && i.Path == "data.strategy");
    }

    [Fact]
    public void A_skill_with_a_missing_damage_type_reports_the_missing_dependency()
    {
        var library = ContentTestKit.Library(null);
        var skill = library.Create(ContentTypeRegistry.Skill, "Плазма");
        var s = (SkillData)skill.Data;
        var hit = library.Registry.Effects.Find("damage")!.CreateDefault();
        Assert.Equal("core.damage.physical", hit.Get("damageType")); // a new effect is explicitly physical
        hit.Set("damageType", "user_damage_plasma");
        s.BaseEffects.Add(hit);
        s.Cells.Add(new SkillCellData { Q = 0, R = -1 });
        Assert.Contains(library.Validate(skill), i => i.IsError && i.Message.Contains("user_damage_plasma", System.StringComparison.Ordinal));
        Assert.Contains(new HexTactics.Content.Model.ContentRef("damage_type", "user_damage_plasma"), skill.References);
    }
}
