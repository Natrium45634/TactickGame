using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Combat;
using HexTactics.Core.Hex;
using HexTactics.Core.Map;
using HexTactics.Core.Skills;
using HexTactics.Core.Spawning;
using HexTactics.Core.Units;
using HexTactics.Tests.Shared;
using Xunit;

namespace HexTactics.Simulation.Tests;

/// <summary>Constant damage modifiers of passives in the one pipeline (spec part 6 §4.2–4.4, §8; acceptance §12.3 P6-20…P6-22).</summary>
public class PassiveDamageTests
{
    private const string Water = "test.damage.water";

    private static readonly IReadOnlyDictionary<string, DamageTypeDefinition> Types = new Dictionary<string, DamageTypeDefinition>(DamageTypes.BuiltIn)
    {
        [Water] = new(Water, "Вода", DamageTypes.Magical, DamageStrategy.ResistanceAffected, System.Array.Empty<string>()),
    };

    private static HexCoord C(int col, int row) => HexCoord.FromOffset(col, row);

    private static DamageModifier Out(string? type, double percent, string group = "outgoing", string source = "пассив") => new(DamageModifierKind.Outgoing, type, percent, group, source);

    private static DamageModifier In(string? type, double percent, string group = "incoming", string source = "пассив") => new(DamageModifierKind.Incoming, type, percent, group, source);

    private sealed class Arena
    {
        public GameSimulation Sim { get; } = new(TestMaps.Empty(), new SimulationSettings { DamageTypes = Types });

        public UnitId Add(HexCoord cell, Side side, UnitStats stats) =>
            Sim.Spawn(SpawnRequest.AtCell(cell, side, HexDirection.North) with { Profile = new UnitProfile("test.unit", "Юнит", stats, new[] { SkillCatalog.LinearStrikeId }) }).Unit!.Value;

        public DamageResult Hit(UnitId? source, UnitId target, string type, double amount, DamageOrigin origin = DamageOrigin.Skill) =>
            Sim.Damage.Resolve(Sim.Damage.Create(source, target, type, amount, origin), Sim.Tick);
    }

    [Fact]
    public void P6_20_plus_50_percent_outgoing_water_turns_20_into_30_before_any_defense()
    {
        var a = new Arena();
        var mage = a.Add(C(5, 5), Side.Player, new UnitStats { DamageModifiers = new[] { Out(Water, 50, source: "пассив «Гидромант»") } });
        var target = a.Add(C(5, 4), Side.Enemy, new UnitStats { MaxHealth = 1000, Resistances = new ResistanceSet(new Dictionary<string, double> { [DamageTypes.Magical] = 50 }) });
        var water = a.Hit(mage, target, Water, 20);
        Assert.Equal((20.0, 1.5, 30.0), (water.BaseAmount, water.OutgoingMultiplier, water.D0));
        Assert.Equal(15, water.HpLoss, 9); // then R 50 %
        Assert.Equal("пассив «Гидромант»", water.Modifiers.Single().Source.Source);
        Assert.Equal(10, a.Hit(mage, target, DamageTypes.Slashing, 10).HpLoss, 9); // another type: unchanged
    }

    [Fact]
    public void P6_21_minus_25_percent_incoming_physical_turns_the_rest_40_after_R_A_and_flat_into_30()
    {
        var flat = new DefenseRule { Id = "flat", Trigger = DefenseTrigger.OnHit, Actions = new[] { new DefenseAction { Kind = DefenseActionKind.FlatReduction, Amount = 10 } } };
        var cap = new DefenseRule { Id = "cap", Trigger = DefenseTrigger.OnHit, Actions = new[] { new DefenseAction { Kind = DefenseActionKind.DamageCap, Amount = 25 } } };
        var a = new Arena();
        var stats = new UnitStats
        {
            MaxHealth = 1000,
            Resistances = new ResistanceSet(new Dictionary<string, double> { [DamageTypes.Physical] = 50 }),
            Armor = new ArmorSet(new Dictionary<string, double> { [ArmorSet.Global] = 100 }),
            DefenseRules = new[] { flat },
            DamageModifiers = new[] { In(DamageTypes.Physical, -25) },
        };
        var target = a.Add(C(5, 5), Side.Player, stats);
        var hit = a.Hit(null, target, DamageTypes.Blunt, 200); // 200 → R 100 → A 50 → flat 40 → ×0.75
        Assert.Equal((100.0, 50.0, 40.0, 0.75), (hit.AfterResistance, hit.AfterArmor, hit.AfterFlat, hit.IncomingMultiplier));
        Assert.Equal(30, hit.HpLoss, 9);

        var capped = new Arena();
        var t2 = capped.Add(C(5, 5), Side.Player, stats with { DefenseRules = new[] { flat, cap } });
        Assert.Equal(25, capped.Hit(null, t2, DamageTypes.Blunt, 400).HpLoss, 9); // 400 → 200 → 100 → 90 → 67.5 → cap 25 (after the passive)
        Assert.Equal(10, capped.Hit(null, t2, DamageTypes.Magical, 40).HpLoss, 9); // magical: no −25 % (40 → A 20 → flat 10)
    }

    [Fact]
    public void P6_22_percents_of_one_group_add_up_and_groups_multiply()
    {
        var a = new Arena();
        var same = a.Add(C(5, 5), Side.Player, new UnitStats { DamageModifiers = new[] { Out(Water, 50), Out(Water, 20) } });
        var split = a.Add(C(6, 5), Side.Player, new UnitStats { DamageModifiers = new[] { Out(Water, 50, "a"), Out(Water, 20, "b") } });
        var target = a.Add(C(5, 4), Side.Enemy, new UnitStats { MaxHealth = 1000 });
        Assert.Equal(34, a.Hit(same, target, Water, 20).HpLoss, 9);   // +70 % — ×1.7, not ×1.5×1.2
        Assert.Equal(36, a.Hit(split, target, Water, 20).HpLoss, 9);  // two groups: ×1.5 × ×1.2

        var defender = a.Add(C(4, 5), Side.Player, new UnitStats { MaxHealth = 1000, DamageModifiers = new[] { In(Water, -50), In(DamageTypes.Magical, -25) } });
        Assert.Equal(10, a.Hit(null, defender, Water, 40).HpLoss, 9); // −75 % in the standard group: ×0.25 (family filter covers the subtype)
    }

    [Fact]
    public void A_reduction_beyond_100_percent_gives_zero_never_healing()
    {
        var a = new Arena();
        var target = a.Add(C(5, 5), Side.Player, new UnitStats { DamageModifiers = new[] { In(null, -150) } });
        a.Sim.State.ApplyDamage(target, 50);
        var hit = a.Hit(null, target, DamageTypes.Slashing, 40);
        Assert.Equal((0.0, 0.0, 0.0), (hit.IncomingMultiplier, hit.HpLoss, hit.Healed));
        Assert.Equal(50, a.Sim.State.GetUnit(target).CurrentHealth, 9);
    }

    [Fact]
    public void Filters_all_types_incoming_skips_true_damage_and_outgoing_covers_it()
    {
        var a = new Arena();
        var target = a.Add(C(5, 5), Side.Player, new UnitStats { MaxHealth = 1000, DamageModifiers = new[] { In(null, -50) } });
        Assert.Equal(20, a.Hit(null, target, DamageTypes.Piercing, 40).HpLoss, 9);
        Assert.Equal(40, a.Hit(null, target, DamageTypes.True, 40).HpLoss, 9); // true damage: only an explicit filter

        var striker = a.Add(C(5, 4), Side.Enemy, new UnitStats { DamageModifiers = new[] { Out(null, 25) } });
        var other = a.Add(C(6, 4), Side.Player, new UnitStats { MaxHealth = 1000 });
        Assert.Equal(50, a.Hit(striker, other, DamageTypes.True, 40).HpLoss, 9);
    }

    [Fact]
    public void Outgoing_passives_act_on_normal_attacks_and_skills_only_not_on_surfaces_statuses_or_reflections()
    {
        var a = new Arena();
        var mage = a.Add(C(5, 5), Side.Player, new UnitStats { DamageModifiers = new[] { Out(null, 100) } });
        var target = a.Add(C(5, 4), Side.Enemy, new UnitStats { MaxHealth = 1000 });
        Assert.Equal(20, a.Hit(mage, target, DamageTypes.Blunt, 10, DamageOrigin.NormalAttack).HpLoss, 9);
        Assert.Equal(10, a.Hit(mage, target, DamageTypes.Blunt, 10, DamageOrigin.Surface).HpLoss, 9);
        Assert.Equal(10, a.Hit(mage, target, DamageTypes.Blunt, 10, DamageOrigin.Status).HpLoss, 9);
        a.Sim.State.ApplyDamage(mage, a.Sim.State.GetUnit(mage).CurrentHealth); // a dead source adds nothing
        Assert.Equal(10, a.Hit(mage, target, DamageTypes.Blunt, 10).HpLoss, 9);
    }

    [Fact]
    public void A_health_condition_switches_the_passive_on_below_its_threshold()
    {
        var a = new Arena();
        var target = a.Add(C(5, 5), Side.Player, new UnitStats { DamageModifiers = new[] { In(null, -50) with { HealthBelowPercent = 50 } } });
        Assert.Equal(40, a.Hit(null, target, DamageTypes.Slashing, 40).HpLoss, 9); // 100 % HP: off
        Assert.Equal(40, a.Hit(null, target, DamageTypes.Slashing, 40).HpLoss, 9); // 60 % HP: still off
        Assert.Equal(10, a.Hit(null, target, DamageTypes.Slashing, 20).HpLoss, 9); // 20 % HP: on, ×0.5
    }
}
