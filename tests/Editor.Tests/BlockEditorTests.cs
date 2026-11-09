using System;
using HexTactics.Content.Kinds;
using HexTactics.Content.Registry;
using HexTactics.Editor.Units;
using HexTactics.Tests.Support;
using Xunit;

namespace HexTactics.Editor.Tests;

/// <summary>«Блок» of a unit (spec part 7 §7.4): one number, the same registry value, its final in the preview.</summary>
public class BlockEditorTests
{
    [Fact]
    public void The_block_of_a_unit_is_one_whole_number_with_undo_and_a_line_in_the_battle_preview()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var unit = new UnitEditorModel(library, library.Create(ContentTypeRegistry.Unit, "Страж"));
        Assert.DoesNotContain(CombatPreviewText.Describe(unit.Unit, library), l => l.StartsWith("Блок", StringComparison.Ordinal)); // 0: no line
        Assert.NotNull(unit.SetCustomStat(UnitStatData.Block, "-1"));
        Assert.Null(unit.SetCustomStat(UnitStatData.Block, "2.6"));
        Assert.Equal(3, unit.Unit.CustomStats[UnitStatData.Block]); // whole slots
        Assert.Contains("Блок: 3 — пока стоит, удерживает до 3 двигающихся врагов на их гексах.", CombatPreviewText.Describe(unit.Unit, library));
        Assert.True(unit.Undo());
        Assert.False(unit.Unit.CustomStats.ContainsKey(UnitStatData.Block));
        Assert.Null(unit.SetCustomStat(UnitStatData.Block, "1"));
        Assert.True(unit.Save().Ok);
        Assert.Equal(1, HexTactics.Content.Runtime.RuntimeFactory.BuildUnit(library.Find(unit.Item.Ref)!, library).Stats.BlockCapacity);
    }
}
