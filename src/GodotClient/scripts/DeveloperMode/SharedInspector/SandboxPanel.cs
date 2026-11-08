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

    public SandboxPanel(string prefix, SandboxSetup initial, bool fixedTarget = false)
    {
        _prefix = prefix;
        _setup = initial;
        FixedTarget = fixedTarget;
    }

    /// <summary>The target is the edited unit itself (the unit editor) — no target picker.</summary>
    public bool FixedTarget { get; }

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

        var types = HexTactics.Editor.Library.ReferenceCatalog.Options(library, ContentTypeRegistry.DamageType, HexTactics.Content.Schema.ReferenceFilter.Any, _setup.DamageTypeId)
            .Select(o => new ChoiceOption(o.Value, o.Label.Replace(" (свой)", string.Empty, StringComparison.Ordinal), null, o.Group)).ToList();
        if (types.All(t => t.Value != _setup.DamageTypeId))
        {
            types.Insert(0, new ChoiceOption(_setup.DamageTypeId, "редактируемый тип"));
        }

        panel.Choice(_prefix + "sandbox.type", "Тип урона", types, _setup.DamageTypeId, v => _setup = _setup with { DamageTypeId = v });
        panel.LineText(_prefix + "sandbox.amount", "Сила удара D0", Inspector.Num(_setup.Amount), v => _setup = _setup with { Amount = Parse(v, _setup.Amount, 0, 100000) });
        panel.LineText(_prefix + "sandbox.penetration", "Пробитие, п.п.", Inspector.Num(_setup.Penetration), v => _setup = _setup with { Penetration = Parse(v, _setup.Penetration, 0, 1000) });
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

    private static double Parse(string text, double fallback, double min, double max) =>
        double.TryParse(text.Trim().Replace(',', '.').TrimEnd('%').Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v)
            ? Math.Clamp(v, min, max)
            : fallback;
}
