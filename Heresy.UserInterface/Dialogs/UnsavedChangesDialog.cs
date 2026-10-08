using System;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

using Heresy.UserInterface.Documents;

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

		Button cancel =
			new()
			{
				Content = "Cancel",
				MinWidth = 90,
			};
		cancel.Click += (_, _) =>
			Close(UnsavedChangesChoice.Cancel);

		Button no =
			new()
			{
				Content = "No",
				MinWidth = 90,
			};
		no.Click += (_, _) =>
			Close(UnsavedChangesChoice.Discard);

		Button yes =
			new()
			{
				Content = "Yes",
				MinWidth = 90,
			};
		yes.Click += (_, _) =>
			Close(UnsavedChangesChoice.Save);

		StackPanel buttons =
			new()
			{
				Orientation =
					Orientation.Horizontal,
				HorizontalAlignment =
					HorizontalAlignment.Right,
				Spacing = 8,
			};
		buttons.Children.Add(cancel);
		buttons.Children.Add(no);
		buttons.Children.Add(yes);

		StackPanel content =
			new()
			{
				Margin = new Thickness(18),
				Spacing = 16,
			};
		content.Children.Add(
			new TextBlock
			{
				Text =
					$"Save changes to {documentName}?",
				TextWrapping =
					TextWrapping.Wrap,
			});
		content.Children.Add(buttons);
		Content = content;
	}
}
