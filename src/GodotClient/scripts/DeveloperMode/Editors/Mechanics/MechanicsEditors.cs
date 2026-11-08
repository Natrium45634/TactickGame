using System.Collections.Generic;
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

        BuildDirectionAndCrits(inspector);
        inspector.Section("Свой K у семейства (дополнительно)");
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

    private bool _asymmetric;
    private int _side = -1;
    private double _testChance = 125;
    private CombatRulesetEditorModel.RollStats? _rolls;

    private SandboxReport RunSandbox(SandboxSetup setup) => HitSandbox.Run(Library, setup with { RulesetId = _model.Item.Id }, rulesetDraft: _model.Item);

    private SandboxPanel SandboxOf() => _sandbox ??= new SandboxPanel(IdPrefix, new SandboxSetup { RulesetId = _model.Item.Id, EnemiesAround = 1 }, fixedRuleset: true);

    /// <summary>«Направление» and «Криты» (spec part 6 §9.5): two small sections, no permanent panels.</summary>
    private void BuildDirectionAndCrits(Inspector inspector)
    {
        var r = _model.Ruleset;
        inspector.Section("Направление и внезапность");
        inspector.Check(IdPrefix + "surprise", "Внезапный удар по стороне цели", r.SurpriseEnabled, on => _model.SetSurpriseEnabled(on),
            "Шанс зависит от стороны цели относительно её логического взгляда; удача — множитель урона до защиты.");
        if (r.SurpriseEnabled)
        {
            // Spec part 6 §9.5: the six sides as a picture; a click selects a side to set or to test.
            var hexagon = inspector.Add(new SectorHexagon(IdPrefix));
            hexagon.Show(r.SurpriseChances, _side);
            hexagon.SectorPressed += sector => { _side = sector; Stamp++; };
            if (_side >= 0)
            {
                var side = _side;
                var mirror = !_asymmetric && side is not (0 or 3);
                inspector.LineText(IdPrefix + "side.value", $"{HexTactics.Core.Combat.SurpriseMath.SectorNames[side]}{(mirror ? " (и зеркально)" : string.Empty)}, %",
                    Inspector.Num(r.SurpriseChances.Length == 6 ? r.SurpriseChances[side] : 0), v => Refuse(_model.SetSurpriseChance(side, v, !_asymmetric)));
                inspector.Button(IdPrefix + "side.test", "▶ Тест атаки с этой стороны", () => { SandboxOf().TestFromSide(side, RunSandbox); Stamp++; }, ButtonKind.Normal,
                    "Открывает «Проверить на ударе» справа: атакующий стоит с выбранной стороны цели");
            }

            inspector.Check(IdPrefix + "surprise.melee", "Только удар с соседнего гекса", r.SurpriseMeleeOnly, on => _model.SetSurpriseMeleeOnly(on),
                "Бонус только для ближнего боя: дальняя атака и навык издалека внезапными не бывают.");
            inspector.Check(IdPrefix + "asymmetric", "Разные левый и правый фланги", _asymmetric, on => { _asymmetric = on; Stamp++; });
            var sectors = _asymmetric ? new[] { 0, 1, 2, 3, 4, 5 } : new[] { 0, 1, 2, 3 };
            foreach (var sector in sectors)
            {
                var index = sector;
                var label = _asymmetric ? HexTactics.Core.Combat.SurpriseMath.SectorNames[sector] : sector switch { 1 => "передние фланги", 2 => "задние фланги", _ => HexTactics.Core.Combat.SurpriseMath.SectorNames[sector] };
                inspector.LineText($"{IdPrefix}surprise.{sector}", $"{label}, %", Inspector.Num(r.SurpriseChances.Length == 6 ? r.SurpriseChances[sector] : 0),
                    v => Refuse(_model.SetSurpriseChance(index, v, !_asymmetric)));
            }

            inspector.LineText(IdPrefix + "surprise.multiplier", "Множитель внезапного удара", Inspector.Num(r.SurpriseMultiplier), v => Refuse(_model.SetValue("surpriseMultiplier", v)));
        }

        inspector.Section("Критические удары");
        inspector.Check(IdPrefix + "crit", "Криты и сверхкриты", r.CritEnabled, on => _model.SetCritEnabled(on),
            "Шанс атакующего в п.п.: до 100 — шанс крита, 100 — крит всегда, сверх 100 — шанс сверхкрита (125 → 25 %), 200 — сверхкрит всегда.");
        if (r.CritEnabled)
        {
            inspector.LineText(IdPrefix + "crit.base", "Базовый шанс всех атакующих, п.п.", Inspector.Num(r.BaseCritChance), v => Refuse(_model.SetValue("baseCritChance", v)));
            inspector.LineText(IdPrefix + "crit.multiplier", "Множитель крита", Inspector.Num(r.CritMultiplier), v => Refuse(_model.SetValue("critMultiplier", v)));
            inspector.LineText(IdPrefix + "crit.super", "Множитель сверхкрита", Inspector.Num(r.SuperCritMultiplier), v => Refuse(_model.SetValue("superCritMultiplier", v)));
            // Spec part 6 §9.5: a visible series of 1000 deterministic rolls next to the exact table.
            inspector.LineText(IdPrefix + "crit.test", "Шанс для проверки, п.п.", Inspector.Num(_testChance), v =>
            {
                if (double.TryParse(v.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var c) && double.IsFinite(c))
                {
                    _testChance = System.Math.Clamp(c, 0, 1000);
                }
            });
            inspector.Button(IdPrefix + "crit.rolls", "▶ Проверить 1000 бросков", () => { _rolls = CombatRulesetEditorModel.RollTest(_testChance); Stamp++; }, ButtonKind.Normal,
                "Те же детерминированные броски, что в бою: результат не меняется от запуска к запуску");
            if (_rolls is not null)
            {
                inspector.Text(CombatRulesetEditorModel.DescribeRolls(_rolls), size: 12);
            }
        }
    }

    protected override void BuildPreview(Inspector preview)
    {
        preview.Section("Как работают правила");
        foreach (var line in _model.Preview())
        {
            preview.Text(line, size: 12);
        }

        if (_model.Ruleset.CritEnabled)
        {
            preview.Section("Вероятности крита");
            foreach (var line in CombatRulesetEditorModel.CritTable())
            {
                preview.Text(line, size: 12);
            }
        }

        SandboxOf().Build(preview, Library, RunSandbox, () => Stamp++);
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
            case "ruleset-rolls":
                // expect:ruleset-rolls:<text of the last 1000-roll check>
                actual = _rolls is null ? "none" : CombatRulesetEditorModel.DescribeRolls(_rolls);
                ok = actual.Contains(string.Join(":", a.Skip(1)), System.StringComparison.Ordinal);
                return true;
            case "ruleset-side":
                // expect:ruleset-side:<selected side 0…5 or -1>
                actual = _side.ToString(CultureInfo.InvariantCulture);
                ok = actual == a[1];
                return true;
            default:
                actual = "unknown";
                ok = false;
                return false;
        }
    }
}

/// <summary>A registry characteristic (spec part 6 §5.7): unit label, base value, range, rounding.</summary>
public partial class UnitStatEditor : SmallBoardEditor
{
    private UnitStatEditorModel _model = null!;

    public override string Kind => ContentTypeRegistry.UnitStat;

    protected override string IdPrefix => "stat.";

    protected override string KindTitle => "Характеристика";

    public void Configure(ContentLibrary library, ContentItem item, ContentItem? draft = null)
    {
        _model = new UnitStatEditorModel(library, item);
        if (draft is not null)
        {
            _model.LoadDraft(draft);
        }

        Attach(library, _model);
    }

    protected override void UpdateCanvas() => Fills?.ClearAll();

    private void Set(string key, string value)
    {
        if (_model.Set(key, value) is { } issue)
        {
            Message("✗ " + issue.Message, HexTactics.Content.Model.IssueSeverity.Error);
            Stamp++;
        }
    }

    protected override void BuildProperties(Inspector inspector)
    {
        var s = _model.Stat;
        inspector.Section("Характеристика");
        inspector.LineText(IdPrefix + "unit", "Единица (очки, %, гекс/с…)", s.Unit, v => Set("unit", v));
        inspector.LineText(IdPrefix + "base", "Базовое значение", Inspector.Num(s.BaseValue), v => Set("base", v), "Его получает каждый юнит без своего значения.");
        inspector.LineText(IdPrefix + "min", "Минимум", Inspector.Num(s.Min), v => Set("min", v));
        inspector.LineText(IdPrefix + "max", "Максимум", Inspector.Num(s.Max), v => Set("max", v));
        inspector.LineText(IdPrefix + "decimals", "Знаков после запятой (0–4)", s.Decimals.ToString(CultureInfo.InvariantCulture), v => Set("decimals", v));
    }

    protected override void BuildPreview(Inspector preview)
    {
        preview.Section("Как работает");
        foreach (var line in _model.Preview())
        {
            preview.Text(line, size: 12);
        }
    }

    protected override bool Expect(string[] a, out string actual, out bool ok)
    {
        if (a[0] == "stat-base")
        {
            actual = _model.Stat.BaseValue.ToString("0.##", CultureInfo.InvariantCulture);
            ok = actual == a[1];
            return true;
        }

        actual = "unknown";
        ok = false;
        return false;
    }
}

/// <summary>A node of the species tree (spec part 6 §5.2): parent, icon, inherited effects, lineage and users.</summary>
public partial class SpeciesEditor : SmallBoardEditor
{
    private SpeciesEditorModel _model = null!;

    public override string Kind => ContentTypeRegistry.Species;

    protected override string IdPrefix => "species.";

    protected override string KindTitle => "Вид";

    public SpeciesEditorModel SpeciesModel => _model;

    public void Configure(ContentLibrary library, ContentItem item, ContentItem? draft = null)
    {
        _model = new SpeciesEditorModel(library, item);
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
        Fills.SetFill(Center, new Color(0.55f, 0.7f, 0.5f, 0.8f));
    }

    protected override void BuildProperties(Inspector inspector)
    {
        inspector.Section("Вид");
        inspector.ReadOnly("Линия", _model.Lineage());
        inspector.Choice(IdPrefix + "parent", "Родитель", _model.Parents().Select(p => new ChoiceOption(p.Value, p.Label)).ToList(), _model.Species.Parent, v =>
        {
            if (_model.SetParent(v) is { } refusal)
            {
                Message("✗ " + refusal, HexTactics.Content.Model.IssueSeverity.Error);
                Stamp++;
            }
        }, "Ровно один родитель или корень. Потомок считается всеми предками; цикл запрещён.");
        inspector.LineText(IdPrefix + "icon", "Значок (до 4 символов)", _model.Species.Icon, v => _model.SetIcon(v));
        inspector.Section("Эффекты вида");
        inspector.Hint("Действуют на юнитов этого вида и всех потомков — один раз. Потомок с тем же ключом эффекта заменяет, отключает или дополняет эффект предка.");
        BuildEffectList(inspector, IdPrefix + "effect.", _model.EffectCards(), _model.Species.Effects, _model.AddEffect, _model.RemoveEffect, _model.SetEffect, inheritance: true, "＋ Добавить эффект");
    }

    protected override void BuildPreview(Inspector preview)
    {
        preview.Section("Вид словами");
        foreach (var line in _model.Preview())
        {
            preview.Text(line, size: 12);
        }
    }

    public override bool Reveal(string id)
    {
        var parts = id.Split('.');
        if (parts.Length > 3 && parts[0] == "species" && parts[1] == "effect" && parts[2] is not ("template" or "add"))
        {
            OpenEffect(IdPrefix + "effect.", parts[2]);
            return true;
        }

        return false;
    }

    protected override bool Expect(string[] a, out string actual, out bool ok)
    {
        switch (a[0])
        {
            case "species-lineage":
                actual = _model.Lineage();
                ok = actual == string.Join(":", a.Skip(1));
                return true;
            case "species-preview":
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

/// <summary>A shared trait (spec part 6 §5.3, §5.6): category, effects, allowed species, conflicts and stacking.</summary>
public partial class TraitEditor : SmallBoardEditor
{
    private TraitEditorModel _model = null!;
    private string? _speciesPick;
    private string? _traitPick;

    public override string Kind => ContentTypeRegistry.Trait;

    protected override string IdPrefix => "trait.";

    protected override string KindTitle => "Тег";

    public TraitEditorModel TraitModel => _model;

    public void Configure(ContentLibrary library, ContentItem item, ContentItem? draft = null)
    {
        _model = new TraitEditorModel(library, item);
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
        var c = HexTactics.Content.Schema.FieldSchema.IsColor(_model.Trait.Color) ? Color.FromHtml(_model.Trait.Color) : new Color(0.7f, 0.6f, 0.4f);
        Fills.SetFill(Center, new Color(c, 0.85f));
    }

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
        var t = _model.Trait;
        inspector.Section("Тег");
        inspector.Choice(IdPrefix + "category", "Категория", TraitData.Categories.Select(c => new ChoiceOption(c.Value, c.Label)).ToList(), t.Category, v => _model.SetCategory(v));
        inspector.LineText(IdPrefix + "icon", "Значок (до 4 символов)", t.Icon, v => _model.SetIcon(v));
        inspector.Section("Эффекты");
        inspector.Hint("Тег может быть и без эффектов — тогда это классификация для фильтров. Эффекты действуют у каждого юнита с тегом.");
        BuildEffectList(inspector, IdPrefix + "effect.", _model.EffectCards(), t.Effects, _model.AddEffect, _model.RemoveEffect, _model.SetEffect, inheritance: false, "＋ Добавить эффект");

        inspector.Section("Совместимость");
        inspector.LineText(IdPrefix + "group", "Группа взаимоисключения (необязательно)", t.ExclusiveGroup, v => Refuse(_model.SetExclusive(v, t.ConflictPolicy, t.Priority)),
            "Например «Здоровье»: «Здоров», «Ранен», «Критически ранен».");
        if (t.ExclusiveGroup.Length > 0)
        {
            inspector.Choice(IdPrefix + "policy", "Если у юнита два тега группы", new List<ChoiceOption>
            {
                new(TraitData.PolicyError, "ошибка — выбрать один"), new(TraitData.PolicyReplace, "действует последний"), new(TraitData.PolicyHighest, "действует старший по приоритету"),
            }, t.ConflictPolicy, v => Refuse(_model.SetExclusive(t.ExclusiveGroup, v, t.Priority)));
            inspector.LineText(IdPrefix + "priority", "Приоритет", t.Priority.ToString(CultureInfo.InvariantCulture), v =>
                Refuse(int.TryParse(v.Trim(), out var p) ? _model.SetExclusive(t.ExclusiveGroup, t.ConflictPolicy, p) : "приоритет — целое число"));
        }

        inspector.Choice(IdPrefix + "stack", "Если тег указан дважды", new List<ChoiceOption>
        {
            new(TraitData.StackUnique, "действует один раз"), new(TraitData.StackStack, "складывается"),
        }, t.StackPolicy, v => Refuse(_model.SetStacking(v, t.MaxStacks)));
        if (t.StackPolicy == TraitData.StackStack)
        {
            inspector.LineText(IdPrefix + "maxstacks", "Не больше раз (1–16)", t.MaxStacks.ToString(CultureInfo.InvariantCulture), v =>
                Refuse(int.TryParse(v.Trim(), out var m) ? _model.SetStacking(t.StackPolicy, m) : "предел — целое число"));
        }

        var species = Library.Items(ContentTypeRegistry.Species).Select(s => new ChoiceOption(s.Id, (t.AllowedSpecies.Contains(s.Id) ? "✓ " : string.Empty) + s.Name)).ToList();
        if (species.Count > 0)
        {
            _speciesPick = species.Any(s => s.Value == _speciesPick) ? _speciesPick : species[0].Value;
            inspector.Choice(IdPrefix + "species.pick", "Только для видов (✓ — выбран; пусто — для всех)", species, _speciesPick!, v => _speciesPick = v);
            inspector.Button(IdPrefix + "species.toggle", "Добавить / убрать вид", () => Refuse(_model.Toggle("allowedSpecies", _speciesPick!)), ButtonKind.Flat);
        }

        var traits = Library.Items(ContentTypeRegistry.Trait).Where(x => x.Id != _model.Item.Id)
            .Select(x => new ChoiceOption(x.Id, (t.RequiredTraits.Contains(x.Id) ? "нужен: " : t.ExcludedTraits.Contains(x.Id) ? "несовместим: " : string.Empty) + x.Name)).ToList();
        if (traits.Count > 0)
        {
            _traitPick = traits.Any(x => x.Value == _traitPick) ? _traitPick : traits[0].Value;
            inspector.Choice(IdPrefix + "other.pick", "Другой тег", traits, _traitPick!, v => _traitPick = v);
            var row = inspector.Row();
            row.AddChild(UiKit.Button("Нужен / не нужен", () => Refuse(_model.Toggle("requiredTraits", _traitPick!)), ButtonKind.Flat, IdPrefix + "require", "Тег действует только вместе с выбранным"));
            row.AddChild(UiKit.Button("Несовместим / совместим", () => Refuse(_model.Toggle("excludedTraits", _traitPick!)), ButtonKind.Flat, IdPrefix + "exclude", "Тег нельзя назначить вместе с выбранным"));
        }
    }

    private readonly List<string> _assign = new();
    private string? _assignPick;
    private string _assignMessage = string.Empty;

    protected override void BuildPreview(Inspector preview)
    {
        preview.Section("Тег словами");
        foreach (var line in _model.Preview())
        {
            preview.Text(line, size: 12);
        }

        // Spec part 6 §9.2: the units with this tag and the explicit mass assignment of the saved tag.
        preview.Section("Юниты с этим тегом");
        var users = _model.UnitsWithTrait();
        preview.Text(users.Count == 0 ? "Пока ни у одного юнита." : string.Join(", ", users.Select(u => u.Name)), size: 12);
        var free = _model.AssignableUnits().Where(u => !_assign.Contains(u.Id)).ToList();
        if (free.Count > 0)
        {
            _assignPick = free.Any(u => u.Id == _assignPick) ? _assignPick : free[0].Id;
            preview.Choice(IdPrefix + "assign.pick", "Назначить юниту", free.Select(u => new ChoiceOption(u.Id, u.Name)).ToList(), _assignPick!, v => _assignPick = v);
            preview.Button(IdPrefix + "assign.add", "＋ В список назначения", () =>
            {
                if (_assignPick is not null && !_assign.Contains(_assignPick))
                {
                    _assign.Add(_assignPick);
                }

                Stamp++;
            }, ButtonKind.Flat);
        }

        if (_assign.Count > 0)
        {
            preview.Text("К назначению: " + string.Join(", ", _assign.Select(id => Library.Find(ContentTypeRegistry.Unit, id)?.Name ?? id)), size: 12);
            preview.Button(IdPrefix + "assign", $"Назначить тег ({_assign.Count})", () =>
            {
                var result = _model.AssignTo(_assign);
                _assignMessage = result.Refusals.Count > 0
                    ? "✗ Ничего не изменено: " + string.Join("; ", result.Refusals)
                    : $"✓ Тег назначен: {string.Join(", ", result.Assigned)}";
                Message(_assignMessage, result.Refusals.Count > 0 ? HexTactics.Content.Model.IssueSeverity.Error : null);
                if (result.Refusals.Count == 0)
                {
                    _assign.Clear();
                }

                Stamp++;
            }, ButtonKind.Primary, "Каждый юнит проверяется с тегом заранее; при ошибке не меняется ни один");
        }
    }

    public override bool Reveal(string id)
    {
        var parts = id.Split('.');
        if (parts.Length > 3 && parts[0] == "trait" && parts[1] == "effect" && parts[2] is not ("template" or "add"))
        {
            OpenEffect(IdPrefix + "effect.", parts[2]);
            return true;
        }

        return false;
    }

    protected override bool Expect(string[] a, out string actual, out bool ok)
    {
        if (a[0] == "trait-preview")
        {
            actual = string.Join(" | ", _model.Preview());
            ok = actual.Contains(string.Join(":", a.Skip(1)), System.StringComparison.Ordinal);
            return true;
        }

        if (a[0] == "trait-units")
        {
            // expect:trait-units:<N> — how many units carry the tag
            actual = _model.UnitsWithTrait().Count.ToString(CultureInfo.InvariantCulture);
            ok = actual == a[1];
            return true;
        }

        actual = "unknown";
        ok = false;
        return false;
    }
}
