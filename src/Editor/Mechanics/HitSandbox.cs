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

    /// <summary>Health of the target before the hit, % of its maximum.</summary>
    public double HealthPercent { get; init; } = 100;

    /// <summary>Enemies of the target on the six adjacent hexes (0–6), the attacker included when it is adjacent.</summary>
    public int EnemiesAround { get; init; } = 2;

    public int AlliesAround { get; init; }

    /// <summary>The attacker stands next to the target (otherwise three hexes away).</summary>
    public bool AttackerAdjacent { get; init; } = true;
}

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
    public static SandboxReport Run(ContentLibrary library, SandboxSetup setup, ContentItem? ruleDraft = null, ContentItem? unitDraft = null, ContentItem? typeDraft = null)
    {
        var problems = new List<string>();
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
        var with = Hit(library, setup, stats with { DefenseRules = edited is null ? others : others.Append(edited).ToList() }, types, names);
        SandboxRun? without = null;
        var comparison = string.Empty;
        if (edited is not null)
        {
            without = Hit(library, setup, stats with { DefenseRules = others }, types, names);
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

    private static SandboxRun Hit(ContentLibrary library, SandboxSetup setup, UnitStats targetStats, IReadOnlyDictionary<string, DamageTypeDefinition> types, IReadOnlyDictionary<string, string> ruleNames)
    {
        var grid = new HexGrid(7, 7);
        foreach (var c in grid.Coords)
        {
            grid.SetTile(c, TileType.Walkable, null, 0);
        }

        var sim = new GameSimulation(new MapDefinition("sandbox", grid, Array.Empty<SpawnZoneDefinition>(), null), new SimulationSettings { DamageTypes = types });
        var noSkills = Array.Empty<string>();
        UnitId Spawn(HexCoord cell, Side side, UnitStats stats, string name) =>
            sim.Spawn(SpawnRequest.AtCell(cell, side, HexDirection.North) with { Profile = new UnitProfile("sandbox." + name, name, stats, noSkills) }).Unit!.Value;

        var target = Spawn(Center, Side.Player, targetStats, "цель");
        var ring = HexDirections.All.Select(d => Center.Neighbor(d)).ToList();
        var labels = new Dictionary<UnitId, string>();
        var enemies = Math.Clamp(setup.EnemiesAround, 0, 6);
        var allies = Math.Clamp(setup.AlliesAround, 0, 6 - enemies);
        var slot = 0;
        UnitId attacker;
        if (setup.AttackerAdjacent && enemies > 0)
        {
            attacker = Spawn(ring[slot++], Side.Enemy, new UnitStats(), "атакующий");
            enemies--;
        }
        else
        {
            attacker = Spawn(HexCoord.FromOffset(3, 0), Side.Enemy, new UnitStats(), "атакующий");
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
        var result = sim.Damage.Resolve(sim.Damage.Create(attacker, target, typeId, setup.Amount, DamageOrigin.Skill, setup.Penetration), sim.Tick);
        var reactions = sim.Damage.Results.Where(r => r.Event.Reactive).ToList();
        var others = labels.Select(x => (x.Value, othersBefore[x.Key], sim.State.FindUnit(x.Key)?.CurrentHealth ?? 0)).ToList();

        var type = types[typeId];
        var stages = new List<string>();
        void Stage(string text) => stages.Add($"{stages.Count + 1}. {text}");
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
            : $"Сопротивление не учитывается: тип обходит численные R → {N(result.AfterResistance)}.");
        var taken = result.ShieldLoss + result.HpLoss;
        if (!result.Immune && taken < result.AfterResistance - 1e-9 && t.IsAlive)
        {
            Stage($"Плоское снижение и предел: {N(result.AfterResistance)} → {N(taken)}.");
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
