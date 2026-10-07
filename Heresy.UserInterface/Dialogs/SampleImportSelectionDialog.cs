using System;
using System.Collections.Generic;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

using Heresy.Core.Objects;
using Heresy.Core.Samples;
using Heresy.UserInterface.Documents;

namespace Heresy.UserInterface.Dialogs;

public sealed class SampleImportSelectionDialog : Window
{
	private readonly List<(SampleDefinition Sample, CheckBox CheckBox)> _choices = [];

	public SampleImportSelectionDialog(
		SongSampleImportSource source)
	{
		ArgumentNullException.ThrowIfNull(source);

		Title = "Import samples from Heresy song";
		Width = 520;
		Height = 460;
		MinWidth = 360;
		MinHeight = 280;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;

		TextBlock sourceLabel =
			new()
			{
				Text =
					$"Choose samples to import from {System.IO.Path.GetFileName(source.Path)}:",
				TextWrapping = Avalonia.Media.TextWrapping.Wrap,
			};

		StackPanel choices =
			new()
			{
				Spacing = 4,
			};
		foreach (SampleDefinition sample in source.Samples)
		{
			CheckBox checkBox =
				new()
				{
					Content = $"{sample.Name}  (Object {sample.Id.Value})",
					IsChecked = true,
				};
			_choices.Add((sample, checkBox));
			choices.Children.Add(checkBox);
		}

		ScrollViewer scroll =
			new()
			{
				Content = choices,
				VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			};

		Button selectAll = new() { Content = "Select all" };
		selectAll.Click += (_, _) =>
		{
			foreach ((_, CheckBox checkBox) in _choices)
				checkBox.IsChecked = true;
		};

		Button selectNone = new() { Content = "Select none" };
		selectNone.Click += (_, _) =>
		{
			foreach ((_, CheckBox checkBox) in _choices)
				checkBox.IsChecked = false;
		};

		Button cancel = new() { Content = "Cancel", MinWidth = 84 };
		cancel.Click += (_, _) => Close(null);

		Button import = new() { Content = "Import selected", MinWidth = 112 };
		import.Click += (_, _) =>
		{
			ObjectId[] selected =
				_choices
					.Where(choice => choice.CheckBox.IsChecked == true)
					.Select(choice => choice.Sample.Id)
					.ToArray();
			Close(selected);
		};

		StackPanel selectionActions =
			new()
			{
				Orientation = Orientation.Horizontal,
				Spacing = 6,
			};
		selectionActions.Children.Add(selectAll);
		selectionActions.Children.Add(selectNone);

		StackPanel dialogActions =
			new()
			{
				Orientation = Orientation.Horizontal,
				HorizontalAlignment = HorizontalAlignment.Right,
				Spacing = 8,
			};
		dialogActions.Children.Add(cancel);
		dialogActions.Children.Add(import);

		Grid root = new()
			{
				Margin = new Thickness(14),
				RowDefinitions =
				new RowDefinitions("Auto,Auto,*,Auto"),
				RowSpacing = 10,
			};
		Grid.SetRow(sourceLabel, 0);
		Grid.SetRow(selectionActions, 1);
		Grid.SetRow(scroll, 2);
		Grid.SetRow(dialogActions, 3);
		root.Children.Add(sourceLabel);
		root.Children.Add(selectionActions);
		root.Children.Add(scroll);
		root.Children.Add(dialogActions);

		Content = root;
	}
}
