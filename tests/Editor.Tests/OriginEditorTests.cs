using System;
using System.Linq;
using HexTactics.Content.Kinds;
using HexTactics.Content.Registry;
using HexTactics.Content.Runtime;
using HexTactics.Editor.Mechanics;
using HexTactics.Editor.Units;
using HexTactics.Tests.Support;
using Xunit;

namespace HexTactics.Editor.Tests;

/// <summary>Species, traits and characteristics in the developer mode (spec part 6 §5, §9.1–9.3; P6-27, P6-28).</summary>
public class OriginEditorTests
{
    [Fact]
    public void Species_and_traits_are_built_in_their_editors_and_a_cycle_is_refused()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var beast = new SpeciesEditorModel(library, library.Create(ContentTypeRegistry.Species, "Зверь"));
        Assert.True(beast.Save().Ok);
        var wolf = new SpeciesEditorModel(library, library.Create(ContentTypeRegistry.Species, "Волк"));
        Assert.Null(wolf.SetParent(beast.Item.Id));
        var (id, refusal) = wolf.AddEffect(PassiveOperations.StatAddPercent);
        Assert.Null(refusal);
        Assert.Null(wolf.SetEffect(id!, "stat", UnitStatData.Agility));
        Assert.Null(wolf.SetEffect(id!, "value", "5"));
        Assert.Equal("Волк → Зверь", wolf.Lineage());
        Assert.Contains(wolf.Preview(), l => l.Contains("ловкость +5 %", StringComparison.Ordinal));
        Assert.True(wolf.Save().Ok);
        Assert.NotNull(beast.SetParent(wolf.Item.Id)); // Зверь → Волк → Зверь
        Assert.DoesNotContain(beast.Parents(), p => p.Value == wolf.Item.Id);

        var leader = new TraitEditorModel(library, library.Create(ContentTypeRegistry.Trait, "Вожак"));
        Assert.Null(leader.Toggle("allowedSpecies", wolf.Item.Id));
        var (count, _) = leader.AddEffect(PassiveOperations.CountMatchingUnits);
        Assert.Null(leader.SetEffect(count!, "countSpecies", wolf.Item.Id));
        Assert.NotNull(leader.SetEffect(count!, "stat", "maxHealth")); // a count bonus changes only attack, speed or rate in battle
        Assert.Null(leader.SetExclusive("rank", TraitData.PolicyHighest, 5));
        Assert.Contains(leader.Preview(), l => l.Contains("наибольшим приоритетом", StringComparison.Ordinal));
        Assert.True(leader.Save().Ok);

        var agility = new UnitStatEditorModel(library, library.Find(ContentTypeRegistry.UnitStat, UnitStatData.Agility)!);
        Assert.NotNull(agility.Set("base", "-5"));
        Assert.Contains(agility.Preview(), l => l.Contains("ничего не ускоряет", StringComparison.Ordinal));
    }

    [Fact]
    public void P6_27_changing_the_species_or_a_trait_recalculates_the_unit_at_once()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var slime = new SpeciesEditorModel(library, library.Create(ContentTypeRegistry.Species, "Слизь"));
        var (e, _) = slime.AddEffect(PassiveOperations.IncomingDamagePercent);
        Assert.Null(slime.SetEffect(e!, "value", "-75"));
        Assert.True(slime.Save().Ok);
        var veteran = new TraitEditorModel(library, library.Create(ContentTypeRegistry.Trait, "Матёрый"));
        var (t, _) = veteran.AddEffect(PassiveOperations.StatAddPercent);
        Assert.Null(veteran.SetEffect(t!, "value", "10"));
        Assert.True(veteran.Save().Ok);

        var unit = new UnitEditorModel(library, library.Create(ContentTypeRegistry.Unit, "Юнит"));
        Assert.Empty(unit.Breakdown());
        Assert.Null(unit.SetSpecies(slime.Item.Id));
        Assert.Contains(unit.Breakdown(), l => l.Contains("−75 %", StringComparison.Ordinal) && l.Contains("вид «Слизь»", StringComparison.Ordinal));
        Assert.Null(unit.AddTrait(veteran.Item.Id));
        Assert.Contains("Сила атаки: 10 → 11 (+10 % · тег «Матёрый»)", unit.Breakdown());
        Assert.Null(unit.AddTrait(veteran.Item.Id)); // a second time: listed, but acts once
        Assert.Equal(new[] { true, false }, unit.TraitChips().Select(c => c.Acts));
        unit.RemoveTrait(1);
        Assert.Null(unit.SetPackId("north"));
        Assert.Null(unit.SetCustomStat(UnitStatData.Agility, "120"));
        Assert.NotNull(unit.SetCustomStat(UnitStatData.Agility, "abc"));
        Assert.Contains("Ловкость: 120 (своя база юнита)", unit.Breakdown());
        Assert.True(unit.Save().Ok);
        var stats = RuntimeFactory.BuildUnit(library.Find(unit.Item.Ref)!, library).Stats;
        Assert.Equal(("north", 11.0), (stats.PackId, stats.AttackPower));

        // The shared trait changes every user after its save, the unit definition itself stays.
        Assert.Null(veteran.SetEffect(t!, "value", "20"));
        Assert.True(veteran.Save().Ok);
        Assert.Equal(12, RuntimeFactory.BuildUnit(library.Find(unit.Item.Ref)!, library).Stats.AttackPower);
    }
}
