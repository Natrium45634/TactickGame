using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HexTactics.Content.Kinds;
using HexTactics.Content.Library;
using HexTactics.Content.Model;
using HexTactics.Content.Registry;
using HexTactics.Core.Hex;
using HexTactics.Editor.Definitions;

namespace HexTactics.Editor.Mechanics;

/// <summary>
/// «Базовая атака» editor (spec part 7 §8.5): the area on the same hex board as a skill, around the unit and turned with its
/// facing; a click adds or removes a hex (stored for a unit facing north); target priority, targets per hit, strength, type
/// and the advanced flags. Changes are undoable; units refer to the saved definition by id.
/// </summary>
public sealed class BasicAttackEditorModel : DefinitionEditorModel
{
    public BasicAttackEditorModel(ContentLibrary library, ContentItem item)
        : base(library, item)
    {
        if (item.Data is not BasicAttackData)
        {
            throw new ArgumentException("Not a base attack.", nameof(item));
        }
    }

    public BasicAttackData Attack => (BasicAttackData)Item.Data;

    /// <summary>Facing of the unit on the board (view only): the area turns with it (M7-46, M7-50).</summary>
    public HexDirection Facing { get; set; } = HexDirection.North;

    public HexCoord ToStored(HexCoord drawnOffset) => drawnOffset.RotateClockwise(-(int)Facing);

    public HexCoord ToDrawn(HexCoord storedOffset) => storedOffset.RotateFor(Facing);

    /// <summary>A click on the board: a hex joins the area or leaves it; the unit's own hex and far hexes are refused.</summary>
    public string? Click(HexCoord drawnOffset)
    {
        if (drawnOffset == default)
        {
            return "это гекс самого юнита — область атаки вокруг него";
        }

        if (drawnOffset.DistanceTo(default) > BasicAttackData.MaxRadius)
        {
            return $"гексы области — не дальше {BasicAttackData.MaxRadius} от юнита";
        }

        var stored = ToStored(drawnOffset);
        if (Attack.Cells.Contains(stored))
        {
            Apply($"Убрать гекс {Show(stored)}", (d, _) => ((BasicAttackData)d).Cells.Remove(stored));
        }
        else if (Attack.Cells.Count >= BasicAttackData.MaxCells)
        {
            return $"не больше {BasicAttackData.MaxCells} гексов в области";
        }
        else
        {
            Apply($"Гекс {Show(stored)} в область", (d, _) => ((BasicAttackData)d).Cells.Add(stored));
        }

        return null;
    }

    public void SetPriority(string priority)
    {
        if (BasicAttackData.Priorities.Any(p => p.Value == priority))
        {
            Apply("Выбор цели", (d, _) => ((BasicAttackData)d).TargetPriority = priority);
        }
    }

    public string? SetMaxTargets(object? input)
    {
        if (!int.TryParse(Convert.ToString(input, CultureInfo.InvariantCulture)?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n is < 1 or > 6)
        {
            return $"целей за удар — целое от 1 до 6, а не «{input}»";
        }

        Apply("Целей за удар", (d, _) => ((BasicAttackData)d).MaxTargets = n);
        return null;
    }

    public string? SetPower(object? input)
    {
        var text = Convert.ToString(input, CultureInfo.InvariantCulture)?.Trim().Replace(',', '.').TrimEnd('%').Trim();
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || !double.IsFinite(v) || v < 0 || v > 1000)
        {
            return $"сила удара — от 0 до 1000 % силы атаки юнита, а не «{input}»";
        }

        Apply("Сила базовой атаки", (d, _) => ((BasicAttackData)d).PowerPercent = v);
        return null;
    }

    /// <summary>Own damage type; empty — the unit's normal attack type.</summary>
    public string? SetDamageType(string typeId)
    {
        if (typeId.Length > 0 && Library.Find(ContentTypeRegistry.DamageType, typeId) is null)
        {
            return $"тип урона «{typeId}» не найден";
        }

        Apply("Тип урона базовой атаки", (d, _) => ((BasicAttackData)d).DamageType = typeId);
        return null;
    }

    public void SetFlag(string key, bool on) => Apply(key switch { "canCrit" => "Крит базовой атаки", "canSurprise" => "Внезапность базовой атаки", _ => "Атака в движении" }, (d, _) =>
    {
        var a = (BasicAttackData)d;
        switch (key)
        {
            case "canCrit": a.CanCrit = on; break;
            case "canSurprise": a.CanSurprise = on; break;
            case "whileMoving": a.WhileMoving = on; break;
        }
    });

    /// <summary>Units that use this base attack.</summary>
    public IReadOnlyList<ContentItem> Users() => Library.UsedBy(Item.Ref).Where(u => u.Kind.Id == ContentTypeRegistry.Unit).ToList();

    /// <summary>The area and the rules in words for the preview column.</summary>
    public IReadOnlyList<string> Preview()
    {
        var a = Attack;
        var type = a.DamageType.Length == 0 ? "тип обычной атаки юнита" : Library.Find(ContentTypeRegistry.DamageType, a.DamageType)?.Name.ToLowerInvariant() ?? a.DamageType;
        var lines = new List<string>
        {
            $"Область: {Area(a)}. Поворачивается вместе со взглядом юнита; юнит сам не поворачивается и не преследует врагов.",
            $"Цель: {BasicAttackData.Priorities.FirstOrDefault(p => p.Value == a.TargetPriority).Label ?? a.TargetPriority}" + (a.MaxTargets > 1 ? $", до {a.MaxTargets} врагов за удар" : ", один враг за удар") + ".",
            $"Сила удара: {N(a.PowerPercent)} % силы атаки юнита · {type}. Частота — «скорость атак» юнита (удар/с).",
            (a.CanCrit ? "Может критовать" : "Без критов") + " · " + (a.CanSurprise ? "внезапность по стороне цели" : "без внезапности") + " · " + (a.WhileMoving ? "бьёт и в движении" : "только стоя") + ".",
        };
        var users = Users();
        lines.Add(users.Count == 0 ? "Пока не назначена ни одному юниту." : $"Используют: {string.Join(", ", users.Select(u => $"«{u.Name}»"))}.");
        return lines;
    }

    /// <summary>«передний гекс», «2 гекса: перед, второй перед» — words for the most common shapes.</summary>
    public static string Area(BasicAttackData a)
    {
        string Name(HexCoord c) => c switch
        {
            { Q: 0, R: -1 } => "перед",
            { Q: 0, R: -2 } => "второй перед",
            { Q: 1, R: -1 } => "справа спереди",
            { Q: 1, R: 0 } => "справа сзади",
            { Q: 0, R: 1 } => "сзади",
            { Q: -1, R: 1 } => "слева сзади",
            { Q: -1, R: 0 } => "слева спереди",
            _ => Show(c),
        };
        return a.Cells.Count == 1 && a.Cells[0] == new HexCoord(0, -1) ? "передний гекс" : $"{a.Cells.Count} гекс. ({string.Join(", ", a.Cells.Select(Name))})";
    }

    public static string Show(HexCoord c) => $"{c.Q},{c.R}";

    private static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
