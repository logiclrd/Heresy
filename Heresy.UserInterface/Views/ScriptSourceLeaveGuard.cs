using System;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.VisualTree;

using Heresy.UserInterface.Documents;

namespace Heresy.UserInterface.Views;

/// <summary>
/// A script editor owns its uncommitted text buffer. Saving here applies
/// source to the song, not to a file; disk persistence remains a host action.
/// The future editor-local Undo history can be added without changing this
/// explicit commit/leave boundary.
/// </summary>
public interface IScriptSourceEditor
{
    string ScriptSourceDisplayName { get; }
    bool HasUnsavedScriptSource { get; }
    bool TrySaveScriptSource();
    void DiscardUnsavedScriptSource();
}

/// <summary>
/// An editor recognizes Ctrl+S only when its source text area or associated
/// Save button originated the key event; other focus contexts keep File Save.
/// A dirty source/group consumes Ctrl+S locally (including failed saves).
/// Only an *already-clean* source/group forwards Ctrl+S to document Save.
/// This decision is made before applying source changes, so two presses
/// perform two deliberate commits without accidentally saving to disk.
/// </summary>
public enum ScriptSourceSaveShortcutRoute
{
    NotApplicable,
    SaveLocally,
    SaveDocument,
}

public interface IScriptSourceShortcutTarget
{
    ScriptSourceSaveShortcutRoute TryHandleSourceSaveShortcut(Visual? keySource);
}

internal static class ScriptSourceShortcutRouting
{
    public static bool IsWithin(Visual? source, Visual target)
    {
        if (source is null)
            return false;
        if (ReferenceEquals(source, target))
            return true;
        foreach (Visual ancestor in source.GetVisualAncestors())
            if (ReferenceEquals(ancestor, target))
                return true;
        return false;
    }
}

/// <summary>One shared Save / Discard / Cancel decision for all script editors.</summary>
public static class ScriptSourceLeaveGuard
{
    public static async Task<bool> CanProceedAsync(
        IScriptSourceEditor editor,
        Func<Task<UnsavedChangesChoice>> chooseAsync)
    {
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(chooseAsync);

        if (!editor.HasUnsavedScriptSource)
            return true;

        UnsavedChangesChoice choice = await chooseAsync();
        switch (choice)
        {
            case UnsavedChangesChoice.Save:
                return editor.TrySaveScriptSource() && !editor.HasUnsavedScriptSource;
            case UnsavedChangesChoice.Discard:
                editor.DiscardUnsavedScriptSource();
                return !editor.HasUnsavedScriptSource;
            case UnsavedChangesChoice.Cancel:
                return false;
            default:
                throw new ArgumentOutOfRangeException(nameof(choice));
        }
    }
}
