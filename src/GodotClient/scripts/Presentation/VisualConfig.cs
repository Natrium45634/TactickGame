using Godot;
using HexTactics.Core.Map;

namespace HexTactics.Client.Presentation;

/// <summary>
/// Tunable visual parameters of the prototype (minimal "default 3D primitives" look).
/// Values are starting points from the spec and may be adjusted after visual tests.
/// </summary>
public static class VisualConfig
{
    /// <summary>World size of one hex side (circumradius). The whole client scales from this.</summary>
    public const float HexSize = 1.0f;

    /// <summary>Thickness of the board slab below ground level.</summary>
    public const float TileThickness = 0.32f;

    /// <summary>Height of blocked hex columns above the ground.</summary>
    public const float BlockedHeight = 0.55f;

    /// <summary>Lift of overlays above the surface they cover (prevents z-fighting).</summary>
    public const float OverlayLift = 0.004f;

    /// <summary>Camera elevation above the map plane. 90° = straight down, 60° = noticeable tilt.</summary>
    public const float CameraElevationDegrees = 60f;

    /// <summary>Relative empty margin around the board when fitting the camera.</summary>
    public const float CameraFitMargin = 0.04f;

    public static readonly Color Background = new(0.141f, 0.149f, 0.169f);

    public static readonly Color TileWalkable = new(0.80f, 0.80f, 0.81f);
    public static readonly Color TileBlocked = new(0.50f, 0.51f, 0.54f);
    public static readonly Color TilePlayerBase = new(0.62f, 0.80f, 0.60f);
    public static readonly Color TileSpawnZone = new(0.86f, 0.79f, 0.64f);
    public static readonly Color TileDeployZone = new(0.76f, 0.86f, 0.74f);
    public static readonly Color TileEdgeLine = new(0.58f, 0.59f, 0.61f);

    /// <summary>Hover fill (soft white). Spec: overlays are always translucent.</summary>
    public static readonly Color HoverFill = new(1f, 1f, 1f, 0.22f);

    /// <summary>Soft green: a valid target (placement / waypoint). Opacity 0.12–0.20 per spec.</summary>
    public static readonly Color FillValid = new(0.20f, 0.78f, 0.36f, 0.15f);

    /// <summary>Soft red: blocked, occupied or unreachable.</summary>
    public static readonly Color FillInvalid = new(0.90f, 0.22f, 0.20f, 0.15f);

    /// <summary>Neutral marker for the source hex of an order drag (rotate-in-place area).</summary>
    public static readonly Color FillNeutral = new(0.95f, 0.85f, 0.35f, 0.24f);

    /// <summary>Drag arrow colours: valid target, rotate-only gesture, no-op, invalid target.</summary>
    public static readonly Color ArrowValid = new(0.98f, 0.97f, 0.92f, 0.82f);
    public static readonly Color ArrowRotate = new(1.0f, 0.86f, 0.38f, 0.85f);
    public static readonly Color ArrowIdle = new(0.75f, 0.77f, 0.80f, 0.55f);
    public static readonly Color ArrowInvalid = new(0.95f, 0.36f, 0.30f, 0.80f);

    /// <summary>Committed order chains (straight thick arrows between order points).</summary>
    /// <summary>Personal order chains are WHITE (spec part 2 §3.1); command chains will be cyan.</summary>
    public static readonly Color ChainSelected = new(1.0f, 1.0f, 1.0f, 0.72f);
    public static readonly Color ChainOther = new(1.0f, 1.0f, 1.0f, 0.30f);
    public static readonly Color WaypointSelectedUnit = new(1.0f, 1.0f, 1.0f, 1.0f);
    public static readonly Color WaypointOtherUnit = new(0.80f, 0.82f, 0.86f, 0.85f);
    public static readonly Color WaypointSelectedRing = new(0.15f, 0.17f, 0.22f, 0.95f);
    public static readonly Color WaypointRim = new(0.13f, 0.14f, 0.18f, 0.95f);

    /// <summary>Thin lines of computed routes (around obstacles).</summary>
    public static readonly Color PathSelected = new(0.14f, 0.16f, 0.22f, 0.88f);
    public static readonly Color PathOther = new(0.14f, 0.16f, 0.22f, 0.38f);
    public static readonly Color PathPreview = new(0.30f, 0.95f, 0.50f, 0.95f);

    public static readonly Color UnitPlayer = new(0.30f, 0.50f, 0.80f);
    public static readonly Color UnitEnemy = new(0.78f, 0.34f, 0.30f);
    public static readonly Color UnitFacingPointer = new(0.98f, 0.98f, 0.99f);
    public static readonly Color FacingIndicatorOutline = new(0.10f, 0.11f, 0.14f, 0.85f);
    public static readonly Color SelectionRing = new(1.0f, 0.90f, 0.55f, 0.85f);

    public static Color TileColor(TileType type) => type switch
    {
        TileType.Blocked => TileBlocked,
        TileType.PlayerBase => TilePlayerBase,
        TileType.SpawnZone => TileSpawnZone,
        TileType.DeployZone => TileDeployZone,
        _ => TileWalkable,
    };

    public static float TileTopHeight(TileType type) => type == TileType.Blocked ? BlockedHeight : 0f;

    private static bool? _srgbPipeline;

    /// <summary>
    /// Converts an authored (sRGB) colour for use as a vertex / instance colour. The Compatibility
    /// renderer used by the prototype shades in sRGB space, so colours pass through unchanged there;
    /// the Forward+/Mobile renderers shade in linear space and need the conversion.
    /// </summary>
    public static Color ToRenderColor(Color srgb)
    {
        _srgbPipeline ??= RenderingServer.GetCurrentRenderingMethod() == "gl_compatibility";
        if (_srgbPipeline.Value)
        {
            return srgb;
        }

        var linear = srgb.SrgbToLinear();
        linear.A = srgb.A;
        return linear;
    }
}
