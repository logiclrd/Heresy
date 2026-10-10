using System.Collections.Generic;

using Avalonia.Controls;
using Avalonia.Interactivity;

using Heresy.UserInterface.Dialogs;
using Heresy.UserInterface.Documents;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class UnsavedChangesDialogActionsTests
{
	[Test]
	public void ButtonsHaveOrderedUniqueAccessKeysAndCorrectKeyboardRoles()
	{
		Button[] buttons = UnsavedChangesDialogActions.Create(_ => { });

		Assert.That(buttons, Has.Length.EqualTo(3));
		Assert.Multiple(() =>
		{
			Assert.That(buttons[0].Content, Is.EqualTo("_Yes"));
			Assert.That(buttons[1].Content, Is.EqualTo("_No"));
			Assert.That(buttons[2].Content, Is.EqualTo("_Cancel"));

			Assert.That(buttons[0].IsDefault, Is.True);
			Assert.That(buttons[0].IsCancel, Is.False);
			Assert.That(buttons[1].IsDefault, Is.False);
			Assert.That(buttons[1].IsCancel, Is.False);
			Assert.That(buttons[2].IsDefault, Is.False);
			Assert.That(buttons[2].IsCancel, Is.True);
		});
	}

	[Test]
	public void ButtonsSendSaveDiscardCancelInVisualOrder()
	{
		List<UnsavedChangesChoice> choices = [];
		Button[] buttons = UnsavedChangesDialogActions.Create(choices.Add);

		foreach (Button button in buttons)
			button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

		Assert.That(choices, Is.EqualTo(new[]
		{
			UnsavedChangesChoice.Save,
			UnsavedChangesChoice.Discard,
			UnsavedChangesChoice.Cancel,
		}));
	}
}
