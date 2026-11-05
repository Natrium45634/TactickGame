using System.Collections.Generic;
using Godot;
using HexTactics.Core.Hex;
using HexTactics.Core.Map;

namespace HexTactics.Client.Presentation;

/// <summary>
/// A translucent per-cell fill drawn on top of the board (one MultiMesh instance per cell, one draw call).
/// Used for reachability (green / red / neutral) and for the hover marker. Fills never replace
/// the tile material: they are blended over it with a small lift to avoid z-fighting.
/// </summary>
public partial class HexFillLayer : Node3D
{
    private readonly Dictionary<HexCoord, int> _index = new();
    private MultiMesh? _multiMesh;

    /// <summary>Render priority among transparent objects; higher layers draw later.</summary>
    public int RenderPriority { get; set; }

    public float ExtraLift { get; set; }

    public void Build(HexGrid grid, HexLayout layout)
    {
        foreach (var child in GetChildren())
        {
            child.QueueFree();
        }

        _index.Clear();
        var material = new ShaderMaterial
        {
            Shader = GD.Load<Shader>("res://shaders/overlay.gdshader"),
            RenderPriority = RenderPriority,
        };

        _multiMesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            Mesh = HexMeshBuilder.BuildFlatHex(layout),
            InstanceCount = grid.CellCount,
        };

        for (var i = 0; i < grid.Coords.Count; i++)
        {
            var coord = grid.Coords[i];
            _index[coord] = i;
            var y = VisualConfig.TileTopHeight(grid[coord].Type) + VisualConfig.OverlayLift + ExtraLift;
            _multiMesh.SetInstanceTransform(i, new Transform3D(Basis.Identity, layout.HexToPlane(coord).ToWorld(y)));
            _multiMesh.SetInstanceColor(i, Colors.Transparent);
        }

        AddChild(new MultiMeshInstance3D
        {
            Name = "Fills",
            Multimesh = _multiMesh,
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
    }

    /// <summary>Sets the fill colour of one cell (sRGB, alpha = opacity). Transparent hides it.</summary>
    public void SetFill(HexCoord coord, Color color)
    {
        if (_multiMesh is null || !_index.TryGetValue(coord, out var i))
        {
            return;
        }

        _multiMesh.SetInstanceColor(i, VisualConfig.ToRenderColor(color));
    }

    public void ClearAll()
    {
        if (_multiMesh is null)
        {
            return;
        }

        for (var i = 0; i < _multiMesh.InstanceCount; i++)
        {
            _multiMesh.SetInstanceColor(i, Colors.Transparent);
        }
    }
}
