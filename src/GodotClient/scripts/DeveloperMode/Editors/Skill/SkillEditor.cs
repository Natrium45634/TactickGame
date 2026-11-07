using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using HexTactics.Client.DeveloperMode.SharedInspector;
using HexTactics.Client.InputLayer;
using HexTactics.Client.Presentation;
using HexTactics.Client.UI;
using HexTactics.Content.Kinds;
using HexTactics.Content.Library;
using HexTactics.Content.Registry;
using HexTactics.Core.Hex;
using HexTactics.Core.Map;
using HexTactics.Editor.Skills;

namespace HexTactics.Client.DeveloperMode.Editors.Skill;

/// <summary>
/// The skill editor (spec part 4 §6): a field of equal flat-top hexes with the base unit in the centre and its facing
/// pointer (six directions, the drawing turns mathematically); LMB adds a hex to the area and opens its settings, LMB on
/// an area hex selects it, the eraser or «Убрать из области» removes it. Each cell: own effects or the skill's base
/// effects with per-field overrides, editor colour and opacity; a per-cell preview with an Attack Power; a test run.
/// </summary>
public partial class SkillEditor : DefinitionEditorShell
{
    private const int GridSide = 13;
    private static readonly HexCoord Center = HexCoord.FromOffset(6, 6);
    private static readonly string[] FacingNames = { "север", "северо-восток", "юго-восток", "юг", "юго-запад", "северо-запад" };

    private SkillEditorModel _model = null!;
    private BoardView _board = null!;
    private HexFillLayer _fills = null!;
    private Node3D _labels = null!;
    private Node3D _unitPivot = null!;
    private HexGrid _grid = null!;
    private BoardPicker? _picker;
    private HexCoord? _hover;
    private Vector2? _pressAt;
    private string _newEffectType = "damage";
    private string _previewUnit = "core.unit.operative";

    public override string Kind => ContentTypeRegistry.Skill;

    protected override string IdPrefix => "skill.";

    protected override string KindTitle => "Навык";

    protected override string? TestButtonText => "▶ Тест навыка";

    public SkillEditorModel SkillModel => _model;

    public void Configure(ContentLibrary library, ContentItem item, ContentItem? draft = null)
    {
        _model = new SkillEditorModel(library, item);
        if (draft is not null)
        {
            _model.LoadDraft(draft);
        }

        Attach(library, _model);
        ApplyPreviewUnit();
    }

    // ------------------------------------------------------------------ canvas

    protected override void BuildCanvas()
    {
        TilePalette.Current = new TilePalette();
        BuildGrid();
        _board = new BoardView { Name = "SkillBoard" };
        AddChild(_board);
        _board.Build(_grid, Layout);
        _fills = new HexFillLayer { Name = "Area", RenderPriority = 2, ExtraLift = 0.002f };
        AddChild(_fills);
        _fills.Build(_grid, Layout);
        _labels = new Node3D { Name = "CellLabels" };
        AddChild(_labels);

        var unit = new Node3D { Name = "BaseUnit", Position = Layout.HexToPlane(Center).ToWorld(0f) };
        AddChild(unit);
        unit.AddChild(new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = UnitView.BodyRadius, BottomRadius = UnitView.BodyRadius, Height = UnitView.BodyHeight, RadialSegments = 32 },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = VisualConfig.UnitPlayer, Roughness = 0.75f },
            Position = new Vector3(0, UnitView.BodyHeight * 0.5f, 0),
        });
        _unitPivot = new Node3D { Name = "Facing" };
        unit.AddChild(_unitPivot);
        var north = Layout.EdgeMidpoint(Center, HexDirection.North) - Layout.HexToPlane(Center);
        var side = new PlaneVector(-north.Z, north.X) * 0.3;
        var arrow = new ImmediateMesh();
        arrow.SurfaceBegin(Mesh.PrimitiveType.Triangles, new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = VisualConfig.UnitFacingPointer, CullMode = BaseMaterial3D.CullModeEnum.Disabled, NoDepthTest = true, RenderPriority = 20 });
        arrow.SurfaceAddVertex((north * 0.98).ToWorld(0.02f));
        arrow.SurfaceAddVertex(((north * 0.55) + side).ToWorld(0.02f));
        arrow.SurfaceAddVertex(((north * 0.55) - side).ToWorld(0.02f));
        arrow.SurfaceEnd();
        _unitPivot.AddChild(new MeshInstance3D { Mesh = arrow });
    }

    private void BuildGrid()
    {
        _grid = new HexGrid(GridSide, GridSide);
        foreach (var c in _grid.Coords)
        {
            if (c.DistanceTo(Center) > _model.Radius)
            {
                _grid.SetTile(c, TileType.Void, null, 0);
            }
        }

        _picker = new BoardPicker(_grid, Layout);
    }

    protected override void UpdateCanvas()
    {
        if (_board is null)
        {
            return;
        }

        if (_grid.Coords.Count(_grid.Exists) != 1 + (3 * _model.Radius * (_model.Radius + 1)))
        {
            BuildGrid();
            _board.Build(_grid, Layout);
            _fills.Build(_grid, Layout);
            FitCamera();
        }

        // The facing pointer: North → rotate around Y (Godot: positive angle = counter-clockwise seen from above).
        _unitPivot.Rotation = new Vector3(0, -Mathf.DegToRad(60f * (int)_model.Facing), 0);

        _fills.ClearAll();
        foreach (var child in _labels.GetChildren())
        {
            child.QueueFree();
        }

        if (_hover is { } h && _grid.Exists(h))
        {
            _fills.SetFill(h, _model.EraseMode ? new Color(0.95f, 0.35f, 0.30f, 0.28f) : new Color(1, 1, 1, 0.16f));
        }

        foreach (var cell in _model.Skill.Cells)
        {
            var at = Center + _model.ToDrawn(cell.Offset);
            if (!_grid.Exists(at))
            {
                continue;
            }

            var color = Color.FromHtml(cell.Color ?? "#6E7686");
            color.A = (float)(cell.Alpha ?? 0.55);
            _fills.SetFill(at, _model.Selected == cell.Offset ? new Color(1f, 0.85f, 0.35f, 0.6f) : color);
            _labels.AddChild(new Label3D
            {
                Text = ShortLabel(cell),
                FontSize = 44,
                PixelSize = 0.0085f,
                Modulate = new Color(0.98f, 0.98f, 0.99f),
                OutlineModulate = new Color(0.08f, 0.09f, 0.11f),
                OutlineSize = 10,
                Position = Layout.HexToPlane(at).ToWorld(0.03f),
                RotationDegrees = new Vector3(-90, 0, 0),
                NoDepthTest = true,
                RenderPriority = 30,
            });
        }
    }

    /// <summary>«3×150%», «20», «+лечение» … on the cell (only a hint for the author; the panel has the details).</summary>
    private string ShortLabel(SkillCellData cell)
    {
        var parts = new List<string>();
        foreach (var e in _model.Skill.EffectsOf(cell))
        {
            var spec = e.ToSpec(Library.Registry.Effects);
            var hits = (int)System.Math.Round(spec.Number("hits", 1));
            var prefix = hits > 1 ? $"{hits}×" : string.Empty;
            parts.Add(e.Type switch
            {
                "damage" when spec.Text("formula", "fixed") == "attack" => $"{prefix}{Inspector.Num(spec.Number("multiplier", 1) * 100)}%",
                "damage" => $"{prefix}{Inspector.Num(spec.Number("amount"))}",
                "heal" => $"+{prefix}{(spec.Text("formula", "fixed") == "fixed" ? Inspector.Num(spec.Number("amount")) : Inspector.Num(spec.Number("multiplier", 1) * 100) + "%")}",
                "buff" => "↑",
                "debuff" => "↓",
                "periodic" => $"{Inspector.Num(spec.Number("perSecond", 5))}/с",
                _ => "?",
            });
        }

        return parts.Count == 0 ? "·" : string.Join(" ", parts.Take(2));
    }

    protected override Aabb CanvasBounds()
    {
        var (min, max) = _grid.PlaneBounds(Layout);
        var r = _model.Radius;
        var c = Layout.HexToPlane(Center);
        var span = Layout.Size * 1.8 * (r + 0.7);
        return new Aabb(new Vector3((float)(c.X - span), -VisualConfig.TileThickness, (float)(c.Z - span)), new Vector3((float)(2 * span), 1.0f, (float)(2 * span)));
    }

    protected override void HandleCanvasInput(InputEvent @event)
    {
        if (ModalDialog.Open is not null || _picker is null)
        {
            return;
        }

        switch (@event)
        {
            case InputEventMouseMotion motion:
                var hover = _picker.Pick(CameraRig.Camera, motion.Position).Hex;
                if (hover != _hover)
                {
                    _hover = hover;
                    Stamp++;
                }

                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } left:
                if (left.Pressed)
                {
                    _pressAt = left.Position;
                }
                else if (_pressAt is { } at && at.DistanceTo(left.Position) <= 6f && _picker.Pick(CameraRig.Camera, left.Position).Hex is { } hex && _grid.Exists(hex))
                {
                    _pressAt = null;
                    ClickHex(hex);
                    GetViewport().SetInputAsHandled();
                }

                break;
        }
    }

    private void ClickHex(HexCoord hex)
    {
        var drawn = hex - Center;
        switch (_model.Click(drawn))
        {
            case SkillCellClick.Added:
                Message($"Клетка {SkillEditorModel.Show(_model.ToStored(drawn))} добавлена в область (наследует эффекты навыка).", null);
                break;
            case SkillCellClick.Removed:
                Message($"Клетка {SkillEditorModel.Show(_model.ToStored(drawn))} убрана из области.", null);
                break;
        }
    }

    protected override bool HandleKey(InputEventKey key)
    {
        switch (key.Keycode)
        {
            case Key.Q:
                Turn(-1);
                return true;
            case Key.E:
                Turn(1);
                return true;
            case Key.Escape:
                _model.Select(null);
                return true;
            default:
                return false;
        }
    }

    private void Turn(int steps)
    {
        _model.Facing = _model.Facing.RotateClockwise(steps);
        Stamp++;
    }

    // ------------------------------------------------------------------ panels

    protected override void BuildProperties(Inspector inspector)
    {
        inspector.Section("Поле предпросмотра");
        var row = inspector.Row();
        row.AddChild(UiKit.Button("↺", () => Turn(-1), ButtonKind.Normal, "skill.turn.left", "Повернуть юнита против часовой (Q)"));
        var facing = UiKit.Label($"взгляд: {FacingNames[(int)_model.Facing]}", 13);
        facing.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        facing.HorizontalAlignment = HorizontalAlignment.Center;
        row.AddChild(facing);
        row.AddChild(UiKit.Button("↻", () => Turn(1), ButtonKind.Normal, "skill.turn.right", "Повернуть юнита по часовой (E)"));
        inspector.Hint("Клетки хранятся относительно взгляда; рисунок поворачивается для всех шести направлений.");
        inspector.Choice("skill.radius", "Размер поля", Enumerable.Range(2, 5).Select(r => new ChoiceOption(r.ToString(CultureInfo.InvariantCulture), $"радиус {r}")).ToList(), _model.Radius.ToString(CultureInfo.InvariantCulture), v =>
        {
            _model.Radius = int.Parse(v, CultureInfo.InvariantCulture);
            Stamp++;
        });
        inspector.Check("skill.erase", "Ластик: клик убирает клетку", _model.EraseMode, on =>
        {
            _model.EraseMode = on;
            Stamp++;
        });

        if (_model.Selected is { } selected && _model.CellAt(selected) is { } cell)
        {
            BuildCellPanel(inspector, selected, cell);
        }
        else
        {
            inspector.Section("Навык");
            inspector.LineText("skill.icon", "Иконка (1–4 символа)", _model.Skill.Icon, v => _model.SetIcon(v), "Показывается в меню ТД и на кнопке навыка.");
            inspector.Hint($"Клеток в области: {_model.Skill.Cells.Count}. Клик по пустому гексу — добавить клетку, по клетке — её настройки.");
            inspector.Section("Эффекты навыка (для клеток без своих)");
            BuildEffects(inspector, null);
        }
    }

    private void BuildCellPanel(Inspector inspector, HexCoord offset, SkillCellData cell)
    {
        var distance = offset.DistanceTo(default);
        inspector.Section($"Клетка {SkillEditorModel.Show(offset)}");
        inspector.ReadOnly("Положение", offset == default ? "сам юнит" : $"{distance} гекс. от юнита (для взгляда на север)");
        inspector.Button("skill.cell.remove", "Убрать из области", () => _model.RemoveCell(offset), ButtonKind.Danger);
        inspector.Button("skill.cell.deselect", "Настройки навыка (Esc)", () => _model.Select(null), ButtonKind.Flat);
        inspector.ColorField("skill.cell.color", "Цвет клетки (только для редактора)", cell.Color ?? "#6E7686", v => _model.SetCellColor(offset, v, cell.Alpha));
        inspector.LineText("skill.cell.alpha", "Прозрачность цвета (0.05–1)", Inspector.Num(cell.Alpha ?? 0.55), v =>
        {
            if (double.TryParse(v.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var a) && a >= 0.05 && a <= 1)
            {
                _model.SetCellColor(offset, cell.Color ?? "#6E7686", a);
            }
            else
            {
                Message($"✗ Прозрачность «{v}»: от 0.05 до 1 — значение не изменено", HexTactics.Content.Model.IssueSeverity.Error);
                Stamp++;
            }
        });
        inspector.Check("skill.cell.own", "Свои эффекты клетки", !cell.Inherits, own => _model.SetCellOwnEffects(offset, own),
            cell.Inherits ? "Сейчас клетка наследует эффекты навыка; изменённые здесь поля — свои только для этой клетки (помечены «•»)." : "Клетка не зависит от эффектов навыка.");
        BuildEffects(inspector, offset);
    }

    private void BuildEffects(Inspector inspector, HexCoord? cell)
    {
        var cellData = cell is { } c ? _model.CellAt(c) : null;
        var inherited = cellData is { Inherits: true };
        var effects = cellData is null ? (IReadOnlyList<EffectData>)_model.Skill.BaseEffects : _model.Skill.EffectsOf(cellData);
        for (var i = 0; i < effects.Count; i++)
        {
            var effect = effects[i];
            var descriptor = Library.Registry.Effects.Find(effect.Type);
            inspector.Section($"Эффект {i + 1}: {descriptor?.Label ?? effect.Type}" + (inherited ? " — из навыка" : string.Empty));
            if (descriptor is null)
            {
                inspector.Text($"✗ неизвестный тип «{effect.Type}»").AddThemeColorOverride("font_color", UiKit.Error);
                continue;
            }

            var index = i;
            foreach (var field in descriptor.Fields)
            {
                if (inspector.Field($"skill.e{i}.", field, effect, (f, v) => ApplyEffectField(cell, index, f.Key, v)) is null)
                {
                    continue;
                }

                if (inherited && _model.IsOverridden(cell!.Value, i, field.Key))
                {
                    var r = inspector.Row();
                    var note = UiKit.Label($"• «{field.Label}» — своё значение клетки", 12, dim: true, wrap: true);
                    note.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                    r.AddChild(note);
                    var key = field.Key;
                    r.AddChild(UiKit.Button("↺ как у навыка", () => _model.ResetOverride(cell.Value, index, key), ButtonKind.Flat, $"skill.e{i}.{field.Key}.reset"));
                }
            }

            if (!inherited)
            {
                inspector.Button($"skill.e{i}.remove", "✕ Убрать эффект", () => _model.RemoveEffect(cell, index), ButtonKind.Flat);
            }
        }

        if (effects.Count == 0)
        {
            inspector.Hint(inherited ? "У навыка нет базовых эффектов — добавьте их в настройках навыка или включите «Свои эффекты»." : "Эффектов нет.");
        }

        if (!inherited)
        {
            var types = Library.Registry.Effects.For(EffectContext.Skill).Select(t => new ChoiceOption(t.Type, t.Label)).ToList();
            _newEffectType = types.Any(t => t.Value == _newEffectType) ? _newEffectType : types[0].Value;
            inspector.Choice("skill.addtype", "Новый эффект", types, _newEffectType, v => _newEffectType = v, "Урон, лечение, бафф, дебафф, периодический — из реестра эффектов.");
            inspector.Button("skill.add", "＋ Добавить эффект", () =>
            {
                if (_model.AddEffect(cell, _newEffectType) is { } refusal)
                {
                    Message("✗ " + refusal, HexTactics.Content.Model.IssueSeverity.Error);
                }
            });
        }
    }

    private void ApplyEffectField(HexCoord? cell, int index, string key, object? value)
    {
        if (_model.SetEffectField(cell, index, key, value) is { } issue)
        {
            Message("✗ " + issue.Message, HexTactics.Content.Model.IssueSeverity.Error);
            Stamp++;
        }
    }

    protected override void BuildPreview(Inspector preview)
    {
        preview.Section("Предпросмотр по клеткам");
        var units = Library.Items(ContentTypeRegistry.Unit).Select(u => new ChoiceOption(u.Id, $"{u.Name}: сила атаки {Inspector.Num(((UnitData)u.Data).AttackPower)}")).ToList();
        preview.Choice("skill.previewUnit", "Юнит для расчёта", units, _previewUnit, v =>
        {
            _previewUnit = v;
            ApplyPreviewUnit();
            Stamp++;
        });
        preview.LineText("skill.previewAp", "Сила атаки (Attack Power)", Inspector.Num(_model.PreviewAttackPower), v =>
        {
            if (double.TryParse(v.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var ap) && ap >= 0)
            {
                _model.PreviewAttackPower = ap;
            }

            Stamp++;
        });
        var lines = _model.Preview();
        if (lines.Count == 0)
        {
            preview.Hint("Область пуста: кликните по гексу на поле.");
        }

        foreach (var line in lines)
        {
            preview.Text(line, size: 12);
        }

        preview.Hint("Цвет клеток — пометка автора; на бой не влияет. В бою зона показывается обычным серым.");
    }

    private void ApplyPreviewUnit()
    {
        if (Library?.Find(ContentTypeRegistry.Unit, _previewUnit)?.Data is UnitData unit)
        {
            _model.PreviewAttackPower = unit.AttackPower;
            _model.PreviewMaxHealth = unit.MaxHealth;
        }
    }

    // ------------------------------------------------------------------ automation

    public override int RunAutomationStep(string step, HexTactics.Client.Debug.AutomationRunner runner)
    {
        var parts = step.Split(':', 2);
        if (parts[0] == "cell" && parts.Length > 1)
        {
            // cell:q,r — click the canvas hex drawn at offset (q,r) from the base unit (current facing).
            var qr = parts[1].Split(',');
            var drawn = new HexCoord(int.Parse(qr[0], CultureInfo.InvariantCulture), int.Parse(qr[1], CultureInfo.InvariantCulture));
            var hex = Center + drawn;
            HexTactics.Client.Debug.AutomationInput.Click(CameraRig.Camera.UnprojectPosition(Layout.HexToPlane(hex).ToWorld(0f)));
            return 3;
        }

        return base.RunAutomationStep(step, runner);
    }

    protected override bool Expect(string[] a, out string actual, out bool ok)
    {
        switch (a[0])
        {
            case "cells":
                actual = _model.Skill.Cells.Count.ToString(CultureInfo.InvariantCulture);
                ok = actual == a[1];
                return true;
            case "selected-cell":
                actual = _model.Selected is { } s ? $"{s.Q},{s.R}" : "none";
                ok = actual == a[1];
                return true;
            case "cell-color":
                {
                    // expect:cell-color:q,r:#RRGGBB — the editor colour of the stored cell (q,r)
                    var qr = a[1].Split(',');
                    var cell = _model.CellAt(new HexCoord(int.Parse(qr[0], CultureInfo.InvariantCulture), int.Parse(qr[1], CultureInfo.InvariantCulture)));
                    actual = cell is null ? "none" : cell.Color ?? "default";
                    ok = string.Equals(actual, a[2], System.StringComparison.OrdinalIgnoreCase);
                    return true;
                }

            case "skill-facing":
                actual = _model.Facing.ToString();
                ok = actual == a[1];
                return true;
            case "preview":
                {
                    // expect:preview:N:text — line N of the per-cell preview contains text
                    var lines = _model.Preview();
                    var n = int.Parse(a[1], CultureInfo.InvariantCulture);
                    actual = n < lines.Count ? lines[n] : "none";
                    ok = actual.Contains(string.Join(":", a.Skip(2)), System.StringComparison.Ordinal);
                    return true;
                }

            default:
                actual = "unknown";
                ok = false;
                return false;
        }
    }
}
