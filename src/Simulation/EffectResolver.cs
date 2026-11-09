using System;
using System.Collections.Generic;
using HexTactics.Core.Combat;
using HexTactics.Core.Effects;
using HexTactics.Core.Units;

namespace HexTactics.Simulation;

/// <summary>Why an effect is applied (for events and visuals).</summary>
public enum EffectSource
{
    Skill = 0,

    /// <summary>A tile surface (spec part 4 §7).</summary>
    Tile = 1,
}

/// <summary>
/// One application of an effect to one target. <paramref name="SourceKey"/> identifies the origin (skill id, tile type
/// id) for the stacking of statuses.
/// </summary>
public sealed record EffectInvocation(EffectSpec Effect, UnitState? Source, UnitState Target, long Tick, EffectSource Origin, string SourceKey = "")
{
    /// <summary>The skill action and hit of the invocation: keys of the reproducible crit / surprise rolls (spec part 6 §7.2).</summary>
    public long ActionId { get; init; }

    public int HitIndex { get; init; }

    /// <summary>
    /// Game time of a continuous application (a «stay» effect of a tile for one tick, spec part 4 §7.2); 0 for one-off
    /// applications. A periodic effect with a slice acts at once for that time instead of putting a status.
    /// </summary>
    public double Slice { get; init; }

    /// <summary>Share of the strength for this target (spec part 5 §2.3: an area effect split among its targets); 1 = full.</summary>
    public double Scale { get; init; } = 1;
}

/// <summary>Executes one registered effect type (spec part 4 §6.3, §9.2: only shared services resolve effects).</summary>
public interface IEffectHandler
{
    string Type { get; }

    void Apply(EffectInvocation invocation, EffectResolver resolver);
}

/// <summary>
/// The single executor of configured effects (spec part 4 §6.3 <c>EffectResolver</c>): skills and tiles describe effects
/// as data; this service checks the target rules and runs the handler registered for the effect type. Unknown types are
/// never executed. Damage, healing, health limits, characteristic modifiers and periodic effects are decided here only —
/// a new effect type = a handler here + a field schema in the content registry.
/// </summary>
public sealed class EffectResolver
{
    private readonly Dictionary<string, IEffectHandler> _handlers = new(StringComparer.Ordinal);

    internal EffectResolver(GameState state, List<SimulationEvent> events, DamageResolver damage)
    {
        State = state;
        Events = events;
        Damage = damage;
        Register(new DamageEffectHandler());
        Register(new HealEffectHandler());
        Register(new ModifierEffectHandler(EffectTypes.Buff, +1));
        Register(new ModifierEffectHandler(EffectTypes.Debuff, -1));
        Register(new PeriodicEffectHandler());
    }

    internal GameState State { get; }

    /// <summary>Every damage of an effect goes through the single damage resolver (spec part 5 §2.3).</summary>
    public DamageResolver Damage { get; }

    internal List<SimulationEvent> Events { get; }

    /// <summary>Effect types this simulation can execute.</summary>
    public IReadOnlyCollection<string> Types => _handlers.Keys;

    public void Register(IEffectHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handlers[handler.Type] = handler;
    }

    public bool CanExecute(string type) => _handlers.ContainsKey(type);

    /// <summary>True if <paramref name="target"/> is a valid target of <paramref name="effect"/> from <paramref name="source"/>.</summary>
    public static bool Matches(EffectSpec effect, UnitState? source, UnitState target)
    {
        if (!target.IsAlive)
        {
            return false;
        }

        if (source is null)
        {
            // Tile effects have no source unit: they filter by side (spec part 4 §7.1 «условие выбора фракции»).
            return effect.Text("sides", TileSides.All) switch
            {
                TileSides.Player => target.Side == Side.Player,
                TileSides.Enemy => target.Side == Side.Enemy,
                _ => true,
            };
        }

        return effect.Targets switch
        {
            EffectTargets.Enemies => target.Side != source.Side,
            EffectTargets.Allies => target.Side == source.Side,
            EffectTargets.Self => target.Id == source.Id,
            _ => true,
        };
    }

    /// <summary>Applies the effect if its type is known and the target matches; returns whether it was applied.</summary>
    public bool Apply(EffectInvocation invocation)
    {
        if (!_handlers.TryGetValue(invocation.Effect.Type, out var handler) || !Matches(invocation.Effect, invocation.Source, invocation.Target))
        {
            return false;
        }

        handler.Apply(invocation, this);
        return true;
    }

    /// <summary>
    /// The amount of a damage/heal effect: fixed HP, or a multiple of the source's current Attack Power (buffs included),
    /// or of the source's maximum health. «150 % от атаки» is 1.5 × AttackPower — never a share of the target's health.
    /// </summary>
    public static double Amount(EffectSpec effect, UnitState? source) => effect.Text("formula", "fixed") switch
    {
        "attack" => effect.Number("multiplier", 1) * (source?.Stats.AttackPower ?? 0),
        "maxHealth" => effect.Number("multiplier", 1) * (source?.MaxHealth ?? 0),
        _ => effect.Number("amount"),
    };

    /// <summary>
    /// Typed damage of an effect: one event through the damage resolver (resistances etc.); death is processed after the
    /// tick. <paramref name="effect"/> gives the damage type and the penetration.
    /// </summary>
    public DamageResult DealDamage(UnitState target, UnitState? source, double amount, long tick, DamageOrigin origin, string damageTypeId, double penetration = 0, bool periodic = false,
        double armorPenetration = 0, double armorPenetrationPercent = 0, bool canCrit = false, bool canSurprise = false, string rollKey = "") =>
        Damage.Resolve(Damage.Create(source?.Id, target.Id, damageTypeId, amount, origin, penetration, periodic, armorPenetration, armorPenetrationPercent, canCrit, canSurprise, rollKey), tick);

    /// <summary>Shared health rule: the single healing path of the damage resolver (never above the maximum).</summary>
    public void Heal(UnitState target, UnitState? source, double amount, long tick, DamageKind kind) =>
        Damage.Heal(target, source?.Id, Math.Max(0, amount), tick, kind);

    /// <summary>Puts a timed status on the target with the effect's stacking policy.</summary>
    public void AddStatus(EffectInvocation invocation, StatusEffect status)
    {
        if (Damage.BlocksStatus(invocation.Target, invocation.Effect.Type))
        {
            Events.Add(new StatusBlocked(invocation.Tick, invocation.Target.Id, status.Key));
            return;
        }

        var policy = invocation.Effect.Text("stacking", "refresh") switch
        {
            "stack" => StackingPolicy.Stack,
            "replace" => StackingPolicy.Replace,
            "ignore" => StackingPolicy.Ignore,
            _ => StackingPolicy.Refresh,
        };
        if (State.AddStatus(invocation.Target.Id, status, policy))
        {
            Events.Add(new StatusApplied(invocation.Tick, invocation.Target.Id, invocation.Source?.Id, status.Key, status.Kind, status.Stat, status.Duration));
        }
    }
}

/// <summary><c>damage</c>: fixed HP or a multiple of the source's Attack Power, per hit (spec part 4 §6.3 p.1).</summary>
internal sealed class DamageEffectHandler : IEffectHandler
{
    public string Type => EffectTypes.Damage;

    public void Apply(EffectInvocation invocation, EffectResolver resolver)
    {
        var e = invocation.Effect;
        resolver.DealDamage(
            invocation.Target,
            invocation.Source,
            EffectResolver.Amount(e, invocation.Source) * invocation.Scale,
            invocation.Tick,
            invocation.Origin == EffectSource.Tile ? DamageOrigin.Surface : DamageOrigin.Skill,
            e.Text("damageType", DamageTypes.Untyped),
            e.Number("penetration", 0),
            armorPenetration: e.Number("armorPenetration", 0),
            armorPenetrationPercent: e.Number("armorPenetrationPercent", 0),
            // Spec part 6 §7.2: a skill crits only when marked; one roll per hit (default) or per series, per target (default) or shared.
            canCrit: invocation.Origin == EffectSource.Skill && e.Flag("canCrit", false),
            canSurprise: invocation.Origin == EffectSource.Skill && e.Flag("canSurprise", true),
            rollKey: invocation.Origin != EffectSource.Skill ? string.Empty
                : $"skill:{invocation.ActionId}:{(e.Text("critRoll", "per_hit") == "per_series" ? "s" : invocation.HitIndex.ToString(System.Globalization.CultureInfo.InvariantCulture))}:{(e.Text("critTargets", "per_target") == "shared" ? "all" : invocation.Target.Id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))}");
    }
}

/// <summary><c>heal</c>: fixed HP or a multiple of a source characteristic; capped at the target's maximum (§6.3 p.2).</summary>
internal sealed class HealEffectHandler : IEffectHandler
{
    public string Type => EffectTypes.Heal;

    public void Apply(EffectInvocation invocation, EffectResolver resolver) =>
        resolver.Heal(invocation.Target, invocation.Source, EffectResolver.Amount(invocation.Effect, invocation.Source), invocation.Tick, invocation.Origin == EffectSource.Tile ? DamageKind.Surface : DamageKind.Skill);
}

/// <summary>
/// <c>buff</c> / <c>debuff</c>: a timed change of an existing characteristic by a fixed value or a percentage
/// (§6.3 p.3–4); debuffs never push a value below its safe minimum.
/// </summary>
internal sealed class ModifierEffectHandler : IEffectHandler
{
    private readonly int _sign;

    public ModifierEffectHandler(string type, int sign)
    {
        Type = type;
        _sign = sign;
    }

    public string Type { get; }

    public void Apply(EffectInvocation invocation, EffectResolver resolver)
    {
        var e = invocation.Effect;
        var stat = e.Text("stat", Stats.MoveSpeed);
        var layered = stat is Stats.Resistance or Stats.Armor; // points on a layer (global / family / type), never percent
        var percent = !layered && stat != Stats.Block && e.Text("mode", "percent") == "percent"; // block: whole slots only
        var key = stat == Stats.Resistance ? e.Text("resistanceOf", ResistanceSet.Global)
                : stat == Stats.Armor ? e.Text("armorOf", ArmorSet.Global)
                : ResistanceSet.Global;
        resolver.AddStatus(invocation, new StatusEffect
        {
            Key = layered ? $"{invocation.SourceKey}|{Type}|{stat}|{key}" : $"{invocation.SourceKey}|{Type}|{stat}",
            Kind = StatusKind.Modifier,
            Stat = stat,
            ResistanceKey = key,
            Delta = percent ? 0 : _sign * e.Number("delta", 0),
            Percent = percent ? _sign * e.Number("percent", 0) : 0,
            Duration = Math.Max(0.01, e.Number("duration", 5)),
            Source = invocation.Source?.Id,
        });
    }
}

/// <summary>
/// <c>periodic</c>: damage or healing of N HP per game second, applied continuously (N × game time every tick). From a
/// skill or a tile's enter/exit trigger — a status for a duration, ticked by the status system; from a tile's «stay»
/// trigger — directly for the time slice the unit spent on the tile this tick (spec part 4 §7.2).
/// </summary>
internal sealed class PeriodicEffectHandler : IEffectHandler
{
    public string Type => EffectTypes.Periodic;

    public void Apply(EffectInvocation invocation, EffectResolver resolver)
    {
        var e = invocation.Effect;
        var heals = e.Text("kind", "damage") == "heal";
        if (invocation.Slice > 0)
        {
            var amount = Math.Max(0, e.Number("perSecond", 5)) * invocation.Slice;
            if (heals)
            {
                resolver.Heal(invocation.Target, invocation.Source, amount, invocation.Tick, DamageKind.Surface);
            }
            else
            {
                resolver.DealDamage(invocation.Target, invocation.Source, amount, invocation.Tick, DamageOrigin.Surface, e.Text("damageType", DamageTypes.Direct), 0, periodic: true);
            }

            return;
        }
        resolver.AddStatus(invocation, new StatusEffect
        {
            Key = $"{invocation.SourceKey}|{Type}|{(heals ? "heal" : "damage")}",
            Kind = StatusKind.Periodic,
            Heals = heals,
            DamageTypeId = e.Text("damageType", DamageTypes.Untyped),
            AmountPerSecond = Math.Max(0, e.Number("perSecond", 5)),
            Duration = Math.Max(0.01, e.Number("duration", 3)),
            Source = invocation.Source?.Id,
        });
    }
}
