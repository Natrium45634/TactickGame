using System.Globalization;
using System.Linq;
using Godot;
using HexTactics.Client.DeveloperMode.SharedInspector;
using HexTactics.Client.Presentation;
using HexTactics.Client.UI;
using HexTactics.Content.Kinds;
using HexTactics.Content.Library;
using HexTactics.Content.Registry;
using HexTactics.Content.Schema;
using HexTactics.Core.Hex;
using HexTactics.Core.Map;
using HexTactics.Editor.Defense;

namespace HexTactics.Client.DeveloperMode.Editors.Defense;

/// <summary>
/// The defense rule constructor (spec part 5 §6.3): «КОГДА → ОТ КАКОГО → ПРИ УСЛОВИИ → ЧТО СДЕЛАТЬ», templates, the actions
/// of the registry that fit the trigger, «Дополнительно ▾» for priority, limits and the response type; the rule in words and
/// a small board with the hexes a reaction reaches. Numbers stay in the developer mode.
/// </summary>
public partial class DefenseRuleEditor : DefinitionEditorShell
{
    private const int Radius = 3;
    private static readonly HexCoord Center = HexCoord.FromOffset(3, 3);
    private static readonly Color DefenderFill = new(0.35f, 0.6f, 1f, 0.55f);
    private static readonly Color ReachFill = new(0.95f, 0.45f, 0.25f, 0.45f);
    private static readonly Color AttackerFill = new(0.95f, 0.3f, 0.25f, 0.7f);

    private DefenseRuleEditorModel _model = null!;
    private BoardView _board = null!;
    private HexFillLayer _fills = null!;
    private HexGrid _grid = null!;
    private string _template = "complex";
    private string _newAction = string.Empty;
    private bool _advanced;
    private SandboxPanel? _sandbox;

    public override string Kind => ContentTypeRegistry.DefenseRule;

    protected override string IdPrefix => "rule.";

    protected override string KindTitle => "Правило защиты";

    public DefenseRuleEditorModel RuleModel => _model;

    public void Configure(ContentLibrary library, ContentItem item, ContentItem? draft = null)
    {
        _model = new DefenseRuleEditorModel(library, item);
        if (draft is not null)
        {
            _model.LoadDraft(draft);
        }

        Attach(library, _model);
    }

    // ------------------------------------------------------------------ canvas

    protected override void BuildCanvas()
    {
        TilePalette.Current = new TilePalette();
        _grid = new HexGrid(7, 7);
        foreach (var c in _grid.Coords)
        {
            _grid.SetTile(c, c.DistanceTo(Center) > Radius ? TileType.Void : TileType.Walkable, null, 0);
        }

        _board = new BoardView { Name = "RuleBoard" };
        AddChild(_board);
        _board.Build(_grid, Layout);
        _fills = new HexFillLayer { Name = "Reach", RenderPriority = 2, ExtraLift = 0.002f };
        AddChild(_fills);
        _fills.Build(_grid, Layout);
    }

    /// <summary>Blue — the defender; orange — hexes an area reaction reaches; red — the attacker (two hexes north).</summary>
    protected override void UpdateCanvas()
    {
        if (_fills is null)
        {
            return;
        }

        _fills.ClearAll();
        var attacker = Center.Neighbor(HexDirection.North).Neighbor(HexDirection.North);
        foreach (var a in _model.Rule.Actions)
        {
            if (a.Get("targeting") as string == DefenseActionRegistry.AreaAround)
            {
                var r = FieldSchema.TryNumber(a.Get("radiusHexes") ?? 1.0, out var radius) ? (int)System.Math.Round(radius) : 1;
                foreach (var c in _grid.Coords.Where(c => _grid.Exists(c) && c.DistanceTo(Center) <= r && (c != Center || a.Get("includeCenter") is true)))
                {
                    _fills.SetFill(c, ReachFill);
                }
            }
        }

        if (_model.Rule.Trigger is DefenseActionRegistry.OnHitContact or DefenseActionRegistry.OnHealthLoss or DefenseActionRegistry.OnDamageDealt)
        {
            _fills.SetFill(attacker, AttackerFill);
        }

        _fills.SetFill(Center, DefenderFill);
    }

    protected override Aabb CanvasBounds()
    {
        var c = Layout.HexToPlane(Center);
        var span = Layout.Size * 1.8 * (Radius + 0.8);
        return new Aabb(new Vector3((float)(c.X - span), -VisualConfig.TileThickness, (float)(c.Z - span)), new Vector3((float)(2 * span), 1.0f, (float)(2 * span)));
    }

    // ------------------------------------------------------------------ panels

    protected override void BuildProperties(Inspector inspector)
    {
        var rule = _model.Rule;
        inspector.Section("Шаблон");
        inspector.Choice(IdPrefix + "template", "Начать с шаблона", DefenseRuleTemplates.All.Select(t => new ChoiceOption(t.Id, t.Label)).ToList(), _template, v => { _template = v; Stamp++; },
            DefenseRuleTemplates.Find(_template)?.Hint);
        inspector.Button(IdPrefix + "template.apply", "Применить шаблон", () => _model.ApplyTemplate(_template), ButtonKind.Normal, "Заменяет условия и реакции правила (можно отменить)");

        inspector.Section("Когда и от чего");
        foreach (var field in DefenseRuleData.Fields.Where(f => !f.Advanced))
        {
            inspector.Field(IdPrefix, field, rule, ApplyField);
        }

        inspector.Section("Что сделать");
        for (var i = 0; i < rule.Actions.Count; i++)
        {
            var action = rule.Actions[i];
            var descriptor = _model.Actions.Find(action.Type);
            inspector.Text($"{i + 1}. {descriptor?.Label ?? "✗ " + action.Type}", size: 13);
            if (descriptor is null)
            {
                inspector.Button($"{IdPrefix}a{i}.remove", "✕ Убрать реакцию", () => _model.RemoveAction(rule.Actions.IndexOf(action)), ButtonKind.Flat);
                continue;
            }

            inspector.Hint(descriptor.Description);
            var index = i;
            foreach (var field in descriptor.Fields.Where(f => _advanced || !f.Advanced))
            {
                inspector.Field($"{IdPrefix}a{i}.", field, action, (f, v) => ApplyActionField(index, f.Key, v));
            }

            inspector.Button($"{IdPrefix}a{i}.remove", "✕ Убрать реакцию", () => _model.RemoveAction(index), ButtonKind.Flat);
        }

        var options = _model.AvailableActions.Select(a => new ChoiceOption(a.Effect, a.Label)).ToList();
        if (options.Count > 0)
        {
            _newAction = options.Any(o => o.Value == _newAction) ? _newAction : options[0].Value;
            inspector.Choice(IdPrefix + "addtype", "Новая реакция", options, _newAction, v => _newAction = v, "Список зависит от «когда».");
            inspector.Button(IdPrefix + "add", "＋ Добавить реакцию", () =>
            {
                if (_model.AddAction(_newAction) is { } refusal)
                {
                    Message("✗ " + refusal, HexTactics.Content.Model.IssueSeverity.Error);
                }
            });
        }

        inspector.Button(IdPrefix + "advanced", _advanced ? "Дополнительно ▴" : "Дополнительно ▾", () => { _advanced = !_advanced; Stamp++; }, ButtonKind.Flat,
            "Приоритет, перезарядка, лимит срабатываний, тег, тип ответного урона");
        if (_advanced)
        {
            foreach (var field in DefenseRuleData.Fields.Where(f => f.Advanced))
            {
                inspector.Field(IdPrefix, field, rule, ApplyField);
            }
        }
    }

    private void ApplyActionField(int index, string key, object? value)
    {
        if (_model.SetActionField(index, key, value) is { } issue)
        {
            Message("✗ " + issue.Message, HexTactics.Content.Model.IssueSeverity.Error);
            Stamp++;
        }
    }

    protected override void BuildPreview(Inspector preview)
    {
        preview.Section("Правило словами");
        preview.Text(_model.Summary(), size: 13);
        var total = _model.ConversionTotal();
        if (total > 0)
        {
            var label = preview.Text($"Поглощение и отражение: {Inspector.Num(total * 100)} % из 100 % силы удара.", size: 12);
            if (total > 1 + 1e-9)
            {
                label.AddThemeColorOverride("font_color", UiKit.Error);
            }
        }

        // «Проверить на ударе» (spec part 5 §6.5): the unsaved rule on the chosen target, compared with the same hit without it.
        _sandbox ??= new SandboxPanel(IdPrefix, new HexTactics.Editor.Mechanics.SandboxSetup
        {
            DamageTypeId = _model.Rule.DamageType is { Length: > 0 } t && t != DefenseRuleData.AnyType ? t : HexTactics.Content.Registry.EffectTypeRegistry.DefaultDamageType,
        });
        _sandbox.Build(preview, Library, setup => HexTactics.Editor.Mechanics.HitSandbox.Run(Library, setup, _model.Item), () => Stamp++);

        preview.Section("Как читать");
        preview.Hint("Порядок расчёта: иммунитет → поглощение/лечение/отражение из силы удара → сопротивление → плоское снижение и предел → щиты → HP → ответный удар и вампиризм. " +
                     "На поле: синий — защитник, красный — атакующий, оранжевый — куда уходит отражение по площади.");
        preview.Hint("В режиме «Играть» правила и числа не показываются; видны только «невосприимчив» и «щит».");
    }

    protected override SandboxPanel? Sandbox => _sandbox;

    // ------------------------------------------------------------------ automation

    protected override bool Expect(string[] a, out string actual, out bool ok)
    {
        switch (a[0])
        {
            case "rule-summary":
                // expect:rule-summary:text — the summary contains text
                actual = _model.Summary();
                ok = actual.Contains(string.Join(":", a.Skip(1)), System.StringComparison.Ordinal);
                return true;
            case "rule-actions":
                actual = string.Join(",", _model.Rule.Actions.Select(x => x.Type));
                ok = actual == a[1] || _model.Rule.Actions.Count.ToString(CultureInfo.InvariantCulture) == a[1];
                return true;
            case "rule-field":
                {
                    var field = DefenseRuleData.Fields.FirstOrDefault(f => f.Key == a[1]);
                    var value = field?.Get?.Invoke(_model.Rule);
                    actual = value switch
                    {
                        double d => d.ToString("0.###", CultureInfo.InvariantCulture),
                        bool b => b ? "true" : "false",
                        null => "none",
                        _ => value.ToString() ?? string.Empty,
                    };
                    ok = actual == a[2];
                    return true;
                }

            default:
                actual = "unknown";
                ok = false;
                return false;
        }
    }
}
