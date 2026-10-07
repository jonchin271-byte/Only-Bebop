using Godot;

namespace OnlyBebop;

/// inputs sheet -> Godot InputMap (Deadlock's default keys).
public static class InputSetup
{
    public static void Apply()
    {
        Add("move_forward", Key.W); Add("move_back", Key.S); Add("move_left", Key.A); Add("move_right", Key.D);
        Add("jump", Key.Space); Add("dash", Key.Shift); Add("sprint", Key.Ctrl);
        Add("ability_1", Key.Key1); Add("ability_3", Key.Key3); Add("menu", Key.Escape);
        if (!InputMap.ActionHasEvent("ability_3", new InputEventMouseButton { ButtonIndex = MouseButton.Xbutton1 }))
            InputMap.ActionAddEvent("ability_3", new InputEventMouseButton { ButtonIndex = MouseButton.Xbutton1 });
    }

    static void Add(string action, Key key)
    {
        if (!InputMap.HasAction(action)) InputMap.AddAction(action);
        InputMap.ActionAddEvent(action, new InputEventKey { PhysicalKeycode = key });
    }
}
