using System;
using System.Threading.Tasks;

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
