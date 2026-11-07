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

    protected override string KindTitle => "Набор сопротивлений";

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

        inspector.Section("Значения, %");
        inspector.Hint("−500…+500; итог для типа = все типы + семейство + подтип. Это данные, а не отдельная механика: юнит применяет набор ссылкой.");
        foreach (var row in _model.Rows())
        {
            var label = (row.Depth > 0 ? "    " : string.Empty) + row.Label;
            if (row.Bypass)
            {
                inspector.ReadOnly(label, "обходит численные сопротивления");
                continue;
            }

            var key = row.Key;
            var hint = row.Source is null ? null : (row.Inherited ? "из основы" : "своё") + (row.Value != 0 ? " · " + UnitEditorModel.Explain(row.Value) : string.Empty);
            inspector.LineText($"{IdPrefix}res.{key}", label + ", %", Inspector.Num(row.Value), v =>
            {
                if (_model.SetValue(key, v) is { } issue)
                {
                    Message("✗ " + issue.Message, HexTactics.Content.Model.IssueSeverity.Error);
                    Stamp++;
                }
            }, hint);
            if (row.Own)
            {
                inspector.Button($"{IdPrefix}res.{key}.reset", "↺ Убрать своё значение", () => _model.Reset(key), ButtonKind.Flat);
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
