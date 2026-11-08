using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Content.Kinds;
using HexTactics.Content.Library;
using HexTactics.Content.Model;
using HexTactics.Content.Registry;
using HexTactics.Content.Runtime;
using HexTactics.Content.Transfer;
using HexTactics.Core.Combat;
using HexTactics.Core.Hex;
using HexTactics.Core.Orders;
using HexTactics.Core.Spawning;
using HexTactics.Core.Units;
using HexTactics.Simulation;
using HexTactics.Tests.Support;
using Xunit;

namespace HexTactics.Content.Tests;

/// <summary>
/// The part 6 chain end to end (spec part 6 §12.5): a user element, a tag, a unit, a skill, a package, a clean library and the
/// same damage in a match (P6-55); tags that grant reflections (P6-54); conflicting tags in an import (P6-53); the direction
/// rules of v0.31.0 (§6.3 p.2, §6.4).
/// </summary>
public class Part6EndToEndTests
{
    private static ContentItem Save(ContentLibrary library, string kind, string name, Action<IContentData> fill)
    {
        var item = library.Create(kind, name);
        fill(item.Data);
        var saved = library.Save(item);
        Assert.True(saved.Ok, string.Join("; ", saved.Issues.Select(i => i.Message)));
        return library.Find(item.Ref)!;
    }

    private static HexCoord C(int col, int row) => HexCoord.FromOffset(col, row);

    /// <summary>A match on the prototype map with the content's own settings (as the game builds it).</summary>
    private static GameSimulation Match(ContentLibrary library, string mapId = RuntimeFactory.DefaultMapId)
    {
        var content = RuntimeFactory.BuildGame(library, library.Find(ContentTypeRegistry.Map, mapId)!);
        return new GameSimulation(content.Map, new SimulationSettings
        {
            DefaultStats = content.DefaultUnit.Stats,
            Skills = content.Skills,
            DefaultSkillSet = content.DefaultUnit.Skills,
            DefaultUnitId = content.DefaultUnit.Id,
            TileEffects = content.TileEffects,
            DamageTypes = content.DamageTypes,
            CombatRules = content.CombatRules,
        });
    }

    private static UnitId Spawn(GameSimulation sim, ContentLibrary library, ContentItem? unit, HexCoord cell, Side side, HexDirection facing = HexDirection.North)
    {
        var request = SpawnRequest.AtCell(cell, side, facing);
        if (unit is not null)
        {
            request = request with { Profile = RuntimeFactory.BuildGame(library).Units[unit.Id].ToProfile() };
        }

        return sim.Spawn(request).Unit!.Value;
    }

    /// <summary>Normal attack 20 of «Вода» and a skill hit 10 of «Вода», both +50 % by the tag: 30 and 15 HP.</summary>
    private static (double Attack, double Skill) Numbers(ContentLibrary library, ContentItem mage)
    {
        var sim = Match(library);
        var caster = Spawn(sim, library, mage, C(3, 5), Side.Player);
        var dummy = Spawn(sim, library, null, C(3, 4), Side.Enemy, HexDirection.South);
        Assert.True(sim.UseSkill(caster, ((UnitData)mage.Data).Skills[0], HexDirection.North).Success);
        for (var i = 0; i < 3; i++)
        {
            sim.Step();
        }

        var skill = 100 - sim.State.GetUnit(dummy).CurrentHealth;
        var target = Spawn(sim, library, null, C(6, 5), Side.Enemy, HexDirection.South);
        var striker = Spawn(sim, library, mage, C(6, 6), Side.Player);
        Assert.True(sim.ApplyOrder(new IndividualOrder(striker, Array.Empty<HexCoord>(), HexDirection.North, target)).Success);
        for (var i = 0; i < 90 && sim.State.GetUnit(target).CurrentHealth > 99.99; i++)
        {
            sim.Step();
        }

        return (100 - sim.State.GetUnit(target).CurrentHealth, skill);
    }

    [Fact]
    public void P6_55_water_tag_unit_and_skill_survive_a_package_into_a_clean_library_with_the_same_damage()
    {
        using var source = new TempDirectory();
        using var target = new TempDirectory();
        var library = ContentTestKit.Library(source.Path);

        // 1. A user element «Вода» of the magical family (no code, no built-in element table).
        var water = Save(library, ContentTypeRegistry.DamageType, "Вода", d => ((DamageTypeData)d).Family = DamageTypes.Magical);
        // 2. The shared tag «Гидромант»: +50 % outgoing «Вода».
        var hydro = Save(library, ContentTypeRegistry.Trait, "Гидромант", d => ((TraitData)d).Effects.Add(new PassiveData
        {
            Id = "water_power", Name = "Сила воды", Operation = PassiveOperations.OutgoingDamagePercent, DamageType = water.Id, Value = 50,
        }));
        // 3. A skill of «Вода»: 10 on the hex in front.
        var wave = Save(library, ContentTypeRegistry.Skill, "Волна", d =>
        {
            var s = (SkillData)d;
            var hit = library.Registry.Effects.Find("damage")!.CreateDefault();
            hit.Set("damageType", water.Id);
            hit.Set("amount", 10.0);
            s.BaseEffects.Add(hit);
            s.Cells.Add(new SkillCellData { Q = 0, R = -1 });
        });
        // 4. The unit with the tag: a normal attack of «Вода» 20 and the skill.
        var mage = Save(library, ContentTypeRegistry.Unit, "Гидромант", d =>
        {
            var u = (UnitData)d;
            (u.AttackDamageType, u.AttackPower, u.AttackRate) = (water.Id, 20, 1);
            u.Traits.Add(hydro.Id);
            u.Skills.Clear();
            u.Skills.Add(wave.Id);
        });
        var before = Numbers(library, mage);
        Assert.Equal((30.0, 15.0), before);

        // 5. The package carries the type, the tag and the skill with the unit; a clean library gets the same ids and numbers.
        var bytes = ContentExporter.ExportPackage(library, new[] { mage }, "Вода", string.Empty, "0.31.0", DateTimeOffset.UnixEpoch);
        var clean = ContentTestKit.Library(target.Path);
        var preview = ContentImporter.Inspect(clean, "Вода.hxp", bytes);
        Assert.False(preview.Rejected, preview.Summary);
        Assert.Equal(new[] { water.Ref, hydro.Ref, wave.Ref, mage.Ref }.OrderBy(r => r.ToString()), preview.Entries.Select(e => e.Item.Ref).OrderBy(r => r.ToString()));
        Assert.True(ContentImporter.Install(clean, preview).Ok);
        var imported = ContentTestKit.Library(target.Path);
        Assert.Equal("Вода", imported.Find(water.Ref)!.Name);
        Assert.Equal(before, Numbers(imported, imported.Find(mage.Ref)!));
    }

    [Fact]
    public void P6_54_tags_that_grant_reflection_to_both_sides_make_no_endless_chain()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var mirror = Save(library, ContentTypeRegistry.DefenseRule, "Зеркало", d =>
        {
            var r = (DefenseRuleData)d;
            r.Actions.Clear();
            var action = library.Registry.DefenseActions.Find("redirect_as_damage")!.CreateDefault();
            action.Set("fraction", 1.0);
            r.Actions.Add(action);
        });
        var mirrored = Save(library, ContentTypeRegistry.Trait, "Зеркальный", d => ((TraitData)d).Effects.Add(new PassiveData
        {
            Id = "mirror", Name = "Зеркало", Operation = PassiveOperations.GrantDefenseRule, Rule = mirror.Id,
        }));
        var a = Save(library, ContentTypeRegistry.Unit, "Первый", d => { var u = (UnitData)d; u.Traits.Add(mirrored.Id); u.MaxHealth = 1000; });
        var b = Save(library, ContentTypeRegistry.Unit, "Второй", d => { var u = (UnitData)d; u.Traits.Add(mirrored.Id); u.MaxHealth = 1000; u.Side = "enemy"; });
        var sim = Match(library);
        var x = Spawn(sim, library, a, C(3, 5), Side.Player);
        var y = Spawn(sim, library, b, C(3, 4), Side.Enemy, HexDirection.South);
        var hit = sim.Damage.Resolve(sim.Damage.Create(x, y, DamageTypes.Blunt, 40, DamageOrigin.NormalAttack), sim.Tick);
        Assert.Equal(0, hit.HpLoss, 9);                                       // y reflects the whole hit
        Assert.Equal(960, sim.State.GetUnit(x).CurrentHealth, 9);             // x takes 40 once: the reflection is not reflected back
        Assert.Equal(1000, sim.State.GetUnit(y).CurrentHealth, 9);
        Assert.True(sim.Damage.Results.Count <= 2);
        Assert.True(sim.Damage.Results.Sum(r => r.D0) <= 80 + 1e-9);          // the budget of D0 still holds
    }

    [Fact]
    public void P6_53_a_conflicting_tag_in_an_import_offers_choices_and_a_copy_keeps_the_users_tag()
    {
        using var mine = new TempDirectory();
        using var theirs = new TempDirectory();
        var library = ContentTestKit.Library(mine.Path);
        var tag = Save(library, ContentTypeRegistry.Trait, "Ветеран", d => ((TraitData)d).Category = "feature");
        var other = ContentTestKit.Library(theirs.Path);
        var copy = tag.Clone();
        ((TraitData)copy.Data).Category = "class"; // the same id, different content
        Assert.True(other.Save(copy).Ok);
        var unit = Save(other, ContentTypeRegistry.Unit, "Сержант", d => ((UnitData)d).Traits.Add(tag.Id));
        var package = ContentExporter.ExportPackage(other, new[] { unit }, "Их", string.Empty, "0.31.0", DateTimeOffset.UnixEpoch);

        var preview = ContentImporter.Inspect(library, "theirs.hxp", package);
        var conflict = preview.Entries.Single(e => e.Item.Ref == tag.Ref);
        Assert.Equal(ImportStatus.Conflict, conflict.Status);
        Assert.Equal(new[] { ImportResolution.Skip, ImportResolution.Replace, ImportResolution.Copy }, conflict.Options);
        Assert.Equal(ImportResolution.Skip, conflict.Resolution); // the default never replaces the user's tag

        conflict.Resolution = ImportResolution.Copy;
        var installed = ContentImporter.Install(library, preview);
        Assert.True(installed.Ok, installed.Message);
        var newTag = installed.Installed.Single(i => i.Kind.Id == ContentTypeRegistry.Trait);
        var newUnit = installed.Installed.Single(i => i.Kind.Id == ContentTypeRegistry.Unit);
        Assert.NotEqual(tag.Id, newTag.Id);
        Assert.Equal(new[] { newTag.Id }, ((UnitData)newUnit.Data).Traits); // the unit follows the copy
        Assert.Equal("feature", ((TraitData)library.Find(tag.Ref)!.Data).Category);
    }

    [Fact]
    public void Melee_only_surprise_and_the_attackers_surprise_bonus_are_data_of_the_rules_and_the_unit()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var rules = Save(library, ContentTypeRegistry.CombatRuleset, "Ближний бой", d =>
        {
            var r = (CombatRulesetData)d;
            (r.SurpriseEnabled, r.SurpriseMeleeOnly) = (true, true);
        });
        var reloaded = ContentTestKit.Library(dir.Path);
        Assert.True(((CombatRulesetData)reloaded.Find(rules.Ref)!.Data).SurpriseMeleeOnly);
        Assert.True(RuntimeFactory.BuildCombatRules(reloaded, rules.Id).SurpriseMeleeOnly);
        Assert.Contains("внезапность вблизи", reloaded.Find(rules.Ref)!.Data.Summary(), StringComparison.Ordinal);

        var scout = Save(library, ContentTypeRegistry.Unit, "Разведчик", d => ((UnitData)d).Passives.Add(new PassiveData
        {
            Id = "ambush", Name = "Засада", Operation = PassiveOperations.SurpriseChanceDelta, Value = 15,
        }));
        Assert.Equal(15, RuntimeFactory.BuildUnit(scout, library).Stats.SurpriseChanceBonus);
        var bad = scout.Clone();
        ((UnitData)bad.Data).Passives[0].Value = 500;
        Assert.Contains(library.Validate(bad), i => i.IsError);
    }
}
