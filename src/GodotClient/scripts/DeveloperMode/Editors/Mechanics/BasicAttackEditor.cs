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
using HexTactics.Editor.Mechanics;

namespace HexTactics.Client.DeveloperMode.Editors.Mechanics;

/// <summary>
/// «Базовая атака» (spec part 7 §8.5, M7-50): the same kind of hex board as the skill editor — the unit in the centre with its
/// facing pointer; LMB adds a hex to the area or removes it; Q / E (or the buttons) turn the unit and the area turns with it.
/// The panel: target choice, targets per hit, strength, damage type and the advanced flags behind «Дополнительно».
/// </summary>
public partial class BasicAttackEditor : DefinitionEditorShell
{
    private const int GridSide = 11;
    private static readonly HexCoord Center = HexCoord.FromOffset(5, 5);
    private static readonly string[] FacingNames = { "север", "северо-восток", "юго-восток", "юг", "юго-запад", "северо-запад" };

    private BasicAttackEditorModel _model = null!;
    private BoardView _board = null!;
    private HexFillLayer _fills = null!;
    private Node3D _unitPivot = null!;
    private HexGrid _grid = null!;
    private BoardPicker? _picker;
    private HexCoord? _hover;
    private Vector2? _pressAt;
    private bool _more;

    public override string Kind => ContentTypeRegistry.BasicAttack;

    protected override string IdPrefix => "battack.";

    protected override string KindTitle => "Базовая атака";

    public BasicAttackEditorModel AttackModel => _model;

    public void Configure(ContentLibrary library, ContentItem item, ContentItem? draft = null)
    {
        _model = new BasicAttackEditorModel(library, item);
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
        _grid = new HexGrid(GridSide, GridSide);
        foreach (var c in _grid.Coords)
        {
            if (c.DistanceTo(Center) > BasicAttackData.MaxRadius)
            {
                _grid.SetTile(c, TileType.Void, null, 0);
            }
        }

        _picker = new BoardPicker(_grid, Layout);
        _board = new BoardView { Name = "AttackBoard" };
        AddChild(_board);
        _board.Build(_grid, Layout);
        _fills = new HexFillLayer { Name = "Area", RenderPriority = 2, ExtraLift = 0.002f };
        AddChild(_fills);
        _fills.Build(_grid, Layout);

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

    protected override void UpdateCanvas()
    {
        if (_fills is null)
        {
            return;
        }

        _unitPivot.Rotation = new Vector3(0, -Mathf.DegToRad(60f * (int)_model.Facing), 0);
        _fills.ClearAll();
        if (_hover is { } h && _grid.Exists(h) && h != Center)
        {
            _fills.SetFill(h, new Color(1, 1, 1, 0.16f));
        }

        // The area as drawn for the current facing; the first hex (the highest «по порядку» priority) a little brighter.
        for (var i = 0; i < _model.Attack.Cells.Count; i++)
        {
            var at = Center + _model.ToDrawn(_model.Attack.Cells[i]);
            if (_grid.Exists(at))
            {
                _fills.SetFill(at, i == 0 ? new Color(0.92f, 0.42f, 0.30f, 0.70f) : new Color(0.85f, 0.38f, 0.28f, 0.52f));
            }
        }
    }

    protected override Aabb CanvasBounds()
    {
        var c = Layout.HexToPlane(Center);
        var span = Layout.Size * 1.8 * (BasicAttackData.MaxRadius + 0.7);
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
                    if (_model.Click(hex - Center) is { } refusal)
                    {
                        Message("✗ " + refusal, HexTactics.Content.Model.IssueSeverity.Error);
                    }

                    GetViewport().SetInputAsHandled();
                }

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
        var a = _model.Attack;
        inspector.Section("Область");
        inspector.Hint("Щёлкните гекс вокруг юнита, чтобы добавить его в область или убрать. Область поворачивается вместе со взглядом юнита.");
        var row = inspector.Row();
        row.AddChild(UiKit.Button("↶ Повернуть", () => Turn(-1), ButtonKind.Normal, IdPrefix + "turn.left", "Q"));
        row.AddChild(UiKit.Button("Повернуть ↷", () => Turn(1), ButtonKind.Normal, IdPrefix + "turn.right", "E"));
        inspector.Text($"Взгляд юнита: {FacingNames[(int)_model.Facing]} · гексов в области: {a.Cells.Count}", dim: true, size: 12);

        inspector.Section("Цели и сила");
        inspector.Choice(IdPrefix + "priority", "Кого бить", BasicAttackData.Priorities.Select(p => new ChoiceOption(p.Value, p.Label)).ToList(), a.TargetPriority, v => _model.SetPriority(v));
        inspector.LineText(IdPrefix + "targets", "Целей за удар", a.MaxTargets.ToString(CultureInfo.InvariantCulture), v => Refuse(_model.SetMaxTargets(v)),
            "1 — один враг; больше — несколько врагов области за один удар, каждый со своим броском крита.");
        inspector.LineText(IdPrefix + "power", "Сила удара, % силы атаки", Inspector.Num(a.PowerPercent), v => Refuse(_model.SetPower(v)),
            "100 — удар силой атаки юнита (Attack Power). Частота — «скорость атак» юнита.");
        var types = HexTactics.Editor.Library.ReferenceCatalog.Options(Library, ContentTypeRegistry.DamageType, HexTactics.Content.Schema.ReferenceFilter.Any, a.DamageType)
            .Select(o => new ChoiceOption(o.Value, o.Label, null, o.Group)).ToList();
        types.Insert(0, new ChoiceOption(string.Empty, "как у обычной атаки юнита"));
        inspector.Choice(IdPrefix + "type", "Тип урона", types, a.DamageType, v => Refuse(_model.SetDamageType(v)));

        inspector.Button(IdPrefix + "more", _more ? "Дополнительно ▴" : "Дополнительно ▾", () => { _more = !_more; Stamp++; }, ButtonKind.Flat);
        if (_more)
        {
            inspector.Check(IdPrefix + "crit", "Может критовать", a.CanCrit, on => _model.SetFlag("canCrit", on), "Если правила боя карты включают криты.");
            inspector.Check(IdPrefix + "surprise", "Внезапный удар по стороне цели", a.CanSurprise, on => _model.SetFlag("canSurprise", on), "Если правила боя карты включают внезапность.");
            inspector.Check(IdPrefix + "moving", "Бьёт и в движении", a.WhileMoving, on => _model.SetFlag("whileMoving", on), "Снято — только когда юнит стоит. Атака сама маршрут не останавливает.");
        }
    }

    private void Refuse(string? refusal)
    {
        if (refusal is not null)
        {
            Message("✗ " + refusal, HexTactics.Content.Model.IssueSeverity.Error);
            Stamp++;
        }
    }

    protected override void BuildPreview(Inspector preview)
    {
        preview.Section("Базовая атака словами");
        foreach (var line in _model.Preview())
        {
            preview.Text(line, size: 12);
        }
    }

    // ------------------------------------------------------------------ automation

    public override int RunAutomationStep(string step, HexTactics.Client.Debug.AutomationRunner runner)
    {
        var parts = step.Split(':', 2);
        if (parts[0] == "cell" && parts.Length > 1)
        {
            // cell:q,r — click the board hex drawn at offset (q,r) from the unit (current facing).
            var qr = parts[1].Split(',');
            var hex = Center + new HexCoord(int.Parse(qr[0], CultureInfo.InvariantCulture), int.Parse(qr[1], CultureInfo.InvariantCulture));
            HexTactics.Client.Debug.AutomationInput.Click(CameraRig.Camera.UnprojectPosition(Layout.HexToPlane(hex).ToWorld(0f)));
            return 3;
        }

        return base.RunAutomationStep(step, runner);
    }

    protected override bool Expect(string[] a, out string actual, out bool ok)
    {
        switch (a[0])
        {
            case "battack-cells":
                // expect:battack-cells:q,r;… is not possible in a script, so: expect:battack-cells:<q,r q,r …> in the stored order
                actual = string.Join(" ", _model.Attack.Cells.Select(c => $"{c.Q},{c.R}"));
                ok = actual == string.Join(":", a.Skip(1));
                return true;
            case "battack-preview":
                actual = string.Join(" | ", _model.Preview());
                ok = actual.Contains(string.Join(":", a.Skip(1)), System.StringComparison.Ordinal);
                return true;
            case "battack-facing":
                actual = _model.Facing.ToString();
                ok = actual == a[1];
                return true;
            default:
                actual = "unknown";
                ok = false;
                return false;
        }
    }
}
