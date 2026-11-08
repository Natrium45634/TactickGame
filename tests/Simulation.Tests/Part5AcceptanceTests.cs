using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Combat;
using HexTactics.Core.Effects;
using HexTactics.Core.Hex;
using HexTactics.Core.Orders;
using HexTactics.Core.Skills;
using HexTactics.Core.Spawning;
using HexTactics.Core.Units;
using HexTactics.Tests.Shared;
using Xunit;

namespace HexTactics.Simulation.Tests;

/// <summary>The remaining acceptance items of spec part 5 §11 at the simulation level (v0.25.0).</summary>
public class Part5AcceptanceTests
{
    private const string Fire = "test.damage.fire";

    private static HexCoord C(int col, int row) => HexCoord.FromOffset(col, row);

    private static EffectSpec E(string type, params (string Key, object Value)[] p) => new(type, p.ToDictionary(x => x.Key, x => x.Value));

    private static readonly IReadOnlyDictionary<string, DamageTypeDefinition> Types = new Dictionary<string, DamageTypeDefinition>(DamageTypes.BuiltIn)
    {
        [Fire] = new(Fire, "Огонь", DamageTypes.Magical, DamageStrategy.ResistanceAffected, new[] { "fire" }),
    };

    private static DefenseRule FireRule() => new()
    {
        Id = "fire",
        Trigger = DefenseTrigger.OnHit,
        DamageType = Fire,
        MinEffectiveResistance = 300,
        Actions = new[]
        {
            new DefenseAction { Kind = DefenseActionKind.ConvertToHealing, Fraction = 0.25 },
            new DefenseAction { Kind = DefenseActionKind.RedirectAsDamage, Fraction = 0.75, Targeting = ReactionTargeting.Area, Radius = 1 },
        },
    };

    private static UnitProfile Profile(string name, UnitStats stats, params string[] skills) => new("test." + name, name, stats, skills);

    private static UnitId Spawn(GameSimulation sim, HexCoord cell, Side side, UnitProfile? profile = null, HexDirection facing = HexDirection.North) =>
        sim.Spawn(SpawnRequest.AtCell(cell, side, facing) with { Profile = profile }).Unit!.Value;

    [Fact]
    public void P13_penetration_never_pierces_an_explicit_immunity()
    {
        var sim = new GameSimulation(TestMaps.Empty(), new SimulationSettings { DamageTypes = Types });
        var target = Spawn(sim, C(5, 5), Side.Player, Profile("t", new UnitStats
        {
            DefenseRules = new[] { new DefenseRule { Id = "imm", Trigger = DefenseTrigger.OnHit, DamageType = DamageTypes.Physical, Actions = new[] { new DefenseAction { Kind = DefenseActionKind.Immunity } } } },
        }));
        var r = sim.Damage.Resolve(sim.Damage.Create(null, target, DamageTypes.Piercing, 40, DamageOrigin.Skill, penetration: 500), sim.Tick);
        Assert.True(r.Immune);
        Assert.Equal(100, sim.State.GetUnit(target).CurrentHealth, 9);
    }

    /// <summary>Five units with different resistances, rules and attack types fight on their own orders (§11.4 p.34).</summary>
    private static (GameSimulation Sim, UnitId[] Ids) Battle(double frame)
    {
        var sim = new GameSimulation(TestMaps.Empty(), new SimulationSettings { DamageTypes = Types, Skills = new Dictionary<string, SkillDefinition>() });
        var tank = Spawn(sim, C(4, 5), Side.Player, Profile("tank", new UnitStats { MaxHealth = 160, AttackDamageTypeId = DamageTypes.Blunt, Resistances = new ResistanceSet(new Dictionary<string, double> { [DamageTypes.Physical] = 50 }) }));
        var guard = Spawn(sim, C(5, 5), Side.Player, Profile("guard", new UnitStats { AttackDamageTypeId = Fire, AttackPower = 15, Resistances = new ResistanceSet(new Dictionary<string, double> { [Fire] = 300 }), DefenseRules = new[] { FireRule() } }));
        var ghost = Spawn(sim, C(6, 5), Side.Player, Profile("ghost", new UnitStats { AttackDamageTypeId = DamageTypes.True, AttackPower = 6, Shields = new[] { new ShieldDefinition("Щит", 30, null, 0) } }));
        var mage = Spawn(sim, C(5, 3), Side.Enemy, Profile("mage", new UnitStats { AttackDamageTypeId = Fire, AttackPower = 20, NormalAttackRangeHex = 3, AttackAreaRadius = 1 }), HexDirection.South);
        var skirm = Spawn(sim, C(4, 3), Side.Enemy, Profile("skirm", new UnitStats { AttackDamageTypeId = DamageTypes.Piercing, AttackPower = 12, Resistances = new ResistanceSet(new Dictionary<string, double> { [DamageTypes.Magical] = -50 }) }), HexDirection.South);
        Assert.True(sim.ApplyOrder(IndividualOrder.Attack(mage, System.Array.Empty<HexCoord>(), guard, HexDirection.South)).Success);
        Assert.True(sim.ApplyOrder(IndividualOrder.Attack(skirm, System.Array.Empty<HexCoord>(), tank, HexDirection.South)).Success);
        Assert.True(sim.ApplyOrder(IndividualOrder.Attack(tank, System.Array.Empty<HexCoord>(), skirm, HexDirection.North)).Success);
        Assert.True(sim.ApplyOrder(IndividualOrder.Attack(ghost, System.Array.Empty<HexCoord>(), mage, HexDirection.North)).Success);
        Assert.True(sim.ApplyOrder(IndividualOrder.Attack(guard, System.Array.Empty<HexCoord>(), mage, HexDirection.North)).Success);
        for (var t = 0.0; t < 6; t += frame)
        {
            sim.Advance(frame);
        }

        return (sim, new[] { tank, guard, ghost, mage, skirm });
    }

    private static string Snapshot(GameSimulation sim, UnitId[] ids) => string.Join(" | ", ids.Select(id => sim.State.FindUnit(id) is { } u
        ? $"{u.CurrentHealth:0.###}/{string.Join(",", u.Shields.Select(s => s.Current.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)))}"
        : "dead"));

    [Fact]
    public void P34_five_units_with_different_defenses_fight_without_manual_reaction_handling_and_P26_P28_reproducibly_at_any_frame_rate()
    {
        var reference = Battle(1.0 / 30);
        var shot = Snapshot(reference.Sim, reference.Ids);
        Assert.Equal(shot, Snapshot(Battle(1.0 / 30).Sim, Battle(1.0 / 30).Ids)); // the same inputs, the same result (no random rolls)
        foreach (var fps in new[] { 60.0, 144.0, 12.5 })
        {
            var run = Battle(1.0 / fps);
            Assert.Equal(shot, Snapshot(run.Sim, run.Ids));
        }

        var sim = reference.Sim;
        var guard = sim.State.FindUnit(reference.Ids[1])!;
        Assert.Equal(100, guard.CurrentHealth, 9); // fire into R 300 % with the rule: healing, never health loss
        var events = sim.DrainEvents();
        Assert.DoesNotContain(events, e => e is CombatLimitReached);
        Assert.Contains(events, e => e is DamageApplied { Kind: DamageKind.Reaction }); // the guard reflected fire on its own
        Assert.Contains(events, e => e is HealApplied h && h.Target == reference.Ids[1]);
        Assert.True((sim.State.FindUnit(reference.Ids[3])?.CurrentHealth ?? 0) < 100); // the mage was hit (reflection, true damage, fire)
        Assert.True((sim.State.FindUnit(reference.Ids[0])?.CurrentHealth ?? 0) < 160, shot); // the tank took blows (next to the guard it is in the mage's fire area)
        Assert.All(reference.Ids.Select(sim.State.FindUnit).Where(u => u is not null), u => Assert.True(u!.CurrentHealth >= 0));
    }

    [Fact]
    public void P28_reactions_freeze_on_the_tactical_pause()
    {
        var (sim, ids) = Battle(1.0 / 30);
        sim.SetPaused(true);
        var before = Snapshot(sim, ids);
        for (var i = 0; i < 90; i++)
        {
            sim.Advance(1.0 / 30);
        }

        Assert.Equal(before, Snapshot(sim, ids));
    }

    [Fact]
    public void P29_a_typed_skill_planned_at_a_waypoint_goes_through_the_resolver_and_the_route_goes_on()
    {
        var bolt = new SkillDefinition
        {
            Id = "test.bolt",
            Name = "Разряд",
            Cells = new[]
            {
                new SkillCell(new HexCoord(0, -1), new[] { E("damage", ("amount", 20.0), ("damageType", DamageTypes.Magical), ("targets", "enemies")) }),
                new SkillCell(new HexCoord(0, -2), new[] { E("damage", ("amount", 20.0), ("damageType", DamageTypes.True), ("targets", "enemies")) }),
            },
        };
        var sim = new GameSimulation(TestMaps.Empty(), new SimulationSettings { DamageTypes = Types, Skills = new Dictionary<string, SkillDefinition> { [bolt.Id] = bolt }, DefaultSkillSet = new[] { bolt.Id } });
        var caster = Spawn(sim, C(1, 7), Side.Player);
        var warded = Spawn(sim, C(3, 4), Side.Enemy, Profile("warded", new UnitStats { Resistances = new ResistanceSet(new Dictionary<string, double> { [DamageTypes.Magical] = 50, [ResistanceSet.Global] = 100 }) }), HexDirection.South);
        var far = Spawn(sim, C(3, 3), Side.Enemy, Profile("far", new UnitStats { Resistances = new ResistanceSet(new Dictionary<string, double> { [ResistanceSet.Global] = 500 }) }), HexDirection.South);
        var order = new IndividualOrder(caster, new[] { C(3, 5), C(5, 5) }, HexDirection.SouthEast, null, new Dictionary<int, WaypointSkillSpec> { [0] = new(bolt.Id, HexDirection.North) });
        Assert.True(sim.ApplyOrder(order).Success);
        for (var i = 0; i < 30 * 20 && !(sim.State.GetUnit(caster).Orders.IsEmpty && !sim.State.GetUnit(caster).Motion.IsStepping); i++)
        {
            sim.Step();
        }

        Assert.Equal(100, sim.State.GetUnit(warded).CurrentHealth, 9); // magical 20 at R 150 % → 0
        Assert.Equal(80, sim.State.GetUnit(far).CurrentHealth, 9);    // true 20 ignores R 500 %
        Assert.Equal(C(5, 5), sim.State.GetUnit(caster).Cell);       // the route went on after the strike
    }

    [Fact]
    public void P30_cells_keep_3_x_150_and_3_x_75_percent_with_their_own_damage_types_and_fractions()
    {
        var skill = new SkillDefinition
        {
            Id = "test.triple",
            Name = "Тройной",
            Cells = new[]
            {
                new SkillCell(new HexCoord(0, -1), new[] { E("damage", ("formula", "attack"), ("multiplier", 1.5), ("hits", 3.0), ("interval", 0.2), ("damageType", DamageTypes.Slashing), ("targets", "enemies")) }),
                new SkillCell(new HexCoord(0, -3), new[] { E("damage", ("formula", "attack"), ("multiplier", 0.75), ("hits", 3.0), ("interval", 0.2), ("damageType", Fire), ("targets", "enemies")) }),
            },
        };
        var sim = new GameSimulation(TestMaps.Empty(), new SimulationSettings { DamageTypes = Types, Skills = new Dictionary<string, SkillDefinition> { [skill.Id] = skill }, DefaultSkillSet = new[] { skill.Id } });
        var caster = Spawn(sim, C(5, 7), Side.Player);
        var near = Spawn(sim, C(5, 6), Side.Enemy, Profile("near", new UnitStats { Resistances = new ResistanceSet(new Dictionary<string, double> { [DamageTypes.Slashing] = 20 }) }), HexDirection.South);
        var farther = Spawn(sim, C(5, 4), Side.Enemy, Profile("farther", new UnitStats { Resistances = new ResistanceSet(new Dictionary<string, double> { [DamageTypes.Magical] = -50 }) }), HexDirection.South);
        sim.UseSkill(caster, skill.Id, HexDirection.North);
        for (var i = 0; i < 30; i++)
        {
            sim.Step();
        }

        Assert.Equal(100 - (3 * 15 * 0.8), sim.State.GetUnit(near).CurrentHealth, 9);   // 3 × 15 slashing at R 20 % = 36
        Assert.Equal(100 - (3 * 7.5 * 1.5), sim.State.GetUnit(farther).CurrentHealth, 9); // 3 × 7.5 fire at R −50 % = 33.75
    }

    [Fact]
    public void P32_a_reaction_does_not_change_the_orders_route_or_facing_of_the_units_it_hits()
    {
        var sim = new GameSimulation(TestMaps.Empty(), new SimulationSettings { DamageTypes = Types });
        var guard = Spawn(sim, C(5, 5), Side.Player, Profile("guard", new UnitStats { Resistances = new ResistanceSet(new Dictionary<string, double> { [Fire] = 300 }), DefenseRules = new[] { FireRule() } }));
        var walker = Spawn(sim, C(6, 5), Side.Enemy, null, HexDirection.South);
        Assert.True(sim.ApplyOrder(new IndividualOrder(walker, new[] { C(6, 8), C(8, 8) }, HexDirection.SouthEast)).Success);
        sim.Step();
        var waypoints = sim.State.GetUnit(walker).Orders.Items.Select(w => w.Cell).ToList();
        var facing = sim.State.GetUnit(walker).Facing;
        sim.Damage.Resolve(sim.Damage.Create(null, guard, Fire, 40, DamageOrigin.Skill), sim.Tick);
        Assert.Equal(70, sim.State.GetUnit(walker).CurrentHealth, 9); // the walker was next to the guard and took the reflection
        Assert.Equal(waypoints, sim.State.GetUnit(walker).Orders.Items.Select(w => w.Cell).ToList());
        Assert.Equal(facing, sim.State.GetUnit(walker).Facing);
        for (var i = 0; i < 30 * 12; i++)
        {
            sim.Step();
        }

        Assert.Equal(C(8, 8), sim.State.GetUnit(walker).Cell);
        Assert.Equal(HexDirection.SouthEast, sim.State.GetUnit(walker).Facing);
    }

    private sealed class DoubleFire : HexTactics.Simulation.IDamageStage
    {
        public string Name => "test: doubled fire";

        public DamageEvent Apply(DamageEvent e, GameState state) => e.DamageTypeId == Fire ? e with { Amount = e.Amount * 2 } : e;
    }

    [Fact]
    public void A_registered_stage_before_D0_changes_the_event_and_none_is_registered_by_default()
    {
        Assert.Empty(new SimulationSettings().PreDamageStages);
        var sim = new GameSimulation(TestMaps.Empty(), new SimulationSettings { DamageTypes = Types, PreDamageStages = new HexTactics.Simulation.IDamageStage[] { new DoubleFire() } });
        var target = Spawn(sim, C(5, 5), Side.Player, Profile("t", new UnitStats { MaxHealth = 200, Resistances = new ResistanceSet(new Dictionary<string, double> { [Fire] = 50 }) }));
        var r = sim.Damage.Resolve(sim.Damage.Create(null, target, Fire, 30, DamageOrigin.Skill), sim.Tick);
        Assert.Equal((60.0, 30.0), (r.D0, r.HpLoss)); // the stage ran before D0 and before R
        Assert.Equal(20, sim.Damage.Resolve(sim.Damage.Create(null, target, DamageTypes.Slashing, 20, DamageOrigin.Skill), sim.Tick).HpLoss, 9);
    }
}
