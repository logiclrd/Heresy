using System;

namespace Heresy.UserInterface.FmEditing;

/// <summary>
/// Tracks the value of one editable FM parameter. Commit is invoked by Enter
/// or focus loss; Escape restores the last successfully committed value.
/// Earlier edits are not an undo stack: once committed, they are permanent
/// until the user makes another edit.
/// </summary>
public sealed class FmSynthParameterTextField
{
	public FmSynthParameterTextField(string committedText)
	{
		CommittedText =
			committedText ?? throw new ArgumentNullException(nameof(committedText));
	}

	public string CommittedText { get; private set; }

	/// <summary>
	/// Calls the model mutation before moving the committed baseline. If model
	/// validation throws, Escape still restores the original value.
	/// </summary>
	public bool Commit(string? draft, Action<string> apply)
	{
		ArgumentNullException.ThrowIfNull(apply);
		string text = draft ?? string.Empty;
		if (text == CommittedText)
			return false;

		apply(text);
		CommittedText = text;
		return true;
	}

	public string Revert() => CommittedText;
}
