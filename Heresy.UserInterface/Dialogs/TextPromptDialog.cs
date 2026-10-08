using System;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Heresy.UserInterface.Dialogs;

public sealed class TextPromptDialog : Window
{
	private readonly TextBox _textBox;

	public TextPromptDialog(
		string title,
		string prompt,
		string initialValue)
	{
		ArgumentNullException.ThrowIfNull(title);
		ArgumentNullException.ThrowIfNull(prompt);
		ArgumentNullException.ThrowIfNull(initialValue);

		Title = title;
		Width = 420;
		Height = 180;
		CanResize = false;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;

		_textBox =
			new TextBox
			{
				Text = initialValue,
				HorizontalAlignment = HorizontalAlignment.Stretch,
			};

		Button cancel = new() { Content = "Cancel", MinWidth = 80 };
		cancel.Click += (_, _) => Close((string?)null);

		Button accept = new() { Content = "OK", MinWidth = 80 };
		accept.Click += (_, _) => Accept();
		DialogActionLayout.ConfigureButtons(accept, cancel);

		StackPanel buttons =
			new()
			{
				Orientation = Orientation.Horizontal,
				HorizontalAlignment = HorizontalAlignment.Right,
				Spacing = 8,
			};
		buttons.Children.Add(cancel);
		buttons.Children.Add(accept);

		StackPanel content =
			new()
			{
				Spacing = 12,
			};
		content.Children.Add(new TextBlock { Text = prompt });
		content.Children.Add(_textBox);
		Content = DialogActionLayout.Create(
			content,
			buttons,
			new Thickness(16));

		Opened += (_, _) =>
		{
			_textBox.Focus();
			_textBox.SelectAll();
		};
	}

	private void Accept()
	{
		string value = (_textBox.Text ?? string.Empty).Trim();
		if (value.Length == 0)
			return;

		Close(value);
	}
}
