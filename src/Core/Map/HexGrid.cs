using System;
using System.Collections.Generic;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Map;

/// <summary>
/// Rectangular odd-q map of <see cref="Columns"/> × <see cref="Rows"/> flat-top hexes.
/// Holds the authoritative tile data. Neighbourhood always comes from cube/axial math,
/// never from how cells are laid out on screen.
/// </summary>
public sealed class HexGrid
{
    private readonly TileState[] _tiles;
    private readonly HexCoord[] _coords;

    public HexGrid(int columns, int rows)
    {
        if (columns <= 0 || rows <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(columns), "Grid dimensions must be positive.");
        }

        Columns = columns;
        Rows = rows;
        _tiles = new TileState[columns * rows];
        _coords = new HexCoord[columns * rows];
        for (var row = 0; row < rows; row++)
        {
            for (var col = 0; col < columns; col++)
            {
                var coord = HexCoord.FromOffset(col, row);
                _coords[Index(col, row)] = coord;
                _tiles[Index(col, row)] = new TileState(coord, TileType.Walkable);
            }
        }
    }

    public int Columns { get; }

    public int Rows { get; }

    public int CellCount => _tiles.Length;

    /// <summary>Incremented on every tile change, so caches (reachability, paths) can detect staleness.</summary>
    public int Version { get; private set; }

    /// <summary>All coordinates in deterministic row-major order.</summary>
    public IReadOnlyList<HexCoord> Coords => _coords;

    public bool Contains(HexCoord coord)
    {
        var o = coord.ToOffset();
        return o.Col >= 0 && o.Col < Columns && o.Row >= 0 && o.Row < Rows;
    }

    public TileState this[HexCoord coord]
    {
        get
        {
            if (!Contains(coord))
            {
                throw new ArgumentOutOfRangeException(nameof(coord), $"{coord} is outside of the {Columns}x{Rows} map.");
            }

            var o = coord.ToOffset();
            return _tiles[Index(o.Col, o.Row)];
        }
    }

    public bool TryGetTile(HexCoord coord, out TileState tile)
    {
        if (!Contains(coord))
        {
            tile = default;
            return false;
        }

        tile = this[coord];
        return true;
    }

    public bool IsWalkable(HexCoord coord) => Contains(coord) && this[coord].IsWalkable;

    /// <summary>True if a real tile stands at the coordinate (inside the workspace and not an empty place).</summary>
    public bool Exists(HexCoord coord) => Contains(coord) && this[coord].Exists;

    public void SetTile(HexCoord coord, TileType type, int zoneId = 0, int moveCost = 1)
    {
        if (!Contains(coord))
        {
            throw new ArgumentOutOfRangeException(nameof(coord), $"{coord} is outside of the map.");
        }

        if (moveCost < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(moveCost), "Move cost must be at least 1.");
        }

        var o = coord.ToOffset();
        _tiles[Index(o.Col, o.Row)] = new TileState(coord, type, type == TileType.SpawnZone ? zoneId : 0, moveCost);
        Version++;
    }

    /// <summary>Places a tile that is an instance of a library tile type (spec part 4 §4.2, §7.1).</summary>
    public void SetTile(HexCoord coord, TileType type, string? tileId, int level, int zoneId = 0)
    {
        if (!Contains(coord))
        {
            throw new ArgumentOutOfRangeException(nameof(coord), $"{coord} is outside of the map.");
        }

        var o = coord.ToOffset();
        _tiles[Index(o.Col, o.Row)] = new TileState(coord, type, type == TileType.SpawnZone ? zoneId : 0, 1, tileId, level);
        Version++;
    }

    /// <summary>In-bounds neighbours in the fixed order of <see cref="HexDirections.All"/>.</summary>
    public IEnumerable<HexCoord> NeighborsInBounds(HexCoord coord)
    {
        foreach (var n in coord.Neighbors())
        {
            if (Contains(n))
            {
                yield return n;
            }
        }
    }

    /// <summary>True if the cell lies on the outer border of the map: a neighbour is outside or an empty place.</summary>
    public bool IsEdge(HexCoord coord)
    {
        if (!Exists(coord))
        {
            return false;
        }

        foreach (var n in coord.Neighbors())
        {
            if (!Exists(n))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Axis-aligned bounds of the whole map on the plane (including hex corners).</summary>
    public (PlaneVector Min, PlaneVector Max) PlaneBounds(HexLayout layout)
    {
        var minX = double.MaxValue;
        var minZ = double.MaxValue;
        var maxX = double.MinValue;
        var maxZ = double.MinValue;
        foreach (var c in _coords)
        {
            for (var i = 0; i < 6; i++)
            {
                var p = layout.Corner(c, i);
                minX = Math.Min(minX, p.X);
                minZ = Math.Min(minZ, p.Z);
                maxX = Math.Max(maxX, p.X);
                maxZ = Math.Max(maxZ, p.Z);
            }
        }

        return (new PlaneVector(minX, minZ), new PlaneVector(maxX, maxZ));
    }

    private int Index(int col, int row) => (row * Columns) + col;
}
