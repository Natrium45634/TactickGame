using System;
using System.Collections.Generic;
using System.Globalization;
using HexTactics.Content.Kinds;
using HexTactics.Content.Library;
using HexTactics.Content.Model;
using HexTactics.Content.Registry;
using HexTactics.Content.Schema;
using HexTactics.Core.Effects;
using HexTactics.Editor.Definitions;

namespace HexTactics.Editor.Tiles;

/// <summary>
/// Model of the tile type editor (spec part 4 §7): the characteristics come from <see cref="TileData.Fields"/>; effects are
/// added from the registry types usable on tiles and edited through their tile-context fields (trigger, side filter,
/// amounts). Every change is an undoable command; invalid values are refused before they reach the object.
/// </summary>
public sealed class TileEditorModel : DefinitionEditorModel
{
    public TileEditorModel(ContentLibrary library, ContentItem item)
        : base(library, item)
    {
        if (item.Data is not TileData)
        {
            throw new ArgumentException("Not a tile type.", nameof(item));
        }
    }

    public TileData Tile => (TileData)Item.Data;

    /// <summary>Effect types that can be put on a tile type.</summary>
    public IEnumerable<EffectTypeDescriptor> EffectTypes => Library.Registry.Effects.For(EffectContext.Tile);

    /// <summary>Adds an effect of a registered type with its tile defaults (periodic: 5 HP/s while on the hex).</summary>
    public string? AddEffect(string type)
    {
        if (Library.Registry.Effects.Find(type) is not { } descriptor || (descriptor.Contexts & EffectContext.Tile) == 0)
        {
            return $"тип эффекта «{type}» нельзя использовать на гексе";
        }

        if (Tile.Effects.Count >= 8)
        {
            return "у типа гекса не больше 8 эффектов";
        }

        Apply($"Добавить эффект «{descriptor.Label}»", (d, _) => ((TileData)d).Effects.Add(descriptor.CreateDefault(EffectContext.Tile)));
        return null;
    }

    public void RemoveEffect(int index) => Apply("Убрать эффект", (d, _) =>
    {
        var effects = ((TileData)d).Effects;
        if (index >= 0 && index < effects.Count)
        {
            effects.RemoveAt(index);
        }
    });

    /// <summary>Sets a field of an effect from user input; invalid input is refused with an explanation.</summary>
    public ContentIssue? SetEffectField(int index, string key, object? input)
    {
        var effect = index >= 0 && index < Tile.Effects.Count ? Tile.Effects[index] : null;
        if (effect is null || Library.Registry.Effects.Find(effect.Type)?.FindField(key, EffectContext.Tile) is not { } field)
        {
            return ContentIssue.Error(key, "нет такого поля");
        }

        var value = input;
        if (field.Kind is FieldKind.Number or FieldKind.Integer && input is string text)
        {
            if (!double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                return ContentIssue.Error(key, $"«{field.Label}»: «{text}» — не число, значение не изменено");
            }

            value = parsed;
        }

        if (value is int i)
        {
            value = (double)i;
        }

        if (field.Check(value, key) is { } issue)
        {
            return issue with { Message = issue.Message + " — значение не изменено" };
        }

        Apply($"«{field.Label}»: {value}", (d, _) => ((TileData)d).Effects[index].Set(key, value));
        return null;
    }

    /// <summary>What the type does, in words (spec part 4 §7.2: «5 HP/с → за 2 с 10 HP»).</summary>
    public IReadOnlyList<string> Preview() => TilePreview.Describe(Tile, Library.Registry.Effects);
}

/// <summary>Human-readable rules of a tile type (shared by the tile and map editors and tests).</summary>
public static class TilePreview
{
    public static IReadOnlyList<string> Describe(TileData tile, EffectTypeRegistry registry)
    {
        var lines = new List<string>
        {
            $"{TileData.CategoryLabel(tile.Category)}; {(tile.Passable && tile.Category != TileData.Obstacle ? "проходим" : "непроходим")}" +
            (tile.Passable && tile.Category != TileData.Obstacle ? (tile.Waypoints ? "; ТД/КТД ставить можно" : "; ТД/КТД ставить нельзя (только пройти)") : string.Empty) + ".",
        };
        if (tile.Effects.Count == 0)
        {
            lines.Add("Эффектов нет.");
        }

        foreach (var e in tile.Effects)
        {
            lines.Add(DescribeEffect(e, registry));
        }

        return lines;
    }

    public static string DescribeEffect(EffectData effect, EffectTypeRegistry registry)
    {
        var s = effect.ToSpec(registry, EffectContext.Tile);
        var trigger = s.Text("trigger", effect.Type == HexTactics.Core.Effects.EffectTypes.Periodic ? "stay" : "enter");
        var when = trigger switch
        {
            "stay" => "Пока юнит на гексе",
            "exit" => "При выходе с гекса",
            _ => "При входе на гекс",
        };
        var who = s.Text("sides", TileSides.All) switch
        {
            TileSides.Player => "юниты игрока",
            TileSides.Enemy => "враги",
            _ => "все юниты",
        };
        string text;
        switch (effect.Type)
        {
            case HexTactics.Core.Effects.EffectTypes.Periodic:
                {
                    var word = s.Text("kind", "damage") == "heal" ? "лечение" : "урон";
                    var per = s.Number("perSecond", 5);
                    text = trigger == "stay"
                        ? $"{word} {N(per)} HP за игровую секунду нахождения (за 2 с — {N(per * 2)} HP{(word == "лечение" ? ", не выше максимума" : string.Empty)})"
                        : $"{word} {N(per)} HP/с в течение {N(s.Number("duration", 3))} с = {N(per * s.Number("duration", 3))} HP";
                    break;
                }

            case HexTactics.Core.Effects.EffectTypes.Damage:
                text = $"урон {N(s.Number("amount", 10))}";
                break;
            case HexTactics.Core.Effects.EffectTypes.Heal:
                text = $"лечение {N(s.Number("amount", 10))}, не выше максимума";
                break;
            case HexTactics.Core.Effects.EffectTypes.Buff:
            case HexTactics.Core.Effects.EffectTypes.Debuff:
                {
                    var sign = effect.Type == HexTactics.Core.Effects.EffectTypes.Buff ? "+" : "−";
                    var size = s.Text("mode", "percent") == "percent" ? N(s.Number("percent") * 100) + " %" : N(s.Number("delta"));
                    var stat = s.Text("stat", Stats.MoveSpeed) switch
                    {
                        Stats.AttackPower => "силы атаки",
                        Stats.AttackRate => "скорости атак",
                        _ => "скорости движения",
                    };
                    text = $"{sign}{size} {stat} на {N(s.Number("duration", 5))} с";
                    break;
                }

            default:
                text = registry.Find(effect.Type)?.Label.ToLowerInvariant() ?? effect.Type;
                break;
        }

        return $"{when}: {text} — {who}.";
    }

    private static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
