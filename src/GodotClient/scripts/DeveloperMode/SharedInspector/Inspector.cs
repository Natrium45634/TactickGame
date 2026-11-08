using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using HexTactics.Client.UI;

namespace HexTactics.Client.DeveloperMode.SharedInspector;

/// <summary>An option of a choice field: stored value, shown text and an optional colour swatch.</summary>
/// <summary>One entry of a drop-down; entries with a new <see cref="Group"/> start with a separator titled by it.</summary>
public sealed record ChoiceOption(string Value, string Text, Color? Swatch = null, string? Group = null);

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

    /// <summary>
    /// Two numbers of one row side by side (spec part 6 §9.4: «одна строка позволяет настроить R, A»): a label line, then
    /// «R, % [   ]  A [   ]». Each field commits on Enter or on leaving it, like <see cref="LineText"/>.
    /// </summary>
    public (LineEdit First, LineEdit Second) PairText(string id1, string label1, string value1, Action<string> commit1,
        string id2, string label2, string value2, Action<string> commit2, string label, string? hint = null, string? tooltip2 = null)
    {
        LineEdit Edit(string id, string value, Action<string> commit, string tooltip)
        {
            var edit = AutomationIds.Register(new LineEdit { Text = value, TooltipText = tooltip, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(56, 0) }, id);
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
            return edit;
        }

        _box.AddChild(UiKit.Label(label, 12, dim: true));
        var row = UiKit.Row(6);
        var first = Edit(id1, value1, commit1, hint ?? string.Empty);
        var second = Edit(id2, value2, commit2, tooltip2 ?? hint ?? string.Empty);
        row.AddChild(UiKit.Label(label1, 12, dim: true));
        row.AddChild(first);
        row.AddChild(UiKit.Label(label2, 12, dim: true));
        row.AddChild(second);
        _box.AddChild(row);
        if (hint is not null)
        {
            Hint(hint);
        }

        return (first, second);
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

    /// <summary>
    /// Library objects a reference field can point to, by kind (set by the editor: damage types etc.). Without it reference
    /// fields are left to the editor's own controls.
    /// </summary>
    /// <summary>The options of a reference field (by its schema: kind, typed filter) for the current value.</summary>
    public Func<HexTactics.Content.Schema.FieldSchema, string?, IReadOnlyList<ChoiceOption>>? References { get; set; }

    /// <summary>
    /// «＋ Создать…» chosen in a reference field (spec part 6 §5.3): the host opens a constructor and calls the second argument
    /// with the id of the saved object.
    /// </summary>
    public Action<string, Action<string>>? CreateRequested { get; set; }

    public OptionButton Choice(string id, string label, IReadOnlyList<ChoiceOption> options, string selected, Action<string> commit, string? hint = null)
    {
        var option = AutomationIds.Register(new OptionButton { FocusMode = Control.FocusModeEnum.None, ClipText = true }, id);
        string? group = null;
        for (var i = 0; i < options.Count; i++)
        {
            var o = options[i];
            if (o.Group is { } g && g != group)
            {
                group = g;
                option.AddSeparator(g); // groups of a long list (spec part 6 §2.3): families, «Создать»
            }

            var index = option.ItemCount;
            if (o.Swatch is { } swatch)
            {
                option.AddIconItem(Swatch(swatch), o.Text, index);
            }
            else
            {
                option.AddItem(o.Text, index);
            }

            option.SetItemMetadata(index, o.Value);
            if (o.Value == selected)
            {
                option.Select(index);
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
            case HexTactics.Content.Schema.FieldKind.Reference when References is not null && field.RefKind is { } refKind:
                {
                    var options = new List<ChoiceOption>();
                    if (field.SpecialValue is { } special)
                    {
                        options.Add(new ChoiceOption(special, field.SpecialLabel ?? special));
                    }

                    var current = value as string ?? string.Empty;
                    options.AddRange(References(field, current));
                    if (current.Length > 0 && options.TrueForAll(o => o.Value != current))
                    {
                        options.Add(new ChoiceOption(current, $"{current} (нет в списке)"));
                    }

                    return Choice(id, label, options, current, v =>
                    {
                        if (v == HexTactics.Editor.Library.ReferenceCatalog.CreateValue)
                        {
                            CreateRequested?.Invoke(refKind, created => apply(field, created));
                            return;
                        }

                        apply(field, v);
                    }, field.Hint);
                }
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
