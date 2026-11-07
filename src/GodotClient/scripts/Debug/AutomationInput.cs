using System;
using Godot;

namespace HexTactics.Client.Debug;

/// <summary>Input events injected by automation scripts — the same path as real mouse and keyboard input.</summary>
public static class AutomationInput
{
    public static void Motion(Vector2 position) =>
        Godot.Input.ParseInputEvent(new InputEventMouseMotion { Position = position, GlobalPosition = position });

    public static void Button(Vector2 position, MouseButton button, bool pressed) =>
        Godot.Input.ParseInputEvent(new InputEventMouseButton { Position = position, GlobalPosition = position, ButtonIndex = button, Pressed = pressed });

    public static void Click(Vector2 position, MouseButton button = MouseButton.Left)
    {
        Motion(position);
        Button(position, button, true);
        Button(position, button, false);
    }

    /// <summary>
    /// Presses and releases a key: <c>Escape</c>, <c>Space</c>, <c>R</c>, or with modifiers <c>ctrl+z</c>,
    /// <c>ctrl+shift+z</c>, <c>ctrl+s</c> (key names of Godot's <see cref="Key"/>, case-insensitive).
    /// </summary>
    public static void Key(string combo)
    {
        var parts = combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool ctrl = false, shift = false, alt = false;
        var key = Godot.Key.None;
        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                    ctrl = true;
                    break;
                case "shift":
                    shift = true;
                    break;
                case "alt":
                    alt = true;
                    break;
                default:
                    key = Enum.Parse<Key>(part, ignoreCase: true);
                    break;
            }
        }

        foreach (var pressed in new[] { true, false })
        {
            Godot.Input.ParseInputEvent(new InputEventKey
            {
                Keycode = key,
                PhysicalKeycode = key,
                Pressed = pressed,
                CtrlPressed = ctrl,
                ShiftPressed = shift,
                AltPressed = alt,
            });
        }
    }
}
