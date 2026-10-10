using System;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;


namespace Heresy.UserInterface.Dialogs;

public sealed class UnsavedChangesDialog : Window
{
	public UnsavedChangesDialog(
		string documentName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(documentName);

		Title = "Save changes?";
		Width = 520;
		Height = 240;
		CanResize = false;
		WindowStartupLocation =
			WindowStartupLocation.CenterOwner;

		// The access keys (_Yes / _No / _Cancel), Enter/Escape roles,
		// and left-to-right order share one headless-testable definition.
		Button[] actions = UnsavedChangesDialogActions.Create(
			choice => Close(choice));

		StackPanel buttons =
			new()
			{
				Orientation =
					Orientation.Horizontal,
				HorizontalAlignment =
					HorizontalAlignment.Right,
				Spacing = 8,
			};
		foreach (Button action in actions)
			buttons.Children.Add(action);

		Content =
			DialogActionLayout.Create(
				new TextBlock
				{
					Text =
						$"Save changes to {documentName}?",
					TextWrapping =
						TextWrapping.Wrap,
				},
				buttons,
				new Thickness(18),
				spacing: 16);
	}
}
