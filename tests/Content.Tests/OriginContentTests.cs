using System;
using System.IO;
using System.Linq;
using HexTactics.Content.Kinds;
using HexTactics.Content.Library;
using HexTactics.Content.Model;
using HexTactics.Content.Registry;
using HexTactics.Content.Runtime;
using HexTactics.Content.Transfer;
using HexTactics.Core.Combat;
using HexTactics.Core.Hex;
using HexTactics.Core.Spawning;
using HexTactics.Core.Units;
using HexTactics.Simulation;
using HexTactics.Tests.Support;
using Xunit;

namespace HexTactics.Content.Tests;

/// <summary>Species tree, shared traits, packs and registry characteristics (spec part 6 §5; acceptance P6-23…P6-29, P6-32).</summary>
public class OriginContentTests
{
    private const string Water = "user.damage.water";

    private static PassiveData E(string id, string op, double value, string type = "", string stat = "attackPower") =>
        new() { Id = id, Name = id, Operation = op, Value = value, DamageType = type, Stat = stat };

    private static ContentItem Save(ContentLibrary library, string kind, string name, Action<IContentData> fill)
    {
        var item = library.Create(kind, name);
        fill(item.Data);
        var saved = library.Save(item);
        Assert.True(saved.Ok, string.Join("; ", saved.Issues.Select(i => i.Message)));
        return library.Find(item.Ref)!;
    }

    private static ContentLibrary Demo(string dir, out ContentItem beast, out ContentItem wolf, out ContentItem moonWolf, out ContentItem slime, out ContentItem metalSlime)
    {
        var library = ContentTestKit.Library(dir);
        var water = library.Create(ContentTypeRegistry.DamageType, "Вода");
        ((DamageTypeData)water.Data).Family = DamageTypes.Magical;
        var w = library.Save(water.CopyAs(Water, ContentOrigin.User, "Вода"));
        Assert.True(w.Ok);
        beast = Save(library, ContentTypeRegistry.Species, "Зверь", _ => { });
        var beastId = beast.Id;
        wolf = Save(library, ContentTypeRegistry.Species, "Волк", d => ((SpeciesData)d).Parent = beastId);
        var wolfId = wolf.Id;
        moonWolf = Save(library, ContentTypeRegistry.Species, "Лунный волк", d =>
        {
            ((SpeciesData)d).Parent = wolfId;
            ((SpeciesData)d).Effects.Add(E("agility", PassiveOperations.StatAddPercent, 5, stat: UnitStatData.Agility));
        });
        slime = Save(library, ContentTypeRegistry.Species, "Слизь", d =>
        {
            var s = (SpeciesData)d;
            s.Effects.Add(E("pierce", PassiveOperations.IncomingDamagePercent, -90, DamageTypes.Piercing));
            s.Effects.Add(E("water", PassiveOperations.IncomingDamagePercent, -50, Water));
        });
        var slimeId = slime.Id;
        metalSlime = Save(library, ContentTypeRegistry.Species, "Металлическая слизь", d =>
        {
            ((SpeciesData)d).Parent = slimeId;
            ((SpeciesData)d).Effects.Add(E("metal_water", PassiveOperations.IncomingDamagePercent, -25, Water));
        });
        return library;
    }

    [Fact]
    public void P6_23_a_parent_effect_acts_once_and_the_child_adds_its_own_water_minus_25_to_minus_75()
    {
        using var dir = new TempDirectory();
        var library = Demo(dir.Path, out _, out _, out _, out _, out var metal);
        var unit = new UnitData { Species = metal.Id };
        var active = PassiveEffects.Active(unit, library);
        Assert.Equal(new[] { "pierce", "water", "metal_water" }, active.Select(a => a.Passive.Id));
        Assert.Equal(new[] { "вид «Слизь»", "вид «Слизь»", "вид «Металлическая слизь»" }, active.Select(a => a.Source));
        var types = RuntimeFactory.BuildDamageTypes(library);
        var mods = PassiveEffects.DamageModifiers(unit, library);
        Assert.Equal(0.25, ModifierMath.DamageMultiplier(mods, DamageModifierKind.Incoming, types[Water], 100), 12); // −50 % −25 %, not ×0.5×0.75 twice
        Assert.Equal(0.1, ModifierMath.DamageMultiplier(mods, DamageModifierKind.Incoming, types[DamageTypes.Piercing], 100), 12);

        // A child effect with the same key replaces (or disables) the parent's — never both.
        var replace = Save(library, ContentTypeRegistry.Species, "Каменная слизь", d =>
        {
            ((SpeciesData)d).Parent = metal.Id;
            ((SpeciesData)d).Effects.Add(E("water", PassiveOperations.IncomingDamagePercent, -10, Water));
            ((SpeciesData)d).Effects.Add(new PassiveData { Id = "pierce", Name = "без брони", Operation = PassiveOperations.IncomingDamagePercent, Inherit = PassiveData.InheritDisable });
        });
        var stone = PassiveEffects.Active(new UnitData { Species = replace.Id }, library);
        Assert.Equal(new[] { "metal_water", "water" }, stone.Select(a => a.Passive.Id));
        Assert.Equal(-10, stone.Single(a => a.Passive.Id == "water").Passive.Value);
    }

    [Fact]
    public void P6_24_a_descendant_is_every_ancestor_for_filters_without_three_species()
    {
        using var dir = new TempDirectory();
        var library = Demo(dir.Path, out var beast, out var wolf, out var moon, out _, out _);
        Assert.True(PassiveEffects.IsA(library, moon.Id, wolf.Id));
        Assert.True(PassiveEffects.IsA(library, moon.Id, beast.Id));
        Assert.False(PassiveEffects.IsA(library, wolf.Id, moon.Id));
        var unit = Save(library, ContentTypeRegistry.Unit, "Лунный", d => ((UnitData)d).Species = moon.Id);
        var stats = RuntimeFactory.BuildUnit(unit, library).Stats;
        Assert.Equal(new[] { moon.Id, wolf.Id, beast.Id }, stats.Species);
        Assert.True(stats.IsA(beast.Id));
    }

    [Fact]
    public void P6_28_a_species_cycle_and_conflicting_traits_are_refused_with_a_clear_error()
    {
        using var dir = new TempDirectory();
        var library = Demo(dir.Path, out var beast, out var wolf, out _, out _, out _);
        var b = library.Find(beast.Ref)!.CopyAs(beast.Id, ContentOrigin.User, beast.Name); // a draft: the library keeps its tree
        ((SpeciesData)b.Data).Parent = wolf.Id; // Зверь → Волк → Зверь
        Assert.Contains(library.Validate(b), i => i.IsError && i.Message.Contains("цикл", StringComparison.Ordinal));

        var healthy = Save(library, ContentTypeRegistry.Trait, "Здоров", d => ((TraitData)d).ExclusiveGroup = "health");
        var hurt = Save(library, ContentTypeRegistry.Trait, "Ранен", d => ((TraitData)d).ExclusiveGroup = "health");
        var unit = library.Create(ContentTypeRegistry.Unit, "Боец");
        ((UnitData)unit.Data).Traits.AddRange(new[] { healthy.Id, hurt.Id });
        Assert.Contains(library.Validate(unit), i => i.IsError && i.Message.Contains("взаимоисключающие", StringComparison.Ordinal));
        Assert.False(library.Save(unit).Ok);

        var wolfOnly = Save(library, ContentTypeRegistry.Trait, "Вожак", d => ((TraitData)d).AllowedSpecies.Add(wolf.Id));
        var slimeUnit = library.Create(ContentTypeRegistry.Unit, "Слизняк");
        ((UnitData)slimeUnit.Data).Traits.Add(wolfOnly.Id);
        Assert.Contains(library.Validate(slimeUnit), i => i.IsError && i.Message.Contains("допустим только для видов", StringComparison.Ordinal));
    }

    [Fact]
    public void P6_29_a_unique_trait_twice_acts_once_and_a_stacking_one_up_to_its_limit()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var veteran = Save(library, ContentTypeRegistry.Trait, "Матёрый", d => ((TraitData)d).Effects.Add(E("atk", PassiveOperations.StatAddPercent, 10)));
        var scar = Save(library, ContentTypeRegistry.Trait, "Шрам", d =>
        {
            var t = (TraitData)d;
            t.StackPolicy = TraitData.StackStack;
            t.MaxStacks = 2;
            t.Effects.Add(E("atk", PassiveOperations.StatAddFlat, 1));
        });
        var unit = new UnitData { AttackPower = 10 };
        unit.Traits.AddRange(new[] { veteran.Id, veteran.Id, scar.Id, scar.Id, scar.Id });
        Assert.Equal(13, PassiveEffects.Stat(unit, library, "attackPower").Final); // 10 × 1.1 + 2 (not 3)
        Assert.Equal(new[] { 1, 2 }, PassiveEffects.ActiveTraits(unit, library).Select(t => t.Times));
    }

    [Fact]
    public void P6_32_agility_is_a_number_changed_by_tags_with_no_invented_effect()
    {
        using var dir = new TempDirectory();
        var library = Demo(dir.Path, out _, out _, out var moon, out _, out _);
        var veteran = Save(library, ContentTypeRegistry.Trait, "Матёрый", d =>
        {
            ((TraitData)d).Effects.Add(E("agi", PassiveOperations.StatAddPercent, 5, stat: UnitStatData.Agility));
            ((TraitData)d).Effects.Add(E("atk", PassiveOperations.StatAddPercent, 10));
        });
        var wounded = Save(library, ContentTypeRegistry.Trait, "Раненый", d =>
        {
            ((TraitData)d).Effects.Add(E("agi", PassiveOperations.StatAddPercent, -15, stat: UnitStatData.Agility));
            ((TraitData)d).Effects.Add(E("hp", PassiveOperations.StatAddPercent, -25, stat: "maxHealth"));
        });
        var plain = new UnitData();
        Assert.Equal(100, PassiveEffects.Stat(plain, library, UnitStatData.Agility).Final);
        var unit = new UnitData { Species = moon.Id };
        unit.Traits.AddRange(new[] { veteran.Id, wounded.Id });
        var agility = PassiveEffects.Stat(unit, library, UnitStatData.Agility);
        Assert.Equal(95, agility.Final); // 100 × (1 + 5 + 5 − 15 %)
        Assert.Equal(new[] { "вид «Лунный волк»", "тег «Матёрый»", "тег «Раненый»" }, agility.Sources.Select(s => s.Source)); // P6-26: each source
        Assert.Equal((11.0, 75.0), (PassiveEffects.Stat(unit, library, "attackPower").Final, PassiveEffects.Stat(unit, library, "maxHealth").Final));
        var stats = RuntimeFactory.BuildUnit(Save(library, ContentTypeRegistry.Unit, "Волк", d => { ((UnitData)d).Species = moon.Id; ((UnitData)d).Traits.Add(veteran.Id); }), library).Stats;
        Assert.Equal((1.0, 3.0), (stats.MoveSpeedHexPerSecond, stats.AttackRatePerSecond)); // agility moves nothing
    }

    [Fact]
    public void P6_25_the_leader_gets_3_percent_per_other_living_wolf_of_its_pack_recounted_on_death()
    {
        using var dir = new TempDirectory();
        var library = Demo(dir.Path, out _, out var wolf, out var moon, out _, out _);
        var leader = Save(library, ContentTypeRegistry.Trait, "Вожак", d =>
        {
            ((TraitData)d).Effects.Add(new PassiveData { Id = "pack", Name = "Стая", Operation = PassiveOperations.CountMatchingUnits, Stat = "attackPower", Value = 3, CountSpecies = wolf.Id });
        });
        var alpha = Save(library, ContentTypeRegistry.Unit, "Вожак стаи", d => { var u = (UnitData)d; u.Species = wolf.Id; u.Traits.Add(leader.Id); u.PackId = "north"; u.AttackPower = 100; });
        var member = Save(library, ContentTypeRegistry.Unit, "Волк стаи", d => { var u = (UnitData)d; u.Species = moon.Id; u.PackId = "north"; });
        var stranger = Save(library, ContentTypeRegistry.Unit, "Чужой волк", d => { var u = (UnitData)d; u.Species = wolf.Id; u.PackId = "south"; });

        var sim = new GameSimulation(HexTactics.Tests.Shared.TestMaps.Empty());
        UnitId Spawn(ContentItem item, int col) => sim.Spawn(SpawnRequest.AtCell(HexCoord.FromOffset(col, 5), Side.Player, HexDirection.North) with { Profile = RuntimeFactory.BuildUnit(item, library).ToProfile() }).Unit!.Value;
        var a = Spawn(alpha, 1);
        var members = Enumerable.Range(2, 4).Select(c => Spawn(member, c)).ToList(); // four other wolves of the pack (a Lunar wolf is a Wolf)
        Spawn(stranger, 7);                                                           // another pack: not counted
        Assert.Equal(112, sim.State.GetUnit(a).Stats.AttackPower, 9);               // +12 %
        sim.State.ApplyDamage(members[0], 1000);
        sim.Step();
        Assert.Equal(109, sim.State.GetUnit(a).Stats.AttackPower, 9);               // one died: +9 %
        Assert.Equal(100, alpha.Data is UnitData u ? u.AttackPower : 0);              // the library definition never changes
    }

    [Fact]
    public void A_schema_6_unit_reaches_schema_7_and_a_package_carries_species_traits_and_stats()
    {
        using var dir = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(dir.Path, "content", "units"));
        File.WriteAllText(Path.Combine(dir.Path, "content", "units", "unit_v28.json"),
            "{\"schemaVersion\":6,\"id\":\"unit_v28\",\"kind\":\"unit\",\"name\":\"Боец v0.28\",\"revision\":1,\"data\":{\"maxHealth\":100,\"passives\":[]}}");
        var old = (UnitData)ContentTestKit.Library(dir.Path).Find(ContentTypeRegistry.Unit, "unit_v28")!.Data;
        Assert.Equal((string.Empty, 0, string.Empty, 0), (old.Species, old.Traits.Count, old.PackId, old.CustomStats.Count));

        using var source = new TempDirectory();
        using var target = new TempDirectory();
        var library = Demo(source.Path, out _, out _, out var moon, out _, out _);
        var trait = Save(library, ContentTypeRegistry.Trait, "Матёрый", d => ((TraitData)d).Effects.Add(E("agi", PassiveOperations.StatAddPercent, 5, stat: UnitStatData.Agility)));
        var unit = Save(library, ContentTypeRegistry.Unit, "Лунный волк", d => { var u = (UnitData)d; u.Species = moon.Id; u.Traits.Add(trait.Id); u.CustomStats[UnitStatData.Agility] = 120; });
        var bytes = ContentExporter.ExportPackage(library, new[] { unit }, "Волк", string.Empty, "0.29.0", DateTimeOffset.UnixEpoch);
        var clean = ContentTestKit.Library(target.Path);
        var preview = ContentImporter.Inspect(clean, "wolf.hxp", bytes);
        Assert.False(preview.Rejected, preview.Summary);
        Assert.True(ContentImporter.Install(clean, preview).Ok);
        var imported = ContentTestKit.Library(target.Path);
        var data = (UnitData)imported.Find(unit.Ref)!.Data;
        Assert.Equal(132, PassiveEffects.Stat(data, imported, UnitStatData.Agility).Final); // 120 × (1 + 5 % + 5 %)
        Assert.Equal(3, PassiveEffects.SpeciesChain(imported, data.Species).Count);
    }
}
