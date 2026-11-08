using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using HexTactics.Content.Kinds;
using HexTactics.Content.Library;
using HexTactics.Content.Registry;
using HexTactics.Content.Schema;
using HexTactics.Content.Transfer;
using HexTactics.Core.Combat;
using HexTactics.Editor.Library;
using HexTactics.Editor.Mechanics;
using HexTactics.Editor.Units;
using HexTactics.Tests.Support;
using Xunit;

namespace HexTactics.Editor.Tests;

/// <summary>
/// The full editor of part 6 (spec part 6 §9.2, §9.5, §11; P6-47…P6-50): the 1000-roll check, a reproducible series of 100
/// events, melee-only surprise, the mass assignment of a tag, «активные эффекты» and a catalog of 500 user types.
/// </summary>
public class FullEditorTests
{
    private static ContentItem Save(ContentLibrary library, string kind, string name, Action<IContentData> fill)
    {
        var item = library.Create(kind, name);
        fill(item.Data);
        var saved = library.Save(item);
        Assert.True(saved.Ok, string.Join("; ", saved.Issues.Select(i => i.Message)));
        return library.Find(item.Ref)!;
    }

    [Fact]
    public void The_1000_roll_check_is_reproducible_and_follows_the_formula()
    {
        var a = CombatRulesetEditorModel.RollTest(125);
        Assert.Equal(a, CombatRulesetEditorModel.RollTest(125));
        Assert.Equal((1000, 0), (a.Normal + a.Crit + a.SuperCrit, a.Normal));
        Assert.InRange(a.SuperCrit, 200, 300); // 25 % of 1000
        Assert.Equal(1000, CombatRulesetEditorModel.RollTest(250).SuperCrit);
        Assert.InRange(CombatRulesetEditorModel.RollTest(35).Crit, 300, 400);
        Assert.StartsWith("1000 бросков при C 125 п.п.: обычный 0, крит ", CombatRulesetEditorModel.DescribeRolls(a), StringComparison.Ordinal);
        Assert.EndsWith("(по формуле 0 / 750 / 250).", CombatRulesetEditorModel.DescribeRolls(a), StringComparison.Ordinal);
    }

    [Fact]
    public void A_series_of_100_events_is_the_same_for_the_same_seed_and_ignores_a_forced_outcome()
    {
        var library = ContentTestKit.Library(null);
        var setup = new SandboxSetup { DamageTypeId = DamageTypes.Blunt, Amount = 20, Sector = 2, EnemiesAround = 1, ForceSurprise = true };
        var first = HitSandbox.Batch(library, setup);
        Assert.Equal(first, HitSandbox.Batch(library, setup));
        Assert.Equal(100, first.Hits);
        Assert.InRange(first.Surprised, 60, 90);                  // rear flank 75 %: rolled, not forced
        Assert.Equal((20.0, 30.0), (first.MinLoss, first.MaxLoss)); // 20 or 20 × 1.5
        var rear = HitSandbox.Batch(library, setup with { Sector = 3 });
        Assert.Equal(100, rear.Surprised);
        Assert.StartsWith("100 событий (seed 20261106): внезапных ", HitSandbox.Describe(first), StringComparison.Ordinal);
        Assert.Equal(7, HitSandbox.Batch(library, setup with { Seed = 7 }).Seed);
    }

    [Fact]
    public void Melee_only_surprise_is_shown_in_words_and_a_distant_hit_gets_none()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var rules = new CombatRulesetEditorModel(library, library.Create(ContentTypeRegistry.CombatRuleset, "Ближний бой"));
        rules.SetSurpriseEnabled(true);
        rules.SetSurpriseMeleeOnly(true);
        Assert.Contains(rules.Preview(), l => l.Contains("Только для удара с соседнего гекса.", StringComparison.Ordinal));
        var near = HitSandbox.Run(library, new SandboxSetup { Amount = 20, Sector = 3, EnemiesAround = 1 }, rulesetDraft: rules.Item).With.Result;
        var far = HitSandbox.Run(library, new SandboxSetup { Amount = 20, Sector = 3, EnemiesAround = 1, AttackerAdjacent = false }, rulesetDraft: rules.Item).With.Result;
        Assert.Equal((true, 30.0), (near.Surprised, near.D0));
        Assert.Equal((false, 20.0), (far.Surprised, far.D0));
    }

    [Fact]
    public void A_saved_tag_is_assigned_to_many_units_at_once_or_to_none()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var wolf = Save(library, ContentTypeRegistry.Species, "Волк", _ => { });
        var a = Save(library, ContentTypeRegistry.Unit, "Альфа", d => ((UnitData)d).Species = wolf.Id);
        var b = Save(library, ContentTypeRegistry.Unit, "Бета", d => ((UnitData)d).Species = wolf.Id);
        var c = Save(library, ContentTypeRegistry.Unit, "Чужак", _ => { });
        var tag = new TraitEditorModel(library, library.Create(ContentTypeRegistry.Trait, "Вожак"));
        Assert.NotEmpty(tag.AssignTo(new[] { a.Id }).Refusals); // not saved yet
        Assert.Null(tag.Toggle("allowedSpecies", wolf.Id));
        Assert.True(tag.Save().Ok);

        var refused = tag.AssignTo(new[] { a.Id, b.Id, c.Id }); // «Чужак» is not a wolf: nothing changes
        Assert.Empty(refused.Assigned);
        Assert.Contains(refused.Refusals, r => r.Contains("«Чужак»", StringComparison.Ordinal));
        Assert.Empty(tag.UnitsWithTrait());
        Assert.Contains(tag.AssignTo(new[] { HexTactics.Content.Runtime.RuntimeFactory.DefaultUnitId }).Refusals, r => r.Contains("встроенный", StringComparison.Ordinal));

        var done = tag.AssignTo(new[] { a.Id, b.Id });
        Assert.Equal(new[] { "Альфа", "Бета" }, done.Assigned);
        Assert.Equal(2, tag.UnitsWithTrait().Count);
        Assert.Equal(2, library.Find(a.Ref)!.Revision);
        Assert.DoesNotContain(tag.AssignableUnits(), u => u.Id == a.Id);
        var unit = new UnitEditorModel(library, library.Find(a.Ref)!);
        Assert.Equal("вид «Волк» · тегов: 1 · активных эффектов: 0", unit.Sections().Features);
    }

    [Fact]
    public void P6_48_a_catalog_of_500_user_types_is_searched_quickly_and_the_unit_shows_only_its_own_values()
    {
        using var dir = new TempDirectory();
        var folder = Path.Combine(dir.Path, "content", "damage_types");
        Directory.CreateDirectory(folder);
        for (var i = 0; i < 500; i++)
        {
            File.WriteAllText(Path.Combine(folder, $"user_type_{i:000}.json"),
                $"{{\"schemaVersion\":1,\"id\":\"user_type_{i:000}\",\"kind\":\"damage_type\",\"name\":\"Стихия {i}\",\"revision\":1,\"data\":{{\"family\":\"{DamageTypes.Magical}\",\"strategy\":\"resistance_affected\"}}}}");
        }

        var library = ContentTestKit.Library(dir.Path);
        Assert.True(library.Items(ContentTypeRegistry.DamageType).Count >= 500);
        var browser = new LibraryBrowser(library) { Kind = ContentTypeRegistry.DamageType };
        var watch = Stopwatch.StartNew();
        foreach (var query in new[] { "С", "Ст", "Сти", "Стих", "Стихия 4", "Стихия 49", "Стихия 499" })
        {
            browser.Search = query;
            _ = browser.Rows();
        }

        Assert.Single(browser.Rows());
        Assert.True(watch.ElapsedMilliseconds < 1500, $"seven searches took {watch.ElapsedMilliseconds} ms");
        watch.Restart();
        var options = ReferenceCatalog.Options(library, ContentTypeRegistry.DamageType, ReferenceFilter.Any, null);
        Assert.True(watch.ElapsedMilliseconds < 1000, $"type options took {watch.ElapsedMilliseconds} ms");
        Assert.True(options.Count >= 500);

        // The unit editor shows only its own values and families by default, not 500 rows (spec part 6 §9.4).
        var unit = new UnitEditorModel(library, library.Create(ContentTypeRegistry.Unit, "Маг"));
        Assert.Null(unit.SetResistance("user_type_123", "40"));
        var view = unit.ResistanceView(string.Empty, showAll: false, new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal));
        Assert.True(view.Rows.Count < 20, $"{view.Rows.Count} rows by default");
        Assert.Contains(view.Rows, r => r.Row.Key == "user_type_123");
        Assert.True(view.Hidden >= 499);
    }

    [Fact]
    public void P6_50_a_new_or_imported_tag_is_offered_at_once_and_renaming_or_exporting_keeps_its_id()
    {
        using var dir = new TempDirectory();
        using var other = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var tag = Save(library, ContentTypeRegistry.Trait, "Ветеран", _ => { });
        Assert.Contains(ReferenceCatalog.Options(library, ContentTypeRegistry.Trait, ReferenceFilter.Any, null), o => o.Value == tag.Id);
        Assert.True(library.Rename(ContentTypeRegistry.Trait, tag.Id, "Старый ветеран").Ok);
        Assert.Equal("Старый ветеран", library.Find(tag.Ref)!.Name);
        var package = ContentExporter.ExportPackage(library, new[] { library.Find(tag.Ref)! }, "Тег", string.Empty, "0.31.0", DateTimeOffset.UnixEpoch);

        var clean = ContentTestKit.Library(other.Path);
        var preview = ContentImporter.Inspect(clean, "tag.hxp", package);
        Assert.True(ContentImporter.Install(clean, preview).Ok);
        var offered = ReferenceCatalog.Options(clean, ContentTypeRegistry.Trait, ReferenceFilter.Any, null).Single(o => o.Value == tag.Id);
        Assert.Contains("Старый ветеран", offered.Label, StringComparison.Ordinal);
    }
}
