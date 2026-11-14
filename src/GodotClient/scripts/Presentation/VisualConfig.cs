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

    /// <summary>Live arrow over an enemy unit: reserved future attack order (not executed yet).</summary>
    public static readonly Color ArrowAttack = new(1.0f, 0.55f, 0.20f, 0.85f);

    /// <summary>Personal order chains are WHITE (spec part 2 §3.1); command chains are cyan.</summary>
    public static readonly Color ChainSelected = new(1.0f, 1.0f, 1.0f, 0.72f);
    public static readonly Color ChainOther = new(1.0f, 1.0f, 1.0f, 0.30f);
    public static readonly Color WaypointSelectedUnit = new(1.0f, 1.0f, 1.0f, 1.0f);
    public static readonly Color WaypointOtherUnit = new(0.80f, 0.82f, 0.86f, 0.85f);
    public static readonly Color WaypointSelectedRing = new(0.15f, 0.17f, 0.22f, 0.95f);
    public static readonly Color WaypointRim = new(0.13f, 0.14f, 0.18f, 0.95f);

    /// <summary>Draft (not yet confirmed) arrows and waypoint rims; the confirmed route of the same unit is ghosted.</summary>
    public static readonly Color DraftChain = new(1.0f, 1.0f, 1.0f, 0.85f);
    public static readonly Color DraftRim = new(0.95f, 0.66f, 0.18f, 1.0f);
    public static readonly Color ChainGhost = new(1.0f, 1.0f, 1.0f, 0.16f);

    /// <summary>Stored final facing of a confirmed order (small white arrow at the edge of the last ТД).</summary>
    public static readonly Color FinalFacingMark = new(1.0f, 1.0f, 1.0f, 0.95f);

    /// <summary>
    /// Command orders are CYAN (spec part 2 §3.1, §6): live arrows from every member, КТД markers, companion slots,
    /// and the rings of the members being collected.
    /// </summary>
    public static readonly Color ArrowCommand = new(0.30f, 0.85f, 1.0f, 0.85f);
    public static readonly Color ArrowCommandCompanion = new(0.30f, 0.85f, 1.0f, 0.60f);
    public static readonly Color ChainCommand = new(0.30f, 0.85f, 1.0f, 0.55f);
    public static readonly Color ChainCommandSelected = new(0.30f, 0.85f, 1.0f, 0.80f);
    public static readonly Color CommandMarker = new(0.62f, 0.93f, 1.0f, 1.0f);
    public static readonly Color CommandRim = new(0.05f, 0.33f, 0.45f, 0.95f);
    public static readonly Color CommandSlot = new(0.30f, 0.85f, 1.0f, 0.85f);
    public static readonly Color MemberRing = new(0.30f, 0.85f, 1.0f, 0.95f);
    /// <summary>Captain's diamond: white (lit) so it stays readable on top of its own cyan command arrow.</summary>
    public static readonly Color CaptainMark = new(0.98f, 0.98f, 1.0f, 1.0f);

    /// <summary>Facing-sector preview (mini arrow at the edge of the source hex).</summary>
    public static readonly Color FacingPreview = new(1.0f, 1.0f, 1.0f, 0.9f);

    /// <summary>Thin lines of computed routes (around obstacles).</summary>
    public static readonly Color PathSelected = new(0.14f, 0.16f, 0.22f, 0.88f);
    public static readonly Color PathOther = new(0.14f, 0.16f, 0.22f, 0.38f);
    public static readonly Color PathPreview = new(0.30f, 0.95f, 0.50f, 0.95f);

    public static readonly Color UnitPlayer = new(0.30f, 0.50f, 0.80f);
    public static readonly Color UnitEnemy = new(0.78f, 0.34f, 0.30f);
    public static readonly Color UnitFacingPointer = new(0.98f, 0.98f, 0.99f);
    public static readonly Color FacingIndicatorOutline = new(0.10f, 0.11f, 0.14f, 0.85f);
    public static readonly Color SelectionRing = new(1.0f, 0.90f, 0.55f, 0.85f);

    /// <summary>
    /// Skills (spec part 3 §5.2, §6.4): the planned hit area is a soft grey fill (α 0.18–0.28, never opaque, a separate
    /// layer from the green/red movement fills); the aiming arrow is red; a fired skill flashes briefly.
    /// </summary>
    public static readonly Color SkillAreaAim = new(0.16f, 0.17f, 0.21f, 0.28f);
    public static readonly Color SkillAreaPlanned = new(0.16f, 0.17f, 0.21f, 0.2f);
    public static readonly Color ArrowSkill = new(0.92f, 0.20f, 0.18f, 0.88f);
    public static readonly Color SkillFlash = new(1.0f, 0.42f, 0.25f, 0.55f);
    public static readonly Color SkillBadge = new(0.86f, 0.16f, 0.14f, 1.0f);

    /// <summary>Translucent body of a roster card held over the map (spec part 2 §5.2).</summary>
    public static readonly Color GhostValid = new(0.30f, 0.82f, 0.42f, 0.55f);
    public static readonly Color GhostInvalid = new(0.92f, 0.30f, 0.26f, 0.45f);

    /// <summary>Placement fills while a roster card is dragged: stronger than the order fills so the allowed cells read at once.</summary>
    public static readonly Color FillPlacementAllowed = new(0.18f, 0.80f, 0.34f, 0.42f);
    public static readonly Color FillPlacementDenied = new(0.90f, 0.22f, 0.20f, 0.12f);

    /// <summary>Exploration: the exit area (spec part 7 §6.3 «маркер выхода виден»).</summary>
    public static readonly Color FillExit = new(0.16f, 0.62f, 0.90f, 0.45f);

    /// <summary>Exploration: the entry area, where the squad comes in.</summary>
    public static readonly Color FillEntry = new(0.30f, 0.80f, 0.40f, 0.28f);

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
