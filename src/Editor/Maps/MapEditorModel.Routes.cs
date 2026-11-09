using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HexTactics.Content.Kinds;
using HexTactics.Content.Model;
using HexTactics.Content.Registry;
using HexTactics.Content.Runtime;
using HexTactics.Core.Hex;
using HexTactics.Core.Map;

namespace HexTactics.Editor.Maps;

/// <summary>What a click in the «Маршруты» tool did.</summary>
public enum RouteClick
{
    Nothing = 0,
    DraftStarted = 1,
    RouteSelected = 2,
    NotABase = 3,
}

/// <summary>
/// «Маршруты врагов» of the map editor (spec part 7 §4.2): LMB on an enemy base starts a draft; the cursor moving over hexes adds
/// each next hex (a fast move is filled with the hexes of the straight line between, never a jump); a closed, missing or outside
/// hex stops the draft there and names the reason; entering a player base finishes the route as one undoable step. Backspace
/// removes the last visit, Esc / RMB cancel only the draft. Repeated visits and crossings are allowed.
/// </summary>
public sealed partial class MapEditorModel
{
    private readonly List<HexCoord> _draft = new();
    private string? _redraw;

    /// <summary>The «Маршруты» tool is on: clicks and the cursor draw routes instead of placing hexes.</summary>
    public bool RouteMode { get; private set; }

    /// <summary>Id of the route shown in the card, or null.</summary>
    public string? SelectedRoute { get; private set; }

    /// <summary>Visits of the route being drawn (empty — no draft).</summary>
    public IReadOnlyList<HexCoord> RouteDraft => _draft;

    public bool IsDrafting => _draft.Count > 0;

    /// <summary>Why the draft stopped (the last move went to a hex a route cannot use), with the hex; null when fine.</summary>
    public string? DraftError { get; private set; }

    public HexCoord? DraftErrorCell { get; private set; }

    /// <summary>The route the draft will replace (keeping its id, name and the waves that use it), or null for a new one.</summary>
    public string? RedrawTarget => _redraw;

    public void SetRouteMode(bool on)
    {
        RouteMode = on;
        CancelRouteDraft();
        if (on)
        {
            Select(null);
        }

        Changed?.Invoke();
    }

    /// <summary>LMB in the route tool: an enemy base starts a draft; a hex of a route selects it (again — the next route there).</summary>
    public RouteClick RouteClickAt(HexCoord c)
    {
        if (IsDrafting)
        {
            return RouteClick.Nothing;
        }

        var map = BuildRuntime();
        if (map.BaseAt(c) is { Side: BaseSide.Enemy } source)
        {
            _draft.Clear();
            _draft.Add(c);
            DraftSource = source.Number;
            DraftError = null;
            DraftErrorCell = null;
            Changed?.Invoke();
            return RouteClick.DraftStarted;
        }

        var through = RoutesThrough(c);
        if (through.Count > 0)
        {
            var at = SelectedRoute is { } current ? through.FindIndex(r => r.Id == current) : -1;
            SelectRoute(through[(at + 1) % through.Count].Id);
            return RouteClick.RouteSelected;
        }

        return RouteClick.NotABase;
    }

    /// <summary>Number of the enemy base the draft starts from.</summary>
    public int DraftSource { get; private set; }

    /// <summary>
    /// The cursor is over <paramref name="c"/>: the draft goes on along the straight line of hexes to it, one neighbour at a time,
    /// up to the first hex a route cannot use; a player base hex finishes the route. Returns true when the route was committed.
    /// </summary>
    public bool RouteHover(HexCoord c)
    {
        if (!IsDrafting || c == _draft[^1])
        {
            return false;
        }

        var map = BuildRuntime();
        var line = _draft[^1].LineTo(c);
        for (var i = 1; i < line.Count; i++)
        {
            var next = line[i];
            if (CellProblem(map, next) is { } problem)
            {
                DraftError = problem;
                DraftErrorCell = next;
                Changed?.Invoke();
                return false;
            }

            _draft.Add(next);
            DraftError = null;
            DraftErrorCell = null;
            if (map.BaseAt(next) is { Side: BaseSide.Player } target)
            {
                CommitDraft(target.Number);
                return true;
            }
        }

        Changed?.Invoke();
        return false;
    }

    /// <summary>Backspace / «Удалить последний сегмент»: the last visit of the draft (never its start).</summary>
    public bool RemoveLastVisit()
    {
        if (_draft.Count < 2)
        {
            return false;
        }

        _draft.RemoveAt(_draft.Count - 1);
        DraftError = null;
        DraftErrorCell = null;
        Changed?.Invoke();
        return true;
    }

    /// <summary>Esc / RMB: only the draft goes; saved routes stay.</summary>
    public void CancelRouteDraft()
    {
        if (_draft.Count == 0 && _redraw is null && DraftError is null)
        {
            return;
        }

        _draft.Clear();
        _redraw = null;
        DraftError = null;
        DraftErrorCell = null;
        Changed?.Invoke();
    }

    /// <summary>«Перерисовать»: the next draft replaces this route's visits; its id, name and the waves that use it stay.</summary>
    public void RedrawRoute(string id)
    {
        if (Map.FindRoute(id) is not null)
        {
            CancelRouteDraft();
            _redraw = id;
            SelectedRoute = id;
            Changed?.Invoke();
        }
    }

    public void SelectRoute(string? id)
    {
        SelectedRoute = id is not null && Map.FindRoute(id) is not null ? id : null;
        Changed?.Invoke();
    }

    public string? RenameRoute(string id, string name)
    {
        if (name.Trim().Length == 0)
        {
            return "у маршрута должно быть название";
        }

        Apply($"Название маршрута «{name.Trim()}»", m => m.FindRoute(id)!.Name = name.Trim());
        return null;
    }

    public void SetRouteEnabled(string id, bool on) => Apply(on ? "Маршрут включён" : "Маршрут выключен", m => m.FindRoute(id)!.Enabled = on);

    /// <summary>A copy with a new id and «(копия)» in its name, selected.</summary>
    public string? CopyRoute(string id)
    {
        if (Map.FindRoute(id) is not { } route)
        {
            return null;
        }

        var copy = route.Clone();
        copy.Id = NewRouteId();
        copy.Name = $"{route.Name} (копия)";
        Apply($"Копия маршрута «{route.Name}»", m => m.Routes.Add(copy.Clone()));
        SelectRoute(copy.Id);
        return copy.Id;
    }

    public void DeleteRoute(string id)
    {
        if (Map.FindRoute(id) is { } route)
        {
            Apply($"Удалить маршрут «{route.Name}»", m => m.Routes.RemoveAll(r => r.Id == id));
            if (SelectedRoute == id)
            {
                SelectedRoute = null;
            }

            Changed?.Invoke();
        }
    }

    public void SetTileBase(HexCoord c, int? number) => EditTile(c, "номер базы", t => t.Base = number);

    public void SetBaseTitle(string side, int id, string name) => Apply($"Название базы {id}", m =>
    {
        m.Bases.RemoveAll(b => b.Side == side && b.Id == id);
        if (name.Trim().Length > 0)
        {
            m.Bases.Add(new MapBaseNameData { Side = side, Id = id, Name = name.Trim() });
        }
    });

    /// <summary>Routes that visit the hex, in the order of the map's list.</summary>
    public List<MapRouteData> RoutesThrough(HexCoord c)
    {
        var o = c.ToOffset();
        return Map.Routes.Where(r => r.Cells.Contains((o.Col, o.Row))).ToList();
    }

    /// <summary>Problems of one route against the current tiles (the same check as the game), or all routes.</summary>
    public IReadOnlyList<RouteProblem> RouteProblems(string id)
    {
        if (Map.FindRoute(id) is not { } route)
        {
            return Array.Empty<RouteProblem>();
        }

        var map = BuildRuntime();
        return RouteCheck.Check(map, new EnemyRoute(route.Id, route.Name, route.Source, route.Target, route.Coords().ToList(), route.Enabled));
    }

    /// <summary>«Проверить маршруты»: one line per route — «Маршрут 1: в порядке» or its first problem.</summary>
    public IReadOnlyList<string> CheckRoutes() => Map.Routes
        .Select(r => RouteProblems(r.Id) is { Count: > 0 } p ? $"✗ «{r.Name}»: {p[0].Message}" + (p.Count > 1 ? $" (и ещё {p.Count - 1})" : string.Empty) : $"✓ «{r.Name}»: {RouteSummary(r)}")
        .ToList();

    /// <summary>«База врага 1 → База игрока 2 · 12 гексов · повторных посещений: 2».</summary>
    public string RouteSummary(MapRouteData r)
    {
        var repeats = r.Cells.Count - r.Cells.Distinct().Count();
        return $"{Map.BaseTitle(MapBaseNameData.Enemy, r.Source)} → {Map.BaseTitle(MapBaseNameData.Player, r.Target)} · {r.Cells.Count} гекс."
            + (repeats > 0 ? $" · повторных посещений: {repeats}" : string.Empty) + (r.Enabled ? string.Empty : " · выключен");
    }

    private void CommitDraft(int target)
    {
        var cells = _draft.Select(c => (c.ToOffset().Col, c.ToOffset().Row)).ToList();
        var source = DraftSource;
        if (_redraw is { } id && Map.FindRoute(id) is { } existing)
        {
            Apply($"Перерисовать маршрут «{existing.Name}»", m =>
            {
                var r = m.FindRoute(id)!;
                r.Source = source;
                r.Target = target;
                r.Cells.Clear();
                r.Cells.AddRange(cells);
            });
        }
        else
        {
            id = NewRouteId();
            var name = "Маршрут " + id["route".Length..];
            Apply($"Новый маршрут «{name}»", m =>
            {
                var r = new MapRouteData { Id = id, Name = name, Source = source, Target = target };
                r.Cells.AddRange(cells);
                m.Routes.Add(r);
            });
        }

        _draft.Clear();
        _redraw = null;
        SelectedRoute = id;
        Changed?.Invoke();
    }

    private string NewRouteId()
    {
        for (var n = 1; ; n++)
        {
            var id = "route" + n.ToString(CultureInfo.InvariantCulture);
            if (Map.FindRoute(id) is null)
            {
                return id;
            }
        }
    }

    /// <summary>Why a route cannot use the hex (outside, no hex, closed), from the same data as movement; null — it can.</summary>
    private string? CellProblem(MapDefinition map, HexCoord c)
    {
        var o = c.ToOffset();
        if (!InWorkspace(c))
        {
            return $"({o.Col},{o.Row}) — за краем рабочей области";
        }

        if (!map.Grid.Exists(c))
        {
            return $"на ({o.Col},{o.Row}) нет гекса — маршрут идёт только по гексам карты";
        }

        return map.Grid.IsWalkable(c) ? null : $"гекс ({o.Col},{o.Row}) непроходим — маршрут через него не проложить";
    }

    /// <summary>The working map as the game builds it (bases, routes, walkability from the tile types).</summary>
    private MapDefinition BuildRuntime() => RuntimeFactory.BuildMap(Item, Library, new IssueList());
}
