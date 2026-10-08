using System;
using System.Collections.Generic;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;

using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
using Heresy.UserInterface.Documents;

namespace Heresy.UserInterface.Dialogs;

public sealed class FmSynthImportSelectionDialog : Window
{
	private readonly List<(FmSynthDefinition Synth, CheckBox CheckBox)> _choices = [];

	public FmSynthImportSelectionDialog(
		SongFmSynthImportSource source)
	{
		ArgumentNullException.ThrowIfNull(source);

		Title = "Import FM synths from Heresy song";
		Width = 540;
		Height = 460;
		MinWidth = 380;
		MinHeight = 280;
		WindowStartupLocation =
			WindowStartupLocation.CenterOwner;

		TextBlock sourceLabel =
			new()
			{
				Text =
					$"Choose FM synths to import from {System.IO.Path.GetFileName(source.Path)}. Referenced envelopes are imported automatically with fresh object IDs.",
				TextWrapping =
					Avalonia.Media.TextWrapping.Wrap,
			};

		StackPanel choices =
			new()
			{
				Spacing = 4,
			};
		foreach (FmSynthDefinition synth in source.Synths)
		{
			CheckBox checkBox =
				new()
				{
					Content =
						$"{synth.Name}  (Object {synth.Id.Value})",
					IsChecked = true,
				};
			_choices.Add(
				(synth, checkBox));
			choices.Children.Add(checkBox);
		}

		ScrollViewer scroll =
			new()
			{
				Content = choices,
				VerticalScrollBarVisibility =
					ScrollBarVisibility.Auto,
			};

		Button selectAll =
			new()
			{
				Content = "Select all",
			};
		selectAll.Click += (_, _) =>
		{
			foreach (var choice in _choices)
				choice.CheckBox.IsChecked = true;
		};

		Button selectNone =
			new()
			{
				Content = "Select none",
			};
		selectNone.Click += (_, _) =>
		{
			foreach (var choice in _choices)
				choice.CheckBox.IsChecked = false;
		};

		Button cancel =
			new()
			{
				Content = "Cancel",
				MinWidth = 84,
			};
		cancel.Click += (_, _) =>
			Close(null);

		Button import =
			new()
			{
				Content = "Import selected",
				MinWidth = 112,
			};
		import.Click += (_, _) =>
		{
			ObjectId[] selected =
				_choices
					.Where(choice =>
						choice.CheckBox.IsChecked == true)
					.Select(choice =>
						choice.Synth.Id)
					.ToArray();
			Close(selected);
		};

		DialogActionLayout.ConfigureButtons(import, cancel);

		StackPanel selectionActions =
			new()
			{
				Orientation =
					Orientation.Horizontal,
				Spacing = 6,
			};
		selectionActions.Children.Add(
			selectAll);
		selectionActions.Children.Add(
			selectNone);

		StackPanel dialogActions =
			new()
			{
				Orientation =
					Orientation.Horizontal,
				HorizontalAlignment =
					HorizontalAlignment.Right,
				Spacing = 8,
			};
		dialogActions.Children.Add(cancel);
		dialogActions.Children.Add(import);
		dialogActions.VerticalAlignment = VerticalAlignment.Bottom;

		Grid root =
			new()
			{
				Margin = new Thickness(14),
				RowDefinitions =
					new RowDefinitions(
						"Auto,Auto,*,Auto"),
				RowSpacing = 10,
			};
		Grid.SetRow(
			sourceLabel,
			0);
		Grid.SetRow(
			selectionActions,
			1);
		Grid.SetRow(
			scroll,
			2);
		Grid.SetRow(
			dialogActions,
			3);
		root.Children.Add(sourceLabel);
		root.Children.Add(selectionActions);
		root.Children.Add(scroll);
		root.Children.Add(dialogActions);

		Content = root;
	}
}
