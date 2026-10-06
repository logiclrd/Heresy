using System;
using System.Collections.Generic;
using System.Globalization;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

using Heresy.Core.Patterns;
using Heresy.UserInterface.PatternEditing;

namespace Heresy.UserInterface.Dialogs;

public sealed record NativePatternEffectEditResult(
	PatternEffect Effect);

public sealed class NativePatternEffectEditorDialog : Window
{
	private readonly PatternEffect _original;
	private readonly NativePatternEffectEditModel _model;
	private readonly Dictionary<string, Control> _inputs = [];
	private readonly TextBlock _message = new();

	public NativePatternEffectEditorDialog(PatternEffect effect)
	{
		_original = effect
			?? throw new ArgumentNullException(nameof(effect));
		_model =
			NativePatternEffectEditor.Describe(
				effect,
				CultureInfo.CurrentCulture);

		Title = $"Edit effect — {_model.Title}";
		Width = 520;
		Height = Math.Max(260, 190 + (_model.Fields.Count * 54));
		CanResize = false;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;
		Content = BuildContent();
	}

	private Control BuildContent()
	{
		Grid form = new();
		form.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
		form.ColumnDefinitions.Add(
			new ColumnDefinition(
				new GridLength(1, GridUnitType.Star)));

		int row = 0;
		foreach (NativePatternEffectField field in _model.Fields)
		{
			Control input = BuildInput(field);
			_inputs.Add(field.Key, input);
			AddField(form, ref row, field.Label, input);
		}

		Button cancel =
			new()
			{
				Content = "Cancel",
				MinWidth = 90,
			};
		cancel.Click += (_, _) => Close(null);

		Button apply =
			new()
			{
				Content = "Apply",
				MinWidth = 90,
			};
		apply.Click += (_, _) => Apply();

		StackPanel buttons =
			new()
			{
				Orientation = Orientation.Horizontal,
				HorizontalAlignment = HorizontalAlignment.Right,
				Spacing = 8,
			};
		buttons.Children.Add(cancel);
		buttons.Children.Add(apply);

		StackPanel content =
			new()
			{
				Margin = new Thickness(18),
				Spacing = 12,
			};
		content.Children.Add(form);
		content.Children.Add(_message);
		content.Children.Add(buttons);
		return content;
	}

	private static Control BuildInput(
		NativePatternEffectField field)
	{
		if (field.Choices is not null)
		{
			ComboBox combo =
				new()
				{
					ItemsSource = field.Choices,
					SelectedItem = field.Value,
				};
			return combo;
		}

		return new TextBox
		{
			Text = field.Value,
		};
	}

	private void Apply()
	{
		try
		{
			Dictionary<string, string> values = [];
			foreach (NativePatternEffectField field in _model.Fields)
			{
				Control input = _inputs[field.Key];
				string value =
					input switch
					{
						TextBox text => text.Text ?? string.Empty,
						ComboBox combo =>
							combo.SelectedItem?.ToString()
								?? string.Empty,
						_ => throw new InvalidOperationException(
							$"Unsupported native-effect input control {input.GetType().Name}."),
					};
				values.Add(field.Key, value);
			}

			PatternEffect rebuilt =
				NativePatternEffectEditor.Rebuild(
					_original,
					values,
					CultureInfo.CurrentCulture);
			Close(
				new NativePatternEffectEditResult(
					rebuilt));
		}
		catch (Exception ex)
		{
			_message.Text = ex.Message;
		}
	}

	private static void AddField(
		Grid grid,
		ref int row,
		string label,
		Control control)
	{
		grid.RowDefinitions.Add(
			new RowDefinition(GridLength.Auto));

		TextBlock labelBlock =
			new()
			{
				Text = label,
				Margin = new Thickness(0, 5, 12, 5),
				VerticalAlignment = VerticalAlignment.Center,
			};
		Grid.SetRow(labelBlock, row);
		Grid.SetColumn(labelBlock, 0);
		grid.Children.Add(labelBlock);

		control.Margin = new Thickness(0, 3);
		Grid.SetRow(control, row);
		Grid.SetColumn(control, 1);
		grid.Children.Add(control);
		row++;
	}
}
