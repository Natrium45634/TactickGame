using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using HexTactics.Client.DeveloperMode.SharedInspector;
using HexTactics.Client.Presentation;
using HexTactics.Client.UI;
using HexTactics.Content.Kinds;
using HexTactics.Content.Library;
using HexTactics.Content.Registry;
using HexTactics.Content.Runtime;
using HexTactics.Core.Hex;
using HexTactics.Core.Map;
using HexTactics.Editor.Tiles;
using TileData = HexTactics.Content.Kinds.TileData;

namespace HexTactics.Client.DeveloperMode.Editors.Tile;

/// <summary>
/// The tile type editor (spec part 4 §7): name/description, category, passability, «can be a ТД», relief level, colour and
/// icon; effects with triggers (while on the hex / on entering / on leaving) and a side filter, built from the effect
/// registry; a preview of the type among ordinary ground, the rules in words and a test run on the prototype map.
/// </summary>
public partial class TileEditor : DefinitionEditorShell
{
    private static readonly HexCoord Center = HexCoord.FromOffset(3, 3);

    private TileEditorModel _model = null!;
    private BoardView _board = null!;
    private HexGrid _grid = null!;
    private string _newEffectType = "periodic";

    public override string Kind => ContentTypeRegistry.Tile;

    protected override string IdPrefix => "tile.";

    protected override string KindTitle => "Тип гекса";

    protected override string? TestButtonText => "▶ Тест гекса";

    public TileEditorModel TileModel => _model;

    public void Configure(ContentLibrary library, ContentItem item, ContentItem? draft = null)
    {
        _model = new TileEditorModel(library, item);
        if (draft is not null)
        {
            _model.LoadDraft(draft);
        }

        Attach(library, _model);
    }

    // ------------------------------------------------------------------ canvas

    protected override void BuildCanvas()
    {
        _grid = new HexGrid(7, 7);
        _board = new BoardView { Name = "PreviewBoard" };
        AddChild(_board);
    }

    protected override void UpdateCanvas()
    {
        if (_board is null)
        {
            return;
        }

        // The edited type (unsaved values) on the centre and its ring, ordinary ground around it for contrast.
        var visuals = new Dictionary<string, TileVisual>();
        foreach (var t in Library.Items(ContentTypeRegistry.Tile))
        {
            visuals[t.Id] = RuntimeFactory.Visual(t);
        }

        visuals[_model.Item.Id] = RuntimeFactory.Visual(_model.Item);
        TilePalette.Current = new TilePalette(visuals);
        var tile = _model.Tile;
        foreach (var c in _grid.Coords)
        {
            var d = c.DistanceTo(Center);
            if (d > 2)
            {
                _grid.SetTile(c, TileType.Void, null, 0);
            }
            else if (d <= 1)
            {
                _grid.SetTile(c, RuntimeFactory.TypeOf(tile) == TileType.Blocked ? TileType.Blocked : TileType.Walkable, _model.Item.Id, tile.Level);
            }
            else
            {
                _grid.SetTile(c, TileType.Walkable, "core.tile.ground", 0);
            }
        }

        _board.Build(_grid, Layout);
    }

    protected override Aabb CanvasBounds()
    {
        var c = Layout.HexToPlane(Center);
        var span = Layout.Size * 1.8 * 2.8;
        return new Aabb(new Vector3((float)(c.X - span), -VisualConfig.TileThickness, (float)(c.Z - span)), new Vector3((float)(2 * span), 1.0f, (float)(2 * span)));
    }

    // ------------------------------------------------------------------ panels

    protected override void BuildProperties(Inspector inspector)
    {
        var tile = _model.Tile;
        inspector.Section("Свойства типа");
        foreach (var field in TileData.Fields.Where(f => f.Section == HexTactics.Content.Schema.FieldSections.Main))
        {
            inspector.Field(IdPrefix, field, tile, ApplyField);
        }

        inspector.Section("Внешний вид");
        foreach (var field in TileData.Fields.Where(f => f.Section == HexTactics.Content.Schema.FieldSections.Appearance))
        {
            inspector.Field(IdPrefix, field, tile, ApplyField);
        }

        inspector.Section("Эффекты (исполняет общая симуляция)");
        inspector.Hint("Гексы карты ссылаются на этот тип: эффекты задаются здесь один раз и действуют на всех картах. " +
                       "Считается логический гекс юнита — тот, в котором он числится, а не то, где нарисовано тело.");
        for (var i = 0; i < tile.Effects.Count; i++)
        {
            var effect = tile.Effects[i];
            var descriptor = Library.Registry.Effects.Find(effect.Type);
            inspector.Section($"Эффект {i + 1}: {descriptor?.Label ?? effect.Type}");
            if (descriptor is null)
            {
                inspector.Text($"✗ неизвестный тип «{effect.Type}»").AddThemeColorOverride("font_color", UiKit.Error);
                continue;
            }

            var index = i;
            foreach (var field in descriptor.FieldsFor(EffectContext.Tile))
            {
                inspector.Field($"tile.e{i}.", field, effect, (f, v) => ApplyEffectField(index, f.Key, v));
            }

            inspector.Button($"tile.e{i}.remove", "✕ Убрать эффект", () => _model.RemoveEffect(index), ButtonKind.Flat);
        }

        if (tile.Effects.Count == 0)
        {
            inspector.Hint("Эффектов нет — обычная местность.");
        }

        var types = _model.EffectTypes.Select(t => new ChoiceOption(t.Type, t.Label)).ToList();
        _newEffectType = types.Any(t => t.Value == _newEffectType) ? _newEffectType : types[0].Value;
        inspector.Choice("tile.addtype", "Новый эффект", types, _newEffectType, v => _newEffectType = v,
            "«Периодический» — урон/лечение HP в секунду, пока юнит на гексе; остальные — при входе или выходе.");
        inspector.Button("tile.add", "＋ Добавить эффект", () =>
        {
            if (_model.AddEffect(_newEffectType) is { } refusal)
            {
                Message("✗ " + refusal, HexTactics.Content.Model.IssueSeverity.Error);
            }
        });
    }

    private void ApplyEffectField(int index, string key, object? value)
    {
        if (_model.SetEffectField(index, key, value) is { } issue)
        {
            Message("✗ " + issue.Message, HexTactics.Content.Model.IssueSeverity.Error);
            Stamp++;
        }
    }

    protected override void BuildPreview(Inspector preview)
    {
        preview.Section("Правила типа");
        foreach (var line in _model.Preview())
        {
            preview.Text(line, size: 12);
        }

        preview.Hint("Периодический эффект считается по игровому времени: на паузе ничего не меняется, от FPS не зависит.");
    }

    // ------------------------------------------------------------------ automation

    protected override bool Expect(string[] a, out string actual, out bool ok)
    {
        switch (a[0])
        {
            case "tile-field":
                {
                    var field = TileData.Fields.FirstOrDefault(f => f.Key == a[1]);
                    var value = field?.Get?.Invoke(_model.Tile);
                    actual = value switch
                    {
                        double d => d.ToString("0.###", CultureInfo.InvariantCulture),
                        bool b => b ? "true" : "false",
                        null => "none",
                        _ => value.ToString() ?? string.Empty,
                    };
                    ok = actual == a[2];
                    return true;
                }

            case "effects":
                actual = _model.Tile.Effects.Count.ToString(CultureInfo.InvariantCulture);
                ok = actual == a[1];
                return true;
            case "tile-preview":
                {
                    // expect:tile-preview:N:text — line N of the rules contains text
                    var lines = _model.Preview();
                    var n = int.Parse(a[1], CultureInfo.InvariantCulture);
                    actual = n < lines.Count ? lines[n] : "none";
                    ok = actual.Contains(string.Join(":", a.Skip(2)), System.StringComparison.Ordinal);
                    return true;
                }

            default:
                actual = "unknown";
                ok = false;
                return false;
        }
    }
}
