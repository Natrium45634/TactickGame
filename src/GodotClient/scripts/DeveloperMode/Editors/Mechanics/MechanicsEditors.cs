using System.Globalization;
using System.Linq;
using Godot;
using HexTactics.Client.DeveloperMode.SharedInspector;
using HexTactics.Client.Presentation;
using HexTactics.Client.UI;
using HexTactics.Content.Kinds;
using HexTactics.Content.Library;
using HexTactics.Content.Registry;
using HexTactics.Core.Hex;
using HexTactics.Core.Map;
using HexTactics.Editor.Mechanics;
using HexTactics.Editor.Units;

namespace HexTactics.Client.DeveloperMode.Editors.Mechanics;

/// <summary>A small board of seven hexes; the centre shows a colour (the damage type) or nothing.</summary>
public abstract partial class SmallBoardEditor : DefinitionEditorShell
{
    protected static readonly HexCoord Center = HexCoord.FromOffset(2, 2);

    private BoardView _board = null!;
    private HexGrid _grid = null!;

    protected HexFillLayer Fills { get; private set; } = null!;

    protected override void BuildCanvas()
    {
        TilePalette.Current = new TilePalette();
        _grid = new HexGrid(5, 5);
        foreach (var c in _grid.Coords)
        {
            _grid.SetTile(c, c.DistanceTo(Center) > 1 ? TileType.Void : TileType.Walkable, null, 0);
        }

        _board = new BoardView { Name = "SmallBoard" };
        AddChild(_board);
        _board.Build(_grid, Layout);
        Fills = new HexFillLayer { Name = "Fills", RenderPriority = 2, ExtraLift = 0.002f };
        AddChild(Fills);
        Fills.Build(_grid, Layout);
    }

    protected override Aabb CanvasBounds()
    {
        var c = Layout.HexToPlane(Center);
        var span = Layout.Size * 1.8 * 2.2;
        return new Aabb(new Vector3((float)(c.X - span), -VisualConfig.TileThickness, (float)(c.Z - span)), new Vector3((float)(2 * span), 1.0f, (float)(2 * span)));
    }
}

/// <summary>
/// The damage type constructor (spec part 5 §6.2): the short path — name, colour, icon — and «Подробнее» with ID, family,
/// strategy (only for a family; a subtype shows its family's), tags and the users of the type; «Проверить на ударе» hits a
/// chosen unit with this (unsaved) type.
/// </summary>
public partial class DamageTypeEditor : SmallBoardEditor
{
    private DamageTypeEditorModel _model = null!;
    private SandboxPanel? _sandbox;
    private bool _more;

    public override string Kind => ContentTypeRegistry.DamageType;

    protected override string IdPrefix => "dtype.";

    protected override string KindTitle => "Тип урона";

    public DamageTypeEditorModel TypeModel => _model;

    public void Configure(ContentLibrary library, ContentItem item, ContentItem? draft = null)
    {
        _model = new DamageTypeEditorModel(library, item);
        if (draft is not null)
        {
            _model.LoadDraft(draft);
        }

        Attach(library, _model);
    }

    protected override void UpdateCanvas()
    {
        if (Fills is null)
        {
            return;
        }

        Fills.ClearAll();
        var color = FieldSchemaColor(_model.Type.Color);
        Fills.SetFill(Center, new Color(color, 0.85f));
        foreach (var d in HexDirections.All)
        {
            Fills.SetFill(Center.Neighbor(d), new Color(color, 0.25f));
        }
    }

    private static Color FieldSchemaColor(string html) => HexTactics.Content.Schema.FieldSchema.IsColor(html) ? Color.FromHtml(html) : new Color(0.7f, 0.7f, 0.7f);

    private void Refuse(string? refusal)
    {
        if (refusal is not null)
        {
            Message("✗ " + refusal, HexTactics.Content.Model.IssueSeverity.Error);
            Stamp++;
        }
    }

    protected override void BuildProperties(Inspector inspector)
    {
        var type = _model.Type;
        var family = type.Family.Length == 0 ? null : Library.Find(ContentTypeRegistry.DamageType, type.Family);
        inspector.Section("Тип урона");
        inspector.ReadOnly("Основа", family is null ? "самостоятельное семейство" : $"подтип «{family.Name}»");
        inspector.ColorField(IdPrefix + "color", "Цвет", type.Color, v => Refuse(_model.SetColor(v)));
        inspector.LineText(IdPrefix + "icon", "Значок (до 4 символов)", type.Icon, v => Refuse(_model.SetIcon(v)));
        inspector.Check(IdPrefix + "element", "Категория: элемент", type.Classification.Contains(DamageTypeData.Element), on => _model.SetElement(on),
            "Пользовательский элемент: группируется и помечается в списках. Отношения элементов не задаются — расчёт как у семейства.");
        inspector.Button(IdPrefix + "more", _more ? "Подробнее ▴" : "Подробнее ▾", () => { _more = !_more; Stamp++; }, ButtonKind.Flat, "ID, семейство, стратегия расчёта, теги");
        if (!_more)
        {
            return;
        }

        inspector.ReadOnly("ID", _model.Item.Id);
        var families = _model.Families().Select(f => new ChoiceOption(f.Value, f.Label)).ToList();
        inspector.Choice(IdPrefix + "family", "Семейство", families, type.Family, v => Refuse(_model.SetFamily(v)), "Подтип считается со слоями «все типы + семейство + подтип».");
        if (type.Family.Length == 0)
        {
            inspector.Choice(IdPrefix + "strategy", "Стратегия расчёта", DamageTypeData.Strategies.Select(s => new ChoiceOption(s.Value, s.Label)).ToList(), type.Strategy,
                v => Refuse(_model.SetStrategy(v)), "Выбор всегда явный: название типа на расчёт не влияет.");
        }
        else
        {
            inspector.ReadOnly("Стратегия", DamageTypeData.Strategies.First(s => s.Value == type.Strategy).Label + " — как у семейства");
        }

        inspector.LineText(IdPrefix + "tags", "Теги (через запятую)", string.Join(", ", type.Tags), v => Refuse(_model.SetTags(v)), "Правила защиты могут фильтровать удары по тегу.");
    }

    protected override void BuildPreview(Inspector preview)
    {
        preview.Section("Как работает тип");
        foreach (var line in _model.Preview())
        {
            var label = preview.Text(line, size: 12);
            if (line.StartsWith('⚠'))
            {
                label.AddThemeColorOverride("font_color", UiKit.Warning);
            }
        }

        _sandbox ??= new SandboxPanel(IdPrefix, new SandboxSetup { DamageTypeId = _model.Item.Id, EnemiesAround = 1 });
        _sandbox.Build(preview, Library, setup => HitSandbox.Run(Library, setup with { DamageTypeId = _model.Item.Id }, typeDraft: _model.Item), () => Stamp++);
    }

    protected override SandboxPanel? Sandbox => _sandbox;

    protected override bool Expect(string[] a, out string actual, out bool ok)
    {
        switch (a[0])
        {
            case "dtype":
                // expect:dtype:<family|strategy|tags>:<value>
                actual = a[1] switch
                {
                    "family" => _model.Type.Family,
                    "strategy" => _model.Type.Strategy,
                    "tags" => string.Join(",", _model.Type.Tags),
                    _ => "unknown",
                };
                ok = actual == a[2];
                return true;
            case "dtype-preview":
                actual = string.Join(" | ", _model.Preview());
                ok = actual.Contains(string.Join(":", a.Skip(1)), System.StringComparison.Ordinal);
                return true;
            default:
                actual = "unknown";
                ok = false;
                return false;
        }
    }
}

/// <summary>
/// The resistance set editor (spec part 5 §6.1, §6.4): an optional base set, the table of values (own or inherited, with
/// «↺» to drop an own value), strengths and weaknesses in words and the users of the set.
/// </summary>
public partial class ResistanceProfileEditor : SmallBoardEditor
{
    private ResistanceProfileEditorModel _model = null!;

    public override string Kind => ContentTypeRegistry.ResistanceProfile;

    protected override string IdPrefix => "profile.";

    protected override string KindTitle => "Профиль защиты и сопротивлений";

    public ResistanceProfileEditorModel ProfileModel => _model;

    public void Configure(ContentLibrary library, ContentItem item, ContentItem? draft = null)
    {
        _model = new ResistanceProfileEditorModel(library, item);
        if (draft is not null)
        {
            _model.LoadDraft(draft);
        }

        Attach(library, _model);
    }

    /// <summary>The centre: green for an overall strength, red for an overall weakness.</summary>
    protected override void UpdateCanvas()
    {
        if (Fills is null)
        {
            return;
        }

        Fills.ClearAll();
        var g = _model.Layered.TryGetValue(UnitData.GlobalResistance, out var v) ? v.Value : 0;
        var sum = g + _model.Layered.Where(x => x.Key != UnitData.GlobalResistance).Sum(x => x.Value.Value);
        Fills.SetFill(Center, sum > 0 ? new Color(0.45f, 0.75f, 0.5f, 0.7f) : sum < 0 ? new Color(0.9f, 0.4f, 0.35f, 0.7f) : new Color(0.7f, 0.7f, 0.7f, 0.4f));
    }

    protected override void BuildProperties(Inspector inspector)
    {
        inspector.Section("Основа");
        inspector.Choice(IdPrefix + "parent", "На основе набора", _model.Parents().Select(p => new ChoiceOption(p.Value, p.Label)).ToList(), _model.Profile.Parent, v =>
        {
            if (_model.SetParent(v) is { } refusal)
            {
                Message("✗ " + refusal, HexTactics.Content.Model.IssueSeverity.Error);
                Stamp++;
            }
        }, "Свои значения набора перекрывают значения основы; циклы запрещены.");

        inspector.Section("Значения: R, % и защита A");
        inspector.Hint("R −500…+500 %, A — очки 0…1 000 000; итог для типа = все типы + семейство + подтип. Это данные, а не отдельная механика: юнит применяет профиль ссылкой.");
        foreach (var row in _model.Rows())
        {
            var label = (row.Depth > 0 ? "    " : string.Empty) + row.Label;
            if (row.Bypass)
            {
                inspector.ReadOnly(label, "обходит численные R и защиту A");
                continue;
            }

            var key = row.Key;
            var parts = new System.Collections.Generic.List<string>();
            if (row.Source is not null)
            {
                parts.Add("R " + (row.Inherited ? "из основы" : "своё") + (row.Value != 0 ? " · " + UnitEditorModel.Explain(row.Value) : string.Empty));
            }

            if (row.ArmorSource is not null)
            {
                parts.Add("A " + (row.ArmorInherited ? "из основы" : "своя") + (row.Armor != 0 ? " · " + UnitEditorModel.ExplainArmor(row.Armor) : string.Empty));
            }

            inspector.PairText($"{IdPrefix}res.{key}", "R, %", Inspector.Num(row.Value), v =>
            {
                if (_model.SetValue(key, v) is { } issue)
                {
                    Message("✗ " + issue.Message, HexTactics.Content.Model.IssueSeverity.Error);
                    Stamp++;
                }
            }, $"{IdPrefix}armor.{key}", "A", Inspector.Num(row.Armor), v =>
            {
                if (_model.SetArmor(key, v) is { } issue)
                {
                    Message("✗ " + issue.Message, HexTactics.Content.Model.IssueSeverity.Error);
                    Stamp++;
                }
            }, label, parts.Count == 0 ? null : string.Join("; ", parts), "Защита A в очках: урон после R × K / (K + A). Не процент.");
            if (row.Own)
            {
                inspector.Button($"{IdPrefix}res.{key}.reset", "↺ Убрать своё R", () => _model.Reset(key), ButtonKind.Flat);
            }

            if (row.ArmorOwn)
            {
                inspector.Button($"{IdPrefix}armor.{key}.reset", "↺ Убрать свою A", () => _model.ResetArmor(key), ButtonKind.Flat);
            }
        }
    }

    protected override void BuildPreview(Inspector preview)
    {
        preview.Section("Набор словами");
        foreach (var line in _model.Preview())
        {
            preview.Text(line, size: 12);
        }
    }

    protected override bool Expect(string[] a, out string actual, out bool ok)
    {
        switch (a[0])
        {
            case "profile-total":
                // expect:profile-total:<type id>:<value>
                actual = _model.Total(a[1]).ToString("0.##", CultureInfo.InvariantCulture);
                ok = actual == a[2];
                return true;
            case "profile-armor":
                // expect:profile-armor:<type id>:<A_raw>
                actual = _model.ArmorTotal(a[1]).ToString("0.##", CultureInfo.InvariantCulture);
                ok = actual == a[2];
                return true;
            case "profile-preview":
                actual = string.Join(" | ", _model.Preview());
                ok = actual.Contains(string.Join(":", a.Skip(1)), System.StringComparison.Ordinal);
                return true;
            default:
                actual = "unknown";
                ok = false;
                return false;
        }
    }
}

/// <summary>
/// The combat rules constructor (spec part 6 §3.2, §9.5): K of the defense formula, optional K per family, a table of what A
/// means and «Проверить на ударе» with these (unsaved) rules. Direction and critical hits join this editor in later versions.
/// </summary>
public partial class CombatRulesetEditor : SmallBoardEditor
{
    private CombatRulesetEditorModel _model = null!;
    private SandboxPanel? _sandbox;
    private string? _pickFamily;

    public override string Kind => ContentTypeRegistry.CombatRuleset;

    protected override string IdPrefix => "rules.";

    protected override string KindTitle => "Правила боя";

    public CombatRulesetEditorModel RulesetModel => _model;

    public void Configure(ContentLibrary library, ContentItem item, ContentItem? draft = null)
    {
        _model = new CombatRulesetEditorModel(library, item);
        if (draft is not null)
        {
            _model.LoadDraft(draft);
        }

        Attach(library, _model);
    }

    /// <summary>The centre shows how strong A = 100 is with this K (darker — stronger defense).</summary>
    protected override void UpdateCanvas()
    {
        if (Fills is null)
        {
            return;
        }

        Fills.ClearAll();
        var m = (float)HexTactics.Core.Combat.ArmorMath.Multiplier(100, _model.Ruleset.DefenseK);
        Fills.SetFill(Center, new Color(0.35f + (0.5f * m), 0.45f + (0.4f * m), 0.75f, 0.8f));
    }

    private void Refuse(HexTactics.Content.Model.ContentIssue? issue)
    {
        if (issue is not null)
        {
            Message("✗ " + issue.Message, HexTactics.Content.Model.IssueSeverity.Error);
            Stamp++;
        }
    }

    protected override void BuildProperties(Inspector inspector)
    {
        var rules = _model.Ruleset;
        inspector.Section("Защита A");
        inspector.LineText(IdPrefix + "k", "K защиты", Inspector.Num(rules.DefenseK), v => Refuse(_model.SetDefenseK(v)),
            "Урон после сопротивления × K / (K + A): при A = K урон вдвое меньше. Больше 0 и не больше 1 000 000.");
        inspector.Section("Свой K у семейства");
        inspector.Hint("Необязательно: типы этого семейства (и его подтипы) считаются со своим K, остальные — с общим.");
        foreach (var (family, k) in rules.DefenseKByFamily)
        {
            var id = family;
            var name = Library.Find(ContentTypeRegistry.DamageType, id)?.Name ?? id;
            inspector.LineText($"{IdPrefix}k.{id}", $"K — {name}", Inspector.Num(k), v => Refuse(_model.SetFamilyK(id, v)));
            inspector.Button($"{IdPrefix}k.{id}.remove", "✕ Общий K для семейства", () => _model.RemoveFamilyK(id), ButtonKind.Flat).Alignment = HorizontalAlignment.Left;
        }

        var families = _model.FamilyChoices();
        if (families.Count > 0)
        {
            _pickFamily = families.Any(f => f.Value == _pickFamily) ? _pickFamily : families[0].Value;
            inspector.Choice(IdPrefix + "k.pick", "Семейство", families.Select(f => new ChoiceOption(f.Value, f.Label)).ToList(), _pickFamily!, v => _pickFamily = v);
            inspector.Button(IdPrefix + "k.add", "＋ Свой K для семейства", () =>
            {
                if (_pickFamily is not null)
                {
                    Refuse(_model.SetFamilyK(_pickFamily, rules.DefenseK));
                }
            });
        }
    }

    protected override void BuildPreview(Inspector preview)
    {
        preview.Section("Как работают правила");
        foreach (var line in _model.Preview())
        {
            preview.Text(line, size: 12);
        }

        _sandbox ??= new SandboxPanel(IdPrefix, new SandboxSetup { RulesetId = _model.Item.Id, EnemiesAround = 1 }, fixedRuleset: true);
        _sandbox.Build(preview, Library, setup => HitSandbox.Run(Library, setup with { RulesetId = _model.Item.Id }, rulesetDraft: _model.Item), () => Stamp++);
    }

    protected override SandboxPanel? Sandbox => _sandbox;

    protected override bool Expect(string[] a, out string actual, out bool ok)
    {
        switch (a[0])
        {
            case "ruleset-k":
                // expect:ruleset-k:<K> or expect:ruleset-k:<family id>:<K>
                actual = (a.Length > 2 ? (_model.Ruleset.DefenseKByFamily.TryGetValue(a[1], out var fk) ? fk : 0) : _model.Ruleset.DefenseK).ToString("0.##", CultureInfo.InvariantCulture);
                ok = actual == a[^1];
                return true;
            case "ruleset-preview":
                actual = string.Join(" | ", _model.Preview());
                ok = actual.Contains(string.Join(":", a.Skip(1)), System.StringComparison.Ordinal);
                return true;
            default:
                actual = "unknown";
                ok = false;
                return false;
        }
    }
}
