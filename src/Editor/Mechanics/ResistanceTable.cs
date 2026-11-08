using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HexTactics.Content.Kinds;
using HexTactics.Content.Library;
using HexTactics.Content.Model;
using HexTactics.Content.Registry;
using HexTactics.Editor.Units;

namespace HexTactics.Editor.Mechanics;

/// <summary>
/// The rows of a resistance table shared by the unit editor and the resistance set editor (spec part 5 §7.2): «все
/// сопротивляемые типы», every family with its subtypes, true-like families last as rows without a number. Each row knows
/// whether its value is the object's own or inherited (from a set / a parent set).
/// </summary>
public static class ResistanceTable
{
    public static IReadOnlyList<ResistanceRow> Rows(ContentLibrary library, IReadOnlyDictionary<string, ResistanceValue> values,
        IReadOnlyDictionary<string, ResistanceValue>? armor = null)
    {
        string? SourceOf(ResistanceValue v) => v.Own ? "своё" : v.ProfileId is { } p ? $"из набора «{library.Find(ContentTypeRegistry.ResistanceProfile, p)?.Name ?? p}»" : null;
        ResistanceRow Row(string key, string label, int depth, bool bypass)
        {
            var row = values.TryGetValue(key, out var v)
                ? new ResistanceRow(key, label, v.Value, depth, bypass, SourceOf(v), v.Own)
                : new ResistanceRow(key, label, 0, depth, bypass);
            return armor is not null && armor.TryGetValue(key, out var a) ? row with { Armor = a.Value, ArmorSource = SourceOf(a), ArmorOwn = a.Own } : row;
        }

        var rows = new List<ResistanceRow> { Row(UnitData.GlobalResistance, "Все сопротивляемые типы", 0, false) };
        var types = library.Items(ContentTypeRegistry.DamageType)
            .Where(t => t.Data is DamageTypeData d && (!d.Legacy || values.ContainsKey(t.Id) || armor?.ContainsKey(t.Id) == true))
            .ToList();
        var families = types.Where(t => ((DamageTypeData)t.Data).Family.Length == 0)
            .OrderBy(t => ((DamageTypeData)t.Data).Strategy == DamageTypeData.TrueLike ? 1 : 0)
            .ThenBy(t => t.Id == "core.damage.physical" ? 0 : t.Id == "core.damage.magical" ? 1 : 2)
            .ThenBy(t => t.Name, StringComparer.CurrentCulture);
        foreach (var family in families)
        {
            var bypass = ((DamageTypeData)family.Data).Strategy == DamageTypeData.TrueLike;
            rows.Add(Row(family.Id, family.Name, 0, bypass));
            foreach (var sub in types.Where(t => ((DamageTypeData)t.Data).Family == family.Id).OrderBy(t => t.Name, StringComparer.CurrentCulture))
            {
                rows.Add(Row(sub.Id, sub.Name, 1, bypass));
            }
        }

        return rows;
    }

    /// <summary>Parses a resistance from input (−500…+500 %, comma or dot, optional «%»), rounded to 0.01 point.</summary>
    public static ContentIssue? Parse(ContentLibrary library, string key, object? input, string path, out double value)
    {
        value = 0;
        switch (input)
        {
            case double d:
                value = d;
                break;
            case string text when double.TryParse(text.Trim().Replace(',', '.').TrimEnd('%').Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed):
                value = parsed;
                break;
            default:
                return ContentIssue.Error(path, $"сопротивление: «{input}» — не число, значение не изменено");
        }

        if (!double.IsFinite(value) || value < UnitData.MinResistance || value > UnitData.MaxResistance)
        {
            return ContentIssue.Error(path, $"сопротивление: от −500 до +500 %, а не {value.ToString("0.##", CultureInfo.InvariantCulture)} — значение не изменено");
        }

        if (key != UnitData.GlobalResistance && library.Find(ContentTypeRegistry.DamageType, key) is null)
        {
            return ContentIssue.Error(path, $"нет типа урона «{key}»");
        }

        value = (double)Math.Round((decimal)value, 2, MidpointRounding.AwayFromZero); // spec part 5 §3.1: 0.01 point
        return null;
    }

    /// <summary>Parses a defense A from input (0…1 000 000 points, comma or dot), rounded to 0.01 point (spec part 6 §3.2).</summary>
    public static ContentIssue? ParseArmor(ContentLibrary library, string key, object? input, string path, out double value)
    {
        value = 0;
        switch (input)
        {
            case double d:
                value = d;
                break;
            case string text when double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed):
                value = parsed;
                break;
            default:
                return ContentIssue.Error(path, $"защита A: «{input}» — не число, значение не изменено");
        }

        if (!double.IsFinite(value) || value < UnitData.MinArmor || value > UnitData.MaxArmor)
        {
            return ContentIssue.Error(path, $"защита A: от 0 до 1 000 000 очков, а не {value.ToString("0.##", CultureInfo.InvariantCulture)} — значение не изменено (снижение урона в процентах задаётся сопротивлением)");
        }

        if (key != UnitData.GlobalResistance && library.Find(ContentTypeRegistry.DamageType, key)?.Data is not DamageTypeData)
        {
            return ContentIssue.Error(path, $"нет типа урона «{key}»");
        }

        if (key != UnitData.GlobalResistance && library.Find(ContentTypeRegistry.DamageType, key)?.Data is DamageTypeData { Strategy: DamageTypeData.TrueLike })
        {
            return ContentIssue.Error(path, "чистый урон обходит защиту A — значение не действовало бы");
        }

        value = (double)Math.Round((decimal)value, 2, MidpointRounding.AwayFromZero);
        return null;
    }

    /// <summary>A_raw against a type: global + family + subtype of the given base values (spec part 6 §3.2); 0 for true-like types.</summary>
    public static double ArmorTotal(ContentLibrary library, IReadOnlyDictionary<string, ResistanceValue> values, string typeId)
    {
        if (library.Find(ContentTypeRegistry.DamageType, typeId)?.Data is not DamageTypeData { Strategy: DamageTypeData.ResistanceAffected } d)
        {
            return 0;
        }

        double V(string key) => values.TryGetValue(key, out var v) ? v.Value : 0;
        var family = d.Family.Length == 0 ? typeId : d.Family;
        return V(UnitData.GlobalResistance) + V(family) + (family == typeId ? 0 : V(typeId));
    }

    /// <summary>The layered total against a type: global + family + subtype of the given base values, clamped to ±500.</summary>
    public static double Total(ContentLibrary library, IReadOnlyDictionary<string, ResistanceValue> values, string typeId)
    {
        if (library.Find(ContentTypeRegistry.DamageType, typeId)?.Data is not DamageTypeData d)
        {
            return 0;
        }

        double V(string key) => values.TryGetValue(key, out var v) ? v.Value : 0;
        var family = d.Family.Length == 0 ? typeId : d.Family;
        return Math.Clamp(V(UnitData.GlobalResistance) + V(family) + (family == typeId ? 0 : V(typeId)), UnitData.MinResistance, UnitData.MaxResistance);
    }

    public static string KeyName(ContentLibrary library, string key) =>
        key == UnitData.GlobalResistance ? "все типы" : library.Find(ContentTypeRegistry.DamageType, key)?.Name ?? key;
}
