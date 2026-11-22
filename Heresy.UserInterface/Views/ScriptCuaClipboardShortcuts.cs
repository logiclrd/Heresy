using System;

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

using AvaloniaEdit;

namespace Heresy.UserInterface.Views;

/// <summary>
/// IBM CUA clipboard aliases for text fields in scripted-object editors.
/// Dispatch through the control's existing Cut/Copy/Paste implementation:
/// selection, token projection, clipboard permissions, read-only handling
/// and undo remain owned by AvaloniaEdit or Avalonia TextBox.
/// </summary>
internal static class ScriptCuaClipboardShortcuts
{
    public static void Attach(TextEditor source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Attach(source.TextArea, source.Cut, source.Copy, source.Paste);
    }

    public static void Attach(TextBox source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Attach(source, source.Cut, source.Copy, source.Paste);
    }

    private static void Attach(InputElement input, Action cut, Action copy, Action paste)
        => input.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Handled)
                return;
            Action? command = (e.Key, e.KeyModifiers) switch
            {
                (Key.Delete, KeyModifiers.Shift) => cut,
                (Key.Insert, KeyModifiers.Control) => copy,
                (Key.Insert, KeyModifiers.Shift) => paste,
                _ => null,
            };
            if (command is null)
                return;
            e.Handled = true;
            command();
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
}
