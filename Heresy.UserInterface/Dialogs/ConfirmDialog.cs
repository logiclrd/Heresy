using System;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Heresy.UserInterface.Dialogs;

public sealed class ConfirmDialog : Window
{
	public ConfirmDialog(
		string title,
		string message,
		string confirmText)
	{
		ArgumentNullException.ThrowIfNull(title);
		ArgumentNullException.ThrowIfNull(message);
		ArgumentNullException.ThrowIfNull(confirmText);

		Title = title;
		Width = 520;
		Height = 260;
		CanResize = false;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;

		Button cancel = new() { Content = "Cancel", MinWidth = 90 };
		cancel.Click += (_, _) => Close(false);

		Button confirm = new() { Content = confirmText, MinWidth = 90 };
		confirm.Click += (_, _) => Close(true);

		StackPanel buttons =
			new()
			{
				Orientation = Orientation.Horizontal,
				HorizontalAlignment = HorizontalAlignment.Right,
				Spacing = 8,
			};
		buttons.Children.Add(cancel);
		buttons.Children.Add(confirm);

		StackPanel content =
			new()
			{
				Margin = new Thickness(18),
				Spacing = 16,
			};
		content.Children.Add(
			new TextBlock
			{
				Text = message,
				TextWrapping = TextWrapping.Wrap,
			});
		content.Children.Add(buttons);
		Content = content;
	}
}
