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
    }
}
