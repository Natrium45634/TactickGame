using System.IO;
using System.Linq;
using HexTactics.Content.Kinds;
using HexTactics.Content.Registry;
using HexTactics.Content.Runtime;
using HexTactics.Core.Effects;
using HexTactics.Tests.Support;
using Xunit;

namespace HexTactics.Content.Tests;

/// <summary>Block as a registry characteristic (spec part 7 §7.1, §7.4, §10; M7-55).</summary>
public class BlockContentTests
{
    [Fact]
    public void Block_is_a_registry_characteristic_with_base_0_that_passives_and_tags_change()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var stat = (UnitStatData)library.Find(ContentTypeRegistry.UnitStat, UnitStatData.Block)!.Data;
        Assert.Equal((0.0, 0.0, 0), (stat.BaseValue, stat.Min, stat.Decimals));
        Assert.Equal(0, RuntimeFactory.BuildUnit(library.Find(ContentTypeRegistry.Unit, RuntimeFactory.DefaultUnitId)!, library).Stats.BlockCapacity);

        var shield = library.Create(ContentTypeRegistry.Trait, "Щит");
        ((TraitData)shield.Data).Effects.Add(new PassiveData { Id = "block", Name = "Щит", Operation = PassiveOperations.StatAddFlat, Stat = UnitStatData.Block, Value = 1 });
        Assert.True(library.Save(shield).Ok);
        var guard = library.Create(ContentTypeRegistry.Unit, "Страж");
        var data = (UnitData)guard.Data;
        data.CustomStats[UnitStatData.Block] = 2;
        data.Traits.Add(shield.Id);
        Assert.True(library.Save(guard).Ok);
        Assert.Equal(3, RuntimeFactory.BuildUnit(ContentTestKit.Library(dir.Path).Find(guard.Ref)!, library).Stats.BlockCapacity); // 2 own + 1 «Щит»
    }

    [Fact]
    public void M7_55_old_units_without_block_get_0_and_a_block_debuff_is_a_registered_effect()
    {
        using var dir = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(dir.Path, "content", "units"));
        File.WriteAllText(Path.Combine(dir.Path, "content", "units", "unit_v32.json"),
            "{\"schemaVersion\":9,\"id\":\"unit_v32\",\"kind\":\"unit\",\"name\":\"Боец v0.32\",\"revision\":1,\"data\":{\"maxHealth\":100}}");
        var library = ContentTestKit.Library(dir.Path);
        Assert.Equal(0, RuntimeFactory.BuildUnit(library.Find(ContentTypeRegistry.Unit, "unit_v32")!, library).Stats.BlockCapacity);

        var debuff = library.Registry.Effects.Find("debuff")!;
        Assert.Contains(debuff.FindField("stat")!.Options!, o => o.Value == Stats.Block);
        var effect = debuff.CreateDefault();
        effect.Set("stat", Stats.Block);
        Assert.False(debuff.FindField("mode")!.VisibleWhen!(effect)); // whole slots: points only
        Assert.True(debuff.FindField("delta")!.VisibleWhen!(effect));
    }
}
