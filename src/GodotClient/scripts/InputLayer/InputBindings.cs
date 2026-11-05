using Godot;

namespace HexTactics.Client.InputLayer;

/// <summary>Input actions of the game, registered in the InputMap at startup (rebindable later).</summary>
public static class InputBindings
{
    public const string TogglePause = "hx_toggle_pause";
    public const string CancelOrder = "hx_cancel_order";

    public static void Register()
    {
        Add(TogglePause, Key.Space);
        Add(CancelOrder, Key.Escape);
    }

    private static void Add(string action, Key key)
    {
        if (InputMap.HasAction(action))
        {
            return;
        }

        InputMap.AddAction(action);
        InputMap.ActionAddEvent(action, new InputEventKey { PhysicalKeycode = key });
    }
}
