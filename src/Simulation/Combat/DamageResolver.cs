using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Combat;
using HexTactics.Core.Units;

namespace HexTactics.Simulation;

/// <summary>Technical limits of combat reactions (spec part 5 §5.5); documented in ARCHITECTURE.md.</summary>
/// <summary>
/// A named stage that may change a damage event before D0 is frozen (spec part 5 §13.4: the reserved point of the future
/// element system — element interactions run here, before resistance, without a new formula in every map, unit or skill).
/// No stage is registered in this edition; <see cref="SimulationSettings.PreDamageStages"/> is empty by default.
/// </summary>
public interface IDamageStage
{
    /// <summary>Shown in traces and logs.</summary>
    string Name { get; }

    /// <summary>The event to resolve instead (the same instance when nothing changes). Must not touch health directly.</summary>
    DamageEvent Apply(DamageEvent e, GameState state);
}

/// <summary>What happened to one defense rule on one hit (the trace of the developer sandbox).</summary>
public enum RuleOutcome
{
    /// <summary>The rule acted on the hit.</summary>
    Applied = 0,
    TypeMismatch = 1,
    TagMismatch = 2,

    /// <summary>The hit's type ignores numeric resistance, so a resistance threshold is never met.</summary>
    ResistanceUnused = 3,
    ResistanceBelow = 4,
    HealthNotBelow = 5,

    /// <summary>Cooldown running or the activation limit of the match reached.</summary>
    NotReady = 6,

    /// <summary>The defender was immune and the rule does not act on immune contact.</summary>
    SkippedByImmunity = 7,

    /// <summary>The rule applied but had nothing to do (e.g. no budget left, a cap above the damage).</summary>
    NothingToDo = 8,
}

/// <summary>One rule considered for one hit: <see cref="Value"/> is the measured R or health %, <see cref="Threshold"/> the rule's.</summary>
public sealed record RuleCheck(long EventId, UnitId Unit, string RuleId, DefenseTrigger Trigger, RuleOutcome Outcome, double? Value = null, double? Threshold = null);

public sealed record CombatLimits
{
    /// <summary>Reactions may come from primary hits only: a reactive packet never produces another one.</summary>
    public int MaxProcDepth { get; init; } = 1;

    /// <summary>Packets generated from one root action (a hit and its reactions).</summary>
    public int MaxGeneratedEventsPerRoot { get; init; } = 64;

    /// <summary>Damage and heal events resolved in one tick.</summary>
    public int MaxEventsPerTick { get; init; } = 4096;

    /// <summary>Targets of one reaction packet.</summary>
    public int MaxReactionTargets { get; init; } = 32;
}

/// <summary>
/// The single damage calculation of the game (spec part 5 §5). Normal attacks, skill hits, tile surfaces, periodic statuses
/// and reactions all hand a typed <see cref="DamageEvent"/> to <see cref="Resolve"/>. Stages of one hit on one target, in this
/// fixed order: (1) eligibility → (2) D0 ≥ 0, frozen → (3) explicit immunity → (4) conversions from one budget D0 by rule
/// priority (negate / heal / shield / redirect) → (5) numeric resistance with penetration (resistance-affected types), flat
/// reduction, cap → (6) shields in order, then health → (7) consequences of the actual loss (retaliation, vampirism) →
/// (8) produced packets go to a queue processed after the current event, never recursively. Healing goes through
/// <see cref="Heal"/> (incoming healing multiplier, never above the maximum). Values are doubles in a fixed order on the
/// fixed tick; nothing is rounded until display.
/// </summary>
public sealed class DamageResolver
{
    private readonly GameState _state;
    private readonly List<SimulationEvent> _events;
    private readonly SimulationSettings _settings;
    private readonly Queue<Pending> _queue = new();
    private readonly Dictionary<(UnitId Unit, string Rule), (long LastTick, int Count, long Hit)> _ruleUse = new();
    private long _hitSerial;
    private readonly Dictionary<long, int> _generated = new();
    private readonly Dictionary<(UnitId Unit, string Rule, int Action, UnitId? Source), Aggregate> _continuous = new();
    private long _nextEventId;
    private long _tick = -1;
    private int _eventsThisTick;
    private bool _draining;

    internal DamageResolver(GameState state, List<SimulationEvent> events, SimulationSettings settings)
    {
        _state = state;
        _events = events;
        _settings = settings;
    }

    /// <summary>The last resolved result of a primary event (tests, traces).</summary>
    public DamageResult? Last { get; private set; }

    /// <summary>Every result of the current tick in resolution order, reactions included (tests, traces).</summary>
    public List<DamageResult> Results { get; } = new();

    /// <summary>
    /// When set, every rule considered for a hit is recorded with its outcome (spec part 5 §6.5: why a rule did or did not
    /// act). Off in matches; the developer sandbox turns it on.
    /// </summary>
    public List<RuleCheck>? Trace { get; set; }

    public CombatLimits Limits => _settings.CombatLimits;

    /// <summary>The damage type of an id; an unknown id falls back to the legacy neutral type (validation prevents it).</summary>
    public DamageTypeDefinition TypeOf(string id) =>
        _settings.DamageTypes.TryGetValue(id, out var t) ? t
        : DamageTypes.BuiltIn.TryGetValue(id, out var b) ? b
        : DamageTypes.BuiltIn[DamageTypes.Untyped];

    /// <summary>A new primary event (its own root action).</summary>
    public DamageEvent Create(UnitId? source, UnitId target, string damageTypeId, double amount, DamageOrigin origin, double penetration = 0, bool periodic = false,
        double armorPenetration = 0, double armorPenetrationPercent = 0)
    {
        var id = ++_nextEventId;
        return new DamageEvent
        {
            EventId = id,
            RootActionId = id,
            Source = source,
            Target = target,
            DamageTypeId = damageTypeId,
            Amount = amount,
            Penetration = penetration,
            ArmorPenetration = armorPenetration,
            ArmorPenetrationPercent = armorPenetrationPercent,
            Origin = origin,
            Periodic = periodic,
        };
    }

    /// <summary>
    /// The defense A of <paramref name="target"/> against a type (spec part 6 §3.2): raw total of the layers and temporary
    /// changes, and the effective value after max(0, ·) and the armor penetration; null for true-like types.
    /// </summary>
    public (double Raw, double Effective)? EffectiveArmor(UnitState target, string damageTypeId, double penetrationFlat = 0, double penetrationPercent = 0)
    {
        var type = TypeOf(damageTypeId);
        if (type.Strategy != DamageStrategy.ResistanceAffected)
        {
            return null;
        }

        var raw = ArmorMath.Raw(target.Stats.Armor, target.Statuses, type);
        return (raw, ArmorMath.Effective(raw, penetrationFlat, penetrationPercent));
    }

    /// <summary>The numeric resistance R of <paramref name="target"/> against a type after penetration; null for true-like types.</summary>
    public double? EffectiveResistance(UnitState target, string damageTypeId, double penetration = 0)
    {
        var type = TypeOf(damageTypeId);
        if (type.Strategy != DamageStrategy.ResistanceAffected)
        {
            return null;
        }

        return ResistanceMath.AfterPenetration(ResistanceMath.BeforePenetration(target.Stats.Resistances, target.Statuses, type), penetration);
    }

    /// <summary>Resolves a damage event and then every reaction it queued (iteratively, in creation order).</summary>
    public DamageResult Resolve(DamageEvent e, long tick)
    {
        BeginTick(tick);
        var result = ResolveOne(e, tick);
        if (!e.Reactive)
        {
            Last = result;
        }

        Drain(tick);
        return result;
    }

    /// <summary>
    /// The single healing path (spec part 5 §4.6 <c>HealResolver</c>): the incoming healing multiplier of the target
    /// (healing block) applies, health never rises above the maximum; healing is never negative damage.
    /// </summary>
    public double Heal(UnitState target, UnitId? source, double amount, long tick, DamageKind kind)
    {
        BeginTick(tick);
        if (!target.IsAlive || !(amount > 0) || !double.IsFinite(amount))
        {
            return 0;
        }

        var healed = _state.Heal(target.Id, amount * Math.Max(0, target.Stats.HealingReceived));
        _events.Add(new HealApplied(tick, target.Id, source, healed, target.CurrentHealth, kind));
        return healed;
    }

    /// <summary>True if a defense rule of the target blocks a status of this category (buff, debuff, periodic).</summary>
    public bool BlocksStatus(UnitState target, string category)
    {
        foreach (var rule in target.Stats.DefenseRules)
        {
            if (rule.Trigger != DefenseTrigger.OnStatusApplied)
            {
                continue;
            }

            foreach (var a in rule.Actions)
            {
                if (a.Kind == DefenseActionKind.StatusImmunity && (a.StatusCategory == "all" || a.StatusCategory == category))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void BeginTick(long tick)
    {
        if (tick == _tick)
        {
            return;
        }

        _tick = tick;
        _eventsThisTick = 0;
        _generated.Clear();
        Results.Clear();
        FlushContinuous(tick, force: false);
    }

    private DamageResult ResolveOne(DamageEvent e, long tick)
    {
        // 1. Eligibility: the target exists and is alive at the moment of the hit; the tick budget is not exhausted.
        var target = _state.FindUnit(e.Target);
        if (target is null || !target.IsAlive)
        {
            return Record(new DamageResult { Event = e, Missed = true });
        }

        if (++_eventsThisTick > Limits.MaxEventsPerTick)
        {
            _events.Add(new CombatLimitReached(tick, e.RootActionId, "maxEventsPerTick"));
            return Record(new DamageResult { Event = e, Missed = true });
        }

        _hitSerial++;

        // 1a. The attacker's outgoing passives (spec part 6 §4.2, §8 stage 2): only a primary normal attack or skill hit of a
        //     living source; reactions, surfaces and statuses keep their strength.
        var type = TypeOf(e.DamageTypeId);
        var baseAmount = double.IsFinite(e.Amount) ? Math.Max(0, e.Amount) : 0;
        var modifiers = new List<(DamageModifierKind, ModifierSource)>();
        var outgoing = 1.0;
        if (!e.Reactive && e.Origin is DamageOrigin.NormalAttack or DamageOrigin.Skill && e.Source is { } src && _state.FindUnit(src) is { IsAlive: true } attackerUnit &&
            attackerUnit.Stats.DamageModifiers.Count > 0)
        {
            var applied = new List<ModifierSource>();
            var attackerHealth = attackerUnit.MaxHealth > 0 ? attackerUnit.CurrentHealth / attackerUnit.MaxHealth * 100 : 0;
            outgoing = ModifierMath.DamageMultiplier(attackerUnit.Stats.DamageModifiers, DamageModifierKind.Outgoing, type, attackerHealth, applied);
            modifiers.AddRange(applied.Select(a => (DamageModifierKind.Outgoing, a)));
            e = e with { Amount = baseAmount * outgoing };
        }

        // 1b. Registered stages before D0 (spec part 5 §13.4; none in this edition).
        foreach (var stage in _settings.PreDamageStages)
        {
            e = stage.Apply(e, _state);
        }

        // 2. D0: the attacker's side is already in the event; frozen, never negative or NaN.
        var d0 = double.IsFinite(e.Amount) ? Math.Max(0, e.Amount) : 0;
        var r = EffectiveResistance(target, e.DamageTypeId, e.Penetration);
        var healthPercent = target.MaxHealth > 0 ? target.CurrentHealth / target.MaxHealth * 100 : 0;
        var rules = Eligible(target, DefenseTrigger.OnHit, type, r, healthPercent, tick, e.EventId);

        // 3. Explicit immunity: no health from this component; contact reactions only with «triggerOnImmuneContact».
        var immune = false;
        foreach (var rule in rules.Where(rule => rule.Actions.Any(a => a.Kind == DefenseActionKind.Immunity)))
        {
            immune = true;
            Use(target.Id, rule, tick);
        }

        // 4. Conversions from one budget D0 (priority, then id); each rule gets only what is left.
        var budget = d0;
        var converted = 0.0;
        var healed = 0.0;
        foreach (var rule in rules)
        {
            if ((immune && !rule.TriggerOnImmuneContact) || !rule.Actions.Any(IsConversion))
            {
                continue;
            }

            var used = false;
            for (var i = 0; i < rule.Actions.Count; i++)
            {
                var a = rule.Actions[i];
                if (!IsConversion(a) || (a.Kind == DefenseActionKind.RedirectAsDamage && e.Reactive))
                {
                    continue; // a reactive packet is never reflected again (spec part 5 §5.5)
                }

                var wanted = Math.Max(0, Math.Min(1, a.Fraction)) * d0;
                var take = Math.Min(wanted, budget);
                if (wanted > take + 1e-12)
                {
                    _events.Add(new CombatLimitReached(tick, e.RootActionId, $"conversions over 100 % of D0 ({rule.Id})"));
                }

                budget -= take;
                converted += take;
                used = true;
                switch (a.Kind)
                {
                    case DefenseActionKind.ConvertToHealing:
                        healed += Heal(target, target.Id, take, tick, DamageKind.Reaction); // before the rest of the hit
                        break;
                    case DefenseActionKind.ConvertToShield when a.Shield is { } name:
                        if (target.Shields.FirstOrDefault(s => s.Definition.Name == name) is { } shield)
                        {
                            shield.Current = Math.Min(shield.Definition.Capacity, shield.Current + take);
                        }

                        break;
                    case DefenseActionKind.RedirectAsDamage:
                        React(target, e, rule, i, a, take, tick);
                        break;
                }
            }

            if (used)
            {
                Use(target.Id, rule, tick);
            }
        }

        // 5. Resistance R (resistance-affected types), once.
        var afterResistance = immune ? 0 : r is { } value ? budget * ResistanceMath.Multiplier(value) : budget;

        // 5a. Defense A with its own penetration: × K / (K + A_eff) (spec part 6 §3.2, §8 stage 7); true-like types bypass it.
        var armor = EffectiveArmor(target, e.DamageTypeId, e.ArmorPenetration, e.ArmorPenetrationPercent);
        var k = _settings.CombatRules.KFor(type);
        var after = afterResistance * (armor is { } av ? ArmorMath.Multiplier(av.Effective, k) : 1);
        var afterArmor = after;

        // 5b. Flat reduction (not for true-like).
        if (!immune)
        {
            foreach (var rule in rules)
            {
                var acted = false;
                foreach (var a in rule.Actions)
                {
                    if (a.Kind == DefenseActionKind.FlatReduction && type.Strategy == DamageStrategy.ResistanceAffected && after > 0)
                    {
                        after = Math.Max(0, after - Math.Max(0, a.Amount));
                        acted = true;
                    }
                }

                if (acted)
                {
                    Use(target.Id, rule, tick);
                }
            }
        }

        // 5c. The target's incoming passives (spec part 6 §4.2, §8 stage 8): after R, A and the flat reduction, never below 0.
        var afterFlat = after;
        var incoming = 1.0;
        if (!immune && target.Stats.DamageModifiers.Count > 0)
        {
            var applied = new List<ModifierSource>();
            incoming = ModifierMath.DamageMultiplier(target.Stats.DamageModifiers, DamageModifierKind.Incoming, type, healthPercent, applied);
            modifiers.AddRange(applied.Select(a => (DamageModifierKind.Incoming, a)));
            after *= incoming;
        }

        // 5d. Damage cap.
        if (!immune)
        {
            foreach (var rule in rules)
            {
                foreach (var cap in rule.Actions.Where(a => a.Kind == DefenseActionKind.DamageCap))
                {
                    if (after > Math.Max(0, cap.Amount))
                    {
                        after = Math.Max(0, cap.Amount);
                        Use(target.Id, rule, tick);
                    }
                }
            }
        }

        // 6. Shields in order (type filter), then health.
        var shieldLoss = 0.0;
        foreach (var shield in target.Shields)
        {
            if (after <= 0)
            {
                break;
            }

            if (shield.Current > 0 && (shield.Definition.DamageType is null || shield.Definition.DamageType == type.Id || shield.Definition.DamageType == type.FamilyId))
            {
                var take = Math.Min(shield.Current, after);
                shield.Current -= take;
                after -= take;
                shieldLoss += take;
            }
        }

        var hpLoss = _state.ApplyDamage(target.Id, after);
        _events.Add(new DamageApplied(tick, target.Id, e.Source ?? target.Id, hpLoss, target.CurrentHealth, KindOf(e))
        {
            DamageTypeId = type.Id,
            Immune = immune,
            ShieldAbsorbed = shieldLoss,
        });

        // 7. Consequences of the actual loss: retaliation (needs a living defender), vampirism of the attacker.
        var reported = new List<(UnitId Unit, DefenseRule Rule)>();
        if (!e.Reactive && target.IsAlive && (hpLoss > 0 || shieldLoss > 0))
        {
            var lossRules = Eligible(target, DefenseTrigger.OnHealthLoss, type, r, healthPercent, tick, e.EventId);
            reported.AddRange(lossRules.Select(x => (target.Id, x)));
            foreach (var rule in lossRules)
            {
                var used = false;
                for (var i = 0; i < rule.Actions.Count; i++)
                {
                    var a = rule.Actions[i];
                    var basis = a.Base == RetaliationBase.ShieldLoss ? shieldLoss : hpLoss;
                    if (a.Kind == DefenseActionKind.Retaliate && basis > 0)
                    {
                        React(target, e, rule, i, a, Math.Max(0, Math.Min(10, a.Fraction)) * basis, tick);
                        used = true;
                    }
                }

                if (used)
                {
                    Use(target.Id, rule, tick);
                }
            }
        }

        if (!e.Reactive && hpLoss > 0 && e.Source is { } sourceId && _state.FindUnit(sourceId) is { IsAlive: true } attacker && attacker.Id != target.Id)
        {
            // «Health below» of a vampirism rule refers to its owner — the attacker; resistance conditions do not apply.
            var attackerHealth = attacker.MaxHealth > 0 ? attacker.CurrentHealth / attacker.MaxHealth * 100 : 0;
            foreach (var rule in Eligible(attacker, DefenseTrigger.OnDamageDealt, type, null, attackerHealth, tick, e.EventId))
            {
                reported.Add((attacker.Id, rule));
                foreach (var a in rule.Actions.Where(a => a.Kind == DefenseActionKind.Vampirism))
                {
                    _queue.Enqueue(new Pending(null, attacker.Id, Math.Max(0, Math.Min(10, a.Fraction)) * hpLoss));
                    Use(attacker.Id, rule, tick);
                }
            }
        }

        if (Trace is not null)
        {
            foreach (var (unit, rule) in rules.Select(x => (target.Id, x)).Concat(reported))
            {
                var acted = _ruleUse.TryGetValue((unit, rule.Id), out var use) && use.Hit == _hitSerial;
                Trace.Add(new RuleCheck(e.EventId, unit, rule.Id, rule.Trigger,
                    acted ? RuleOutcome.Applied : immune && !rule.TriggerOnImmuneContact && rule.Trigger == DefenseTrigger.OnHit ? RuleOutcome.SkippedByImmunity : RuleOutcome.NothingToDo,
                    rule.Trigger == DefenseTrigger.OnHit ? r : null, rule.MinEffectiveResistance));
            }
        }

        return Record(new DamageResult
        {
            Event = e,
            BaseAmount = baseAmount,
            OutgoingMultiplier = outgoing,
            D0 = d0,
            Immune = immune,
            Converted = converted,
            Healed = healed,
            Resistance = r,
            AfterResistance = afterResistance,
            ArmorRaw = armor?.Raw,
            Armor = armor?.Effective,
            DefenseK = k,
            AfterArmor = afterArmor,
            AfterFlat = afterFlat,
            IncomingMultiplier = incoming,
            Modifiers = modifiers,
            ShieldLoss = shieldLoss,
            HpLoss = hpLoss,
            Killed = !target.IsAlive,
        });
    }

    private DamageResult Record(DamageResult result)
    {
        Results.Add(result);
        return result;
    }

    private static bool IsConversion(DefenseAction a) =>
        a.Kind is DefenseActionKind.Negate or DefenseActionKind.ConvertToHealing or DefenseActionKind.ConvertToShield or DefenseActionKind.RedirectAsDamage;

    /// <summary>
    /// The rules of <paramref name="unit"/> for this trigger that apply to the hit now, in their order (priority, then id).
    /// With <see cref="Trace"/> on, every rule that does not apply is recorded with the reason.
    /// </summary>
    private List<DefenseRule> Eligible(UnitState unit, DefenseTrigger trigger, DamageTypeDefinition type, double? r, double healthPercent, long tick, long eventId)
    {
        var list = new List<DefenseRule>();
        foreach (var rule in unit.Stats.DefenseRules.Where(x => x.Trigger == trigger).OrderBy(x => x.Priority).ThenBy(x => x.Id, StringComparer.Ordinal))
        {
            var (outcome, value, threshold) = Check(rule, type, r, healthPercent, trigger == DefenseTrigger.OnDamageDealt, unit.Id, tick);
            if (outcome == RuleOutcome.Applied)
            {
                list.Add(rule);
            }
            else
            {
                Trace?.Add(new RuleCheck(eventId, unit.Id, rule.Id, trigger, outcome, value, threshold));
            }
        }

        return list;
    }

    private (RuleOutcome Outcome, double? Value, double? Threshold) Check(DefenseRule rule, DamageTypeDefinition type, double? r, double healthPercent, bool ignoreResistance, UnitId unit, long tick)
    {
        if (rule.DamageType is not null && rule.DamageType != type.Id && rule.DamageType != type.FamilyId)
        {
            return (RuleOutcome.TypeMismatch, null, null);
        }

        if (rule.DamageTag is not null && !type.Tags.Contains(rule.DamageTag))
        {
            return (RuleOutcome.TagMismatch, null, null);
        }

        if (!ignoreResistance && rule.MinEffectiveResistance is { } min)
        {
            if (r is not { } value)
            {
                return (RuleOutcome.ResistanceUnused, null, min);
            }

            if (value < min - 1e-9)
            {
                return (RuleOutcome.ResistanceBelow, value, min);
            }
        }

        if (rule.HealthBelowPercent is { } below && healthPercent >= below)
        {
            return (RuleOutcome.HealthNotBelow, healthPercent, below);
        }

        return Ready(unit, rule, tick) ? (RuleOutcome.Applied, null, null) : (RuleOutcome.NotReady, null, null);
    }

    private bool Ready(UnitId unit, DefenseRule rule, long tick)
    {
        if (!_ruleUse.TryGetValue((unit, rule.Id), out var use))
        {
            return true;
        }

        var cooldownTicks = (long)Math.Round(Math.Max(0, rule.Cooldown) * _settings.TicksPerSecond);
        return (rule.MaxTriggers <= 0 || use.Count < rule.MaxTriggers) && tick - use.LastTick >= Math.Max(cooldownTicks, rule.Cooldown > 0 ? 1 : 0);
    }

    /// <summary>Counts one activation of a rule per hit, however many of its actions acted.</summary>
    private void Use(UnitId unit, DefenseRule rule, long tick)
    {
        var known = _ruleUse.TryGetValue((unit, rule.Id), out var use);
        if (known && use.Hit == _hitSerial)
        {
            return;
        }

        _ruleUse[(unit, rule.Id)] = (tick, (known ? use.Count : 0) + 1, _hitSerial);
    }

    /// <summary>
    /// A redirected or retaliating packet (spec part 5 §4.4): reactive, one level deeper, typed as the hit unless the action
    /// says otherwise; targets are chosen now; without targets the share vanishes. Reactions to continuous damage (surfaces,
    /// periodic statuses) are summed and sent once per game second (spec part 5 §8.2).
    /// </summary>
    private void React(UnitState defender, DamageEvent e, DefenseRule rule, int actionIndex, DefenseAction a, double amount, long tick)
    {
        if (!(amount > 0))
        {
            return;
        }

        if (e.Periodic)
        {
            var key = (defender.Id, rule.Id, actionIndex, e.Source);
            if (!_continuous.TryGetValue(key, out var acc))
            {
                acc = new Aggregate(defender.Id, e, a, tick);
                _continuous[key] = acc;
            }

            acc.Amount += amount;
            return;
        }

        Send(defender, e, a, amount, tick);
    }

    private void Send(UnitState defender, DamageEvent e, DefenseAction a, double amount, long tick)
    {
        if (e.ProcDepth + 1 > Limits.MaxProcDepth)
        {
            _events.Add(new CombatLimitReached(tick, e.RootActionId, "maxProcDepth"));
            return;
        }

        var targets = new List<UnitState>();
        if (a.Targeting == ReactionTargeting.Attacker)
        {
            if (e.Source is { } s && _state.FindUnit(s) is { IsAlive: true } attacker && attacker.Id != defender.Id && Allowed(defender, attacker, a.Filter))
            {
                targets.Add(attacker);
            }
        }
        else
        {
            targets.AddRange(_state.Units
                .Where(u => u.IsAlive && u.Cell.DistanceTo(defender.Cell) <= Math.Max(0, a.Radius) && (a.IncludeCenter || u.Id != defender.Id) && Allowed(defender, u, a.Filter))
                .OrderBy(u => u.Id.Value)
                .Take(Limits.MaxReactionTargets));
        }

        if (targets.Count == 0)
        {
            return; // the share is spent anyway (spec part 5 §4.3)
        }

        var each = a.Split ? amount / targets.Count : amount;
        foreach (var t in targets)
        {
            var generated = _generated.TryGetValue(e.RootActionId, out var g) ? g : 0;
            if (generated >= Limits.MaxGeneratedEventsPerRoot)
            {
                _events.Add(new CombatLimitReached(tick, e.RootActionId, "maxGeneratedEventsPerRoot"));
                return;
            }

            _generated[e.RootActionId] = generated + 1;
            _queue.Enqueue(new Pending(new DamageEvent
            {
                EventId = ++_nextEventId,
                RootActionId = e.RootActionId,
                Source = defender.Id,
                Target = t.Id,
                DamageTypeId = a.ResponseDamageType ?? e.DamageTypeId,
                Amount = each,
                Origin = DamageOrigin.Reaction,
                Reactive = true,
                ProcDepth = e.ProcDepth + 1,
            }, null, 0));
        }
    }

    private static bool Allowed(UnitState defender, UnitState other, ReactionFilter filter) => filter switch
    {
        ReactionFilter.EnemiesOfDefender => other.Side != defender.Side,
        ReactionFilter.AlliesOfDefender => other.Side == defender.Side,
        _ => true,
    };

    private void FlushContinuous(long tick, bool force)
    {
        if (_continuous.Count == 0)
        {
            return;
        }

        foreach (var key in _continuous.Keys.OrderBy(k => k.Unit.Value).ThenBy(k => k.Rule, StringComparer.Ordinal).ThenBy(k => k.Action).ToList())
        {
            var acc = _continuous[key];
            if (!force && tick - acc.StartTick < _settings.TicksPerSecond)
            {
                continue;
            }

            _continuous.Remove(key);
            if (_state.FindUnit(acc.Defender) is { } defender)
            {
                Send(defender, acc.Event, acc.Action, acc.Amount, tick);
            }
        }
    }

    private void Drain(long tick)
    {
        if (_draining)
        {
            return;
        }

        _draining = true;
        try
        {
            while (_queue.Count > 0)
            {
                var p = _queue.Dequeue();
                if (p.Damage is { } damage)
                {
                    ResolveOne(damage, tick);
                }
                else if (p.HealTarget is { } healTarget && _state.FindUnit(healTarget) is { } unit)
                {
                    Heal(unit, unit.Id, p.HealAmount, tick, DamageKind.Reaction);
                }
            }
        }
        finally
        {
            _draining = false;
        }
    }

    /// <summary>Sends pending summed reactions now (end of a test or a match).</summary>
    public void FlushReactions(long tick)
    {
        FlushContinuous(tick, force: true);
        Drain(tick);
    }

    internal void AfterTick(long tick)
    {
        BeginTick(tick);
        FlushContinuous(tick, force: false);
        Drain(tick);
    }

    private static DamageKind KindOf(DamageEvent e) => e.Origin switch
    {
        DamageOrigin.NormalAttack => DamageKind.NormalAttack,
        DamageOrigin.Surface => DamageKind.Surface,
        DamageOrigin.Status => DamageKind.Periodic,
        DamageOrigin.Debug => DamageKind.Debug,
        DamageOrigin.Reaction => DamageKind.Reaction,
        _ => DamageKind.Skill,
    };

    private sealed record Pending(DamageEvent? Damage, UnitId? HealTarget, double HealAmount);

    private sealed class Aggregate
    {
        public Aggregate(UnitId defender, DamageEvent e, DefenseAction action, long startTick)
        {
            Defender = defender;
            Event = e;
            Action = action;
            StartTick = startTick;
        }

        public UnitId Defender { get; }

        public DamageEvent Event { get; }

        public DefenseAction Action { get; }

        public long StartTick { get; }

        public double Amount { get; set; }
    }
}
