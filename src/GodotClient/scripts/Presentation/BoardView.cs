using Godot;
using HexTactics.Core.Hex;
using HexTactics.Core.Map;

namespace HexTactics.Client.Presentation;

/// <summary>
/// Visual layer 0: the board itself. Built once from <see cref="HexGrid"/> data; it never decides
/// anything about passability — it only shows what the model says.
/// </summary>
public partial class BoardView : Node3D
{
    private MeshInstance3D? _mesh;
    private Node3D? _icons;
    private ShaderMaterial? _material;
    private int _builtVersion = -1;

    /// <summary>Rebuilds the mesh if the terrain changed (debug obstacle editing).</summary>
    public void SyncTerrain(HexGrid grid, HexLayout layout)
    {
        if (grid.Version != _builtVersion)
        {
            Build(grid, layout);
        }
    }

    public void Build(HexGrid grid, HexLayout layout)
    {
        _mesh?.QueueFree();
        _builtVersion = grid.Version;
        _material ??= new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/tile.gdshader") };
        _material.SetShaderParameter("line_color", VisualConfig.TileEdgeLine);

        _mesh = new MeshInstance3D
        {
            Name = "BoardMesh",
            Mesh = HexMeshBuilder.BuildBoard(grid, layout),
            MaterialOverride = _material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.On,
        };
        AddChild(_mesh);

        // Icons of tile types (burning, healing…) lie flat on their hexes; units and arrows draw over them.
        _icons?.QueueFree();
        _icons = new Node3D { Name = "TileIcons" };
        AddChild(_icons);
        foreach (var c in grid.Coords)
        {
            var tile = grid[c];
            if (!tile.Exists || TilePalette.Current.IconOf(tile) is not { } icon)
            {
                continue;
            }

            _icons.AddChild(new Label3D
            {
                Text = icon,
                FontSize = 64,
                PixelSize = 0.0075f,
                Modulate = new Color(0.12f, 0.13f, 0.16f, 0.72f),
                OutlineSize = 0,
                Position = layout.HexToPlane(c).ToWorld(TilePalette.Current.HeightOf(tile) + 0.004f),
                RotationDegrees = new Vector3(-90, 0, 0),
                DoubleSided = false,
            });
        }
    }
}
