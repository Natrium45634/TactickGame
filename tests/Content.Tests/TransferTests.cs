using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using HexTactics.Content.Kinds;
using HexTactics.Content.Library;
using HexTactics.Content.Model;
using HexTactics.Content.Registry;
using HexTactics.Content.Runtime;
using HexTactics.Content.Transfer;
using HexTactics.Core.Effects;
using HexTactics.Core.Hex;
using HexTactics.Core.Spawning;
using HexTactics.Simulation;
using HexTactics.Tests.Support;
using Xunit;

namespace HexTactics.Content.Tests;

/// <summary>Saving, export/import of objects and packages, conflicts, migrations, limits (spec part 4 §8; §12.3 p.24–30).</summary>
public class TransferTests
{
    private static readonly DateTimeOffset Created = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    private static HexCoord C(int col, int row) => HexCoord.FromOffset(col, row);

    /// <summary>A user tile «Жгучая поверхность», skill «Тройной удар», unit «Тяжёлый» with the skill, map using the tile.</summary>
    private static (ContentItem Tile, ContentItem Skill, ContentItem Unit, ContentItem Map) Content(ContentLibrary library)
    {
        var effects = library.Registry.Effects;
        var tile = library.Create(ContentTypeRegistry.Tile, "Жгучая поверхность");
        var t = (TileData)tile.Data;
        t.Category = TileData.Special;
        t.Color = "#E0805A";
        t.Icon = "♨";
        t.Effects.Add(effects.Find(EffectTypes.Periodic)!.CreateDefault(EffectContext.Tile));
        Assert.True(library.Save(tile).Ok);

        var skill = library.Create(ContentTypeRegistry.Skill, "Тройной удар");
        var s = (SkillData)skill.Data;
        var hit = effects.Find(EffectTypes.Damage)!.CreateDefault();
        hit.Set("formula", "attack");
        hit.Set("multiplier", 1.5);
        hit.Set("hits", 3.0);
        hit.Set("interval", 0.5);
        s.BaseEffects.Add(hit);
        s.Cells.Add(new SkillCellData { Q = 0, R = -1 });
        var far = new SkillCellData { Q = 0, R = -2, Color = "#D04040", Alpha = 0.6 };
        far.Overrides[0] = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["multiplier"] = 0.75 };
        s.Cells.Add(far);
        Assert.True(library.Save(skill).Ok);

        var unit = library.Create(ContentTypeRegistry.Unit, "Тяжёлый");
        var u = (UnitData)unit.Data;
        u.MaxHealth = 175;
        u.AttackPower = 12.5;
        u.Skills.Add(skill.Id);
        Assert.True(library.Save(unit).Ok);

        var map = library.Create(ContentTypeRegistry.Map, "Поверхности");
        var m = (MapData)map.Data;
        m.Tiles.Add(new MapTileData { Col = 2, Row = 4, Tile = "core.tile.player_base" });
        m.Tiles.Add(new MapTileData { Col = 3, Row = 4, Tile = tile.Id });
        m.Tiles.Add(new MapTileData { Col = 4, Row = 4, Tile = "core.tile.ground" });
        Assert.True(library.Save(map).Ok);
        return (library.Find(tile.Ref)!, library.Find(skill.Ref)!, library.Find(unit.Ref)!, library.Find(map.Ref)!);
    }

    private static string Json(ContentItem item) => ContentSerializer.Write(item);

    private static string Snapshot(string root) =>
        string.Join("\n", Directory.GetFiles(Path.Combine(root, "content"), "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => Path.GetRelativePath(root, f) + "=" + ContentExporter.Sha256(File.ReadAllBytes(f))));

    private static byte[] Zip(params (string Path, byte[] Bytes)[] files)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, bytes) in files)
            {
                using var s = zip.CreateEntry(path).Open();
                s.Write(bytes, 0, bytes.Length);
            }
        }

        return stream.ToArray();
    }

    [Fact]
    public void P24_every_kind_survives_a_restart_and_links_resolve()
    {
        using var dir = new TempDirectory();
        var (tile, skill, unit, map) = Content(ContentTestKit.Library(dir.Path));
        var restarted = ContentTestKit.Library(dir.Path);
        foreach (var item in new[] { tile, skill, unit, map })
        {
            var again = restarted.Find(item.Ref)!;
            Assert.Equal(Json(item), Json(again));
            Assert.DoesNotContain(restarted.Validate(again), i => i.IsError);
        }

        var game = RuntimeFactory.BuildGame(restarted, map, squad: new[] { unit.Id });
        Assert.Equal(new[] { skill.Id }, game.Squad[0].Profile.Skills);
        Assert.True(game.TileEffects.ContainsKey(tile.Id));
    }

    [Fact]
    public void P25_a_skill_exported_alone_imports_into_a_clean_storage_with_every_field()
    {
        using var source = new TempDirectory();
        using var target = new TempDirectory();
        var (_, skill, unit, _) = Content(ContentTestKit.Library(source.Path));
        var export = ContentExporter.ExportObject(ContentTestKit.Library(source.Path), skill);
        Assert.True(export.SelfContained);

        var clean = ContentTestKit.Library(target.Path);
        var preview = ContentImporter.Inspect(clean, "triple.json", Encoding.UTF8.GetBytes(export.Json));
        var entry = Assert.Single(preview.Entries);
        Assert.Equal(ImportStatus.New, entry.Status);
        Assert.Equal(ImportResolution.Install, entry.Resolution);
        Assert.True(ContentImporter.Install(clean, preview).Ok);
        var imported = ContentTestKit.Library(target.Path).Find(skill.Ref)!;
        Assert.Equal(skill.Data.ToJson().ToJsonString(), imported.Data.ToJson().ToJsonString()); // cells, overrides, colours, intervals
        Assert.Equal((skill.Name, skill.Revision), (imported.Name, imported.Revision));

        // A unit needs its skill: the single file is not self-contained, and the import says so explicitly.
        var unitExport = ContentExporter.ExportObject(ContentTestKit.Library(source.Path), unit);
        Assert.False(unitExport.SelfContained);
        Assert.Equal(new[] { skill.Ref }, unitExport.NotIncluded.Select(i => i.Ref));
        var other = new TempDirectory();
        using (other)
        {
            var empty = ContentTestKit.Library(other.Path);
            var unitPreview = ContentImporter.Inspect(empty, "unit.json", Encoding.UTF8.GetBytes(unitExport.Json));
            var unitEntry = Assert.Single(unitPreview.Entries);
            Assert.Equal(ImportStatus.Invalid, unitEntry.Status);
            Assert.Contains(unitEntry.Issues, i => i.Message.Contains("не самодостаточен", StringComparison.Ordinal));
            Assert.False(unitPreview.CanInstall);
        }
    }

    [Fact]
    public void P26_a_map_package_with_its_tiles_unit_and_skill_installs_on_a_clean_storage_and_plays()
    {
        using var source = new TempDirectory();
        using var target = new TempDirectory();
        var library = ContentTestKit.Library(source.Path);
        var (tile, skill, unit, map) = Content(library);
        var bytes = ContentExporter.ExportPackage(library, new[] { map, unit }, "Поверхности", "карта, тип, юнит, навык", "0.20.0", Created);

        var clean = ContentTestKit.Library(target.Path);
        var preview = ContentImporter.Inspect(clean, "surfaces.hxp", bytes);
        Assert.True(preview.IsPackage);
        Assert.False(preview.Rejected, preview.Summary);
        Assert.Equal(new[] { tile.Ref, map.Ref, skill.Ref, unit.Ref }.OrderBy(r => r.ToString()), preview.Entries.Select(e => e.Item.Ref).OrderBy(r => r.ToString()));
        Assert.Contains(new ContentRef("tile", "core.tile.player_base"), preview.Manifest!.BuiltInDependencies);
        Assert.All(preview.Entries, e => Assert.Equal(ImportStatus.New, e.Status));
        Assert.True(ContentImporter.Install(clean, preview).Ok);
        Assert.Single(Directory.GetFiles(Path.Combine(target.Path, "packs"), "*.hxp"));

        var restarted = ContentTestKit.Library(target.Path);
        var game = RuntimeFactory.BuildGame(restarted, restarted.Find(map.Ref)!, squad: new[] { unit.Id });
        Assert.Equal(new[] { skill.Id }, game.Squad[0].Profile.Skills);
        var sim = new GameSimulation(game.Map, new SimulationSettings { Skills = game.Skills, DefaultSkillSet = game.DefaultUnit.Skills, TileEffects = game.TileEffects });
        var walker = sim.Spawn(SpawnRequest.AtCell(C(3, 4), facing: HexDirection.North)).Unit!.Value;
        for (var i = 0; i < 60; i++)
        {
            sim.Step();
        }

        Assert.Equal(90, sim.State.GetUnit(walker).CurrentHealth, 9); // the imported surface works in the game
    }

    [Fact]
    public void P27_a_conflict_offers_three_choices_and_nothing_is_replaced_without_one()
    {
        using var mine = new TempDirectory();
        using var theirs = new TempDirectory();
        var library = ContentTestKit.Library(mine.Path);
        var (_, skill, unit, _) = Content(library);
        var original = File.ReadAllText(library.User!.PathOf("skills", skill.Id));

        // Another installation changed the same skill (same id) and exports it with the unit.
        var other = ContentTestKit.Library(theirs.Path);
        foreach (var item in new[] { skill, unit })
        {
            var to = other.User!.PathOf(item.Kind.Folder, item.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(library.User.PathOf(item.Kind.Folder, item.Id), to);
        }

        other.Reload();
        var changed = other.Find(skill.Ref)!.Clone();
        ((SkillData)changed.Data).Icon = "⚔";
        Assert.True(other.Save(changed).Ok);
        var package = ContentExporter.ExportPackage(other, new[] { other.Find(unit.Ref)! }, "Их набор", string.Empty, "0.20.0", Created);

        var preview = ContentImporter.Inspect(library, "theirs.hxp", package);
        var conflict = preview.Entries.Single(e => e.Item.Ref == skill.Ref);
        var same = preview.Entries.Single(e => e.Item.Ref == unit.Ref);
        Assert.Equal(ImportStatus.Conflict, conflict.Status);
        Assert.Equal(new[] { ImportResolution.Skip, ImportResolution.Replace, ImportResolution.Copy }, conflict.Options);
        Assert.Equal(ImportResolution.Skip, conflict.Resolution); // the default never replaces
        Assert.Equal(ImportStatus.Same, same.Status);
        Assert.False(ContentImporter.Install(library, preview).Ok); // nothing chosen
        Assert.Equal(original, File.ReadAllText(library.User.PathOf("skills", skill.Id)));

        // «Импортировать копией с новым ID»: the unit of the package follows the copy, the user's skill stays.
        conflict.Resolution = ImportResolution.Copy;
        same.Resolution = ImportResolution.Copy;
        var copied = ContentImporter.Install(library, preview);
        Assert.True(copied.Ok, copied.Message);
        var copySkill = copied.Installed.Single(i => i.Kind.Id == ContentTypeRegistry.Skill);
        var copyUnit = copied.Installed.Single(i => i.Kind.Id == ContentTypeRegistry.Unit);
        Assert.NotEqual(skill.Id, copySkill.Id);
        Assert.Equal("⚔", ((SkillData)copySkill.Data).Icon);
        Assert.Equal(new[] { copySkill.Id }, ((UnitData)copyUnit.Data).Skills);
        Assert.Equal(original, File.ReadAllText(library.User.PathOf("skills", skill.Id)));

        // «Заменить после резервной копии».
        var again = ContentImporter.Inspect(library, "theirs.hxp", package);
        again.Entries.Single(e => e.Item.Ref == skill.Ref).Resolution = ImportResolution.Replace;
        Assert.True(ContentImporter.Install(library, again).Ok);
        Assert.Equal("⚔", ((SkillData)library.Find(skill.Ref)!.Data).Icon);
        var backup = Directory.GetFiles(Path.Combine(mine.Path, "backups"), skill.Id + ".json", SearchOption.AllDirectories).Single(f => f.Contains("-import", StringComparison.Ordinal));
        Assert.Equal(original, File.ReadAllText(backup));
    }

    [Fact]
    public void Built_in_objects_can_be_skipped_or_copied_but_never_replaced()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var strike = library.Find(ContentTypeRegistry.Skill, "core.skill.linear_strike")!.Clone();
        strike.Name = "Линейный удар (чужой)";
        var preview = ContentImporter.Inspect(library, "strike.json", Encoding.UTF8.GetBytes(Json(strike)));
        var entry = Assert.Single(preview.Entries);
        Assert.Equal(ImportStatus.Conflict, entry.Status);
        Assert.DoesNotContain(ImportResolution.Replace, entry.Options);
        entry.Resolution = ImportResolution.Replace;
        Assert.False(ContentImporter.Install(library, preview).Ok);
        entry.Resolution = ImportResolution.Copy;
        Assert.True(ContentImporter.Install(library, preview).Ok);
        Assert.Equal("Линейный удар", library.Find(ContentTypeRegistry.Skill, "core.skill.linear_strike")!.Name);
    }

    public static IEnumerable<object[]> BadPackages()
    {
        var obj = Encoding.UTF8.GetBytes("{}");
        yield return new object[] { "../evil.json", "опасный путь" };
        yield return new object[] { "/abs/evil.json", "опасный путь" };
        yield return new object[] { "C:/evil.json", "опасный путь" };
        yield return new object[] { "content\\skills\\evil.json", "опасный путь" };
        yield return new object[] { "tools/run.exe", "недопустимый файл" };
        yield return new object[] { "content/skills/lib.dll", "недопустимый файл" };
        yield return new object[] { "scripts/hack.gd", "недопустимый файл" };
        _ = obj;
    }

    [Theory]
    [MemberData(nameof(BadPackages))]
    public void P28_dangerous_paths_and_foreign_files_reject_the_whole_package_and_change_nothing(string path, string reason)
    {
        using var source = new TempDirectory();
        using var dir = new TempDirectory();
        var good = ContentTestKit.Library(source.Path);
        var (_, skill, _, _) = Content(good);
        var library = ContentTestKit.Library(dir.Path);
        var before = Snapshot(dir.Path);
        var package = ContentExporter.ExportPackage(good, new[] { skill }, "x", string.Empty, "0.20.0", Created);
        using var stream = new MemoryStream();
        stream.Write(package);
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true))
        {
            using var s = zip.CreateEntry(path).Open();
            s.Write(Encoding.UTF8.GetBytes("{}"));
        }

        var preview = ContentImporter.Inspect(library, "bad.hxp", stream.ToArray());
        Assert.True(preview.Rejected);
        Assert.Contains(preview.Problems, p => p.Message.Contains(reason, StringComparison.Ordinal));
        Assert.False(ContentImporter.Install(library, preview).Ok);
        Assert.Equal(before, Snapshot(dir.Path));
    }

    [Fact]
    public void P28_damaged_json_unknown_effects_missing_dependencies_wrong_checksums_and_big_packages_are_reported()
    {
        using var source = new TempDirectory();
        using var dir = new TempDirectory();
        var good = ContentTestKit.Library(source.Path);
        var (_, skill, unit, _) = Content(good);
        var library = ContentTestKit.Library(dir.Path);
        var before = Snapshot(dir.Path);

        var broken = ContentImporter.Inspect(library, "broken.json", Encoding.UTF8.GetBytes("{ \"schemaVersion\": 1, \"id\": "));
        Assert.True(broken.Rejected);
        Assert.Contains(broken.Problems, p => p.Message.Contains("повреждённый JSON", StringComparison.Ordinal));

        var unknown = Json(skill).Replace("\"damage\"", "\"teleport\"", StringComparison.Ordinal);
        var unknownPreview = ContentImporter.Inspect(library, "teleport.json", Encoding.UTF8.GetBytes(unknown));
        Assert.Equal(ImportStatus.Invalid, Assert.Single(unknownPreview.Entries).Status);
        Assert.Contains(unknownPreview.Entries[0].Issues, i => i.Message.Contains("неизвестный тип эффекта «teleport»", StringComparison.Ordinal));

        // A package whose unit needs a skill that is neither inside nor in the library.
        var objects = new[] { (unit, Encoding.UTF8.GetBytes(Json(unit))) };
        var manifest = new PackageManifest("x", string.Empty, "0.20.0", Created,
            objects.Select(o => new PackageObject("unit", o.unit.Id, o.unit.Name, 1, 1, $"content/units/{o.unit.Id}.json", ContentExporter.Sha256(o.Item2), o.Item2.Length)).ToList(),
            Array.Empty<ContentRef>());
        var manifestBytes = Encoding.UTF8.GetBytes(manifest.ToJson().ToJsonString());
        var missing = ContentImporter.Inspect(library, "missing.hxp", Zip(("manifest.json", manifestBytes), ($"content/units/{unit.Id}.json", objects[0].Item2)));
        Assert.False(missing.Rejected);
        Assert.Equal(ImportStatus.Invalid, Assert.Single(missing.Entries).Status);
        Assert.Contains(missing.Entries[0].Issues, i => i.Message.Contains("нет ни в пакете, ни в библиотеке", StringComparison.Ordinal));

        var tampered = Zip(("manifest.json", manifestBytes), ($"content/units/{unit.Id}.json", Encoding.UTF8.GetBytes(Json(unit).Replace("175", "999", StringComparison.Ordinal))));
        var tamperedPreview = ContentImporter.Inspect(library, "tampered.hxp", tampered);
        Assert.True(tamperedPreview.Rejected);
        Assert.Contains(tamperedPreview.Problems, p => p.Message.Contains("контрольная сумма", StringComparison.Ordinal));

        var unlisted = ContentImporter.Inspect(library, "unlisted.hxp", Zip(("manifest.json", manifestBytes), ($"content/units/{unit.Id}.json", objects[0].Item2), ("content/skills/extra.json", Encoding.UTF8.GetBytes("{}"))));
        Assert.Contains(unlisted.Problems, p => p.Message.Contains("не указан в manifest.json", StringComparison.Ordinal));

        var noManifest = ContentImporter.Inspect(library, "nomanifest.hxp", Zip(($"content/units/{unit.Id}.json", objects[0].Item2)));
        Assert.Contains(noManifest.Problems, p => p.Message.Contains("нет manifest.json", StringComparison.Ordinal));

        var notZip = ContentImporter.Inspect(library, "junk.hxp", Encoding.UTF8.GetBytes("PK\u0003\u0004 this is not a zip"));
        Assert.True(notZip.Rejected);

        var small = new ContentLibrary(library.Registry, library.User, library.Limits with { MaxPackageBytes = 512 });
        small.LoadBuiltIn(ContentTestKit.BuiltInFiles());
        var big = ContentImporter.Inspect(small, "big.hxp", ContentExporter.ExportPackage(good, new[] { unit }, "big", string.Empty, "0.20.0", Created));
        Assert.True(big.Rejected);
        Assert.Contains(big.Problems, p => p.Message.Contains("слишком большой", StringComparison.Ordinal));

        foreach (var p in new[] { broken, unknownPreview, missing, tamperedPreview, unlisted, noManifest, notZip, big })
        {
            Assert.False(ContentImporter.Install(library, p).Ok);
        }

        Assert.Equal(before, Snapshot(dir.Path));
    }

    [Fact]
    public void A_failed_installation_leaves_the_storage_exactly_as_it_was()
    {
        using var mine = new TempDirectory();
        using var theirs = new TempDirectory();
        var library = ContentTestKit.Library(mine.Path);
        var (_, skill, _, _) = Content(library);
        var other = ContentTestKit.Library(theirs.Path);
        var (otherTile, _, otherUnit, otherMap) = Content(other);
        File.Copy(library.User!.PathOf("skills", skill.Id), other.User!.PathOf("skills", skill.Id));
        other.Reload();
        var package = ContentExporter.ExportPackage(other, new[] { otherMap, otherUnit, other.Find(skill.Ref)! }, "x", string.Empty, "0.20.0", Created);
        var before = Snapshot(mine.Path);

        var preview = ContentImporter.Inspect(library, "x.hxp", package);
        preview.Entries.Single(e => e.Item.Ref == skill.Ref).Resolution = ImportResolution.Copy;
        var writes = 0;
        var result = ContentImporter.Install(library, preview, item =>
        {
            if (++writes == 3)
            {
                throw new IOException("диск переполнен (имитация)");
            }
        });

        Assert.False(result.Ok);
        Assert.Contains("хранилище возвращено в исходное состояние", result.Message, StringComparison.Ordinal);
        Assert.Equal(before, Snapshot(mine.Path));
        Assert.Null(library.Find(otherTile.Ref));
    }

    [Fact]
    public void P29_an_older_schema_is_migrated_with_a_backup_and_a_newer_one_is_left_untouched()
    {
        using var dir = new TempDirectory();
        IContentData Read(System.Text.Json.Nodes.JsonObject o, IssueList i) => TileData.Read(o, i);
        var registry = ContentTypeRegistry.CreateDefault();
        registry.Register(new ContentKind
        {
            Id = "marker", Title = "Метки", Singular = "метка", Folder = "markers", Order = 90, SchemaVersion = 2,
            Read = Read, CreateDefault = () => new TileData(),
            Migrate = (from, data, issues) =>
            {
                if (from != 1)
                {
                    return null;
                }

                // Schema 1 called the colour «tint».
                var copy = (System.Text.Json.Nodes.JsonObject)data.DeepClone();
                copy["color"] = copy["tint"]?.GetValue<string>() ?? "#CCCCCF";
                copy.Remove("tint");
                return copy;
            },
        });
        var folder = Path.Combine(dir.Path, "content", "markers");
        Directory.CreateDirectory(folder);
        const string old = "{\"schemaVersion\":1,\"id\":\"marker_old\",\"kind\":\"marker\",\"name\":\"Старая метка\",\"revision\":3,\"data\":{\"category\":\"ground\",\"passable\":true,\"level\":0,\"tint\":\"#123456\",\"effects\":[]}}";
        const string future = "{\"schemaVersion\":7,\"id\":\"marker_new\",\"kind\":\"marker\",\"name\":\"Из будущего\",\"revision\":1,\"data\":{}}";
        File.WriteAllText(Path.Combine(folder, "marker_old.json"), old);
        File.WriteAllText(Path.Combine(folder, "marker_new.json"), future);

        var library = ContentTestKit.Library(dir.Path, registry);
        var migrated = library.Find("marker", "marker_old")!;
        Assert.Equal("#123456", ((TileData)migrated.Data).Color); // not an empty object
        Assert.Equal(("Старая метка", 3), (migrated.Name, migrated.Revision));
        var rewritten = File.ReadAllText(Path.Combine(folder, "marker_old.json"));
        Assert.Contains("\"schemaVersion\": 2", rewritten, StringComparison.Ordinal);
        var backup = Directory.GetFiles(Path.Combine(dir.Path, "backups"), "marker_old.json", SearchOption.AllDirectories).Single();
        Assert.Contains("migration-schema1", backup, StringComparison.Ordinal);
        Assert.Equal(old, File.ReadAllText(backup));

        Assert.Null(library.Find("marker", "marker_new"));
        Assert.Contains(library.Problems, p => p.Path.EndsWith("marker_new.json", StringComparison.Ordinal) && p.Issues.Any(i => i.Message.Contains("новее", StringComparison.Ordinal)));
        Assert.Equal(future, File.ReadAllText(Path.Combine(folder, "marker_new.json")));
    }

    private sealed class PulseHandler : IEffectHandler
    {
        public string Type => "pulse";

        public void Apply(EffectInvocation invocation, EffectResolver resolver) =>
            resolver.DealDamage(invocation.Target, invocation.Source, invocation.Effect.Number("amount") * 2, invocation.Tick, HexTactics.Core.Combat.DamageOrigin.Skill, HexTactics.Core.Combat.DamageTypes.Untyped);
    }

    [Fact]
    public void P30_a_new_effect_type_registered_in_code_reaches_editors_validation_files_and_the_shared_executor()
    {
        using var dir = new TempDirectory();
        var effects = EffectTypeRegistry.CreateDefault();
        effects.Register(new EffectTypeDescriptor("pulse", "Импульс", "Двойной урон (тестовый тип)", EffectContext.Skill, new[]
        {
            EffectTypeRegistry.Param("amount", "Сила", HexTactics.Content.Schema.FieldKind.Number, 4.0) with { Min = 0, Max = 100 },
        }));
        var library = ContentTestKit.Library(dir.Path, ContentTypeRegistry.CreateDefault(effects));
        Assert.Contains(library.Registry.Effects.For(EffectContext.Skill), t => t.Type == "pulse"); // the editors list this

        var skill = library.Create(ContentTypeRegistry.Skill, "Импульс");
        var data = (SkillData)skill.Data;
        var pulse = library.Registry.Effects.Find("pulse")!.CreateDefault();
        data.Cells.Add(new SkillCellData { Q = 0, R = -1 });
        data.Cells[0].Effects.Add(pulse);
        pulse.Set("amount", -1.0);
        Assert.Contains(library.Validate(skill), i => i.IsError && i.Path.EndsWith("amount", StringComparison.Ordinal));
        pulse.Set("amount", 5.0);
        Assert.True(library.Save(skill).Ok);

        var export = ContentExporter.ExportPackage(library, new[] { skill }, "pulse", string.Empty, "0.20.0", Created);
        using var other = new TempDirectory();
        var target = ContentTestKit.Library(other.Path, ContentTypeRegistry.CreateDefault(effects));
        var preview = ContentImporter.Inspect(target, "pulse.hxp", export);
        Assert.True(ContentImporter.Install(target, preview).Ok);

        var runtime = RuntimeFactory.BuildSkill(target.Find(skill.Ref)!, target.Registry.Effects);
        var sim = new GameSimulation(HexTactics.Tests.Shared.TestMaps.Empty(), new SimulationSettings
        {
            Skills = new Dictionary<string, HexTactics.Core.Skills.SkillDefinition> { [runtime.Id] = runtime },
            DefaultSkillSet = new[] { runtime.Id },
        });
        sim.Effects.Register(new PulseHandler());
        var caster = sim.Spawn(SpawnRequest.AtCell(C(5, 7), HexTactics.Core.Units.Side.Player, HexDirection.North)).Unit!.Value;
        var enemy = sim.Spawn(SpawnRequest.AtCell(C(5, 6), HexTactics.Core.Units.Side.Enemy, HexDirection.South)).Unit!.Value;
        Assert.True(sim.UseSkill(caster, runtime.Id, HexDirection.North).Success);
        sim.Step();
        Assert.Equal(90, sim.State.GetUnit(enemy).CurrentHealth, 9);
    }

    [Fact]
    public void Limits_bound_the_cost_of_a_skill_and_can_be_lowered_but_not_raised_past_the_hard_caps()
    {
        var library = ContentTestKit.Library(null);
        var skill = library.Create(ContentTypeRegistry.Skill, "Дорогой");
        var data = (SkillData)skill.Data;
        var hit = library.Registry.Effects.Find(EffectTypes.Damage)!.CreateDefault();
        hit.Set("hits", 20.0);
        data.BaseEffects.Add(hit);
        data.BaseEffects.Add(hit.Clone());
        data.BaseEffects.Add(hit.Clone());
        for (var q = -3; q <= 3; q++)
        {
            for (var r = -3; r <= 3; r++)
            {
                if (new HexCoord(q, r).DistanceTo(default) is > 0 and <= 3)
                {
                    data.Cells.Add(new SkillCellData { Q = q, R = r });
                }
            }
        }

        Assert.Contains(library.Validate(skill), i => i.IsError && i.Message.Contains("слишком дорогой навык", StringComparison.Ordinal)); // 36 × 3 × 20 > 1024

        var issues = new IssueList();
        var limits = ContentLimits.FromJson("{\"maxSkillHitEvents\": 100, \"maxPackageBytes\": 999999999999, \"warp\": 1}", issues);
        Assert.Equal(100, limits.MaxSkillHitEvents);
        Assert.Equal(ContentLimits.Default.MaxPackageBytes, limits.MaxPackageBytes);
        Assert.Equal(2, issues.Items.Count);
        Assert.Equal(ContentLimits.Default, ContentLimits.FromJson("{ broken", new IssueList()));
    }
}
