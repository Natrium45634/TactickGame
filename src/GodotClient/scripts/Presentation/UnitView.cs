using Godot;
using HexTactics.Core.Hex;
using HexTactics.Core.Units;

namespace HexTactics.Client.Presentation;

/// <summary>
/// Visual of one unit built from primitive shapes: a cylinder body with a white arrow on its top cap
/// and a flat arrow on the ground in front of it showing the facing; a thin ring marks selection.
/// Purely visual: position and facing come from <see cref="UnitState"/>.
/// </summary>
public partial class UnitView : Node3D
{
    public const float BodyRadius = 0.32f;
    public const float BodyHeight = 0.5f;
    public const float PickRadius = 0.42f;

    private Node3D _facingPivot = null!;
    private MeshInstance3D _selectionRing = null!;
    private StandardMaterial3D _bodyMaterial = null!;
    private float _visualAngle;
    private float _targetAngle;
    private bool _initialized;

    public UnitId UnitId { get; private set; }

    public void Build(UnitState unit)
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

        var pointerMaterial = new StandardMaterial3D
        {
            AlbedoColor = VisualConfig.UnitFacingPointer,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };

        // Arrow painted on the top cap: visible from the 60° camera for all six directions.
        _facingPivot.AddChild(new MeshInstance3D
        {
            Name = "TopPointer",
            Mesh = BuildDart(tip: 0.28f, back: -0.22f, notch: -0.09f, halfWidth: 0.14f),
            MaterialOverride = pointerMaterial,
            Position = new Vector3(0, BodyHeight + 0.004f, 0),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });

        // Flat arrow on the ground in front of the unit, pointing at the neighbouring hex.
        _facingPivot.AddChild(new MeshInstance3D
        {
            Name = "GroundPointer",
            Mesh = BuildDart(tip: 0.86f, back: 0.56f, notch: 0.63f, halfWidth: 0.17f),
            MaterialOverride = pointerMaterial,
            Position = new Vector3(0, 0.012f, 0),
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
    }

    public bool Selected
    {
        get => _selectionRing.Visible;
        set => _selectionRing.Visible = value;
    }

    /// <summary>Updates the visual from the model. The facing turns smoothly; position is set directly.</summary>
    public void Sync(Vector3 position, HexDirection facing, float delta)
    {
        Position = position;
        _targetAngle = Mathf.DegToRad((float)facing.AngleDegrees());
        if (!_initialized)
        {
            _visualAngle = _targetAngle;
            _initialized = true;
        }

        _visualAngle = Mathf.LerpAngle(_visualAngle, _targetAngle, Mathf.Clamp(delta * 14f, 0f, 1f));
        _facingPivot.Rotation = new Vector3(0, _visualAngle, 0);
    }

    /// <summary>Shows a preview facing (e.g. while dragging an order) without changing the model.</summary>
    public void PreviewFacing(HexDirection facing)
    {
        _targetAngle = Mathf.DegToRad((float)facing.AngleDegrees());
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
