using System.Collections.Generic;
using Godot;
using HexTactics.Core.Hex;
using HexTactics.Core.Map;

namespace HexTactics.Client.Presentation;

/// <summary>Procedural meshes for hex tiles and flat hex overlays.</summary>
public static class HexMeshBuilder
{
    /// <summary>UV.x value on faces that must never show edge lines.</summary>
    private const float NoLine = 10f;

    /// <summary>
    /// Builds a single mesh with one hexagonal prism per cell (one draw call for the whole board).
    /// Adjacent prisms share edges exactly, so there are no gaps between tiles.
    /// </summary>
    public static ArrayMesh BuildBoard(HexGrid grid, HexLayout layout)
    {
        var verts = new List<Vector3>();
        var normals = new List<Vector3>();
        var colors = new List<Color>();
        var uvs = new List<Vector2>();
        var indices = new List<int>();

        var apothem = (float)layout.Apothem;
        foreach (var coord in grid.Coords)
        {
            var tile = grid[coord];
            var top = VisualConfig.TileTopHeight(tile.Type);
            var bottom = -VisualConfig.TileThickness;
            var color = VisualConfig.ToRenderColor(VisualConfig.TileColor(tile.Type));
            var sideColor = VisualConfig.ToRenderColor(VisualConfig.TileColor(tile.Type).Darkened(0.14f));
            var center = layout.HexToPlane(coord);

            // Top face: triangle fan, centre UV.x = apothem, rim UV.x = 0 → linear distance to border.
            var baseIndex = verts.Count;
            AddVertex(center.ToWorld(top), Vector3.Up, color, new Vector2(apothem, 0));
            for (var i = 0; i < 6; i++)
            {
                AddVertex(layout.Corner(coord, i).ToWorld(top), Vector3.Up, color, Vector2.Zero);
            }

            for (var i = 0; i < 6; i++)
            {
                // Corners run counter-clockwise on screen (flat-top layout), Godot front faces are
                // clockwise when seen from above: emit (centre, next corner, corner).
                indices.Add(baseIndex);
                indices.Add(baseIndex + 1 + ((i + 1) % 6));
                indices.Add(baseIndex + 1 + i);
            }

            // Side faces (hidden ones between neighbours are fully enclosed and never visible).
            for (var i = 0; i < 6; i++)
            {
                var a = layout.Corner(coord, i);
                var b = layout.Corner(coord, (i + 1) % 6);
                var mid = PlaneVector.Lerp(a, b, 0.5) - center;
                var n = new Vector3((float)mid.X, 0, (float)mid.Z).Normalized();
                var s = verts.Count;
                AddVertex(b.ToWorld(top), n, sideColor, new Vector2(NoLine, 0));
                AddVertex(a.ToWorld(top), n, sideColor, new Vector2(NoLine, 0));
                AddVertex(a.ToWorld(bottom), n, sideColor, new Vector2(NoLine, 0));
                AddVertex(b.ToWorld(bottom), n, sideColor, new Vector2(NoLine, 0));
                // Outward-facing quad for counter-clockwise corner order.
                indices.Add(s);
                indices.Add(s + 2);
                indices.Add(s + 1);
                indices.Add(s);
                indices.Add(s + 3);
                indices.Add(s + 2);
            }
        }

        return Commit(verts, normals, colors, uvs, indices);

        void AddVertex(Vector3 v, Vector3 n, Color c, Vector2 uv)
        {
            verts.Add(v);
            normals.Add(n);
            colors.Add(c);
            uvs.Add(uv);
        }
    }

    /// <summary>Flat hexagon centred at the origin, facing up, slightly inset so neighbouring fills do not touch.</summary>
    public static ArrayMesh BuildFlatHex(HexLayout layout, float inset = 0.985f)
    {
        var verts = new List<Vector3> { Vector3.Zero };
        var normals = new List<Vector3> { Vector3.Up };
        var colors = new List<Color> { Colors.White };
        var uvs = new List<Vector2> { Vector2.Zero };
        var indices = new List<int>();
        for (var i = 0; i < 6; i++)
        {
            var c = layout.CornerOffset(i) * inset;
            verts.Add(new Vector3((float)c.X, 0, (float)c.Z));
            normals.Add(Vector3.Up);
            colors.Add(Colors.White);
            uvs.Add(Vector2.Zero);
        }

        for (var i = 0; i < 6; i++)
        {
            indices.Add(0);
            indices.Add(1 + ((i + 1) % 6));
            indices.Add(1 + i);
        }

        return Commit(verts, normals, colors, uvs, indices);
    }

    private static ArrayMesh Commit(List<Vector3> verts, List<Vector3> normals, List<Color> colors, List<Vector2> uvs, List<int> indices)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = normals.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = colors.ToArray();
        arrays[(int)Mesh.ArrayType.TexUV] = uvs.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    /// <summary>Hex polygon outline points on the plane (for debug / UI drawing).</summary>
    public static Vector3[] HexOutline(HexLayout layout, HexCoord coord, float height)
    {
        var pts = new Vector3[6];
        for (var i = 0; i < 6; i++)
        {
            pts[i] = layout.Corner(coord, i).ToWorld(height);
        }

        return pts;
    }
}
