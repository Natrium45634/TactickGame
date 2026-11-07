using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using HexTactics.Content.Model;
using HexTactics.Content.Registry;
using HexTactics.Content.Schema;

namespace HexTactics.Content.Kinds;

/// <summary>
/// A tile type of the library (spec part 4 §7, <c>TileDefinition</c>): its role on the map, passability, relief level,
/// colour and effects. Map tiles are instances of a type; the logic of a type lives here once, not in every map cell.
/// </summary>
public sealed class TileData : IContentData
{
    public const string Ground = "ground";
    public const string Obstacle = "obstacle";
    public const string PlayerBase = "playerBase";
    public const string EnemyBase = "enemyBase";
    public const string Deploy = "deploy";
    public const string Spawn = "spawn";

    /// <summary>A walkable surface with its own rules (spec part 4 §7.1 «особый»): burning, healing…</summary>
    public const string Special = "special";

    public string Category { get; set; } = Ground;

    public bool Passable { get; set; } = true;

    /// <summary>Relief level 0…3 (appearance; no combat rules, spec part 4 §4.2).</summary>
    public int Level { get; set; }

    public string Color { get; set; } = "#CCCCCF";

    /// <summary>Up to 4 characters drawn on every hex of this type (empty — none).</summary>
    public string Icon { get; set; } = string.Empty;

    /// <summary>Whether ТД/КТД may be put on hexes of this type (spec part 4 §7.1); walking across is not affected.</summary>
    public bool Waypoints { get; set; } = true;

    /// <summary>
    /// Effects of the tile type with their triggers (<c>trigger</c>: stay / enter / exit, spec part 4 §7.1–7.2); executed
    /// by the simulation's effect resolver for the unit's logical hex.
    /// </summary>
    public List<EffectData> Effects { get; } = new();

    public static IReadOnlyList<FieldOption> Categories { get; } = new[]
    {
        new FieldOption(Ground, "обычный"),
        new FieldOption(Obstacle, "препятствие"),
        new FieldOption(PlayerBase, "база игрока"),
        new FieldOption(EnemyBase, "база врага"),
        new FieldOption(Deploy, "зона выставления"),
        new FieldOption(Spawn, "край / зона появления"),
        new FieldOption(Special, "особый (поверхность)"),
    };

    public static IReadOnlyList<FieldSchema> Fields { get; } = new FieldSchema[]
    {
        new()
        {
            Key = "category", Label = "Категория", Kind = FieldKind.Choice, Default = Ground, Options = Categories,
            Hint = "Роль местности: база, спавн и т. д. — метки, а не ИИ или волны.",
            Get = o => ((TileData)o).Category, Set = (o, v) => ((TileData)o).Category = (string)v!,
        },
        new()
        {
            Key = "passable", Label = "Проходимость", Kind = FieldKind.Flag, Default = true,
            Hint = "Можно ли ходить по гексу. Препятствие всегда непроходимо; базы и зоны — проходимы.",
            Get = o => ((TileData)o).Passable, Set = (o, v) => ((TileData)o).Passable = (bool)v!,
        },
        new()
        {
            Key = "waypoints", Label = "Можно ставить ТД/КТД", Kind = FieldKind.Flag, Default = true,
            Hint = "Если снято, гекс красный при прокладке маршрута: по нему можно пройти, но остановиться в ТД нельзя.",
            Get = o => ((TileData)o).Waypoints, Set = (o, v) => ((TileData)o).Waypoints = (bool)v!,
        },
        new()
        {
            Key = "level", Label = "Уровень (высота)", Kind = FieldKind.Integer, Default = 0, Min = 0, Max = 3,
            Section = FieldSections.Appearance, Hint = "Только вид гекса: правила боя от высоты не меняются.",
            Get = o => (double)((TileData)o).Level, Set = (o, v) => ((TileData)o).Level = (int)System.Math.Round((double)v!),
        },
        new()
        {
            Key = "color", Label = "Цвет", Kind = FieldKind.Color, Default = "#CCCCCF", Section = FieldSections.Appearance,
            Get = o => ((TileData)o).Color, Set = (o, v) => ((TileData)o).Color = (string)v!,
        },
        new()
        {
            Key = "icon", Label = "Значок на гексе (до 4 символов)", Kind = FieldKind.Text, Default = string.Empty, MaxLength = 4,
            Section = FieldSections.Appearance, Hint = "Рисуется на каждом гексе этого типа — в игре и в редакторе карт.",
            Get = o => ((TileData)o).Icon, Set = (o, v) => ((TileData)o).Icon = ((string?)v ?? string.Empty).Trim(),
        },
    };

    public static string CategoryLabel(string category)
    {
        foreach (var o in Categories)
        {
            if (o.Value == category)
            {
                return o.Label;
            }
        }

        return category;
    }

    public JsonObject ToJson()
    {
        var effects = new JsonArray();
        foreach (var e in Effects)
        {
            effects.Add(e.ToJson());
        }

        return new JsonObject
        {
            ["category"] = Category,
            ["passable"] = Passable,
            ["level"] = Level,
            ["color"] = Color,
            ["icon"] = Icon,
            ["waypoints"] = Waypoints,
            ["effects"] = effects,
        };
    }

    public static TileData Read(JsonObject obj, IssueList issues)
    {
        var data = new TileData
        {
            Category = JsonFields.Text(obj, "category", Ground, issues, string.Empty),
            Passable = JsonFields.Flag(obj, "passable", true, issues, string.Empty),
            Level = JsonFields.Integer(obj, "level", 0, issues, string.Empty),
            Color = JsonFields.Text(obj, "color", "#CCCCCF", issues, string.Empty),
            Icon = JsonFields.Text(obj, "icon", string.Empty, issues, string.Empty),
            Waypoints = JsonFields.Flag(obj, "waypoints", true, issues, string.Empty),
        };
        if (JsonFields.Array(obj, "effects", issues, string.Empty) is { } effects)
        {
            for (var i = 0; i < effects.Count; i++)
            {
                data.Effects.Add(EffectData.Read(effects[i], issues, $"effects[{i}]"));
            }
        }

        return data;
    }

    public IEnumerable<ContentRef> References()
    {
        var seen = new HashSet<ContentRef>();
        foreach (var e in Effects)
        {
            foreach (var r in e.References())
            {
                if (seen.Add(r))
                {
                    yield return r;
                }
            }
        }
    }

    public void Validate(ValidationContext context)
    {
        foreach (var field in Fields)
        {
            if (field.Check(field.Get!(this), field.Key) is { } issue)
            {
                context.Issues.AddRange(new[] { issue });
            }
        }

        if (Category == Obstacle && Passable)
        {
            context.Issues.Error("passable", "препятствие не может быть проходимым");
        }

        if (Category is PlayerBase or EnemyBase or Deploy or Spawn && !Passable)
        {
            context.Issues.Error("passable", $"«{CategoryLabel(Category)}» должна быть проходимой");
        }

        if (Effects.Count > 8)
        {
            context.Issues.Error("effects", "у типа гекса не больше 8 эффектов");
        }

        for (var i = 0; i < Effects.Count; i++)
        {
            Effects[i].Validate(context, $"effects[{i}]", EffectContext.Tile);
        }

        if (Effects.Count > 0 && !Passable)
        {
            context.Issues.Warning("effects", "по непроходимому гексу никто не ходит — его эффекты не сработают");
        }
    }

    public string Summary()
    {
        var text = CategoryLabel(Category);
        if (!Passable && Category != Obstacle)
        {
            text += ", непроходимый";
        }

        if (Level > 0)
        {
            text += ", уровень " + Level.ToString(CultureInfo.InvariantCulture);
        }

        return Effects.Count > 0 ? text + $", эффектов: {Effects.Count}" : text;
    }

    public IContentData Clone()
    {
        var copy = new TileData { Category = Category, Passable = Passable, Level = Level, Color = Color, Icon = Icon, Waypoints = Waypoints };
        foreach (var e in Effects)
        {
            copy.Effects.Add(e.Clone());
        }

        return copy;
    }
}
