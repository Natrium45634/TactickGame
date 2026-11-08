using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HexTactics.Client.UI;
using HexTactics.Content.Kinds;
using HexTactics.Content.Library;
using HexTactics.Content.Registry;
using HexTactics.Editor.Mechanics;

namespace HexTactics.Client.DeveloperMode.SharedInspector;

/// <summary>
/// «Проверить на ударе» (spec part 5 §6.5) inside an editor's preview column: target, damage type, D0, penetration, health,
/// enemies and allies around; «Проверить» runs <see cref="HitSandbox"/> on unsaved data and shows the stages, the reasons of
/// the rules and «с правилом / без правила». The library, a match and the files never change.
/// </summary>
public sealed class SandboxPanel
{
    private readonly string _prefix;
    private SandboxSetup _setup;
    private SandboxReport? _report;
    private bool _open;

    public SandboxPanel(string prefix, SandboxSetup initial, bool fixedTarget = false, bool fixedRuleset = false)
    {
        _prefix = prefix;
        _setup = initial;
        FixedTarget = fixedTarget;
        FixedRuleset = fixedRuleset;
    }

    /// <summary>The target is the edited unit itself (the unit editor) — no target picker.</summary>
    public bool FixedTarget { get; }

    /// <summary>The rules are the edited combat ruleset (its own editor) — no ruleset picker.</summary>
    public bool FixedRuleset { get; }

    /// <summary>Attacker value «the edited unit hits an empty target» of a fixed-target panel (the unit editor).</summary>
    public const string SelfAttacker = "__self__";

    public SandboxReport? Report => _report;

    public string Text => _report is null ? string.Empty : HitSandbox.Describe(_report);

    /// <summary>Builds the panel; <paramref name="run"/> runs the sandbox with the editor's unsaved objects.</summary>
    public void Build(Inspector panel, ContentLibrary library, Func<SandboxSetup, SandboxReport> run, Action rebuild)
    {
        panel.Section("Проверить на ударе");
        panel.Button(_prefix + "sandbox", _open ? "Проверить на ударе ▴" : "Проверить на ударе ▾", () => { _open = !_open; rebuild(); }, ButtonKind.Flat,
            "Один удар в отдельной песочнице: стадии расчёта с точными числами, причины срабатывания правил");
        if (!_open)
        {
            return;
        }

        // The report first (right under the button), then the inputs: the numbers stay in view after «▶ Проверить».
        if (_report is not null)
        {
            _report = run(_setup); // live: the edited (unsaved) values are checked again after every change
            foreach (var p in _report.Problems)
            {
                panel.Text("⚠ " + p, size: 12).AddThemeColorOverride("font_color", UiKit.Warning);
            }

            foreach (var line in _report.With.Stages)
            {
                panel.Text(line, size: 12);
            }

            foreach (var line in _report.With.Reasons)
            {
                panel.Text(line, dim: true, size: 12);
            }

            if (_report.Comparison.Length > 0)
            {
                panel.Text(_report.Comparison, size: 12);
            }
        }

        if (!FixedTarget)
        {
            var targets = new List<ChoiceOption> { new(string.Empty, "пустая цель (100 HP, без защиты)") };
            targets.AddRange(library.Items(ContentTypeRegistry.Unit).Where(u => !u.HasErrors).Select(u => new ChoiceOption(u.Id, u.Name)));
            panel.Choice(_prefix + "sandbox.target", "Цель", targets, _setup.TargetUnitId ?? string.Empty, v => _setup = _setup with { TargetUnitId = v.Length == 0 ? null : v });
        }

        // The attacker (spec part 6 §11 p.1): its outgoing passives change the strength before D0.
        var attackers = new List<ChoiceOption> { new(string.Empty, "обычный атакующий (без пассивов)") };
        if (FixedTarget)
        {
            attackers.Add(new ChoiceOption(SelfAttacker, "этот юнит бьёт пустую цель"));
        }

        attackers.AddRange(library.Items(ContentTypeRegistry.Unit).Where(u => !u.HasErrors).Select(u => new ChoiceOption(u.Id, u.Name)));
        panel.Choice(_prefix + "sandbox.attacker", "Атакующий", attackers, _setup.AttackerUnitId ?? string.Empty, v => _setup = _setup with { AttackerUnitId = v.Length == 0 ? null : v });

        var types = HexTactics.Editor.Library.ReferenceCatalog.Options(library, ContentTypeRegistry.DamageType, HexTactics.Content.Schema.ReferenceFilter.Any, _setup.DamageTypeId)
            .Select(o => new ChoiceOption(o.Value, o.Label.Replace(" (свой)", string.Empty, StringComparison.Ordinal), null, o.Group)).ToList();
        if (types.All(t => t.Value != _setup.DamageTypeId))
        {
            types.Insert(0, new ChoiceOption(_setup.DamageTypeId, "редактируемый тип"));
        }

        panel.Choice(_prefix + "sandbox.type", "Тип урона", types, _setup.DamageTypeId, v => _setup = _setup with { DamageTypeId = v });
        panel.LineText(_prefix + "sandbox.amount", "Сила удара", Inspector.Num(_setup.Amount), v => _setup = _setup with { Amount = Parse(v, _setup.Amount, 0, 100000) },
            "D0 = эта сила × пассивы атакующего (исходящий урон); без пассивов D0 равно ей.");
        panel.LineText(_prefix + "sandbox.penetration", "Пробитие R, п.п.", Inspector.Num(_setup.Penetration), v => _setup = _setup with { Penetration = Parse(v, _setup.Penetration, 0, 1000) });
        panel.PairText(_prefix + "sandbox.armorpen", "очки", Inspector.Num(_setup.ArmorPenetration), v => _setup = _setup with { ArmorPenetration = Parse(v, _setup.ArmorPenetration, 0, 1_000_000) },
            _prefix + "sandbox.armorpenpct", "%", Inspector.Num(_setup.ArmorPenetrationPercent), v => _setup = _setup with { ArmorPenetrationPercent = Parse(v, _setup.ArmorPenetrationPercent, 0, 100) },
            "Пробитие защиты A", "Сначала вычитаются очки, затем не учитывается доля оставшейся A; R не меняется.");
        if (!FixedRuleset)
        {
            var rulesets = library.Items(ContentTypeRegistry.CombatRuleset).Where(r => !r.HasErrors).Select(r => new ChoiceOption(r.Id, r.Name)).ToList();
            panel.Choice(_prefix + "sandbox.ruleset", "Правила боя (K)", rulesets, _setup.RulesetId, v => _setup = _setup with { RulesetId = v });
        }
        // Spec part 6 §6, §11 p.3: the side of the target and a forced outcome for provable checks (never in a match).
        panel.Choice(_prefix + "sandbox.side", "Сторона цели (цель смотрит вперёд)", Enumerable.Range(0, 6)
            .Select(i => new ChoiceOption(i.ToString(CultureInfo.InvariantCulture), HexTactics.Core.Combat.SurpriseMath.SectorNames[i])).ToList(),
            _setup.Sector.ToString(CultureInfo.InvariantCulture), v => _setup = _setup with { Sector = int.Parse(v, CultureInfo.InvariantCulture) });
        panel.Choice(_prefix + "sandbox.outcome", "Исход (только для проверки)", new List<ChoiceOption>
        {
            new("roll", "по броску"), new("normal", "обычный, не внезапный"), new("surprise", "внезапный"), new("crit", "крит"), new("super", "сверхкрит"), new("surprise_super", "внезапный сверхкрит"),
        }, Outcome(), v => _setup = v switch
        {
            "normal" => _setup with { ForceSurprise = false, ForceCrit = HexTactics.Core.Combat.CritTier.Normal },
            "surprise" => _setup with { ForceSurprise = true, ForceCrit = HexTactics.Core.Combat.CritTier.Normal },
            "crit" => _setup with { ForceSurprise = false, ForceCrit = HexTactics.Core.Combat.CritTier.Crit },
            "super" => _setup with { ForceSurprise = false, ForceCrit = HexTactics.Core.Combat.CritTier.SuperCrit },
            "surprise_super" => _setup with { ForceSurprise = true, ForceCrit = HexTactics.Core.Combat.CritTier.SuperCrit },
            _ => _setup with { ForceSurprise = null, ForceCrit = null },
        });
        panel.LineText(_prefix + "sandbox.health", "Здоровье цели, %", Inspector.Num(_setup.HealthPercent), v => _setup = _setup with { HealthPercent = Parse(v, _setup.HealthPercent, 1, 100) });
        panel.LineText(_prefix + "sandbox.enemies", "Врагов цели вокруг (0–6)", _setup.EnemiesAround.ToString(CultureInfo.InvariantCulture), v => _setup = _setup with { EnemiesAround = (int)Parse(v, _setup.EnemiesAround, 0, 6) },
            "Атакующий стоит рядом и считается одним из них.");
        panel.LineText(_prefix + "sandbox.allies", "Союзников цели вокруг", _setup.AlliesAround.ToString(CultureInfo.InvariantCulture), v => _setup = _setup with { AlliesAround = (int)Parse(v, _setup.AlliesAround, 0, 6) });
        panel.Button(_prefix + "sandbox.run", "▶ Проверить", () =>
        {
            _report = run(_setup);
            rebuild();
        }, ButtonKind.Primary);
    }

    private string Outcome() => (_setup.ForceSurprise, _setup.ForceCrit) switch
    {
        (null, null) => "roll",
        (true, HexTactics.Core.Combat.CritTier.SuperCrit) => "surprise_super",
        (true, _) => "surprise",
        (_, HexTactics.Core.Combat.CritTier.SuperCrit) => "super",
        (_, HexTactics.Core.Combat.CritTier.Crit) => "crit",
        _ => "normal",
    };

    private static double Parse(string text, double fallback, double min, double max) =>
        double.TryParse(text.Trim().Replace(',', '.').TrimEnd('%').Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v)
            ? Math.Clamp(v, min, max)
            : fallback;
}
