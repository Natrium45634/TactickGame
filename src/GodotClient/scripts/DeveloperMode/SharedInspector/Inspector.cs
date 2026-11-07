using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using HexTactics.Client.UI;

namespace HexTactics.Client.DeveloperMode.SharedInspector;

/// <summary>An option of a choice field: stored value, shown text and an optional colour swatch.</summary>
public sealed record ChoiceOption(string Value, string Text, Color? Swatch = null);

/// <summary>
/// Builds the left property panel of the editors (spec part 4 §3.1): clear labels, the control, a built-in hint, read-only
/// derived values. Text fields commit on Enter / leaving the field (one undo step per edit, not per key); numbers, choices
/// and flags commit at once. Every control can be addressed by automation through its id.
/// </summary>
public sealed class Inspector
{
    private readonly VBoxContainer _box;

    public Inspector(VBoxContainer box)
    {
        _box = box;
    }

    public void Clear()
    {
        foreach (var child in _box.GetChildren())
        {
            _box.RemoveChild(child);
            child.QueueFree();
        }
    }

    public void Section(string title)
    {
        if (_box.GetChildCount() > 0)
        {
            _box.AddChild(new HSeparator());
        }

        _box.AddChild(UiKit.Header(title));
    }

    public Label Text(string text, bool dim = false, int size = 13)
    {
        var label = UiKit.Label(text, size, dim, wrap: true);
        label.CustomMinimumSize = new Vector2(280, 0);
        _box.AddChild(label);
        return label;
    }

    public Label Hint(string text) => Text(text, dim: true, size: 12);

    public Label ReadOnly(string label, string value, Color? swatch = null)
    {
        var row = UiKit.Row(6);
        var name = UiKit.Label(label, 12, dim: true);
        name.CustomMinimumSize = new Vector2(120, 0);
        row.AddChild(name);
        if (swatch is { } color)
        {
            row.AddChild(new ColorRect { Color = color, CustomMinimumSize = new Vector2(14, 14), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter });
        }

        var text = UiKit.Label(value, 13, wrap: true);
        text.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.AddChild(text);
        _box.AddChild(row);
        return text;
    }

    public LineEdit LineText(string id, string label, string value, Action<string> commit, string? hint = null)
    {
        var edit = AutomationIds.Register(new LineEdit { Text = value, TooltipText = hint ?? string.Empty }, id);
        edit.SetMeta("hx_commit_on_submit", true);
        var last = value;
        void Commit(string text)
        {
            if (text != last)
            {
                last = text;
                commit(text);
            }
        }

        edit.TextSubmitted += Commit;
        edit.FocusExited += () => Commit(edit.Text);
        Add(label, edit, hint);
        return edit;
    }

    public TextEdit LongText(string id, string label, string value, Action<string> commit, string? hint = null)
    {
        var edit = AutomationIds.Register(new TextEdit { Text = value, CustomMinimumSize = new Vector2(0, 64), WrapMode = TextEdit.LineWrappingMode.Boundary }, id);
        var last = value;
        edit.FocusExited += () =>
        {
            if (edit.Text != last)
            {
                last = edit.Text;
                commit(edit.Text);
            }
        };
        edit.TextChanged += () =>
        {
            if (!edit.HasFocus() && edit.Text != last)
            {
                last = edit.Text;
                commit(edit.Text); // set programmatically (automation)
            }
        };
        Add(label, edit, hint);
        return edit;
    }

    public SpinBox Number(string id, string label, double value, double min, double max, double step, Action<double> commit, string? suffix = null, string? hint = null)
    {
        var spin = AutomationIds.Register(new SpinBox { MinValue = min, MaxValue = max, Step = step, Value = value, Suffix = suffix ?? string.Empty, AllowGreater = false, AllowLesser = false, SelectAllOnFocus = true }, id);
        spin.GetLineEdit().AddThemeColorOverride("font_color", UiKit.Text);
        spin.ValueChanged += v => commit(v);
        Add(label, spin, hint);
        return spin;
    }

    public OptionButton Choice(string id, string label, IReadOnlyList<ChoiceOption> options, string selected, Action<string> commit, string? hint = null)
    {
        var option = AutomationIds.Register(new OptionButton { FocusMode = Control.FocusModeEnum.None, ClipText = true }, id);
        for (var i = 0; i < options.Count; i++)
        {
            var o = options[i];
            if (o.Swatch is { } swatch)
            {
                option.AddIconItem(Swatch(swatch), o.Text, i);
            }
            else
            {
                option.AddItem(o.Text, i);
            }

            option.SetItemMetadata(i, o.Value);
            if (o.Value == selected)
            {
                option.Select(i);
            }
        }

        option.ItemSelected += index => commit(option.GetItemMetadata((int)index).AsString());
        Add(label, option, hint);
        return option;
    }

    public CheckBox Check(string id, string label, bool value, Action<bool> commit, string? hint = null)
    {
        var check = AutomationIds.Register(new CheckBox { Text = label, ButtonPressed = value, FocusMode = Control.FocusModeEnum.None, TooltipText = hint ?? string.Empty }, id);
        check.Toggled += on => commit(on);
        _box.AddChild(check);
        if (hint is not null)
        {
            Hint(hint);
        }

        return check;
    }

    public Button Button(string id, string text, Action action, ButtonKind kind = ButtonKind.Normal, string? tooltip = null)
    {
        var button = UiKit.Button(text, action, kind, id, tooltip);
        _box.AddChild(button);
        return button;
    }

    public ColorPickerButton ColorField(string id, string label, string value, Action<string> commit, string? hint = null)
    {
        var picker = AutomationIds.Register(new ColorPickerButton { Color = Color.FromHtml(value), CustomMinimumSize = new Vector2(0, 30), EditAlpha = false }, id);
        picker.ColorChanged += c => commit("#" + c.ToHtml(false).ToUpperInvariant());
        Add(label, picker, hint);
        return picker;
    }

    /// <summary>
    /// A control for a schema field (spec part 4 §9.2: the UI is built from the same field schemas as validation).
    /// Numbers are typed as text so every input — also «abc» or «-5» — goes through <paramref name="apply"/>, which
    /// refuses invalid values with an explanation.
    /// </summary>
    public Control? Field(string idPrefix, HexTactics.Content.Schema.FieldSchema field, object target, Action<HexTactics.Content.Schema.FieldSchema, object?> apply)
    {
        if (field.VisibleWhen is { } visible && !visible(target))
        {
            return null;
        }

        var id = idPrefix + field.Key;
        var value = field.Get?.Invoke(target) ?? field.Default;
        var label = field.Unit is null ? field.Label : $"{field.Label}, {field.Unit}";
        switch (field.Kind)
        {
            case HexTactics.Content.Schema.FieldKind.Number:
            case HexTactics.Content.Schema.FieldKind.Integer:
                {
                    var shown = HexTactics.Content.Schema.FieldSchema.TryNumber(value, out var d) ? (field.Percent ? Num(d * 100) : Num(d)) : string.Empty;
                    var hint = field.Hint;
                    if (field.Min is { } min && field.Max is { } max)
                    {
                        var range = field.Percent ? $"{Num(min * 100)}–{Num(max * 100)} %" : $"{Num(min)}–{Num(max)}";
                        hint = hint is null ? $"Допустимо: {range}." : $"{hint} Допустимо: {range}.";
                    }

                    return LineText(id, field.Percent ? $"{field.Label}, %" : label, shown, text =>
                    {
                        if (field.Percent && double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var p))
                        {
                            apply(field, Num(p / 100));
                        }
                        else
                        {
                            apply(field, text);
                        }
                    }, hint);
                }

            case HexTactics.Content.Schema.FieldKind.Choice:
                {
                    var options = new List<ChoiceOption>();
                    foreach (var o in field.Options)
                    {
                        options.Add(new ChoiceOption(o.Value, o.Label));
                    }

                    return Choice(id, label, options, value as string ?? string.Empty, v => apply(field, v), field.Hint);
                }

            case HexTactics.Content.Schema.FieldKind.Flag:
                return Check(id, label, value is true, on => apply(field, on), field.Hint);
            case HexTactics.Content.Schema.FieldKind.Color:
                return ColorField(id, label, value as string ?? "#FFFFFF", v => apply(field, v), field.Hint);
            case HexTactics.Content.Schema.FieldKind.LongText:
                return LongText(id, label, value as string ?? string.Empty, v => apply(field, v), field.Hint);
            case HexTactics.Content.Schema.FieldKind.Text:
                return LineText(id, label, value as string ?? string.Empty, v => apply(field, v), field.Hint);
            default:
                return null; // references are edited by the editor's own list controls
        }
    }

    public HBoxContainer Row()
    {
        var row = UiKit.Row(6);
        _box.AddChild(row);
        return row;
    }

    private void Add(string label, Control control, string? hint)
    {
        var name = UiKit.Label(label, 12, dim: true);
        _box.AddChild(name);
        control.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _box.AddChild(control);
        if (hint is not null)
        {
            Hint(hint);
        }
    }

    private static ImageTexture Swatch(Color color)
    {
        var image = Image.CreateEmpty(14, 14, false, Image.Format.Rgba8);
        image.Fill(color);
        for (var i = 0; i < 14; i++)
        {
            image.SetPixel(i, 0, color.Darkened(0.4f));
            image.SetPixel(i, 13, color.Darkened(0.4f));
            image.SetPixel(0, i, color.Darkened(0.4f));
            image.SetPixel(13, i, color.Darkened(0.4f));
        }

        return ImageTexture.CreateFromImage(image);
    }

    public static string Num(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
