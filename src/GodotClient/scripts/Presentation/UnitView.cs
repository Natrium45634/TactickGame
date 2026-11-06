using Godot;
using HexTactics.Core.Hex;
using HexTactics.Core.Units;

namespace HexTactics.Client.Presentation;

/// <summary>
/// Visual of one unit built from primitive shapes: a cylinder body and — as the facing indicator — a
/// small white arrow at the edge of its hex in the direction it looks; a thin ring marks selection.
/// Purely visual: position and facing come from <see cref="UnitState"/>.
/// </summary>
public partial class UnitView : Node3D
{
    public const float BodyRadius = 0.28f;
    public const float BodyHeight = 0.5f;
    public const float PickRadius = 0.42f;

    // Edge indicator geometry (local +X = facing); the hex edge is at the apothem ≈ 0.866.
    private const float IndicatorTip = 0.80f;
    private const float IndicatorBack = 0.58f;
    private const float IndicatorNotch = 0.64f;
    private const float IndicatorHalfWidth = 0.15f;

    private Node3D _facingPivot = null!;
    private MeshInstance3D _selectionRing = null!;
    private Label3D _blockedMarker = null!;
    private Label3D _pendingMarker = null!;
    private StandardMaterial3D _bodyMaterial = null!;
    private float _visualAngle;
    private float _targetAngle;
    private bool _initialized;

    public UnitId UnitId { get; private set; }

    /// <param name="caption">Name shown above the unit (roster operatives), or null.</param>
    public void Build(UnitState unit, string? caption = null)
    {
        UnitId = unit.Id;
        Name = $"Unit{unit.Id.Value}";

        _bodyMaterial = new StandardMaterial3D
        {
            AlbedoColor = unit.Side == HexTactics.Core.Units.Side.Player ? VisualConfig.UnitPlayer : VisualConfig.UnitEnemy,
            Roughness = 0.75f,
        };

        AddChild(new MeshInstance3D
        {
            Name = "Body",
            Mesh = new CylinderMesh { TopRadius = BodyRadius, BottomRadius = BodyRadius, Height = BodyHeight, RadialSegments = 32 },
            MaterialOverride = _bodyMaterial,
            Position = new Vector3(0, BodyHeight * 0.5f, 0),
        });

        _facingPivot = new Node3D { Name = "Facing" };
        AddChild(_facingPivot);

        // Facing indicator (spec part 2 §3.2): a small white arrow lying on the ground right at the
        // edge of the unit's hex it looks at, with a thin dark outline for readability. Always visible,
        // much smaller than the wide movement arrows.
        _facingPivot.AddChild(new MeshInstance3D
        {
            Name = "EdgeIndicatorOutline",
            Mesh = BuildDart(tip: IndicatorTip + 0.05f, back: IndicatorBack - 0.045f, notch: IndicatorNotch - 0.03f, halfWidth: IndicatorHalfWidth + 0.05f),
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = VisualConfig.FacingIndicatorOutline,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                RenderPriority = 4,
            },
            Position = new Vector3(0, 0.010f, 0),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
        _facingPivot.AddChild(new MeshInstance3D
        {
            Name = "EdgeIndicator",
            Mesh = BuildDart(tip: IndicatorTip, back: IndicatorBack, notch: IndicatorNotch, halfWidth: IndicatorHalfWidth),
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = VisualConfig.UnitFacingPointer,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                RenderPriority = 5,
            },
            Position = new Vector3(0, 0.014f, 0),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });

        _selectionRing = new MeshInstance3D
        {
            Name = "SelectionRing",
            Mesh = new TorusMesh { InnerRadius = 0.53f, OuterRadius = 0.575f, Rings = 64, RingSegments = 8 },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = VisualConfig.SelectionRing,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            },
            Scale = new Vector3(1, 0.25f, 1),
            Position = new Vector3(0, 0.02f, 0),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
        };
        AddChild(_selectionRing);

        _blockedMarker = new Label3D
        {
            Name = "Blocked",
            Text = "!",
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            NoDepthTest = true,
            FontSize = 64,
            OutlineSize = 14,
            PixelSize = 0.006f,
            Modulate = new Color(1f, 0.38f, 0.32f),
            OutlineModulate = new Color(0.12f, 0.1f, 0.1f),
            Position = new Vector3(0, BodyHeight + 0.62f, 0),
            Visible = false,
            RenderPriority = 10,
            OutlineRenderPriority = 9,
        };
        AddChild(_blockedMarker);

        _pendingMarker = new Label3D
        {
            Name = "PendingFacing",
            Text = "?",
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            NoDepthTest = true,
            FontSize = 64,
            OutlineSize = 14,
            PixelSize = 0.006f,
            Modulate = new Color(1f, 0.85f, 0.35f),
            OutlineModulate = new Color(0.12f, 0.1f, 0.1f),
            Position = new Vector3(0, BodyHeight + 0.62f, 0),
            Visible = false,
            RenderPriority = 10,
            OutlineRenderPriority = 9,
        };
        AddChild(_pendingMarker);

        if (caption is not null)
        {
            AddChild(new Label3D
            {
                Name = "Caption",
                Text = caption,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                FontSize = 44,
                OutlineSize = 12,
                PixelSize = 0.0058f,
                Modulate = new Color(0.97f, 0.98f, 1f),
                OutlineModulate = new Color(0.10f, 0.11f, 0.14f),
                Position = new Vector3(0, BodyHeight + 0.22f, 0),
                RenderPriority = 6,
                OutlineRenderPriority = 5,
            });
        }
    }

    /// <summary>Shows a red "!" above a unit that stopped because no path exists.</summary>
    public bool Blocked
    {
        get => _blockedMarker.Visible;
        set => _blockedMarker.Visible = value;
    }

    public bool Selected
    {
        get => _selectionRing.Visible;
        set => _selectionRing.Visible = value;
    }

    /// <summary>
    /// Updates the visual from the model. The facing turns smoothly; position is set directly. A null facing (unit placed
    /// from the roster, waiting for its facing) hides the edge indicator and shows a yellow "?".
    /// </summary>
    public void Sync(Vector3 position, HexDirection? facingOrNull, float delta)
    {
        Position = position;
        _facingPivot.Visible = facingOrNull is not null;
        _pendingMarker.Visible = facingOrNull is null;
        if (facingOrNull is not { } facing)
        {
            return;
        }

        _targetAngle = Mathf.DegToRad((float)facing.AngleDegrees());
        if (!_initialized)
        {
            _visualAngle = _targetAngle;
            _initialized = true;
        }

        _visualAngle = Mathf.LerpAngle(_visualAngle, _targetAngle, Mathf.Clamp(delta * 14f, 0f, 1f));
        _facingPivot.Rotation = new Vector3(0, _visualAngle, 0);
    }

    /// <summary>
    /// Flat arrowhead ("dart") in the XZ plane pointing along local +X. The notch at the back makes the
    /// direction unambiguous, unlike a plain triangle.
    /// </summary>
    private static ArrayMesh BuildDart(float tip, float back, float notch, float halfWidth)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        st.SetNormal(Vector3.Up);
        var t = new Vector3(tip, 0, 0);
        var n = new Vector3(notch, 0, 0);
        var l = new Vector3(back, 0, halfWidth);
        var r = new Vector3(back, 0, -halfWidth);
        st.AddVertex(t);
        st.AddVertex(l);
        st.AddVertex(n);
        st.AddVertex(t);
        st.AddVertex(n);
        st.AddVertex(r);
        return st.Commit();
    }
}
