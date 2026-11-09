using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Content.Kinds;
using HexTactics.Content.Library;
using HexTactics.Content.Model;
using HexTactics.Content.Registry;
using HexTactics.Core.Hex;
using HexTactics.Editor.History;

namespace HexTactics.Editor.Maps;

/// <summary>What a click on the canvas did.</summary>
public enum MapClick
{
    Nothing = 0,

    /// <summary>A real tile was created on an empty place and selected.</summary>
    Created = 1,

    /// <summary>An existing tile was selected.</summary>
    Selected = 2,

    /// <summary>In the deletion mode: the tile was marked or unmarked for deletion.</summary>
    ToggledForDeletion = 3,

    /// <summary>Outside of the workspace.</summary>
    Outside = 4,
}

/// <summary>What a batch deletion or a resize will remove: tiles and linked objects (shown before confirming).</summary>
public sealed record MapRemovalImpact(int Tiles, IReadOnlyList<string> Links)
{
    public bool NeedsConfirmation => Links.Count > 0;
}

/// <summary>
/// Model of the map editor (spec part 4 §4): a working copy of a map definition, the selected hex, the type of new hexes,
/// the two-stage red deletion tool and every change as an undoable command. Pure C# — the Godot canvas and panels only
/// display it and forward clicks.
/// </summary>
public sealed class MapEditorModel
{
    private readonly HashSet<HexCoord> _deleteSelection = new();

    public MapEditorModel(ContentLibrary library, ContentItem item)
    {
        if (item.Data is not MapData)
        {
            throw new ArgumentException("Not a map.", nameof(item));
        }

        Library = library;
        Item = item.Clone();
        History.Changed += () => Changed?.Invoke();
    }

    public ContentLibrary Library { get; }

    /// <summary>The edited working copy (never the library's own object).</summary>
    public ContentItem Item { get; private set; }

    public MapData Map => (MapData)Item.Data;

    public CommandHistory History { get; } = new();

    /// <summary>Raised after any change of the map, the selection or the deletion tool.</summary>
    public event Action? Changed;

    /// <summary>The hex whose settings are shown in the left panel (null = map settings).</summary>
    public HexCoord? Selected { get; private set; }

    /// <summary>Tile type of hexes created by clicking an empty place.</summary>
    public string Brush { get; set; } = "core.tile.ground";

    /// <summary>The red tool is active: clicks mark hexes instead of creating/selecting them.</summary>
    public bool DeleteMode { get; private set; }

    public IReadOnlyCollection<HexCoord> DeleteSelection => _deleteSelection;

    /// <summary>Unsaved: changed since the last save, or never saved at all.</summary>
    public bool IsDirty => History.IsDirty || Item.Revision == 0;

    public bool IsBuiltIn => Item.IsBuiltIn;

    public bool InWorkspace(HexCoord c)
    {
        var o = c.ToOffset();
        return o.Col >= 0 && o.Row >= 0 && o.Col < Map.Columns && o.Row < Map.Rows;
    }

    public MapTileData? TileAt(HexCoord c)
    {
        var o = c.ToOffset();
        return Map.TileAt(o.Col, o.Row);
    }

    /// <summary>
    /// LMB on the canvas (spec part 4 §4.1): an empty place gets exactly one real tile of the <see cref="Brush"/> type and
    /// it is selected; an existing tile is selected. In the deletion mode the tile is marked/unmarked instead.
    /// </summary>
    public MapClick Click(HexCoord c)
    {
        if (!InWorkspace(c))
        {
            return MapClick.Outside;
        }

        var tile = TileAt(c);
        if (DeleteMode)
        {
            if (tile is null)
            {
                return MapClick.Nothing;
            }

            if (!_deleteSelection.Remove(c))
            {
                _deleteSelection.Add(c);
            }

            Changed?.Invoke();
            return MapClick.ToggledForDeletion;
        }

        if (tile is not null)
        {
            Select(c);
            return MapClick.Selected;
        }

        var o = c.ToOffset();
        var brush = Brush;
        var tileName = Library.Find(ContentTypeRegistry.Tile, brush)?.Name ?? brush;
        Apply($"Добавить гекс ({o.Col},{o.Row}) «{tileName}»", m => m.Tiles.Add(new MapTileData { Col = o.Col, Row = o.Row, Tile = brush }));
        Select(c);
        return MapClick.Created;
    }

    public void Select(HexCoord? c)
    {
        Selected = c is { } h && TileAt(h) is not null ? h : null;
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ hex settings

    public void SetTileType(HexCoord c, string tileId) => EditTile(c, "тип", t => t.Tile = tileId);

    public void SetTileLevel(HexCoord c, int? level) => EditTile(c, "уровень", t => t.Level = level);

    public void SetTileZone(HexCoord c, int? zone) => EditTile(c, "зона появления", t => t.Zone = zone);

    public void SetTileNote(HexCoord c, string? note) => EditTile(c, "комментарий", t => t.Note = string.IsNullOrWhiteSpace(note) ? null : note);

    /// <summary>Puts or removes a test point of the route check on a tile (labels A, B, C … in order of creation).</summary>
    public void SetTestPoint(HexCoord c, bool on)
    {
        var o = c.ToOffset();
        if (TileAt(c) is null || (Map.PointAt(o.Col, o.Row) is not null) == on)
        {
            return;
        }

        if (on)
        {
            var label = NextLabel();
            Apply($"Тестовая точка «{label}» на ({o.Col},{o.Row})", m => m.TestPoints.Add(new MapPointData { Col = o.Col, Row = o.Row, Label = label }));
        }
        else
        {
            Apply($"Убрать тестовую точку ({o.Col},{o.Row})", m => m.TestPoints.RemoveAll(p => p.Col == o.Col && p.Row == o.Row));
        }
    }

    // ------------------------------------------------------------------ map settings

    public void SetName(string name) => ApplyItem("Название карты", i => i.Name = name.Trim());

    public void SetDescription(string text) => ApplyItem("Описание карты", i => i.Description = text);

    public void SetBaseName(string name) => Apply("Название базы", m => m.BaseName = name);

    /// <summary>Combat rulesets the map can play with (spec part 6 §9.5): saved sets without errors, archived ones only if chosen.</summary>
    public IReadOnlyList<(string Id, string Name)> Rulesets() => Library.Items(ContentTypeRegistry.CombatRuleset)
        .Where(r => (!r.HasErrors && !r.Archived) || r.Id == Map.EffectiveRuleset)
        .Select(r => (r.Id, r.Name + (r.IsBuiltIn ? string.Empty : " (свой)")))
        .ToList();

    /// <summary>Spec part 7 §8, D-170: units hit enemies in their base attack area on their own on this map.</summary>
    public void SetAutoAttacks(bool on) => Apply(on ? "Автоатака юнитов включена" : "Автоатака юнитов выключена", m => m.AutoAttacks = on);

    /// <summary>Assigns the map's combat ruleset (the compatible one stays the meaning of an empty value).</summary>
    public string? SetRuleset(string rulesetId)
    {
        if (Library.Find(ContentTypeRegistry.CombatRuleset, rulesetId) is not { } ruleset)
        {
            return $"правила боя «{rulesetId}» не найдены";
        }

        Apply($"Правила боя: «{ruleset.Name}»", m => m.CombatRuleset = rulesetId);
        return null;
    }

    public void SetZoneName(int zone, string name) => Apply($"Название зоны {zone}", m =>
    {
        var z = m.Zones.Find(x => x.Id == zone);
        if (z is null)
        {
            m.Zones.Add(new MapZoneData { Id = zone, Name = name });
            m.Zones.Sort((a, b) => a.Id.CompareTo(b.Id));
        }
        else
        {
            z.Name = name;
        }
    });

    /// <summary>Tiles and test points that a resize to <paramref name="columns"/> × <paramref name="rows"/> removes.</summary>
    public MapRemovalImpact ResizeImpact(int columns, int rows)
    {
        var cells = new List<HexCoord>();
        foreach (var t in Map.Tiles)
        {
            if (t.Col >= columns || t.Row >= rows)
            {
                cells.Add(t.Coord);
            }
        }

        return Impact(cells);
    }

    /// <summary>Changes the workspace size; tiles (and their test points) outside of it go in the same undo step.</summary>
    public void Resize(int columns, int rows)
    {
        if (columns == Map.Columns && rows == Map.Rows)
        {
            return;
        }

        Apply($"Размер области {columns}×{rows}", m =>
        {
            m.Columns = columns;
            m.Rows = rows;
            m.Tiles.RemoveAll(t => t.Col >= columns || t.Row >= rows);
            m.TestPoints.RemoveAll(p => p.Col >= columns || p.Row >= rows);
        });
        Select(Selected);
    }

    // ------------------------------------------------------------------ red deletion tool (spec part 4 §4.3)

    /// <summary>
    /// The red button. First press: enter the selection mode (nothing is deleted). Second press: returns true — the caller
    /// deletes the marked tiles with <see cref="ExecuteDelete"/> (after a confirmation if <see cref="DeleteImpactNow"/> lists
    /// links). With nothing marked the second press just leaves the mode.
    /// </summary>
    public bool PressDeleteButton()
    {
        if (!DeleteMode)
        {
            DeleteMode = true;
            _deleteSelection.Clear();
            Changed?.Invoke();
            return false;
        }

        if (_deleteSelection.Count == 0)
        {
            CancelDelete();
            return false;
        }

        return true;
    }

    public MapRemovalImpact DeleteImpactNow() => Impact(_deleteSelection);

    /// <summary>Deletes every marked tile (and the test points on them) as one atomic, undoable step; leaves the mode.</summary>
    public void ExecuteDelete()
    {
        var cells = new HashSet<HexCoord>(_deleteSelection);
        DeleteMode = false;
        _deleteSelection.Clear();
        if (cells.Count > 0)
        {
            Apply($"Удалить гексы: {cells.Count}", m =>
            {
                m.Tiles.RemoveAll(t => cells.Contains(t.Coord));
                m.TestPoints.RemoveAll(p => cells.Contains(p.Coord));
            });
        }

        Select(Selected);
    }

    /// <summary>Esc / «Отмена»: leaves the deletion mode without deleting anything.</summary>
    public void CancelDelete()
    {
        DeleteMode = false;
        _deleteSelection.Clear();
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ history, validation, saving

    public bool Undo()
    {
        var done = History.Undo();
        AfterHistoryMove();
        return done;
    }

    public bool Redo()
    {
        var done = History.Redo();
        AfterHistoryMove();
        return done;
    }

    public IReadOnlyList<ContentIssue> Validate() => Library.Validate(Item);

    /// <summary>
    /// Validates and saves. A built-in map is never overwritten: it is saved as a copy with a new id and the editor
    /// continues with the copy.
    /// </summary>
    public ContentOperationResult Save()
    {
        var target = Item;
        if (Item.IsBuiltIn)
        {
            target = Item.CopyAs(Library.NewId(ContentTypeRegistry.Map), ContentOrigin.User, $"{Item.Name} (копия)");
            target.Revision = 0;
        }

        var result = Library.Save(target);
        if (result.Ok)
        {
            Item = result.Item!.Clone();
            History.MarkSaved();
            Library.User?.DeleteDraft(Item.Kind.Folder, Item.Id);
        }

        Changed?.Invoke();
        return result;
    }

    /// <summary>JSON of the working copy (for drafts and test runs).</summary>
    public string Snapshot() => ContentSerializer.Write(Item);

    /// <summary>Replaces the working copy by a restored draft (the change is undoable and unsaved).</summary>
    public void LoadDraft(ContentItem draft)
    {
        var data = (MapData)draft.Data.Clone();
        var name = draft.Name;
        var description = draft.Description;
        ApplyFull("Восстановить черновик", (m, i) => (data, name, description));
    }

    private void AfterHistoryMove()
    {
        _deleteSelection.Clear();
        DeleteMode = false;
        Select(Selected);
    }

    private MapRemovalImpact Impact(IEnumerable<HexCoord> cells)
    {
        var links = new List<string>();
        var count = 0;
        foreach (var c in cells)
        {
            count++;
            var o = c.ToOffset();
            if (Map.PointAt(o.Col, o.Row) is { } p)
            {
                links.Add($"тестовая точка «{p.Label}» на ({o.Col},{o.Row}) будет снята");
            }
        }

        return new MapRemovalImpact(count, links);
    }

    private string NextLabel()
    {
        for (var ch = 'A'; ch <= 'Z'; ch++)
        {
            var label = ch.ToString();
            if (!Map.TestPoints.Exists(p => p.Label == label))
            {
                return label;
            }
        }

        return (Map.TestPoints.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private void EditTile(HexCoord c, string what, Action<MapTileData> change)
    {
        var o = c.ToOffset();
        if (TileAt(c) is null)
        {
            return;
        }

        Apply($"Гекс ({o.Col},{o.Row}): {what}", m => change(m.TileAt(o.Col, o.Row)!));
    }

    private void Apply(string description, Action<MapData> mutate) =>
        ApplyFull(description, (m, i) =>
        {
            var copy = (MapData)m.Clone();
            mutate(copy);
            return (copy, i.Name, i.Description);
        });

    private void ApplyItem(string description, Action<ContentItem> mutate) =>
        ApplyFull(description, (m, i) =>
        {
            var probe = i.Clone();
            mutate(probe);
            return ((MapData)m.Clone(), probe.Name, probe.Description);
        });

    /// <summary>Every change is a snapshot command (before/after of the whole map — maps are small).</summary>
    private void ApplyFull(string description, Func<MapData, ContentItem, (MapData Data, string Name, string Description)> change)
    {
        var before = ((MapData)Map.Clone(), Item.Name, Item.Description);
        var after = change(Map, Item);
        if (after.Data.ToJson().ToJsonString() == before.Item1.ToJson().ToJsonString() && after.Name == before.Name && after.Description == before.Description)
        {
            return; // nothing changed: no history entry
        }

        History.Execute(new DelegateCommand(description, () => Restore(after), () => Restore(before)));
    }

    private void Restore((MapData Data, string Name, string Description) state)
    {
        Item.Data = state.Data.Clone();
        Item.Name = state.Name;
        Item.Description = state.Description;
    }
}
