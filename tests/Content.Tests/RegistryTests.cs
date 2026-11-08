using System;
using System.Linq;
using HexTactics.Content.Kinds;
using HexTactics.Content.Registry;
using HexTactics.Content.Schema;
using HexTactics.Core.Effects;
using HexTactics.Simulation;
using HexTactics.Tests.Support;
using Xunit;

namespace HexTactics.Content.Tests;

/// <summary>Categories and effect types are registered once and used everywhere (spec part 4 §2.2, §9.2, §12.1 p.2).</summary>
public class RegistryTests
{
    [Fact]
    public void The_default_catalog_has_maps_units_skills_tiles_in_this_order()
    {
        var registry = ContentTypeRegistry.CreateDefault();
        Assert.Equal(new[] { "Карты", "Юниты", "Дерево видов", "Теги", "Навыки", "Гексы", "Типы урона", "Правила защиты", "Профили защиты и сопротивлений", "Правила боя", "Характеристики" }, registry.Visible.Select(k => k.Title));
        Assert.Equal(new[] { "maps", "units", "species", "traits", "skills", "tiles", "damage_types", "defense_rules", "resistance_profiles", "combat_rulesets", "unit_stats" }, registry.Visible.Select(k => k.Folder));
        Assert.Equal(new[] { "damage_type", "defense_rule", "resistance_profile", "combat_ruleset", "unit_stat" }, registry.Visible.Where(k => k.Group == ContentTypeRegistry.MechanicsGroup).Select(k => k.Id));
        Assert.Equal(new[] { "species", "trait" }, registry.Visible.Where(k => k.Group == ContentTypeRegistry.OriginGroup).Select(k => k.Id));
    }

    [Fact]
    public void A_new_category_is_registered_without_touching_the_others_and_hidden_until_available()
    {
        var registry = ContentTypeRegistry.CreateDefault();
        registry.Register(new ContentKind
        {
            Id = "status", Title = "Статусы", Singular = "статус", Folder = "statuses", Order = 50, Available = false,
            Read = (o, i) => TileData.Read(o, i), CreateDefault = () => new TileData(),
        });
        Assert.Equal(12, registry.All.Count); // + species, traits, characteristics (v0.29.0) and the new one
        Assert.Equal(11, registry.Visible.Count);
        Assert.Throws<InvalidOperationException>(() => registry.Register(registry.Find("map")!));
    }

    [Fact]
    public void Every_registered_effect_type_has_a_simulation_handler()
    {
        var sim = new GameSimulation(HexTactics.Tests.Shared.TestMaps.Empty());
        foreach (var type in EffectTypeRegistry.CreateDefault().All)
        {
            Assert.True(sim.Effects.CanExecute(type.Type), type.Type);
        }
    }

    [Fact]
    public void Effect_defaults_come_from_the_field_schemas()
    {
        var damage = EffectTypeRegistry.CreateDefault().Find(EffectTypes.Damage)!;
        var effect = damage.CreateDefault();
        Assert.Equal(10.0, effect.Get("amount"));
        Assert.Equal("enemies", effect.Get("targets"));
        var spec = effect.ToSpec(EffectTypeRegistry.CreateDefault());
        Assert.Equal(10, spec.Number("amount"));
        Assert.Equal(EffectTargets.Enemies, spec.Targets);
        Assert.Equal(FieldKind.Number, damage.FindField("amount")!.Kind);
    }

    [Fact]
    public void Every_effect_usable_on_tiles_has_tile_fields_with_a_trigger_and_a_side_filter_and_no_source_formulas()
    {
        var registry = EffectTypeRegistry.CreateDefault();
        var onTiles = registry.For(EffectContext.Tile).ToList();
        Assert.Equal(new[] { "damage", "heal", "buff", "debuff", "periodic" }, onTiles.Select(t => t.Type));
        foreach (var type in onTiles)
        {
            var fields = type.FieldsFor(EffectContext.Tile).Select(f => f.Key).ToList();
            Assert.Contains("trigger", fields);
            Assert.Contains("sides", fields);
            Assert.DoesNotContain("formula", fields);
            Assert.DoesNotContain("targets", fields);
            Assert.DoesNotContain("hits", fields);
        }

        var stay = registry.Find(EffectTypes.Periodic)!.FindField("trigger", EffectContext.Tile)!;
        Assert.Equal(new[] { "stay", "enter", "exit" }, stay.Options.Select(o => o.Value));
        Assert.DoesNotContain(registry.Find(EffectTypes.Damage)!.FindField("trigger", EffectContext.Tile)!.Options, o => o.Value == "stay");
    }
}
