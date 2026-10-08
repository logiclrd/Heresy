using System;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Heresy.UserInterface.Dialogs;

/// <summary>
/// Standard dialog keyboard roles and a footer that remains at the bottom of
/// the window, even when the content area grows or the window is resized.
/// </summary>
public static class DialogActionLayout
{
	public static void ConfigureButtons(
		Button accept,
		Button cancel)
	{
		ArgumentNullException.ThrowIfNull(accept);
		ArgumentNullException.ThrowIfNull(cancel);
		if (ReferenceEquals(accept, cancel))
			throw new ArgumentException(
				"Accept and cancel must be different buttons.");

		accept.IsDefault = true;
		cancel.IsCancel = true;
	}

	public static Grid Create(
		Control content,
		Control actions,
		Thickness margin,
		double spacing = 12)
	{
		ArgumentNullException.ThrowIfNull(content);
		ArgumentNullException.ThrowIfNull(actions);

		Grid root =
			new()
			{
				Margin = margin,
				RowDefinitions = new RowDefinitions("*,Auto"),
				RowSpacing = spacing,
			};
		actions.HorizontalAlignment = HorizontalAlignment.Right;
		actions.VerticalAlignment = VerticalAlignment.Bottom;
		Grid.SetRow(content, 0);
		Grid.SetRow(actions, 1);
		root.Children.Add(content);
		root.Children.Add(actions);
		return root;
	}
}
