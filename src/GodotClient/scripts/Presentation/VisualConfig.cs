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
    public static readonly Color TilePlayerBase = new(0.68f, 0.76f, 0.86f);
    public static readonly Color TileSpawnZone = new(0.86f, 0.79f, 0.64f);
    public static readonly Color TileEdgeLine = new(0.58f, 0.59f, 0.61f);

    /// <summary>Hover fill (soft white). Spec: overlays are always translucent.</summary>
    public static readonly Color HoverFill = new(1f, 1f, 1f, 0.22f);

    public static Color TileColor(TileType type) => type switch
    {
        TileType.Blocked => TileBlocked,
        TileType.PlayerBase => TilePlayerBase,
        TileType.SpawnZone => TileSpawnZone,
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
