using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using HexTactics.Content.Model;

namespace HexTactics.Content.Schema;

/// <summary>Value type of a field (spec part 4 §9.2 <c>FieldSchemaRegistry</c>: type, range, units, default, UI control).</summary>
public enum FieldKind
{
    Number = 0,
    Integer = 1,
    Text = 2,
    Flag = 3,

    /// <summary>One of <see cref="FieldSchema.Options"/>.</summary>
    Choice = 4,

    /// <summary><c>#RRGGBB</c>.</summary>
    Color = 5,

    /// <summary>Stable id of an object of <see cref="FieldSchema.RefKind"/>.</summary>
    Reference = 6,

    /// <summary>List of stable ids of objects of <see cref="FieldSchema.RefKind"/>.</summary>
    ReferenceList = 7,

    /// <summary>Multi-line text.</summary>
    LongText = 8,
}

public sealed record FieldOption(string Value, string Label);

/// <summary>Standard sections of a property panel (spec part 4 §3.1).</summary>
public static class FieldSections
{
    public const string Main = "Основное";
    public const string Effects = "Эффекты";
    public const string Appearance = "Внешний вид";
    public const string Advanced = "Дополнительно";
}

/// <summary>
/// Description of one editable field: what the property panel shows (label, unit, hint, control) and what the validator
/// accepts. The same description serves the editor UI, validation, saving and import (spec part 4 §9.1 «одна трасса
/// данных»). <see cref="Get"/>/<see cref="Set"/> read and write the value on a typed definition (or a parameter bag).
/// </summary>
public sealed partial record FieldSchema
{
    public required string Key { get; init; }

    public required string Label { get; init; }

    public required FieldKind Kind { get; init; }

    public object? Default { get; init; }

    public double? Min { get; init; }

    public double? Max { get; init; }

    /// <summary>Unit shown next to the value, e.g. «HP», «гекс/с».</summary>
    public string? Unit { get; init; }

    /// <summary>Built-in help text of the field.</summary>
    public string? Hint { get; init; }

    public string Section { get; init; } = FieldSections.Main;

    /// <summary>Collapsed under «Дополнительно» by default.</summary>
    public bool Advanced { get; init; }

    /// <summary>A number shown as a percentage (1.5 → 150 %).</summary>
    public bool Percent { get; init; }

    public bool ReadOnly { get; init; }

    public IReadOnlyList<FieldOption> Options { get; init; } = Array.Empty<FieldOption>();

    /// <summary>Kind of the referenced objects for <see cref="FieldKind.Reference"/> / <see cref="FieldKind.ReferenceList"/>.</summary>
    public string? RefKind { get; init; }

    /// <summary>Version of the field (for migrations of user data).</summary>
    public int Version { get; init; } = 1;

    /// <summary>Shown only when this predicate holds for the edited object (e.g. «множитель» only for the multiplier formula).</summary>
    public Func<object, bool>? VisibleWhen { get; init; }

    public Func<object, object?>? Get { get; init; }

    public Action<object, object?>? Set { get; init; }

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex ColorPattern();

    public static bool IsColor(string? text) => text is not null && ColorPattern().IsMatch(text);

    /// <summary>Checks one value; returns null when it is acceptable.</summary>
    public ContentIssue? Check(object? value, string path)
    {
        switch (Kind)
        {
            case FieldKind.Number:
            case FieldKind.Integer:
                {
                    if (!TryNumber(value, out var d))
                    {
                        return ContentIssue.Error(path, $"«{Label}»: ожидается число");
                    }

                    if (double.IsNaN(d) || double.IsInfinity(d))
                    {
                        return ContentIssue.Error(path, $"«{Label}»: недопустимое значение (NaN/бесконечность)");
                    }

                    if (Kind == FieldKind.Integer && Math.Abs(d - Math.Round(d)) > 1e-9)
                    {
                        return ContentIssue.Error(path, $"«{Label}»: ожидается целое число");
                    }

                    if (Min is { } min && d < min - 1e-12)
                    {
                        return ContentIssue.Error(path, $"«{Label}»: не меньше {Show(min)}{UnitSuffix}, сейчас {Show(d)}");
                    }

                    if (Max is { } max && d > max + 1e-12)
                    {
                        return ContentIssue.Error(path, $"«{Label}»: не больше {Show(max)}{UnitSuffix}, сейчас {Show(d)}");
                    }

                    return null;
                }

            case FieldKind.Flag:
                return value is bool ? null : ContentIssue.Error(path, $"«{Label}»: ожидается да/нет");
            case FieldKind.Choice:
                {
                    if (value is string s)
                    {
                        foreach (var o in Options)
                        {
                            if (o.Value == s)
                            {
                                return null;
                            }
                        }
                    }

                    return ContentIssue.Error(path, $"«{Label}»: недопустимое значение «{value}»");
                }

            case FieldKind.Color:
                return IsColor(value as string) ? null : ContentIssue.Error(path, $"«{Label}»: ожидается цвет #RRGGBB");
            case FieldKind.Reference:
                return value is string id && ContentIds.IsValid(id) ? null : ContentIssue.Error(path, $"«{Label}»: неверная ссылка «{value}»");
            case FieldKind.ReferenceList:
                {
                    if (value is not IEnumerable<string> ids)
                    {
                        return ContentIssue.Error(path, $"«{Label}»: ожидается список ссылок");
                    }

                    foreach (var refId in ids)
                    {
                        if (!ContentIds.IsValid(refId))
                        {
                            return ContentIssue.Error(path, $"«{Label}»: неверная ссылка «{refId}»");
                        }
                    }

                    return null;
                }

            default:
                return value is string || value is null ? null : ContentIssue.Error(path, $"«{Label}»: ожидается текст");
        }
    }

    private string UnitSuffix => Unit is null ? string.Empty : " " + Unit;

    private string Show(double d) => Percent ? (d * 100).ToString("0.##", CultureInfo.InvariantCulture) + " %" : d.ToString("0.###", CultureInfo.InvariantCulture);

    public static bool TryNumber(object? value, out double d)
    {
        switch (value)
        {
            case double x:
                d = x;
                return true;
            case int i:
                d = i;
                return true;
            case long l:
                d = l;
                return true;
            case float f:
                d = f;
                return true;
            case decimal m:
                d = (double)m;
                return true;
            default:
                d = 0;
                return false;
        }
    }
}
