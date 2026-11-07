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
using HexTactics.Content.Runtime;
using HexTactics.Core.Hex;
using HexTactics.Core.Map;
using HexTactics.Editor.Units;

namespace HexTactics.Client.DeveloperMode.Editors.Unit;

/// <summary>
/// The unit editor (spec part 4 §5): name/description, characteristics from the field schema (HP, movement, attack rate,
/// Attack Power, range, side), the skill set as references to saved skills (add from a filtered catalog, reorder, remove),
/// appearance (tint, size); a preview of the unit with the area of the chosen skill, the combat numbers, validation and a
/// test run with the unit in the roster.
/// </summary>
public partial class UnitEditor : DefinitionEditorShell
{
    private UnitEditorModel _model = null!;
    private BoardView _board = null!;
    private HexFillLayer _area = null!;
    private Node3D _body = null!;
    private MeshInstance3D _bodyMesh = null!;
    private StandardMaterial3D _bodyMaterial = null!;
    private HexGrid _grid = null!;
    private string _filter = string.Empty;
    private string? _previewSkill;
    private string? _pick;

    private static readonly HexCoord Center = HexCoord.FromOffset(3, 3);

    public override string Kind => ContentTypeRegistry.Unit;

    protected override string IdPrefix => "unit.";

    protected override string KindTitle => "Юнит";

    protected override string? TestButtonText => "▶ Тест юнита";

    public UnitEditorModel UnitModel => _model;

    public void Configure(ContentLibrary library, ContentItem item, ContentItem? draft = null)
    {
        _model = new UnitEditorModel(library, item);
        if (draft is not null)
        {
            _model.LoadDraft(draft);
        }

        Attach(library, _model);
    }

    protected override void BuildCanvas()
    {
        TilePalette.Current = new TilePalette();
        _grid = new HexGrid(7, 7);
        foreach (var c in _grid.Coords)
        {
            if (c.DistanceTo(Center) > 2)
            {
                _grid.SetTile(c, TileType.Void, null, 0);
            }
        }

        _board = new BoardView { Name = "PreviewBoard" };
        AddChild(_board);
        _board.Build(_grid, Layout);
        _area = new HexFillLayer { Name = "SkillArea", RenderPriority = 2, ExtraLift = 0.002f };
        AddChild(_area);
        _area.Build(_grid, Layout);

        _body = new Node3D { Name = "UnitPreview", Position = Layout.HexToPlane(Center).ToWorld(0f) };
        AddChild(_body);
        _bodyMaterial = new StandardMaterial3D { Roughness = 0.75f };
        _bodyMesh = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = UnitView.BodyRadius, BottomRadius = UnitView.BodyRadius, Height = UnitView.BodyHeight, RadialSegments = 32 },
            MaterialOverride = _bodyMaterial,
        };
        _body.AddChild(_bodyMesh);

        // Facing pointer towards North (the skill area preview is drawn for this facing).
        var north = Layout.EdgeMidpoint(Center, HexDirection.North) - Layout.HexToPlane(Center);
        var tip = north * 0.95;
        var side = new PlaneVector(-north.Z, north.X) * 0.25;
        var arrow = new ImmediateMesh();
        arrow.SurfaceBegin(Mesh.PrimitiveType.Triangles, new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = VisualConfig.UnitFacingPointer, CullMode = BaseMaterial3D.CullModeEnum.Disabled });
        arrow.SurfaceAddVertex(tip.ToWorld(0.01f));
        arrow.SurfaceAddVertex(((north * 0.6) + side).ToWorld(0.01f));
        arrow.SurfaceAddVertex(((north * 0.6) - side).ToWorld(0.01f));
        arrow.SurfaceEnd();
        _body.AddChild(new MeshInstance3D { Mesh = arrow });
    }

    protected override void UpdateCanvas()
    {
        var unit = _model.Unit;
        _bodyMaterial.AlbedoColor = unit.Side == "enemy" ? VisualConfig.UnitEnemy : Color.FromHtml(unit.Color);
        var s = (float)unit.Scale;
        _bodyMesh.Scale = new Vector3(s, s, s);
        _bodyMesh.Position = new Vector3(0, UnitView.BodyHeight * 0.5f * s, 0);

        _area.ClearAll();
        var skillId = _previewSkill is not null && unit.Skills.Contains(_previewSkill) ? _previewSkill : unit.Skills.FirstOrDefault();
        if (skillId is not null && Library.Find(ContentTypeRegistry.Skill, skillId) is { } skill)
        {
            foreach (var cell in RuntimeFactory.BuildSkill(skill, Library.Registry.Effects).Area(Center, HexDirection.North, _grid))
            {
                _area.SetFill(cell, VisualConfig.SkillAreaAim);
            }
        }
    }

    protected override Aabb CanvasBounds()
    {
        var (min, max) = _grid.PlaneBounds(Layout);
        return new Aabb(new Vector3((float)min.X, -VisualConfig.TileThickness, (float)min.Z), new Vector3((float)(max.X - min.X), 1.2f, (float)(max.Z - min.Z)));
    }

    protected override void BuildProperties(Inspector inspector)
    {
        var unit = _model.Unit;
        inspector.Section("Характеристики");
        foreach (var field in UnitData.Fields)
        {
            if (field.Kind != HexTactics.Content.Schema.FieldKind.ReferenceList && field.Section == HexTactics.Content.Schema.FieldSections.Main)
            {
                inspector.Field(IdPrefix, field, unit, ApplyField);
            }
        }

        inspector.Hint("Сила атаки (Attack Power) — урон одного удара и база навыков «% от атаки»; скорость атак — сколько ударов в секунду.");

        inspector.Section("Навыки");
        inspector.Hint("Ссылки на сохранённые навыки. Первый — кнопка R и меню ТД в бою; в одной ТД — одно применение.");
        for (var i = 0; i < unit.Skills.Count; i++)
        {
            var id = unit.Skills[i];
            var item = Library.Find(ContentTypeRegistry.Skill, id);
            var row = inspector.Row();
            var name = UiKit.Button(item is null ? $"✗ {id}" : $"{((SkillData)item.Data).Icon} {item.Name}", () => { _previewSkill = id; Stamp++; }, ButtonKind.Flat, $"{IdPrefix}skill.{i}", "Показать зону навыка на поле");
            name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            name.Alignment = HorizontalAlignment.Left;
            if (item is null)
            {
                name.AddThemeColorOverride("font_color", UiKit.Error);
            }

            row.AddChild(name);
            var index = i;
            row.AddChild(UiKit.Button("↑", () => _model.MoveSkill(id, -1), ButtonKind.Normal, $"{IdPrefix}skill.{index}.up", "Выше"));
            row.AddChild(UiKit.Button("↓", () => _model.MoveSkill(id, 1), ButtonKind.Normal, $"{IdPrefix}skill.{index}.down", "Ниже"));
            row.AddChild(UiKit.Button("✕", () => _model.RemoveSkill(id), ButtonKind.Normal, $"{IdPrefix}skill.{index}.remove", "Убрать навык у юнита (сам навык остаётся в библиотеке)"));
        }

        if (unit.Skills.Count == 0)
        {
            inspector.Hint("Навыков нет.");
        }

        inspector.LineText(IdPrefix + "skill.filter", "Добавить навык: поиск", _filter, v => { _filter = v; Stamp++; });
        var choices = _model.AvailableSkills(_filter);
        if (choices.Count == 0)
        {
            inspector.Hint(_filter.Length > 0 ? "Ничего не найдено." : "Все навыки библиотеки уже у юнита.");
        }
        else
        {
            _pick = choices.Any(c => c.Id == _pick) ? _pick : choices[0].Id;
            inspector.Choice(IdPrefix + "skill.pick", "Навык из библиотеки", choices.Select(c => new ChoiceOption(c.Id, $"{c.Icon} {c.Name}{(c.IsBuiltIn ? string.Empty : " (свой)")} — {c.Summary}")).ToList(), _pick, v => { _pick = v; _previewSkill = null; Stamp++; });
            inspector.Button(IdPrefix + "skill.add", "＋ Добавить навык", () =>
            {
                if (_pick is not null && _model.AddSkill(_pick) is { } refusal)
                {
                    Message("✗ " + refusal, HexTactics.Content.Model.IssueSeverity.Error);
                }
                else
                {
                    _previewSkill = _pick;
                }
            });
        }

        inspector.Section("Внешний вид");
        foreach (var field in UnitData.Fields.Where(f => f.Section == HexTactics.Content.Schema.FieldSections.Appearance))
        {
            inspector.Field(IdPrefix, field, unit, ApplyField);
        }
    }

    protected override void BuildPreview(Inspector preview)
    {
        preview.Section("В бою");
        foreach (var line in _model.CombatPreview())
        {
            preview.Text(line);
        }

        preview.Hint("Зона на поле — навык, выбранный в списке (для взгляда на север).");
    }

    protected override bool Expect(string[] a, out string actual, out bool ok)
    {
        var unit = _model.Unit;
        switch (a[0])
        {
            case "unit-field":
                {
                    var field = UnitData.Fields.FirstOrDefault(f => f.Key == a[1]);
                    var value = field?.Get?.Invoke(unit);
                    actual = value switch
                    {
                        double d => d.ToString("0.###", CultureInfo.InvariantCulture),
                        null => "none",
                        _ => value.ToString() ?? string.Empty,
                    };
                    ok = actual == a[2];
                    return true;
                }

            case "skills":
                actual = string.Join(",", unit.Skills.Select(s => Library.Find(ContentTypeRegistry.Skill, s)?.Name ?? s));
                ok = a[1] == unit.Skills.Count.ToString(CultureInfo.InvariantCulture) || actual == a[1];
                return true;
            default:
                actual = "unknown";
                ok = false;
                return false;
        }
    }
}
