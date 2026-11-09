using System;
using System.Linq;
using HexTactics.Content.Kinds;
using HexTactics.Content.Registry;
using HexTactics.Content.Schema;
using HexTactics.Core.Combat;
using HexTactics.Core.Hex;
using HexTactics.Editor.Library;
using HexTactics.Editor.Maps;
using HexTactics.Editor.Mechanics;
using HexTactics.Editor.Units;
using HexTactics.Tests.Support;
using Xunit;

namespace HexTactics.Editor.Tests;

/// <summary>«Базовая атака» in the developer mode (spec part 7 §8.5, M7-50, M7-53).</summary>
public class BasicAttackEditorTests
{
    [Fact]
    public void M7_50_the_area_is_edited_on_the_board_turned_with_the_facing_and_undone()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var model = new BasicAttackEditorModel(library, library.Create(ContentTypeRegistry.BasicAttack, "Копьё"));
        Assert.Equal(new[] { new HexCoord(0, -1) }, model.Attack.Cells);
        Assert.Null(model.Click(new HexCoord(0, -2)));
        Assert.Equal("Область: 2 гекс. (перед, второй перед). Поворачивается вместе со взглядом юнита; юнит сам не поворачивается и не преследует врагов.", model.Preview()[0]);

        model.Facing = HexDirection.SouthEast; // a hex drawn in front of a unit looking south-east is stored as the front of north
        Assert.Equal(new HexCoord(0, -1).RotateFor(HexDirection.SouthEast), model.ToDrawn(new HexCoord(0, -1)));
        Assert.Null(model.Click(new HexCoord(0, -1).RotateFor(HexDirection.SouthEast)));
        Assert.Equal(new[] { new HexCoord(0, -2) }, model.Attack.Cells); // the front was removed in the stored frame
        Assert.NotNull(model.Click(default));                               // the unit's own hex
        Assert.NotNull(model.Click(new HexCoord(0, -5)));                   // too far
        Assert.True(model.Undo());
        Assert.Equal(2, model.Attack.Cells.Count);

        Assert.NotNull(model.SetMaxTargets("7"));
        Assert.NotNull(model.SetPower("-5"));
        Assert.Null(model.SetMaxTargets("2"));
        Assert.Null(model.SetPower("150 %"));
        model.SetPriority(BasicAttackData.LowestHealth);
        model.SetFlag("whileMoving", false);
        Assert.Contains("Цель: враг с наименьшим здоровьем, до 2 врагов за удар.", model.Preview());
        Assert.Contains(model.Preview(), l => l.Contains("только стоя", StringComparison.Ordinal));
        Assert.True(model.Save().Ok);
        Assert.Equal(TargetPriority.LowestHealth, ((BasicAttackData)library.Find(model.Item.Ref)!.Data).ToDefinition("x", "x").Priority);
    }

    [Fact]
    public void M7_53_a_user_type_is_offered_at_once_and_the_unit_shows_its_base_attack_in_one_line()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var fire = library.Create(ContentTypeRegistry.DamageType, "Огонь");
        ((DamageTypeData)fire.Data).Family = DamageTypes.Magical;
        Assert.True(library.Save(fire).Ok);
        Assert.Contains(ReferenceCatalog.Options(library, ContentTypeRegistry.DamageType, ReferenceFilter.Any, null), o => o.Value == fire.Id);
        var attack = new BasicAttackEditorModel(library, library.Create(ContentTypeRegistry.BasicAttack, "Дыхание"));
        Assert.Null(attack.SetDamageType(fire.Id));
        Assert.NotNull(attack.SetDamageType("user_damage_none"));
        Assert.True(attack.Save().Ok);
        Assert.Contains(attack.Preview(), l => l.Contains("огонь", StringComparison.Ordinal));

        var unit = new UnitEditorModel(library, library.Create(ContentTypeRegistry.Unit, "Дракончик"));
        Assert.Contains("Базовая атака: 3 удар/с · передний гекс · 1 враг · 10 урона до защиты (на картах с автоатакой).", CombatPreviewText.Describe(unit.Unit, library));
        Assert.Null(unit.TrySetField(UnitData.Fields.Single(f => f.Key == "basicAttack"), attack.Item.Id));
        Assert.Null(unit.TrySetField(UnitData.Fields.Single(f => f.Key == "attackPower"), "20"));
        Assert.Contains(CombatPreviewText.Describe(unit.Unit, library), l => l.StartsWith("Базовая атака: 3 удар/с · передний гекс · 1 враг · 20 урона", StringComparison.Ordinal));
        Assert.True(unit.Save().Ok);
        Assert.Contains(attack.Users(), u => u.Id == unit.Item.Id);
        Assert.Null(unit.TrySetField(UnitData.Fields.Single(f => f.Key == "autoAttackEnabled"), false));
        Assert.Contains("Базовая атака: автоатака выключена — юнит бьёт только по приказу атаки.", CombatPreviewText.Describe(unit.Unit, library));
    }

    [Fact]
    public void A_map_turns_automatic_attacks_on_explicitly_with_undo()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var map = new MapEditorModel(library, library.Create(ContentTypeRegistry.Map, "Арена"));
        Assert.False(map.Map.AutoAttacks); // D-170: off until the author turns it on
        map.SetAutoAttacks(true);
        Assert.True(map.Map.AutoAttacks);
        Assert.True(map.Undo());
        Assert.False(map.Map.AutoAttacks);
    }
}
