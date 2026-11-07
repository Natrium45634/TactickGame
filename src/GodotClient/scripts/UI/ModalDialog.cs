using System;
using System.Collections.Generic;
using Godot;

namespace HexTactics.Client.UI;

/// <summary>A button of a <see cref="ModalDialog"/>.</summary>
public sealed record DialogButton(string Text, string Id, ButtonKind Kind, Action? Action, bool IsCancel = false);

/// <summary>
/// A small modal panel over the whole window (confirmations, «Сохранить / Не сохранять / Отмена», renaming). While it is
/// open nothing behind it receives the mouse or the keyboard; Esc presses the cancel button. Destructive actions always go
/// through it (spec part 4 §3.2).
/// </summary>
public partial class ModalDialog : CanvasLayer
{
    private readonly string _title;
    private readonly string _text;
    private readonly IReadOnlyList<DialogButton> _buttons;
    private readonly string? _inputValue;
    private readonly Action<VBoxContainer>? _content;
    private readonly float _width;
    private LineEdit? _input;
    private Control _root = null!;

    public ModalDialog(string title, string text, IReadOnlyList<DialogButton> buttons, string? inputValue = null, Action<VBoxContainer>? content = null, float width = 440)
    {
        _title = title;
        _text = text;
        _buttons = buttons;
        _inputValue = inputValue;
        _content = content;
        _width = width;
    }

    /// <summary>The currently open dialog, if any (one at a time).</summary>
    public static ModalDialog? Open { get; private set; }

    /// <summary>Automation-visible kind of the dialog (its title).</summary>
    public string Title => _title;

    /// <summary>Text of the input field, if the dialog has one.</summary>
    public string InputText => _input?.Text ?? string.Empty;

    /// <summary>Input text of the dialog whose button was pressed last (read by the button action).</summary>
    public static string LastInput { get; private set; } = string.Empty;

    /// <summary>
    /// Opens a dialog. <paramref name="content"/> fills a scrollable area under the text (lists, check boxes, choices of the
    /// import/export dialogs); <paramref name="width"/> is the panel width in UI units.
    /// </summary>
    public static ModalDialog Show(Node parent, string title, string text, IReadOnlyList<DialogButton> buttons, string? inputValue = null, Action<VBoxContainer>? content = null, float width = 440)
    {
        Open?.Close();
        var dialog = new ModalDialog(title, text, buttons, inputValue, content, width) { Name = "ModalDialog" };
        parent.AddChild(dialog);
        Open = dialog;
        return dialog;
    }

    public override void _Ready()
    {
        Layer = 40;
        var scale = UiKit.ScaleFor(GetViewport().GetVisibleRect().Size);
        _root = new Control { Theme = UiKit.Theme, MouseFilter = Control.MouseFilterEnum.Stop, Scale = new Vector2(scale, scale) };
        _root.Size = GetViewport().GetVisibleRect().Size / scale;
        AddChild(_root);
        _root.AddChild(new ColorRect { Color = new Color(0, 0, 0, 0.45f), Size = _root.Size, MouseFilter = Control.MouseFilterEnum.Stop });

        var panel = UiKit.Panel(UiKit.PanelLight, 18);
        panel.CustomMinimumSize = new Vector2(_width, 0);
        _root.AddChild(panel);
        var column = UiKit.Column(10);
        panel.AddChild(column);
        column.AddChild(UiKit.Label(_title, 18));
        if (_text.Length > 0)
        {
            var text = UiKit.Label(_text, 14, dim: true, wrap: true);
            text.CustomMinimumSize = new Vector2(_width - 40, 0);
            column.AddChild(text);
        }

        if (_inputValue is not null)
        {
            _input = AutomationIds.Register(new LineEdit { Text = _inputValue, CustomMinimumSize = new Vector2(_width - 40, 0) }, "dialog.input");
            _input.TextSubmitted += _ => PressDefault();
            column.AddChild(_input);
        }

        if (_content is not null)
        {
            var scroll = new ScrollContainer
            {
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
                CustomMinimumSize = new Vector2(_width - 40, Mathf.Min(460f, _root.Size.Y * 0.55f)),
            };
            column.AddChild(scroll);
            var box = UiKit.Column(6);
            box.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            scroll.AddChild(box);
            _content(box);
        }

        var row = UiKit.Row(8);
        row.Alignment = BoxContainer.AlignmentMode.End;
        column.AddChild(row);
        foreach (var b in _buttons)
        {
            var button = UiKit.Button(b.Text, null, b.Kind, b.Id, fontSize: 14);
            var captured = b;
            button.Pressed += () => Press(captured);
            row.AddChild(button);
        }

        panel.ResetSize();
        panel.Position = (_root.Size - panel.GetCombinedMinimumSize()) * 0.5f;
        if (_input is not null)
        {
            _input.CallDeferred(Control.MethodName.GrabFocus);
            _input.CallDeferred(LineEdit.MethodName.SelectAll);
        }
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Keycode: Key.Escape })
        {
            foreach (var b in _buttons)
            {
                if (b.IsCancel)
                {
                    Press(b);
                    break;
                }
            }

            GetViewport().SetInputAsHandled();
        }
        else if (@event is InputEventKey && (_input is null || !_input.HasFocus()))
        {
            GetViewport().SetInputAsHandled(); // nothing behind the dialog gets keys
        }
    }

    public void Close()
    {
        if (Open == this)
        {
            Open = null;
        }

        QueueFree();
    }

    private void PressDefault()
    {
        foreach (var b in _buttons)
        {
            if (b.Kind is ButtonKind.Primary or ButtonKind.Danger)
            {
                Press(b);
                return;
            }
        }
    }

    private void Press(DialogButton button)
    {
        LastInput = InputText;
        Close();
        button.Action?.Invoke();
    }
}
