using System;
using System.Linq;
using HexTactics.Content.Kinds;
using HexTactics.Content.Registry;
using HexTactics.Core.Combat;
using HexTactics.Editor.Mechanics;
using HexTactics.Editor.Units;
using HexTactics.Tests.Support;
using Xunit;

namespace HexTactics.Editor.Tests;

/// <summary>«Особенности → Пассивы» of the unit editor and «откуда взялось» (spec part 6 §4, §9.2, §11).</summary>
public class PassiveEditorTests
{
    [Fact]
    public void A_passive_is_added_from_a_template_edited_field_by_field_and_refusals_change_nothing()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var unit = new UnitEditorModel(library, library.Create(ContentTypeRegistry.Unit, "Гидромант"));
        var (id, refusal) = unit.AddPassive(PassiveOperations.OutgoingDamagePercent);
        Assert.Null(refusal);
        Assert.Equal(("p1", 20.0), (id, unit.Unit.Passives.Single().Value));
        Assert.Null(unit.SetPassive(id!, "name", "Водная мощь"));
        Assert.Null(unit.SetPassive(id!, "damageTypeId", DamageTypes.Magical));
        Assert.Null(unit.SetPassive(id!, "value", "50 %"));
        Assert.NotNull(unit.SetPassive(id!, "value", "5000"));          // above +1000 %
        Assert.NotNull(unit.SetPassive(id!, "damageTypeId", "user.damage.nothing"));
        Assert.NotNull(unit.SetPassive(id!, "healthBelowPercent", "120"));
        Assert.Equal((50.0, DamageTypes.Magical), (unit.Unit.Passives[0].Value, unit.Unit.Passives[0].DamageType));
        Assert.Equal("Водная мощь", unit.PassiveCards().Single().Name);
        Assert.Contains("+50 % исходящего урона: магический", unit.PassiveCards().Single().Summary, StringComparison.Ordinal);
        Assert.Equal("активных эффектов: 1", unit.Sections().Features);
        Assert.True(unit.Undo()); // the value back to 20
        Assert.Equal(20, unit.Unit.Passives[0].Value);
        Assert.Equal("p2", unit.AddPassive(PassiveOperations.StatAddPercent).Id);
        unit.RemovePassive("p1");
        Assert.Equal(new[] { "p2" }, unit.Unit.Passives.Select(p => p.Id));
        Assert.True(unit.Save().Ok);
        Assert.NotNull(unit.AddPassive("summon_dragon").Refusal);
    }

    [Fact]
    public void Where_it_came_from_shows_base_final_and_every_source_and_the_preview_uses_the_final_numbers()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var unit = new UnitEditorModel(library, library.Create(ContentTypeRegistry.Unit, "Матёрый волк"));
        var (attack, _) = unit.AddPassive(PassiveOperations.StatAddPercent);
        Assert.Null(unit.SetPassive(attack!, "name", "Матёрый"));
        Assert.Null(unit.SetPassive(attack!, "value", "10"));
        var (hp, _) = unit.AddPassive(PassiveOperations.StatAddPercent);
        Assert.Null(unit.SetPassive(hp!, "name", "Раненый"));
        Assert.Null(unit.SetPassive(hp!, "stat", "maxHealth"));
        Assert.Null(unit.SetPassive(hp!, "value", "-25"));
        var (a, _) = unit.AddPassive(PassiveOperations.IncomingDamagePercent);
        Assert.Null(unit.SetPassive(a!, "name", "Шкура"));
        Assert.Null(unit.SetPassive(a!, "damageTypeId", DamageTypes.Physical));
        var lines = unit.Breakdown();
        Assert.Contains("Сила атаки: 10 → 11 (+10 % · пассив «Матёрый»)", lines);
        Assert.Contains("Максимум здоровья: 100 → 75 (−25 % · пассив «Раненый»)", lines);
        Assert.Contains(lines, l => l.StartsWith("Получаемый урон (группа «incoming», проценты складываются): −25 % при совпадении типа = ×0.75", StringComparison.Ordinal));
        Assert.Contains("Обычная атака: 11 урона за удар", string.Join(" ", unit.CombatPreview()), StringComparison.Ordinal);
    }

    [Fact]
    public void The_sandbox_shows_the_attackers_outgoing_and_the_targets_incoming_passives_on_their_own_stages()
    {
        using var dir = new TempDirectory();
        var library = ContentTestKit.Library(dir.Path);
        var mage = new UnitEditorModel(library, library.Create(ContentTypeRegistry.Unit, "Гидромант"));
        var (id, _) = mage.AddPassive(PassiveOperations.OutgoingDamagePercent);
        Assert.Null(mage.SetPassive(id!, "name", "Вода +50"));
        Assert.Null(mage.SetPassive(id!, "value", "50"));
        // Unsaved: the sandbox takes the draft as the attacker.
        var text = HitSandbox.Describe(HitSandbox.Run(library, new SandboxSetup { DamageTypeId = DamageTypes.Magical, Amount = 20, EnemiesAround = 1 }, attackerDraft: mage.Item));
        Assert.Contains("1. Пассивы атакующего: сила 20 → 30 (×1.5: +50 % пассив «Вода +50»).", text, StringComparison.Ordinal);
        Assert.Contains("2. До защиты: сила удара D0 = 30 (магический).", text, StringComparison.Ordinal);
        Assert.True(mage.Save().Ok);

        var guard = new UnitEditorModel(library, library.Create(ContentTypeRegistry.Unit, "Стойкий"));
        var (g, _) = guard.AddPassive(PassiveOperations.IncomingDamagePercent);
        Assert.Null(guard.SetPassive(g!, "name", "Стойкость"));
        var report = HitSandbox.Run(library, new SandboxSetup { DamageTypeId = DamageTypes.Blunt, Amount = 40, AttackerUnitId = mage.Item.Id, EnemiesAround = 1 }, unitDraft: guard.Item);
        Assert.Equal(45, report.With.Result.HpLoss, 9); // the mage's +50 % is for every type: 40 → 60, then the guard's −25 %
        var stages = HitSandbox.Describe(report);
        Assert.Contains("Пассивы цели: 60 → 45 (×0.75: −25 % пассив «Стойкость»).", stages, StringComparison.Ordinal);
    }
}
