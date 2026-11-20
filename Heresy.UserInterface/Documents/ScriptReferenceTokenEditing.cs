using System;
using System.Collections.Generic;

namespace Heresy.UserInterface.Documents;

/// <summary>
/// Edits the canonical underlying script source when Backspace/Delete
/// crosses an atomic *visual* object-reference token. Tokens are a display
/// projection, never an alternate document representation.
/// </summary>
public static class ScriptReferenceTokenEditing
{
    public readonly record struct Deletion(int Offset, int CaretAfter);

    public static bool TryPlan(
        IReadOnlyList<ScriptReferenceVisualToken> tokens,
        int caretOffset,
        bool backspace,
        out Deletion deletion)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        foreach (ScriptReferenceVisualToken token in tokens)
        {
            if (backspace && caretOffset == token.SourceSpan.End)
            {
                // Backspace *into* the token from the right removes ')'.
                // The remaining _O(137 is free text, no longer a token.
                int offset = token.SourceSpan.End - 1;
                deletion = new Deletion(offset, offset);
                return true;
            }
            if (!backspace && caretOffset == token.SourceSpan.Start)
            {
                // Delete *into* the token from the left removes '_'.
                // The remaining O(137) is ordinary editable source text.
                deletion = new Deletion(token.SourceSpan.Start,
                    token.SourceSpan.Start);
                return true;
            }
        }
        deletion = default;
        return false;
    }
}
