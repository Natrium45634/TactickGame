using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using HexTactics.Content.Kinds;
using HexTactics.Content.Library;
using HexTactics.Content.Registry;
using HexTactics.Content.Runtime;
using HexTactics.Core.Combat;
using HexTactics.Core.Hex;
using HexTactics.Core.Map;
using HexTactics.Core.Skills;
using HexTactics.Core.Spawning;
using HexTactics.Core.Units;
using HexTactics.Simulation;

namespace HexTactics.Editor.Mechanics;

/// <summary>
/// What the «Проверить на ударе» panel hits (spec part 5 §6.5): the target (a library unit or an empty one), the damage type,
/// D0, penetration, the target's health and how many enemies and allies of the target stand on the adjacent hexes.
/// </summary>
public sealed record SandboxSetup
{
    /// <summary>A unit of the library; null — an empty target (100 HP, no resistances or rules).</summary>
    public string? TargetUnitId { get; init; }

    public string DamageTypeId { get; init; } = EffectTypeRegistry.DefaultDamageType;

    public double Amount { get; init; } = 40;

    public double Penetration { get; init; }

    /// <summary>Armor penetration in defense points (spec part 6 §3.2); lowers only A.</summary>
    public double ArmorPenetration { get; init; }

    /// <summary>Armor penetration in percent of the remaining A (0…100).</summary>
    public double ArmorPenetrationPercent { get; init; }

    /// <summary>The attacker: a unit of the library (its outgoing passives act, spec part 6 §4.2, §11); null — a plain attacker.</summary>
    public string? AttackerUnitId { get; init; }

    /// <summary>The side of the target the attacker stands on (spec part 6 §6.1): 0 — front … 3 — rear, clockwise.</summary>
    public int Sector { get; init; }

    /// <summary>A forced outcome (spec part 6 §11 p.3: «принудительно: обычный / крит / сверхкрит, внезапный / нет»); null — the roll.</summary>
    public bool? ForceSurprise { get; init; }

    public CritTier? ForceCrit { get; init; }

    /// <summary>The combat ruleset whose K is used (spec part 6 §3.2); the standard one by default.</summary>
    public string RulesetId { get; init; } = CombatRulesetData.Standard;

    /// <summary>Health of the target before the hit, % of its maximum.</summary>
    public double HealthPercent { get; init; } = 100;

    /// <summary>Enemies of the target on the six adjacent hexes (0–6), the attacker included when it is adjacent.</summary>
    public int EnemiesAround { get; init; } = 2;

    public int AlliesAround { get; init; }

    /// <summary>The attacker stands next to the target (otherwise three hexes away).</summary>
    public bool AttackerAdjacent { get; init; } = true;

    /// <summary>Seed of the deterministic rolls (spec part 6 §11 p.3): the same seed and key give the same outcome.</summary>
    public int Seed { get; init; } = DefaultSeed;

    /// <summary>Key of the roll of this hit; a series of checks uses <c>sandbox:{i}</c>.</summary>
    public string RollKey { get; init; } = "sandbox";

    /// <summary>PROTOTYPE_DEFAULT seed of the battle (the same as <c>SimulationSettings.Seed</c>).</summary>
    public const int DefaultSeed = 20261106;
}

/// <summary>A reproducible series of hits (spec part 6 §11 p.6 «Проверить 100 событий»): what happened and how much HP was lost.</summary>
public sealed record SandboxBatch(int Hits, int Seed, int Surprised, int Crits, int SuperCrits, double MinLoss, double AverageLoss, double MaxLoss);

/// <summary>One hit in the sandbox: the result, the stages in words, the reasons of every rule and what the others lost.</summary>
public sealed record SandboxRun(
    DamageResult Result,
    double TargetBefore,
    double TargetAfter,
    double TargetMax,
    double ShieldsLeft,
    IReadOnlyList<string> Stages,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<(string Who, double Before, double After)> Others);

/// <summary>The sandbox report: the hit with the edited object and, for a rule, the same hit without it.</summary>
public sealed record SandboxReport(SandboxRun With, SandboxRun? Without, string Comparison, IReadOnlyList<string> Problems);

/// <summary>
/// The developer sandbox (spec part 5 §6.5): one hit of a chosen type and strength against a chosen target in a throwaway
/// simulation on an empty board, with the edited (also unsaved) rule, unit or damage type. Nothing of the library, a match
/// or the files changes. The same <see cref="DamageResolver"/> as in battle computes the numbers; its trace explains the rules.
/// </summary>
public static class HitSandbox
{
    private static readonly HexCoord Center = HexCoord.FromOffset(3, 3);

    /// <param name="ruleDraft">A defense rule being edited: the hit is resolved with it on the target and once more without it.</param>
    /// <param name="unitDraft">A unit being edited: used as the target instead of its saved version.</param>
    /// <param name="typeDraft">A damage type being edited: replaces its saved definition (also a new, unsaved type).</param>
    /// <param name="rulesetDraft">Combat rules being edited: their K is used instead of the saved ones.</param>
    /// <param name="attackerDraft">A unit being edited as the attacker (its unsaved outgoing passives act).</param>
    public static SandboxReport Run(ContentLibrary library, SandboxSetup setup, ContentItem? ruleDraft = null, ContentItem? unitDraft = null, ContentItem? typeDraft = null,
        ContentItem? rulesetDraft = null, ContentItem? attackerDraft = null)
    {
        var problems = new List<string>();
        var rulesetId = rulesetDraft?.Id ?? setup.RulesetId;
        if (rulesetDraft is null && library.Find(ContentTypeRegistry.CombatRuleset, rulesetId) is null)
        {
            problems.Add($"правила боя «{rulesetId}» не найдены — K = {N(CombatRules.DefaultDefenseK)}");
        }

        var rules = RuntimeFactory.BuildCombatRules(library, rulesetId, rulesetDraft);
        var types = new Dictionary<string, DamageTypeDefinition>(RuntimeFactory.BuildDamageTypes(library), StringComparer.Ordinal);
        if (typeDraft?.Data is DamageTypeData)
        {
            types[typeDraft.Id] = RuntimeFactory.DamageTypeOf(typeDraft);
        }

        if (!types.ContainsKey(setup.DamageTypeId))
        {
            problems.Add($"тип урона «{setup.DamageTypeId}» не найден — удар считается как физический");
        }

        var targetItem = unitDraft ?? (setup.TargetUnitId is { } id ? library.Find(ContentTypeRegistry.Unit, id) : null);
        if (setup.TargetUnitId is not null && targetItem is null)
        {
            problems.Add($"юнит «{setup.TargetUnitId}» не найден — цель пустая");
        }

        var issues = new HexTactics.Content.Model.IssueList();
        var stats = targetItem is not null ? RuntimeFactory.BuildUnit(targetItem, library, issues).Stats : new UnitStats();
        var attackerItem = attackerDraft ?? (setup.AttackerUnitId is { } aid ? library.Find(ContentTypeRegistry.Unit, aid) : null);
        if (setup.AttackerUnitId is not null && attackerItem is null)
        {
            problems.Add($"юнит «{setup.AttackerUnitId}» не найден — атакующий без пассивов");
        }

        var attackerStats = attackerItem is not null ? RuntimeFactory.BuildUnit(attackerItem, library).Stats with { DefenseRules = Array.Empty<DefenseRule>() } : new UnitStats();
        problems.AddRange(issues.Items.Select(i => i.Message));
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in library.Items(ContentTypeRegistry.DefenseRule))
        {
            names[r.Id] = r.Name;
        }

        DefenseRule? edited = null;
        if (ruleDraft?.Data is DefenseRuleData draft)
        {
            edited = draft.ToRule(ruleDraft.Id, ruleDraft.Name, library.Registry.DefenseActions);
            names[ruleDraft.Id] = ruleDraft.Name;
        }

        var others = stats.DefenseRules.Where(r => edited is null || r.Id != edited.Id).ToList();
        var with = Hit(library, setup, stats with { DefenseRules = edited is null ? others : others.Append(edited).ToList() }, types, names, rules, attackerStats);
        SandboxRun? without = null;
        var comparison = string.Empty;
        if (edited is not null)
        {
            without = Hit(library, setup, stats with { DefenseRules = others }, types, names, rules, attackerStats);
            comparison = $"С правилом: {Short(with)}. Без правила: {Short(without)}.";
        }

        return new SandboxReport(with, without, comparison, problems);
    }

    private static string Short(SandboxRun run)
    {
        var parts = new List<string> { $"HP цели {N(run.TargetBefore)} → {N(run.TargetAfter)}" };
        if (run.Result.Healed > 0)
        {
            parts.Add($"лечение +{N(run.Result.Healed)}");
        }

        if (run.Result.ShieldLoss > 0)
        {
            parts.Add($"щит −{N(run.Result.ShieldLoss)}");
        }

        var hurt = run.Others.Where(o => o.After < o.Before - 1e-9).ToList();
        parts.Add(hurt.Count == 0 ? "другим — 0" : "другим: " + string.Join(", ", hurt.Select(o => $"{o.Who} −{N(o.Before - o.After)}")));
        return string.Join(", ", parts);
    }

    private static SandboxRun Hit(ContentLibrary library, SandboxSetup setup, UnitStats targetStats, IReadOnlyDictionary<string, DamageTypeDefinition> types, IReadOnlyDictionary<string, string> ruleNames,
        CombatRules rules, UnitStats attackerStats)
    {
        var grid = new HexGrid(7, 7);
        foreach (var c in grid.Coords)
        {
            grid.SetTile(c, TileType.Walkable, null, 0);
        }

        var sim = new GameSimulation(new MapDefinition("sandbox", grid, Array.Empty<SpawnZoneDefinition>(), null), new SimulationSettings { DamageTypes = types, CombatRules = rules, Seed = setup.Seed });
        var noSkills = Array.Empty<string>();
        UnitId Spawn(HexCoord cell, Side side, UnitStats stats, string name) =>
            sim.Spawn(SpawnRequest.AtCell(cell, side, HexDirection.North) with { Profile = new UnitProfile("sandbox." + name, name, stats, noSkills) }).Unit!.Value;

        var target = Spawn(Center, Side.Player, targetStats, "цель");
        // The target faces north; the attacker stands on the chosen side (spec part 6 §6.1), the others around.
        var side = Math.Clamp(setup.Sector, 0, 5);
        var ring = Enumerable.Range(0, 6).Select(i => Center.Neighbor(HexDirections.All[(side + i) % 6])).ToList();
        var labels = new Dictionary<UnitId, string>();
        var enemies = Math.Clamp(setup.EnemiesAround, 0, 6);
        var allies = Math.Clamp(setup.AlliesAround, 0, 6 - enemies);
        var slot = 0;
        UnitId attacker;
        if (setup.AttackerAdjacent && enemies > 0)
        {
            attacker = Spawn(ring[slot++], Side.Enemy, attackerStats, "атакующий");
            enemies--;
        }
        else
        {
            var far = Center;
            for (var step = 0; step < 3; step++)
            {
                far = far.Neighbor(HexDirections.All[side]);
            }

            attacker = Spawn(far, Side.Enemy, attackerStats, "атакующий");
        }

        labels[attacker] = "атакующий";
        for (var i = 0; i < enemies; i++)
        {
            var u = Spawn(ring[slot++], Side.Enemy, new UnitStats(), "враг");
            labels[u] = $"враг {i + 1}";
        }

        for (var i = 0; i < allies; i++)
        {
            var u = Spawn(ring[slot++], Side.Player, new UnitStats(), "союзник");
            labels[u] = $"союзник {i + 1}";
        }

        var t = sim.State.GetUnit(target);
        var missing = t.MaxHealth * (1 - (Math.Clamp(setup.HealthPercent, 1, 100) / 100));
        if (missing > 0)
        {
            sim.State.ApplyDamage(target, missing);
        }

        var before = t.CurrentHealth;
        var othersBefore = labels.ToDictionary(x => x.Key, x => sim.State.GetUnit(x.Key).CurrentHealth);
        var trace = new List<RuleCheck>();
        sim.Damage.Trace = trace;
        var typeId = types.ContainsKey(setup.DamageTypeId) ? setup.DamageTypeId : DamageTypes.Physical;
        var result = sim.Damage.Resolve(sim.Damage.Create(attacker, target, typeId, setup.Amount, DamageOrigin.Skill, setup.Penetration,
            armorPenetration: setup.ArmorPenetration, armorPenetrationPercent: setup.ArmorPenetrationPercent, canCrit: true, canSurprise: true, rollKey: setup.RollKey)
            with { ForcedSurprise = setup.ForceSurprise, ForcedCrit = setup.ForceCrit }, sim.Tick);
        var reactions = sim.Damage.Results.Where(r => r.Event.Reactive).ToList();
        var others = labels.Select(x => (x.Value, othersBefore[x.Key], sim.State.FindUnit(x.Key)?.CurrentHealth ?? 0)).ToList();

        var type = types[typeId];
        var stages = new List<string>();
        void Stage(string text) => stages.Add($"{stages.Count + 1}. {text}");
        string Sources(DamageModifierKind kind) => string.Join("; ", result.Modifiers.Where(m => m.Kind == kind)
            .Select(m => $"{m.Source.Percent.ToString("+0.##;−0.##;0", CultureInfo.InvariantCulture)} % {m.Source.Source}"));
        if (result.Modifiers.Any(m => m.Kind == DamageModifierKind.Outgoing))
        {
            // Spec part 6 §4.2, §8 stage 2: the attacker's passives change the strength before any defense.
            Stage($"Пассивы атакующего: сила {N(result.BaseAmount)} → {N(result.BaseAmount * result.OutgoingMultiplier)} (×{N3(result.OutgoingMultiplier)}: {Sources(DamageModifierKind.Outgoing)}).");
        }

        if (rules.SurpriseEnabled && result.SurpriseSector is { } sector && (result.SurpriseChance > 0 || setup.ForceSurprise is not null))
        {
            // Spec part 6 §6.3 p.4: «Зона: задний фланг → 75% → бросок → да/нет → ×1,5».
            var roll = setup.ForceSurprise is null ? $"бросок {N3(result.SurpriseRoll ?? 0)}" : "исход задан";
            Stage($"Внезапность: {SurpriseMath.SectorNames[sector]} → {N(result.SurpriseChance)} % → {roll} → {(result.Surprised ? $"да, ×{N(rules.SurpriseMultiplier)}" : "нет")}.");
        }

        if (rules.CritEnabled && (result.CritChance > 0 || setup.ForceCrit is not null))
        {
            var c = result.CritChance;
            var roll = setup.ForceCrit is null ? $"бросок {N3(result.CritRoll ?? 0)}" : "исход задан";
            var tier = result.Crit switch { CritTier.SuperCrit => $"сверхкрит ×{N(rules.SuperCritMultiplier)}", CritTier.Crit => $"крит ×{N(rules.CritMultiplier)}", _ => "обычный" };
            Stage($"Крит: шанс {N(c)} п.п. (крит {N(CritMath.CritOnlyChance(c) * 100)} %, сверхкрит {N(CritMath.SuperChance(c) * 100)} %) → {roll} → {tier}.");
        }

        Stage($"До защиты: сила удара D0 = {N(result.D0)} ({type.Name.ToLowerInvariant()}).");
        Stage(result.Immune ? "Иммунитет: урон HP не наносится." : "Иммунитета нет.");
        var sent = reactions.Where(r => r.Event.Source == target).ToList();
        if (result.Converted > 0)
        {
            Stage($"Из силы удара израсходовано {N(result.Converted)}: лечение +{N(result.Healed)}" +
                  (sent.Count > 0 ? $", ответных ударов: {sent.Count}" : string.Empty) + $"; остаток {N(Math.Max(0, result.D0 - result.Converted))}.");
        }
        else
        {
            Stage("Поглощения, лечения и отражения из силы удара нет.");
        }

        Stage(result.Resistance is { } r
            ? $"Сопротивление: R после пробития = {N(r)} % → {N(result.AfterResistance)}" + (r > 100 ? " (сверх 100 % — просто 0, без лечения)." : ".")
            : $"Сопротивление не учитывается: тип обходит численные R и защиту A → {N(result.AfterResistance)}.");
        if (result.Armor is { } armor && (result.ArmorRaw != 0 || setup.ArmorPenetration > 0 || setup.ArmorPenetrationPercent > 0))
        {
            // Spec part 6 §3.2, §8 stage 7: the defense A is a separate stage after R; its penetration is shown on its own.
            var raw = result.ArmorRaw ?? 0;
            var pen = setup.ArmorPenetration > 0 || setup.ArmorPenetrationPercent > 0
                ? $", после пробития A = {N(armor)}" : string.Empty;
            Stage($"Защита: A = {N(raw)}{(raw < 0 ? " (меньше 0 — считается 0)" : string.Empty)}{pen}, K = {N(result.DefenseK)} → ×{N3(ArmorMath.Multiplier(armor, result.DefenseK))} → {N(result.AfterArmor)}.");
        }

        var taken = result.ShieldLoss + result.HpLoss;
        var incoming = result.Modifiers.Any(m => m.Kind == DamageModifierKind.Incoming);
        if (incoming)
        {
            if (result.AfterFlat < result.AfterArmor - 1e-9)
            {
                Stage($"Плоское снижение: {N(result.AfterArmor)} → {N(result.AfterFlat)}.");
            }

            // Spec part 6 §4.2, §8 stage 8: a multiplier of what is left, not a change of R.
            var afterIncoming = result.AfterFlat * result.IncomingMultiplier;
            Stage($"Пассивы цели: {N(result.AfterFlat)} → {N(afterIncoming)} (×{N3(result.IncomingMultiplier)}: {Sources(DamageModifierKind.Incoming)}).");
            if (!result.Immune && taken < afterIncoming - 1e-9 && t.IsAlive)
            {
                Stage($"Предел: {N(afterIncoming)} → {N(taken)}.");
            }
        }
        else if (!result.Immune && taken < result.AfterArmor - 1e-9 && t.IsAlive)
        {
            Stage($"Плоское снижение и предел: {N(result.AfterArmor)} → {N(taken)}.");
        }

        if (result.ShieldLoss > 0)
        {
            Stage($"Щиты приняли {N(result.ShieldLoss)}.");
        }

        Stage($"HP цели: {N(before)} → {N(t.CurrentHealth)} из {N(t.MaxHealth)} (потеря {N(result.HpLoss)}).");
        foreach (var g in reactions.GroupBy(x => x.Event.Target))
        {
            var who = labels.TryGetValue(g.Key, out var label) ? label : g.Key == target ? "цель" : g.Key.ToString();
            var kind = g.First().Event.Source == target ? "Ответ цели" : "Ответ";
            Stage($"{kind}: {who} −{N(g.Sum(x => x.HpLoss))} ({TypeName(types, g.First().Event.DamageTypeId)}).");
        }

        var reasons = trace.Select(c => Reason(c, ruleNames, c.Unit == target ? "цель" : labels.GetValueOrDefault(c.Unit, "юнит"))).ToList();
        var shields = t.Shields.Sum(s => s.Current);
        return new SandboxRun(result, before, t.CurrentHealth, t.MaxHealth, shields, stages, reasons, others);
    }

    /// <summary>Why a rule did or did not act, in words (spec part 5 §6.5: «R после пробития = 250%, порог 300% не достигнут»).</summary>
    public static string Reason(RuleCheck check, IReadOnlyDictionary<string, string> names, string owner)
    {
        var name = names.TryGetValue(check.RuleId, out var n) ? n : check.RuleId;
        var why = check.Outcome switch
        {
            RuleOutcome.Applied => "сработало",
            RuleOutcome.TypeMismatch => "не сработало: другой тип урона",
            RuleOutcome.TagMismatch => "не сработало: у типа урона нет нужного тега",
            RuleOutcome.ResistanceUnused => $"не сработало: у этого типа нет численного R, порог {N(check.Threshold ?? 0)} % не проверяется",
            RuleOutcome.ResistanceBelow => $"не сработало: R после пробития = {N(check.Value ?? 0)} %, порог {N(check.Threshold ?? 0)} % не достигнут",
            RuleOutcome.HealthNotBelow => $"не сработало: здоровье {N(check.Value ?? 0)} % не ниже порога {N(check.Threshold ?? 0)} %",
            RuleOutcome.NotReady => "не сработало: перезарядка или исчерпан лимит срабатываний",
            RuleOutcome.SkippedByImmunity => "не сработало: юнит невосприимчив, а правило не действует при иммунитете",
            _ => "условия выполнены, но делать нечего (сила удара уже израсходована, предел выше урона или потери не было)",
        };
        return $"«{name}» ({owner}): {why}.";
    }

    private static string TypeName(IReadOnlyDictionary<string, DamageTypeDefinition> types, string id) =>
        types.TryGetValue(id, out var t) ? t.Name.ToLowerInvariant() : id;

    internal static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    internal static string N3(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>
    /// <paramref name="hits"/> hits by the rolls (keys <c>sandbox:0…</c>, forced outcomes ignored) with the same drafts as
    /// <see cref="Run"/>: the same seed always gives the same series; nothing is saved.
    /// </summary>
    public static SandboxBatch Batch(ContentLibrary library, SandboxSetup setup, int hits = 100, ContentItem? ruleDraft = null, ContentItem? unitDraft = null,
        ContentItem? typeDraft = null, ContentItem? rulesetDraft = null, ContentItem? attackerDraft = null) =>
        Batch(setup, s => Run(library, s, ruleDraft, unitDraft, typeDraft, rulesetDraft, attackerDraft), hits);

    /// <summary>The same series through an editor's own sandbox call (it carries the editor's unsaved drafts).</summary>
    public static SandboxBatch Batch(SandboxSetup setup, Func<SandboxSetup, SandboxReport> runOne, int hits = 100)
    {
        hits = Math.Clamp(hits, 1, 1000);
        var losses = new List<double>(hits);
        int surprised = 0, crits = 0, supers = 0;
        for (var i = 0; i < hits; i++)
        {
            var run = runOne(setup with { RollKey = $"sandbox:{i}", ForceSurprise = null, ForceCrit = null }).With;
            losses.Add(run.TargetBefore - run.TargetAfter);
            surprised += run.Result.Surprised ? 1 : 0;
            crits += run.Result.Crit == CritTier.Crit ? 1 : 0;
            supers += run.Result.Crit == CritTier.SuperCrit ? 1 : 0;
        }

        return new SandboxBatch(hits, setup.Seed, surprised, crits, supers, losses.Min(), losses.Average(), losses.Max());
    }

    /// <summary>«100 событий (seed 20261106): внезапных 74, критов 50, сверхкритов 25; потеря HP цели: мин 10, сред 18.4, макс 33.75.»</summary>
    public static string Describe(SandboxBatch b) =>
        $"{b.Hits} событий (seed {b.Seed.ToString(CultureInfo.InvariantCulture)}): внезапных {b.Surprised}, критов {b.Crits}, сверхкритов {b.SuperCrits}; " +
        $"потеря HP цели: мин {N(b.MinLoss)}, сред {N(b.AverageLoss)}, макс {N(b.MaxLoss)}.";

    /// <summary>The whole report as text (the developer panel and tests).</summary>
    public static string Describe(SandboxReport report)
    {
        var sb = new StringBuilder();
        foreach (var p in report.Problems)
        {
            sb.AppendLine("⚠ " + p);
        }

        foreach (var s in report.With.Stages)
        {
            sb.AppendLine(s);
        }

        foreach (var r in report.With.Reasons)
        {
            sb.AppendLine(r);
        }

        if (report.Comparison.Length > 0)
        {
            sb.AppendLine(report.Comparison);
        }

        return sb.ToString().TrimEnd();
    }
}
