using System;
using System.IO;
using System.Linq;
using HexTactics.Content.Kinds;
using HexTactics.Content.Library;
using HexTactics.Content.Registry;
using HexTactics.Content.Runtime;
using HexTactics.Content.Transfer;
using HexTactics.Core.Combat;
using HexTactics.Core.Hex;
using HexTactics.Tests.Support;
using Xunit;

namespace HexTactics.Content.Tests;

/// <summary>The base attack as content (spec part 7 §8.2, §10; M7-43, M7-53, M7-55).</summary>
public class BasicAttackContentTests
{
    [Fact]
    public void The_built_in_front_attack_is_the_default_of_units_and_reaches_the_runtime()
    {
        var library = ContentTestKit.Library(null);
        var front = (BasicAttackData)library.Find(ContentTypeRegistry.BasicAttack, BasicAttackDefinition.DefaultId)!.Data;
        Assert.Equal(new[] { new HexCoord(0, -1) }, front.Cells);
        var stats = RuntimeFactory.BuildUnit(library.Find(ContentTypeRegistry.Unit, RuntimeFactory.DefaultUnitId)!, library).Stats;
        Assert.True(stats.AutoAttackEnabled);
        Assert.Equal(new[] { new HexCoord(0, -1) }, stats.BasicAttack!.Cells);
        Assert.Equal(BasicAttackDefinition.DefaultId, ((UnitData)library.Create(ContentTypeRegistry.Unit, "Новый").Data).BasicAttack);
        Assert.False(RuntimeFactory.BuildGame(library).AutoAttacks); // the prototype map: no automatic attacks (D-170)
    }

    [Fact]
    public void A_base_attack_is_checked_saved_reloaded_and_its_own_area_never_follows_a_skill()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var item = library.Create(ContentTypeRegistry.BasicAttack, "Копьё");
        var a = (BasicAttackData)item.Data;
        a.Cells.Add(new HexCoord(0, -2));
        (a.TargetPriority, a.MaxTargets, a.PowerPercent, a.CanSurprise) = (BasicAttackData.LowestHealth, 2, 150, false);
        Assert.True(library.Save(item).Ok);
        var unit = library.Create(ContentTypeRegistry.Unit, "Копейщик");
        ((UnitData)unit.Data).BasicAttack = item.Id;
        ((UnitData)unit.Data).Skills.Add("core.skill.linear_strike");
        Assert.True(library.Save(unit).Ok);

        var reloaded = ContentTestKit.Library(dir.Path);
        var basic = RuntimeFactory.BuildUnit(reloaded.Find(unit.Ref)!, reloaded).Stats.BasicAttack!;
        Assert.Equal(new[] { new HexCoord(0, -1), new HexCoord(0, -2) }, basic.Cells);
        Assert.Equal((TargetPriority.LowestHealth, 2, 150.0, false), (basic.Priority, basic.MaxTargets, basic.PowerPercent, basic.CanSurprise));
        Assert.Contains(item.Ref, reloaded.Find(unit.Ref)!.References);

        // M7-43: editing the unit's skill does not change its base attack.
        var skill = reloaded.Find(ContentTypeRegistry.Skill, "core.skill.linear_strike")!.CopyAs("skill_copy", ContentOrigin.User, "Удар");
        ((SkillData)skill.Data).Cells.Add(new SkillCellData { Q = 1, R = -1 });
        Assert.True(reloaded.Save(skill).Ok);
        Assert.Equal(2, RuntimeFactory.BuildUnit(reloaded.Find(unit.Ref)!, reloaded).Stats.BasicAttack!.Cells.Count);

        foreach (var (mutate, path) in new (Action<BasicAttackData>, string)[]
        {
            (d => d.Cells.Clear(), "data.cells"),
            (d => d.Cells.Add(default), "data.cells"),
            (d => d.Cells.Add(new HexCoord(0, -5)), "data.cells"),
            (d => d.Cells.Add(new HexCoord(0, -1)), "data.cells"),
            (d => d.MaxTargets = 7, "data.maxTargets"),
            (d => d.PowerPercent = -1, "data.powerPercent"),
            (d => d.TargetPriority = "random", "data.targetPriority"),
        })
        {
            var bad = reloaded.Find(item.Ref)!.Clone();
            mutate((BasicAttackData)bad.Data);
            Assert.Contains(reloaded.Validate(bad), i => i.IsError && i.Path == path);
        }
    }

    [Fact]
    public void M7_55_an_old_unit_migrates_once_to_the_front_attack_and_an_old_map_keeps_automatic_attacks_off()
    {
        using var dir = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(dir.Path, "content", "units"));
        File.WriteAllText(Path.Combine(dir.Path, "content", "units", "unit_v31.json"),
            "{\"schemaVersion\":8,\"id\":\"unit_v31\",\"kind\":\"unit\",\"name\":\"Боец v0.31\",\"revision\":1,\"data\":{\"maxHealth\":100,\"critChance\":0}}");
        File.WriteAllText(Path.Combine(dir.Path, "content", "units", "unit_mine.json"),
            "{\"schemaVersion\":8,\"id\":\"unit_mine\",\"kind\":\"unit\",\"name\":\"Свой\",\"revision\":1,\"data\":{\"maxHealth\":100,\"basicAttack\":\"basic_attack_x\",\"autoAttackEnabled\":false}}");
        var library = ContentTestKit.Library(dir.Path);
        var old = (UnitData)library.Find(ContentTypeRegistry.Unit, "unit_v31")!.Data;
        Assert.Equal((BasicAttackDefinition.DefaultId, true), (old.BasicAttack, old.AutoAttackEnabled));
        var mine = (UnitData)library.Find(ContentTypeRegistry.Unit, "unit_mine")!.Data;
        Assert.Equal(("basic_attack_x", false), (mine.BasicAttack, mine.AutoAttackEnabled)); // idempotent: present values stay

        var data = new System.Text.Json.Nodes.JsonObject { ["basicAttack"] = "x", ["autoAttackEnabled"] = false };
        var twice = Migrations.Unit(8, Migrations.Unit(8, data, new HexTactics.Content.Model.IssueList())!, new HexTactics.Content.Model.IssueList())!;
        Assert.Equal(("x", false), (twice["basicAttack"]!.GetValue<string>(), twice["autoAttackEnabled"]!.GetValue<bool>()));

        var map = library.Duplicate(library.Find(ContentTypeRegistry.Map, RuntimeFactory.DefaultMapId)!, "Арена").Item!;
        Assert.False(((MapData)map.Data).AutoAttacks);
        Assert.DoesNotContain("autoAttacks", File.ReadAllText(Path.Combine(dir.Path, "content", "maps", map.Id + ".json")), StringComparison.Ordinal);
        ((MapData)map.Data).AutoAttacks = true;
        Assert.True(library.Save(map).Ok);
        Assert.True(RuntimeFactory.BuildGame(ContentTestKit.Library(dir.Path), map.Id).AutoAttacks);
    }

    [Fact]
    public void M7_53_a_user_fire_type_drives_the_base_attack_and_a_package_carries_attack_and_type()
    {
        using var source = new TempDirectory();
        using var target = new TempDirectory();
        var library = ContentTestKit.Library(source.Path);
        var fire = library.Create(ContentTypeRegistry.DamageType, "Огонь");
        ((DamageTypeData)fire.Data).Family = DamageTypes.Magical;
        Assert.True(library.Save(fire).Ok);

        var attack = library.Create(ContentTypeRegistry.BasicAttack, "Огненное дыхание");
        ((BasicAttackData)attack.Data).DamageType = fire.Id;
        Assert.True(library.Save(attack).Ok);
        var unit = library.Create(ContentTypeRegistry.Unit, "Дракончик");
        ((UnitData)unit.Data).BasicAttack = attack.Id;
        Assert.True(library.Save(unit).Ok);
        Assert.Equal(fire.Id, RuntimeFactory.BuildUnit(library.Find(unit.Ref)!, library).Stats.BasicAttack!.DamageTypeId);

        var bytes = ContentExporter.ExportPackage(library, new[] { library.Find(unit.Ref)! }, "Дракончик", string.Empty, "0.32.0", DateTimeOffset.UnixEpoch);
        var clean = ContentTestKit.Library(target.Path);
        var preview = ContentImporter.Inspect(clean, "dragon.hxp", bytes);
        Assert.Contains(preview.Entries, e => e.Item.Ref == attack.Ref);
        Assert.Contains(preview.Entries, e => e.Item.Ref == fire.Ref);
        Assert.True(ContentImporter.Install(clean, preview).Ok);
        var imported = ContentTestKit.Library(target.Path);
        Assert.Equal(fire.Id, RuntimeFactory.BuildUnit(imported.Find(unit.Ref)!, imported).Stats.BasicAttack!.DamageTypeId);

        var lost = library.Find(unit.Ref)!.Clone();
        ((UnitData)lost.Data).BasicAttack = "basic_attack_missing";
        var issues = new HexTactics.Content.Model.IssueList();
        Assert.Null(RuntimeFactory.BuildUnit(lost, library, issues).Stats.BasicAttack); // a missing one means no automatic attack
        Assert.Contains(issues.Items, i => i.Message.Contains("автоатаки нет", StringComparison.Ordinal));
    }
}
