using Forms = System.Windows.Forms;

namespace ExperienceX;

internal readonly record struct ModifierFramingInput(Forms.Keys Modifiers, ScreenPoint Pointer, bool Focused, bool MouseButton = false, bool OtherKey = false, bool Escape = false);

// Modifier release commits; losing focus/context cancels. A shortcut blocks re-entry until release.
internal sealed class ModifierFramingGesture(VideoFramingEditor editor, Action started)
{
    private Forms.Keys _modifier;
    private string? _screen;
    private bool _blocked;
    internal bool Active => _screen is not null;

    internal void Update(ModifierFramingInput input, ScreenOptions? screen, bool enabled, double logicalWidth, double logicalHeight)
    {
        if (input.Modifiers == Forms.Keys.None) _blocked = false;
        if (Active)
        {
            if (!enabled || !input.Focused || screen?.Id != _screen || !editor.Dragging)
            { Cancel("ModifierContextLost"); return; }
            if (input.Escape) { Cancel("Escape"); return; }
            if (input.OtherKey || input.MouseButton || input.Modifiers != _modifier)
            { Finish(); _blocked = input.Modifiers != Forms.Keys.None; return; }
            editor.MoveDrag(input.Pointer.X, input.Pointer.Y);
            return;
        }
        if (input.Modifiers == Forms.Keys.None) return;
        if (_blocked) return;
        if (!enabled || !input.Focused || input.OtherKey || input.MouseButton || input.Escape ||
            screen?.IsVideo != true || input.Modifiers is not (Forms.Keys.Shift or Forms.Keys.Control or Forms.Keys.Alt) ||
            editor.Dragging || editor.Busy || !ScreenSelection.Contains(screen.Points, input.Pointer.X, input.Pointer.Y))
        { _blocked = true; return; }
        var began = input.Modifiers switch
        {
            Forms.Keys.Shift => editor.BeginPan(screen, input.Pointer.X, input.Pointer.Y),
            Forms.Keys.Alt => editor.BeginRotate(screen, input.Pointer.X, input.Pointer.Y, logicalWidth, logicalHeight),
            _ => editor.BeginZoom(screen, input.Pointer.X, input.Pointer.Y, logicalWidth, logicalHeight)
        };
        if (!began) { _blocked = true; return; }
        _screen = screen.Id; _modifier = input.Modifiers; started();
    }
    internal void Cancel(string reason)
    {
        if (Active) editor.Cancel(reason);
        _screen = null; _blocked = true;
    }
    internal void Finish()
    {
        if (Active) editor.EndDrag();
        _screen = null; _blocked = true;
    }
    internal void Suspend()
    {
        Finish(); _blocked = true;
    }
}
