using System;
using System.Collections.Generic;
using System.Globalization;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Map;

public sealed class MapFormatException : Exception
{
    public MapFormatException(int line, string message)
        : base($"Map line {line}: {message}")
    {
        Line = line;
    }

    public int Line { get; }
}

/// <summary>
/// Parses the plain-text <c>.hexmap</c> format (see docs/ARCHITECTURE.md, "Формат карты").
/// Grids are flat-top with odd-q offsets: one text line per row, one token per column, odd columns
/// lie half a hex lower than even ones.
/// <code>
/// // comment
/// @layout flat-top odd-q  required: the only supported layout (pointy-top maps are rejected)
/// @name Прототип
/// @size 12 10            columns rows
/// @zone 1 Север          spawn zone id and display name
/// @base База игрока      display name of the player base
/// . . # B 1 ...          one line per row, one token per column
/// </code>
/// Tokens: <c>.</c> walkable, <c>#</c> blocked, <c>B</c> player base, <c>D</c> deploy zone (roster placement),
/// <c>1</c>..<c>9</c> spawn zone id.
/// </summary>
public static class MapParser
{
    public const string SupportedLayout = "flat-top odd-q";

    public static MapDefinition Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string? layout = null;

        var name = "Unnamed map";
        var baseName = "Base";
        int? columns = null;
        int? rows = null;
        var zoneNames = new Dictionary<int, string>();
        var gridLines = new List<(int Line, string[] Tokens)>();

        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var lineNo = i + 1;
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith('@'))
            {
                var parts = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
                var arg = parts.Length > 1 ? parts[1].Trim() : string.Empty;
                switch (parts[0])
                {
                    case "@layout":
                        layout = string.Join(' ', arg.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                        if (layout != SupportedLayout)
                        {
                            throw new MapFormatException(lineNo, $"Unsupported layout '{arg}'. Only '{SupportedLayout}' is supported.");
                        }

                        break;
                    case "@name":
                        name = arg;
                        break;
                    case "@base":
                        baseName = arg;
                        break;
                    case "@size":
                        {
                            var sz = arg.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                            if (sz.Length != 2 || !TryParsePositive(sz[0], out var c) || !TryParsePositive(sz[1], out var r))
                            {
                                throw new MapFormatException(lineNo, "@size expects two positive integers: columns rows.");
                            }

                            columns = c;
                            rows = r;
                            break;
                        }

                    case "@zone":
                        {
                            var z = arg.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
                            if (z.Length < 1 || !int.TryParse(z[0], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id < 1 || id > 9)
                            {
                                throw new MapFormatException(lineNo, "@zone expects an id 1..9 followed by a name.");
                            }

                            zoneNames[id] = z.Length > 1 ? z[1].Trim() : $"Zone {id}";
                            break;
                        }

                    default:
                        throw new MapFormatException(lineNo, $"Unknown directive '{parts[0]}'.");
                }

                continue;
            }

            gridLines.Add((lineNo, line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
        }

        if (layout is null)
        {
            throw new MapFormatException(1, $"Missing '@layout {SupportedLayout}' directive.");
        }

        if (columns is null || rows is null)
        {
            throw new MapFormatException(lines.Length, "Missing @size directive.");
        }

        if (gridLines.Count != rows)
        {
            throw new MapFormatException(lines.Length, $"Expected {rows} grid rows, found {gridLines.Count}.");
        }

        var grid = new HexGrid(columns.Value, rows.Value);
        var zoneCells = new SortedDictionary<int, List<HexCoord>>();
        var baseCells = new List<HexCoord>();
        var deployCells = new List<HexCoord>();

        for (var row = 0; row < gridLines.Count; row++)
        {
            var (lineNo, tokens) = gridLines[row];
            if (tokens.Length != columns)
            {
                throw new MapFormatException(lineNo, $"Expected {columns} cells in row {row}, found {tokens.Length}.");
            }

            for (var col = 0; col < tokens.Length; col++)
            {
                var coord = HexCoord.FromOffset(col, row);
                var token = tokens[col];
                switch (token)
                {
                    case ".":
                        break;
                    case "#":
                        grid.SetTile(coord, TileType.Blocked);
                        break;
                    case "B":
                        grid.SetTile(coord, TileType.PlayerBase);
                        baseCells.Add(coord);
                        break;
                    case "D":
                        grid.SetTile(coord, TileType.DeployZone);
                        deployCells.Add(coord);
                        break;
                    default:
                        if (token.Length == 1 && token[0] >= '1' && token[0] <= '9')
                        {
                            var zoneId = token[0] - '0';
                            grid.SetTile(coord, TileType.SpawnZone, zoneId);
                            if (!zoneCells.TryGetValue(zoneId, out var list))
                            {
                                list = new List<HexCoord>();
                                zoneCells[zoneId] = list;
                            }

                            list.Add(coord);
                        }
                        else
                        {
                            throw new MapFormatException(lineNo, $"Unknown cell token '{token}' at column {col}.");
                        }

                        break;
                }
            }
        }

        var zones = new List<SpawnZoneDefinition>();
        foreach (var (id, cells) in zoneCells)
        {
            foreach (var cell in cells)
            {
                if (!grid.IsEdge(cell))
                {
                    var o = cell.ToOffset();
                    throw new MapFormatException(gridLines[o.Row].Line, $"Spawn zone {id} cell {cell} is not on the map edge.");
                }
            }

            zones.Add(new SpawnZoneDefinition(id, zoneNames.TryGetValue(id, out var zn) ? zn : $"Zone {id}", cells));
        }

        var playerBase = baseCells.Count > 0 ? new PlayerBaseDefinition(baseName, baseCells) : null;
        return new MapDefinition(name, grid, zones, playerBase, deployCells);
    }

    private static bool TryParsePositive(string s, out int value) =>
        int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value > 0;
}
