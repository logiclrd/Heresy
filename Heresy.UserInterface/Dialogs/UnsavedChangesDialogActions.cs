using System;

using Avalonia.Controls;

using Heresy.UserInterface.Documents;

namespace Heresy.UserInterface.Dialogs;

/// <summary>
/// Creates the Save / Discard / Cancel actions without constructing a Window,
/// allowing their ordering, access keys and Enter/Escape roles to be tested
/// on headless CI. The Fluent button presenter renders _X as an Alt+X
/// access key.
/// </summary>
public static class UnsavedChangesDialogActions
{
	public static Button[] Create(Action<UnsavedChangesChoice> choose)
	{
		ArgumentNullException.ThrowIfNull(choose);

		Button yes = new()
		{
			Content = "_Yes",
			MinWidth = 90,
		};
		yes.Click += (_, _) => choose(UnsavedChangesChoice.Save);

		Button no = new()
		{
			Content = "_No",
			MinWidth = 90,
		};
		no.Click += (_, _) => choose(UnsavedChangesChoice.Discard);

		Button cancel = new()
		{
			Content = "_Cancel",
			MinWidth = 90,
		};
		cancel.Click += (_, _) => choose(UnsavedChangesChoice.Cancel);

		DialogActionLayout.ConfigureButtons(yes, cancel);
		return [yes, no, cancel];
	}
}
