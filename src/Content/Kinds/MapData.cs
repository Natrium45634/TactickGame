using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using HexTactics.Content.Model;
using HexTactics.Content.Registry;
using HexTactics.Core.Hex;

namespace HexTactics.Content.Kinds;

/// <summary>
/// A real tile of a map (spec part 4 §4.1–4.2): its geometric position (odd-q <c>col,row</c>, never a list index), the
/// library tile type and allowed local overrides. An instance never copies the logic of its type.
/// </summary>
public sealed class MapTileData
{
    public int Col { get; set; }

    public int Row { get; set; }

    /// <summary>Stable id of the tile type (<c>kind = tile</c>).</summary>
    public string Tile { get; set; } = "core.tile.ground";

    /// <summary>Local relief level override (null = the level of the type).</summary>
    public int? Level { get; set; }

    /// <summary>Spawn zone number for tiles of the «край / зона появления» category.</summary>
    public int? Zone { get; set; }

    /// <summary>Base number (1…9) for tiles of the «база врага» / «база игрока» categories; null — base 1 (spec part 7 §4.1).</summary>
    public int? Base { get; set; }

    /// <summary>Developer comment.</summary>
    public string? Note { get; set; }

    public HexCoord Coord => HexCoord.FromOffset(Col, Row);

    public MapTileData Clone() => new() { Col = Col, Row = Row, Tile = Tile, Level = Level, Zone = Zone, Base = Base, Note = Note };
}

/// <summary>
/// A test point of the map (spec part 4 §4.4): the validator checks that consecutive test points are connected by a
/// passable route. A point lives on a real tile; deleting that tile removes the point in the same undoable operation.
/// </summary>
public sealed class MapPointData
{
    public int Col { get; set; }

    public int Row { get; set; }

    public string Label { get; set; } = "A";

    public HexCoord Coord => HexCoord.FromOffset(Col, Row);

    public MapPointData Clone() => new() { Col = Col, Row = Row, Label = Label };
}

/// <summary>Display name of a numbered base of the map (spec part 7 §4.1); unnamed bases get «База врага 2» etc.</summary>
public sealed class MapBaseNameData
{
    public const string Enemy = "enemy";

    public const string Player = "player";

    public string Side { get; set; } = Enemy;

    public int Id { get; set; } = 1;

    public string Name { get; set; } = string.Empty;

    public MapBaseNameData Clone() => new() { Side = Side, Id = Id, Name = Name };
}

/// <summary>
/// An enemy route of the map (spec part 7 §4.1, §10 <c>EnemyRouteDefinition</c>): a stable id (scenarios and waves refer to it), a
/// name, the enemy base it starts from, the player base it ends on and the ordered hex visits (repeats allowed).
/// </summary>
public sealed class MapRouteData
{
    public const int MaxCells = 400;

    public string Id { get; set; } = "route1";

    public string Name { get; set; } = "Маршрут 1";

    public int Source { get; set; } = 1;

    public int Target { get; set; } = 1;

    public bool Enabled { get; set; } = true;

    /// <summary>Visits in order, odd-q offset coordinates.</summary>
    public List<(int Col, int Row)> Cells { get; } = new();

    public IEnumerable<HexCoord> Coords()
    {
        foreach (var (col, row) in Cells)
        {
            yield return HexCoord.FromOffset(col, row);
        }
    }

    public MapRouteData Clone()
    {
        var copy = new MapRouteData { Id = Id, Name = Name, Source = Source, Target = Target, Enabled = Enabled };
        copy.Cells.AddRange(Cells);
        return copy;
    }
}

/// <summary>Display name of a spawn zone of the map.</summary>
public sealed class MapZoneData
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public MapZoneData Clone() => new() { Id = Id, Name = Name };
}

/// <summary>
/// A map definition (spec part 4 §4, <c>MapDefinition</c>): the size of the workspace and the list of real tiles —
/// places without a tile are empty (impassable, not targets). The structure is not tied to exactly two bases or one spawn.
/// </summary>
public sealed class MapData : IContentData
{
    public const string SupportedLayout = "flat-top odd-q";

    public string Layout { get; set; } = SupportedLayout;

    /// <summary>Workspace width in columns (default 12, spec part 4 §4.1).</summary>
    public int Columns { get; set; } = 12;

    /// <summary>Workspace height in rows (default 10).</summary>
    public int Rows { get; set; } = 10;

    public string BaseName { get; set; } = "База игрока";

    public List<MapZoneData> Zones { get; } = new();

    public List<MapTileData> Tiles { get; } = new();

    /// <summary>Optional test points for the route check (none = no route check).</summary>
    public List<MapPointData> TestPoints { get; } = new();

    /// <summary>Names of numbered bases (optional, since v0.35.0).</summary>
    public List<MapBaseNameData> Bases { get; } = new();

    /// <summary>Enemy routes (optional, since v0.35.0; spec part 7 §4, §9.2 — the geometry belongs to the map).</summary>
    public List<MapRouteData> Routes { get; } = new();

    /// <summary>«База врага 2» unless the author named it; base 1 of the player is <see cref="BaseName"/>.</summary>
    public string BaseTitle(string side, int id)
    {
        foreach (var b in Bases)
        {
            if (b.Side == side && b.Id == id && b.Name.Length > 0)
            {
                return b.Name;
            }
        }

        return side == MapBaseNameData.Player ? (id == 1 ? BaseName : $"База игрока {id}") : $"База врага {id}";
    }

    public MapRouteData? FindRoute(string id) => Routes.Find(r => r.Id == id);

    /// <summary>
    /// Combat rules of the map (spec part 6 §9.5, §10.3): a <c>combat_ruleset</c> id; empty — the compatible
    /// <see cref="CombatRulesetData.Legacy"/>, so maps made before part 6 keep their results. New maps get the standard set.
    /// </summary>
    public string CombatRuleset { get; set; } = string.Empty;

    /// <summary>The ruleset the map plays with.</summary>
    public string EffectiveRuleset => CombatRuleset.Length == 0 ? CombatRulesetData.Legacy : CombatRuleset;

    /// <summary>
    /// Spec part 7 §8: units hit enemies in their base attack area on their own. Optional since v0.32.0 and written only when on:
    /// maps without it (all made before) keep their matches unchanged until the author turns it on (D-170).
    /// </summary>
    public bool AutoAttacks { get; set; }

    public MapTileData? TileAt(int col, int row) => Tiles.Find(t => t.Col == col && t.Row == row);

    public MapPointData? PointAt(int col, int row) => TestPoints.Find(p => p.Col == col && p.Row == row);

    public string ZoneName(int id)
    {
        foreach (var z in Zones)
        {
            if (z.Id == id)
            {
                return z.Name;
            }
        }

        return $"Зона {id}";
    }

    public JsonObject ToJson()
    {
        var zones = new JsonArray();
        foreach (var z in Zones)
        {
            zones.Add(new JsonObject { ["id"] = z.Id, ["name"] = z.Name });
        }

        var tiles = new JsonArray();
        foreach (var t in Tiles)
        {
            var tile = new JsonObject { ["col"] = t.Col, ["row"] = t.Row, ["tile"] = t.Tile };
            if (t.Level is { } level)
            {
                tile["level"] = level;
            }

            if (t.Zone is { } zone)
            {
                tile["zone"] = zone;
            }

            if (t.Base is { } baseNumber)
            {
                tile["base"] = baseNumber;
            }

            if (!string.IsNullOrEmpty(t.Note))
            {
                tile["note"] = t.Note;
            }

            tiles.Add(tile);
        }

        var result = new JsonObject
        {
            ["layout"] = Layout,
            ["columns"] = Columns,
            ["rows"] = Rows,
            ["baseName"] = BaseName,
            ["zones"] = zones,
            ["tiles"] = tiles,
        };
        if (TestPoints.Count > 0)
        {
            var points = new JsonArray();
            foreach (var p in TestPoints)
            {
                points.Add(new JsonObject { ["col"] = p.Col, ["row"] = p.Row, ["label"] = p.Label });
            }

            result["testPoints"] = points;
        }

        if (AutoAttacks)
        {
            result["autoAttacks"] = true;
        }

        if (Bases.Count > 0)
        {
            var bases = new JsonArray();
            foreach (var b in Bases)
            {
                bases.Add(new JsonObject { ["side"] = b.Side, ["id"] = b.Id, ["name"] = b.Name });
            }

            result["bases"] = bases;
        }

        if (Routes.Count > 0)
        {
            var routes = new JsonArray();
            foreach (var r in Routes)
            {
                var cells = new JsonArray();
                foreach (var (col, row) in r.Cells)
                {
                    cells.Add(new JsonArray(col, row));
                }

                routes.Add(new JsonObject
                {
                    ["id"] = r.Id, ["name"] = r.Name, ["source"] = r.Source, ["target"] = r.Target, ["enabled"] = r.Enabled, ["cells"] = cells,
                });
            }

            result["routes"] = routes;
        }

        if (CombatRuleset.Length > 0)
        {
            result["combatRuleset"] = CombatRuleset;
        }

        return result;
    }

    public static MapData Read(JsonObject obj, IssueList issues)
    {
        var data = new MapData
        {
            Layout = JsonFields.Text(obj, "layout", SupportedLayout, issues, string.Empty),
            Columns = JsonFields.Integer(obj, "columns", 12, issues, string.Empty),
            Rows = JsonFields.Integer(obj, "rows", 10, issues, string.Empty),
            BaseName = JsonFields.Text(obj, "baseName", "База игрока", issues, string.Empty),
            CombatRuleset = JsonFields.Text(obj, "combatRuleset", string.Empty, issues, string.Empty),
            AutoAttacks = JsonFields.Flag(obj, "autoAttacks", false, issues, string.Empty),
        };
        if (JsonFields.Array(obj, "zones", issues, string.Empty) is { } zones)
        {
            for (var i = 0; i < zones.Count; i++)
            {
                if (zones[i] is JsonObject z)
                {
                    data.Zones.Add(new MapZoneData
                    {
                        Id = JsonFields.Integer(z, "id", 1, issues, $"zones[{i}]"),
                        Name = JsonFields.Text(z, "name", string.Empty, issues, $"zones[{i}]"),
                    });
                }
                else
                {
                    issues.Error($"zones[{i}]", "зона должна быть объектом");
                }
            }
        }

        if (JsonFields.Array(obj, "tiles", issues, string.Empty) is { } tiles)
        {
            for (var i = 0; i < tiles.Count; i++)
            {
                var path = $"tiles[{i}]";
                if (tiles[i] is not JsonObject t)
                {
                    issues.Error(path, "гекс должен быть объектом");
                    continue;
                }

                data.Tiles.Add(new MapTileData
                {
                    Col = JsonFields.Integer(t, "col", 0, issues, path),
                    Row = JsonFields.Integer(t, "row", 0, issues, path),
                    Tile = JsonFields.Text(t, "tile", string.Empty, issues, path),
                    Level = t.ContainsKey("level") ? JsonFields.Integer(t, "level", 0, issues, path) : null,
                    Zone = t.ContainsKey("zone") ? JsonFields.Integer(t, "zone", 1, issues, path) : null,
                    Base = t.ContainsKey("base") ? JsonFields.Integer(t, "base", 1, issues, path) : null,
                    Note = t.ContainsKey("note") ? JsonFields.Text(t, "note", string.Empty, issues, path) : null,
                });
            }
        }

        if (obj["bases"] is JsonArray baseNames)
        {
            for (var i = 0; i < baseNames.Count; i++)
            {
                if (baseNames[i] is JsonObject b)
                {
                    data.Bases.Add(new MapBaseNameData
                    {
                        Side = JsonFields.Text(b, "side", MapBaseNameData.Enemy, issues, $"bases[{i}]"),
                        Id = JsonFields.Integer(b, "id", 1, issues, $"bases[{i}]"),
                        Name = JsonFields.Text(b, "name", string.Empty, issues, $"bases[{i}]"),
                    });
                }
                else
                {
                    issues.Error($"bases[{i}]", "база должна быть объектом { side, id, name }");
                }
            }
        }

        if (obj["routes"] is JsonArray routes)
        {
            for (var i = 0; i < routes.Count; i++)
            {
                var path = $"routes[{i}]";
                if (routes[i] is not JsonObject r)
                {
                    issues.Error(path, "маршрут должен быть объектом");
                    continue;
                }

                var route = new MapRouteData
                {
                    Id = JsonFields.Text(r, "id", string.Empty, issues, path),
                    Name = JsonFields.Text(r, "name", string.Empty, issues, path),
                    Source = JsonFields.Integer(r, "source", 1, issues, path),
                    Target = JsonFields.Integer(r, "target", 1, issues, path),
                    Enabled = JsonFields.Flag(r, "enabled", true, issues, path),
                };
                if (r["cells"] is JsonArray cells)
                {
                    foreach (var cell in cells)
                    {
                        if (cell is JsonArray { Count: 2 } pair && pair[0] is JsonValue c && pair[1] is JsonValue w && c.TryGetValue<int>(out var col) && w.TryGetValue<int>(out var row))
                        {
                            route.Cells.Add((col, row));
                        }
                        else
                        {
                            issues.Error(path + ".cells", "гекс маршрута — пара [col, row]");
                        }
                    }
                }

                data.Routes.Add(route);
            }
        }

        if (JsonFields.Array(obj, "testPoints", issues, string.Empty) is { } points)
        {
            for (var i = 0; i < points.Count; i++)
            {
                var path = $"testPoints[{i}]";
                if (points[i] is not JsonObject p)
                {
                    issues.Error(path, "тестовая точка должна быть объектом");
                    continue;
                }

                data.TestPoints.Add(new MapPointData
                {
                    Col = JsonFields.Integer(p, "col", 0, issues, path),
                    Row = JsonFields.Integer(p, "row", 0, issues, path),
                    Label = JsonFields.Text(p, "label", "A", issues, path),
                });
            }
        }

        return data;
    }

    public void RemapReferences(IReadOnlyDictionary<ContentRef, string> newIds)
    {
        foreach (var t in Tiles)
        {
            if (newIds.TryGetValue(new ContentRef("tile", t.Tile), out var id))
            {
                t.Tile = id;
            }
        }

        if (newIds.TryGetValue(new ContentRef(ContentTypeRegistry.CombatRuleset, CombatRuleset), out var ruleset))
        {
            CombatRuleset = ruleset;
        }
    }

    public IEnumerable<ContentRef> References()
    {
        var seen = new HashSet<string>();
        foreach (var t in Tiles)
        {
            if (seen.Add(t.Tile))
            {
                yield return new ContentRef("tile", t.Tile);
            }
        }

        if (CombatRuleset.Length > 0)
        {
            yield return new ContentRef(ContentTypeRegistry.CombatRuleset, CombatRuleset);
        }
    }

    public void Validate(ValidationContext context)
    {
        var issues = context.Issues;
        if (Layout != SupportedLayout)
        {
            issues.Error("layout", $"поддерживается только раскладка «{SupportedLayout}»");
        }

        if (Columns < 1 || Rows < 1 || Columns > context.Limits.MaxMapSide || Rows > context.Limits.MaxMapSide)
        {
            issues.Error("columns", $"размер рабочей области — от 1 до {context.Limits.MaxMapSide} по каждой стороне");
        }

        if (Tiles.Count == 0)
        {
            issues.Warning("tiles", "на карте нет ни одного гекса");
        }

        var seen = new HashSet<(int, int)>();
        for (var i = 0; i < Tiles.Count; i++)
        {
            var t = Tiles[i];
            var path = $"tiles[{i}]";
            if (t.Col < 0 || t.Row < 0 || t.Col >= Columns || t.Row >= Rows)
            {
                issues.Error(path, $"гекс ({t.Col},{t.Row}) вне рабочей области {Columns}×{Rows}");
            }

            if (!seen.Add((t.Col, t.Row)))
            {
                issues.Error(path, $"два гекса на одной координате ({t.Col},{t.Row})");
            }

            if (!ContentIds.IsValid(t.Tile))
            {
                issues.Error(path + ".tile", $"неверная ссылка на тип гекса «{t.Tile}»");
            }

            if (t.Level is { } level && (level < 0 || level > 3))
            {
                issues.Error(path + ".level", "уровень — от 0 до 3");
            }

            if (t.Zone is { } zone && (zone < 1 || zone > 9))
            {
                issues.Error(path + ".zone", "номер зоны появления — от 1 до 9");
            }

            if (t.Base is { } baseNumber && (baseNumber < 1 || baseNumber > 9))
            {
                issues.Error(path + ".base", "номер базы — от 1 до 9");
            }

            if (t.Note is { Length: > 0 } note && note.Length > context.Limits.MaxTextLength)
            {
                issues.Error(path + ".note", "слишком длинный комментарий");
            }
        }

        for (var i = 0; i < TestPoints.Count; i++)
        {
            var p = TestPoints[i];
            if (TileAt(p.Col, p.Row) is null)
            {
                issues.Error($"testPoints[{i}]", $"тестовая точка «{p.Label}» ({p.Col},{p.Row}) не на гексе карты");
            }
        }

        var routeIds = new HashSet<string>();
        for (var i = 0; i < Routes.Count; i++)
        {
            var r = Routes[i];
            var path = $"routes[{i}]";
            if (!ContentIds.IsValid(r.Id) || !routeIds.Add(r.Id))
            {
                issues.Error(path + ".id", $"ID маршрута «{r.Id}» пустой, неверный или повторяется — ID маршрутов карты разные");
            }

            if (r.Name.Trim().Length == 0)
            {
                issues.Error(path + ".name", "у маршрута нет названия");
            }

            if (r.Source is < 1 or > 9 || r.Target is < 1 or > 9)
            {
                issues.Error(path, "номера баз маршрута — от 1 до 9");
            }

            if (r.Cells.Count > MapRouteData.MaxCells)
            {
                issues.Error(path + ".cells", $"в маршруте не больше {MapRouteData.MaxCells} посещений гексов");
            }

            for (var k = 1; k < r.Cells.Count; k++)
            {
                var a = HexCoord.FromOffset(r.Cells[k - 1].Col, r.Cells[k - 1].Row);
                var b = HexCoord.FromOffset(r.Cells[k].Col, r.Cells[k].Row);
                if (a.DistanceTo(b) != 1)
                {
                    issues.Error(path + ".cells", $"маршрут «{r.Name}», шаг {k}: ({r.Cells[k - 1].Col},{r.Cells[k - 1].Row}) → ({r.Cells[k].Col},{r.Cells[k].Row}) — не соседние гексы");
                    break;
                }
            }
        }

        for (var i = 0; i < Bases.Count; i++)
        {
            if (Bases[i].Side is not (MapBaseNameData.Enemy or MapBaseNameData.Player) || Bases[i].Id is < 1 or > 9)
            {
                issues.Error($"bases[{i}]", "база — сторона enemy или player и номер от 1 до 9");
            }
        }

        var zoneIds = new HashSet<int>();
        for (var i = 0; i < Zones.Count; i++)
        {
            if (Zones[i].Id < 1 || Zones[i].Id > 9 || !zoneIds.Add(Zones[i].Id))
            {
                issues.Error($"zones[{i}]", "номера зон появления — разные, от 1 до 9");
            }
        }
    }

    public string Summary() => string.Create(CultureInfo.InvariantCulture, $"{Columns}×{Rows}, гексов: {Tiles.Count}");

    public IContentData Clone()
    {
        var copy = new MapData { Layout = Layout, Columns = Columns, Rows = Rows, BaseName = BaseName, CombatRuleset = CombatRuleset, AutoAttacks = AutoAttacks };
        foreach (var z in Zones)
        {
            copy.Zones.Add(z.Clone());
        }

        foreach (var t in Tiles)
        {
            copy.Tiles.Add(t.Clone());
        }

        foreach (var p in TestPoints)
        {
            copy.TestPoints.Add(p.Clone());
        }

        foreach (var b in Bases)
        {
            copy.Bases.Add(b.Clone());
        }

        foreach (var r in Routes)
        {
            copy.Routes.Add(r.Clone());
        }

        return copy;
    }
}
