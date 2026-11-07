using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Combat;
using HexTactics.Core.Effects;
using HexTactics.Core.Hex;
using HexTactics.Core.Map;
using HexTactics.Core.Skills;
using HexTactics.Core.Spawning;
using HexTactics.Core.Units;
using HexTactics.Tests.Shared;
using Xunit;

namespace HexTactics.Simulation.Tests;

/// <summary>Immunity, conversions, reflections, shields, reductions, vampirism, limits (spec part 5 §4–§5; acceptance §11.3).</summary>
public class DefenseRulesTests
{
    private const string Fire = "test.damage.fire";

    private static HexCoord C(int col, int row) => HexCoord.FromOffset(col, row);

    private static ResistanceSet R(params (string Key, double Value)[] values) => new(values.ToDictionary(v => v.Key, v => v.Value));

    private static readonly IReadOnlyDictionary<string, DamageTypeDefinition> Types = new Dictionary<string, DamageTypeDefinition>(DamageTypes.BuiltIn)
    {
        [Fire] = new(Fire, "Огонь (пример)", DamageTypes.Magical, DamageStrategy.ResistanceAffected, new[] { "fire" }),
    };

    private static DefenseRule Rule(string id, DefenseTrigger trigger, string? type, params DefenseAction[] actions) =>
        new() { Id = id, Trigger = trigger, DamageType = type, Actions = actions };

    private static DefenseAction A(DefenseActionKind kind, double fraction = 0, double amount = 0) => new() { Kind = kind, Fraction = fraction, Amount = amount };

    private sealed class Arena
    {
        public GameSimulation Sim { get; } = new(TestMaps.Empty(), new SimulationSettings { DamageTypes = Types });

        public UnitId Add(HexCoord cell, Side side, ResistanceSet? r = null, IReadOnlyList<DefenseRule>? rules = null, IReadOnlyList<ShieldDefinition>? shields = null, double hp = 100) =>
            Sim.Spawn(SpawnRequest.AtCell(cell, side, HexDirection.North) with
            {
                Profile = new UnitProfile("test.unit", "Юнит", new UnitStats
                {
                    MaxHealth = (int)hp,
                    Resistances = r ?? ResistanceSet.Empty,
                    DefenseRules = rules ?? System.Array.Empty<DefenseRule>(),
                    Shields = shields ?? System.Array.Empty<ShieldDefinition>(),
                }, new[] { SkillCatalog.LinearStrikeId }),
            }).Unit!.Value;

        public DamageResult Hit(UnitId? source, UnitId target, string type, double amount) =>
            Sim.Damage.Resolve(Sim.Damage.Create(source, target, type, amount, DamageOrigin.Skill), Sim.Tick);

        public UnitState U(UnitId id) => Sim.State.GetUnit(id);

        public double Hp(UnitId id) => Sim.State.FindUnit(id)?.CurrentHealth ?? 0;
    }

    // ------------------------------------------------------------------ spec part 5 §5.3: the author's fire example

    private static DefenseRule FireRule() => new()
    {
        Id = "test.rule.fire_convert_25_reflect_75",
        Trigger = DefenseTrigger.OnHit,
        DamageType = Fire,
        MinEffectiveResistance = 300,
        Priority = 100,
        Actions = new[]
        {
            A(DefenseActionKind.ConvertToHealing, 0.25),
            new DefenseAction { Kind = DefenseActionKind.RedirectAsDamage, Fraction = 0.75, Targeting = ReactionTargeting.Area, Radius = 1, IncludeCenter = false, Filter = ReactionFilter.EnemiesOfDefender },
        },
    };

    [Fact]
    public void P18_fire_40_at_R_300_heals_10_reflects_30_to_every_adjacent_enemy_and_takes_no_health()
    {
        var arena = new Arena();
        var defender = arena.Add(C(5, 5), Side.Player, R((Fire, 300)), new[] { FireRule() });
        var ally = arena.Add(C(5, 4), Side.Player);
        var enemyA = arena.Add(C(6, 5), Side.Enemy);
        var enemyB = arena.Add(C(4, 5), Side.Enemy);
        var far = arena.Add(C(5, 8), Side.Enemy); // the source of the fire, three hexes away
        arena.Sim.State.ApplyDamage(defender, 40); // HP 60 / 100

        var result = arena.Hit(far, defender, Fire, 40);
        Assert.Equal(40, result.D0, 9);
        Assert.Equal(40, result.Converted, 9);
        Assert.Equal(10, result.Healed, 9);
        Assert.Equal(0, result.HpLoss, 9); // R = 300 % alone gives nothing; the rule did the work
        Assert.Equal(70, arena.Hp(defender), 9);
        Assert.Equal(70, arena.Hp(enemyA), 9); // 30 of fire each, before their own (zero) resistance
        Assert.Equal(70, arena.Hp(enemyB), 9);
        Assert.Equal(100, arena.Hp(ally), 9);
        Assert.Equal(100, arena.Hp(far), 9);
        var reflected = arena.Sim.Damage.Results.Where(r => r.Event.Reactive).ToList();
        Assert.Equal(2, reflected.Count);
        Assert.All(reflected, r => Assert.Equal((Fire, 30.0, DamageOrigin.Reaction), (r.Event.DamageTypeId, r.Event.Amount, r.Event.Origin)));
    }

    [Fact]
    public void P19_at_98_of_100_health_the_heal_restores_2_and_the_reflection_is_still_30()
    {
        var arena = new Arena();
        var defender = arena.Add(C(5, 5), Side.Player, R((Fire, 300)), new[] { FireRule() });
        var enemy = arena.Add(C(6, 5), Side.Enemy);
        arena.Sim.State.ApplyDamage(defender, 2);
        var result = arena.Hit(null, defender, Fire, 40);
        Assert.Equal(2, result.Healed, 9);
        Assert.Equal(100, arena.Hp(defender), 9);
        Assert.Equal(0, result.HpLoss, 9); // the unused 8 never come back as damage
        Assert.Equal(70, arena.Hp(enemy), 9);
    }

    [Fact]
    public void P20_without_enemies_around_the_reflected_share_vanishes_and_spares_allies()
    {
        var arena = new Arena();
        var defender = arena.Add(C(5, 5), Side.Player, R((Fire, 300)), new[] { FireRule() });
        var ally = arena.Add(C(5, 4), Side.Player);
        arena.Sim.State.ApplyDamage(defender, 40);
        var result = arena.Hit(null, defender, Fire, 40);
        Assert.Equal(0, result.HpLoss, 9);
        Assert.Equal(70, arena.Hp(defender), 9);
        Assert.Equal(100, arena.Hp(ally), 9);
        Assert.DoesNotContain(arena.Sim.Damage.Results, r => r.Event.Reactive);
    }

    [Fact]
    public void Below_the_threshold_the_fire_rule_does_not_act()
    {
        var arena = new Arena();
        var defender = arena.Add(C(5, 5), Side.Player, R((Fire, 250)), new[] { FireRule() });
        arena.Sim.State.ApplyDamage(defender, 40);
        var result = arena.Hit(null, defender, Fire, 40);
        Assert.Equal(0, result.Converted);
        Assert.Equal(60, arena.Hp(defender), 9); // R 250 % → 0 damage, no healing
    }

    // ------------------------------------------------------------------ spec part 5 §5.4 reference table

    [Fact]
    public void Reference_table_of_section_5_4()
    {
        // Slashing 40, R +50 % → 20.
        var a = new Arena();
        var t = a.Add(C(5, 5), Side.Player, R((DamageTypes.Slashing, 50)));
        Assert.Equal(20, a.Hit(null, t, DamageTypes.Slashing, 40).HpLoss, 9);

        // Piercing 40, R −100 % → 80.
        a = new Arena();
        t = a.Add(C(5, 5), Side.Player, R((DamageTypes.Piercing, -100)));
        Assert.Equal(80, a.Hit(null, t, DamageTypes.Piercing, 40).HpLoss, 9);

        // Magical 40, R +100 %, no reactions → 0 / 0 / 0; fire 40 at R +300 % without a rule → the same.
        a = new Arena();
        t = a.Add(C(5, 5), Side.Player, R((DamageTypes.Magical, 100), (Fire, 200)));
        var m = a.Hit(null, t, DamageTypes.Magical, 40);
        var f = a.Hit(null, t, Fire, 40);
        Assert.Equal((0.0, 0.0, 0.0), (m.HpLoss, m.Healed, m.Converted));
        Assert.Equal((0.0, 0.0, 0.0), (f.HpLoss, f.Healed, f.Converted));

        // True 40 at R +500 % for everything → 40 to health.
        a = new Arena();
        t = a.Add(C(5, 5), Side.Player, R((ResistanceSet.Global, 500)));
        Assert.Equal(40, a.Hit(null, t, DamageTypes.True, 40).HpLoss, 9);

        // True 40 with an explicit immunity to true damage → 0; contact reactions off by default.
        a = new Arena();
        t = a.Add(C(5, 5), Side.Player, null, new[] { Rule("imm", DefenseTrigger.OnHit, DamageTypes.True, A(DefenseActionKind.Immunity)), Rule("heal", DefenseTrigger.OnHit, DamageTypes.True, A(DefenseActionKind.ConvertToHealing, 0.25)) });
        a.Sim.State.ApplyDamage(t, 50);
        var imm = a.Hit(null, t, DamageTypes.True, 40);
        Assert.True(imm.Immune);
        Assert.Equal((0.0, 0.0), (imm.HpLoss, imm.Healed));

        // Slashing 40, R +50 %, flat 5, shield 12 → shield −12, health −3.
        a = new Arena();
        t = a.Add(C(5, 5), Side.Player, R((DamageTypes.Slashing, 50)), new[] { Rule("flat", DefenseTrigger.OnHit, null, A(DefenseActionKind.FlatReduction, amount: 5)) }, new[] { new ShieldDefinition("Щит", 12, null, 0) });
        var s = a.Hit(null, t, DamageTypes.Slashing, 40);
        Assert.Equal((12.0, 3.0), (s.ShieldLoss, s.HpLoss));
        Assert.Equal(0, a.U(t).Shields[0].Current, 9);

        // Slashing 40, R −50 %, 25 % to healing, health 50/100 → +10 (60), then 30 × 1.5 = 45 → 15.
        a = new Arena();
        t = a.Add(C(5, 5), Side.Player, R((DamageTypes.Slashing, -50)), new[] { Rule("heal", DefenseTrigger.OnHit, null, A(DefenseActionKind.ConvertToHealing, 0.25)) });
        a.Sim.State.ApplyDamage(t, 50);
        var h = a.Hit(null, t, DamageTypes.Slashing, 40);
        Assert.Equal((10.0, 45.0), (h.Healed, h.HpLoss));
        Assert.Equal(15, a.Hp(t), 9);

        // Physical 40, R +50 %, retaliation 50 % of the actual health loss → takes 20, answers 10, not lowering the hit.
        a = new Arena();
        var attacker = a.Add(C(6, 5), Side.Enemy);
        t = a.Add(C(5, 5), Side.Player, R((DamageTypes.Physical, 50)), new[] { Rule("thorns", DefenseTrigger.OnHealthLoss, null, A(DefenseActionKind.Retaliate, 0.5)) });
        var thorns = a.Hit(attacker, t, DamageTypes.Physical, 40);
        Assert.Equal(20, thorns.HpLoss, 9);
        Assert.Equal(90, a.Hp(attacker), 9);

        // Magical 40, immunity «magical», 25 % to healing without the contact flag → 0 / 0; with the flag → 0 / +10.
        foreach (var (flag, heal) in new[] { (false, 0.0), (true, 10.0) })
        {
            a = new Arena();
            t = a.Add(C(5, 5), Side.Player, null, new[]
            {
                Rule("imm", DefenseTrigger.OnHit, DamageTypes.Magical, A(DefenseActionKind.Immunity)),
                Rule("heal", DefenseTrigger.OnHit, DamageTypes.Magical, A(DefenseActionKind.ConvertToHealing, 0.25)) with { TriggerOnImmuneContact = flag },
            });
            a.Sim.State.ApplyDamage(t, 50);
            var r = a.Hit(null, t, DamageTypes.Magical, 40);
            Assert.Equal((0.0, heal), (r.HpLoss, r.Healed));
        }
    }

    // ------------------------------------------------------------------ acceptance §11.3

    [Fact]
    public void P16_immunity_to_a_subtype_or_a_family_leaves_the_other_types_normal()
    {
        var a = new Arena();
        var t = a.Add(C(5, 5), Side.Player, null, new[] { Rule("imm", DefenseTrigger.OnHit, DamageTypes.Piercing, A(DefenseActionKind.Immunity)) }, hp: 1000);
        Assert.Equal(0, a.Hit(null, t, DamageTypes.Piercing, 40).HpLoss);
        Assert.Equal(40, a.Hit(null, t, DamageTypes.Slashing, 40).HpLoss, 9);
        Assert.Equal(40, a.Hit(null, t, DamageTypes.Blunt, 40).HpLoss, 9);

        a = new Arena();
        t = a.Add(C(5, 5), Side.Player, null, new[] { Rule("imm", DefenseTrigger.OnHit, DamageTypes.Physical, A(DefenseActionKind.Immunity)) }, hp: 1000);
        Assert.Equal(0, a.Hit(null, t, DamageTypes.Blunt, 40).HpLoss);
        Assert.Equal(40, a.Hit(null, t, DamageTypes.Magical, 40).HpLoss, 9);
    }

    [Fact]
    public void P21_overlapping_conversions_never_spend_more_than_D0_and_report_the_conflict()
    {
        var a = new Arena();
        var t = a.Add(C(5, 5), Side.Player, null, new[]
        {
            Rule("b", DefenseTrigger.OnHit, null, A(DefenseActionKind.Negate, 0.5)) with { Priority = 20 },
            Rule("a", DefenseTrigger.OnHit, null, A(DefenseActionKind.ConvertToHealing, 0.8)) with { Priority = 10 },
        });
        a.Sim.State.ApplyDamage(t, 50);
        a.Sim.DrainEvents();
        var r = a.Hit(null, t, DamageTypes.Slashing, 40);
        Assert.Equal(40, r.Converted, 9);
        Assert.Equal(32, r.Healed, 9); // the lower priority first: 80 % → 32; the next rule gets the remaining 8 only
        Assert.Equal(0, r.HpLoss, 9);
        Assert.Contains(a.Sim.DrainEvents().OfType<CombatLimitReached>(), e => e.Reason.Contains("100 %", System.StringComparison.Ordinal));
    }

    [Fact]
    public void P23_two_reflecting_units_do_not_create_an_endless_chain()
    {
        var a = new Arena();
        DefenseRule Mirror() => new()
        {
            Id = "mirror",
            Trigger = DefenseTrigger.OnHit,
            Actions = new[] { new DefenseAction { Kind = DefenseActionKind.RedirectAsDamage, Fraction = 1, Targeting = ReactionTargeting.Attacker } },
        };
        DefenseRule Thorns() => Rule("thorns", DefenseTrigger.OnHealthLoss, null, A(DefenseActionKind.Retaliate, 1));
        var x = a.Add(C(5, 5), Side.Player, null, new[] { Mirror(), Thorns() }, hp: 1000);
        var y = a.Add(C(6, 5), Side.Enemy, null, new[] { Mirror(), Thorns() }, hp: 1000);
        a.Hit(y, x, DamageTypes.Slashing, 40); // x reflects 40 to y; the reactive packet is not reflected back
        Assert.Equal(1000, a.Hp(x), 9);
        Assert.Equal(960, a.Hp(y), 9);
        Assert.Equal(2, a.Sim.Damage.Results.Count);
    }

    [Fact]
    public void P25_a_shield_drains_over_several_hits_and_also_takes_true_damage()
    {
        var a = new Arena();
        var t = a.Add(C(5, 5), Side.Player, null, null, new[] { new ShieldDefinition("Щит", 30, null, 0) });
        var first = a.Hit(null, t, DamageTypes.Slashing, 20);
        Assert.Equal((20.0, 0.0), (first.ShieldLoss, first.HpLoss));
        var second = a.Hit(null, t, DamageTypes.True, 20);
        Assert.Equal((10.0, 10.0), (second.ShieldLoss, second.HpLoss));
        Assert.Equal(0, a.U(t).Shields[0].Current, 9);
        Assert.Equal(90, a.Hp(t), 9);
    }

    [Fact]
    public void A_shield_with_a_type_filter_takes_only_that_type_and_conversion_can_refill_it()
    {
        var a = new Arena();
        var t = a.Add(C(5, 5), Side.Player, null, new[]
        {
            new DefenseRule { Id = "charge", Trigger = DefenseTrigger.OnHit, DamageType = DamageTypes.Magical, Actions = new[] { new DefenseAction { Kind = DefenseActionKind.ConvertToShield, Fraction = 0.5, Shield = "Барьер" } } },
        }, new[] { new ShieldDefinition("Барьер", 50, DamageTypes.Physical, 0) });
        a.U(t).Shields[0].Current = 0;
        var m = a.Hit(null, t, DamageTypes.Magical, 40);
        Assert.Equal((20.0, 0.0), (m.HpLoss, m.ShieldLoss)); // half refills the shield, the shield does not take magic
        Assert.Equal(20, a.U(t).Shields[0].Current, 9);
        var p = a.Hit(null, t, DamageTypes.Blunt, 30);
        Assert.Equal((20.0, 10.0), (p.ShieldLoss, p.HpLoss));
    }

    [Fact]
    public void P27_vampirism_heals_only_from_health_actually_taken()
    {
        var a = new Arena();
        var vampire = a.Add(C(6, 5), Side.Enemy, null, new[] { Rule("vamp", DefenseTrigger.OnDamageDealt, null, A(DefenseActionKind.Vampirism, 0.5)) });
        var immune = a.Add(C(5, 5), Side.Player, null, new[] { Rule("imm", DefenseTrigger.OnHit, null, A(DefenseActionKind.Immunity)) });
        var shielded = a.Add(C(5, 6), Side.Player, null, null, new[] { new ShieldDefinition("Щит", 100, null, 0) });
        var plain = a.Add(C(7, 5), Side.Player);
        a.Sim.State.ApplyDamage(vampire, 50);
        a.Hit(vampire, immune, DamageTypes.Slashing, 40);
        a.Hit(vampire, shielded, DamageTypes.Slashing, 40);
        Assert.Equal(50, a.Hp(vampire), 9);
        a.Hit(vampire, plain, DamageTypes.Slashing, 20);
        Assert.Equal(60, a.Hp(vampire), 9);
    }

    [Fact]
    public void Retaliation_to_continuous_damage_is_summed_once_per_game_second()
    {
        var map = TestMaps.Empty();
        map.Grid.SetTile(C(5, 5), TileType.Walkable, "test.tile.burn", 0);
        var sim = new GameSimulation(map, new SimulationSettings
        {
            DamageTypes = Types,
            TileEffects = new Dictionary<string, IReadOnlyList<TileEffect>>
            {
                ["test.tile.burn"] = new[] { new TileEffect(TileTrigger.Stay, new EffectSpec("periodic", new Dictionary<string, object> { ["kind"] = "damage", ["perSecond"] = 6.0, ["damageType"] = DamageTypes.Physical })) },
            },
        });
        var hot = sim.Spawn(SpawnRequest.AtCell(C(5, 5), Side.Player, HexDirection.North) with
        {
            Profile = new UnitProfile("t", "t", new UnitStats
            {
                DefenseRules = new[] { new DefenseRule { Id = "spikes", Trigger = DefenseTrigger.OnHealthLoss, Actions = new[] { new DefenseAction { Kind = DefenseActionKind.Retaliate, Fraction = 1, Targeting = ReactionTargeting.Area, Radius = 1 } } } },
            }, new[] { SkillCatalog.LinearStrikeId }),
        }).Unit!.Value;
        var neighbour = sim.Spawn(SpawnRequest.AtCell(C(6, 5), Side.Enemy, HexDirection.North)).Unit!.Value;
        sim.DrainEvents();
        for (var i = 0; i < 60; i++)
        {
            sim.Step();
        }

        Assert.Equal(88, sim.State.GetUnit(hot).CurrentHealth, 9); // 6 HP/s × 2 s
        var reactions = sim.DrainEvents().OfType<DamageApplied>().Where(d => d.Kind == DamageKind.Reaction).ToList();
        Assert.InRange(reactions.Count, 1, 2); // at most once per game second, not 60 times
        Assert.Equal(100 - reactions.Sum(r => r.Amount), sim.State.GetUnit(neighbour).CurrentHealth, 9);
        Assert.InRange(reactions.Sum(r => r.Amount), 5.9, 12.0001);
    }

    [Fact]
    public void Status_immunity_blocks_a_debuff_and_a_healing_block_stops_healing()
    {
        var a = new Arena();
        var guarded = a.Add(C(5, 5), Side.Player, null, new[] { new DefenseRule { Id = "calm", Trigger = DefenseTrigger.OnStatusApplied, Actions = new[] { new DefenseAction { Kind = DefenseActionKind.StatusImmunity, StatusCategory = "debuff" } } } });
        var slow = new EffectSpec("debuff", new Dictionary<string, object> { ["stat"] = Stats.MoveSpeed, ["mode"] = "percent", ["percent"] = 0.5, ["duration"] = 3.0, ["targets"] = "all" });
        a.Sim.Effects.Apply(new EffectInvocation(slow, null, a.U(guarded), 0, EffectSource.Skill, "test"));
        Assert.Empty(a.U(guarded).Statuses);

        var cursed = a.Add(C(7, 5), Side.Player);
        var noHeal = new EffectSpec("debuff", new Dictionary<string, object> { ["stat"] = Stats.HealingReceived, ["mode"] = "percent", ["percent"] = 1.0, ["duration"] = 3.0, ["targets"] = "all" });
        a.Sim.Effects.Apply(new EffectInvocation(noHeal, null, a.U(cursed), 0, EffectSource.Skill, "test"));
        a.Sim.State.ApplyDamage(cursed, 50);
        Assert.Equal(0, a.Sim.Damage.Heal(a.U(cursed), null, 30, 0, DamageKind.Skill));
        Assert.Equal(50, a.Hp(cursed), 9);
    }

    [Fact]
    public void P33_a_unit_killed_by_reflected_damage_dies_once()
    {
        var a = new Arena();
        var defender = a.Add(C(5, 5), Side.Player, null, new[] { new DefenseRule { Id = "mirror", Actions = new[] { new DefenseAction { Kind = DefenseActionKind.RedirectAsDamage, Fraction = 1, Targeting = ReactionTargeting.Attacker } } } });
        var attacker = a.Add(C(6, 5), Side.Enemy, hp: 10);
        a.Hit(attacker, defender, DamageTypes.Slashing, 40);
        a.Sim.DrainEvents();
        a.Sim.Step();
        Assert.Null(a.Sim.State.FindUnit(attacker));
        Assert.Single(a.Sim.State.Fallen, u => u.Id == attacker);
    }

    [Fact]
    public void A_tag_filter_and_trigger_limits_restrict_a_rule()
    {
        var a = new Arena();
        var t = a.Add(C(5, 5), Side.Player, null, new[]
        {
            new DefenseRule { Id = "fireproof", Trigger = DefenseTrigger.OnHit, DamageTag = "fire", MaxTriggers = 2, Actions = new[] { A(DefenseActionKind.Immunity) } },
        }, hp: 1000);
        Assert.Equal(30, a.Hit(null, t, DamageTypes.Magical, 30).HpLoss, 9); // magical without the tag
        Assert.True(a.Hit(null, t, Fire, 30).Immune);
        Assert.True(a.Hit(null, t, Fire, 30).Immune);
        Assert.False(a.Hit(null, t, Fire, 30).Immune); // two activations per match only
    }

    [Fact]
    public void A_cooldown_counts_game_time_and_one_hit_activates_a_rule_once()
    {
        var a = new Arena();
        var t = a.Add(C(5, 5), Side.Player, null, new[]
        {
            new DefenseRule { Id = "guard", Trigger = DefenseTrigger.OnHit, Cooldown = 1, Actions = new[] { A(DefenseActionKind.FlatReduction, amount: 10), A(DefenseActionKind.DamageCap, amount: 5) } },
        }, hp: 1000);
        Assert.Equal(5, a.Hit(null, t, DamageTypes.Slashing, 40).HpLoss, 9);
        Assert.Equal(40, a.Hit(null, t, DamageTypes.Slashing, 40).HpLoss, 9); // same tick: on cooldown
        for (var i = 0; i < 29; i++)
        {
            a.Sim.Step();
        }

        Assert.Equal(40, a.Hit(null, t, DamageTypes.Slashing, 40).HpLoss, 9); // 29 ticks < 1 s
        a.Sim.Step();
        Assert.Equal(5, a.Hit(null, t, DamageTypes.Slashing, 40).HpLoss, 9);
    }

    [Fact]
    public void Vampirism_respects_its_owner_health_condition()
    {
        var a = new Arena();
        var vampire = a.Add(C(6, 5), Side.Enemy, null, new[] { new DefenseRule { Id = "thirst", Trigger = DefenseTrigger.OnDamageDealt, HealthBelowPercent = 50, Actions = new[] { A(DefenseActionKind.Vampirism, 1) } } });
        var victim = a.Add(C(5, 5), Side.Player, hp: 1000);
        a.Sim.State.ApplyDamage(vampire, 40); // 60 % — above the threshold
        a.Hit(vampire, victim, DamageTypes.Slashing, 10);
        Assert.Equal(60, a.Hp(vampire), 9);
        a.Sim.State.ApplyDamage(vampire, 20); // 40 %
        a.Hit(vampire, victim, DamageTypes.Slashing, 10);
        Assert.Equal(50, a.Hp(vampire), 9);
    }
}
