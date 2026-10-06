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

/// <summary>
/// One generic native-effect dialog used for both editing an existing effect and
/// creating a new one. Creation adds a type selector; both modes render and
/// rebuild parameters through NativePatternEffectEditor.
/// </summary>
public sealed class NativePatternEffectEditorDialog : Window
{
	private readonly bool _creationMode;
	private readonly Dictionary<string, Control> _inputs = [];
	private readonly TextBlock _message = new();
	private readonly Grid _form = new();
	private readonly ComboBox? _typeSelector;

	private PatternEffect _prototype;
	private NativePatternEffectEditModel _model;

	public NativePatternEffectEditorDialog(PatternEffect effect)
	{
		_creationMode = false;
		_prototype = effect
			?? throw new ArgumentNullException(nameof(effect));
		_model =
			NativePatternEffectEditor.Describe(
				effect,
				CultureInfo.CurrentCulture);

		Title = $"Edit effect — {_model.Title}";
		Width = 520;
		Height = 380;
		CanResize = false;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;
		Content = BuildContent();
		RebuildForm();
	}

	public NativePatternEffectEditorDialog()
	{
		_creationMode = true;
		NativePatternEffectChoice[] choices =
			NativePatternEffectEditor.GetCreationChoices();
		if (choices.Length == 0)
		{
			throw new InvalidOperationException(
				"No native pattern effects are available for creation.");
		}

		_typeSelector =
			new ComboBox
			{
				ItemsSource = choices,
				SelectedIndex = 0,
			};
		NativePatternEffectChoice initial = choices[0];
		_prototype =
			NativePatternEffectEditor.CreateDefault(initial.Kind);
		_model =
			NativePatternEffectEditor.Describe(
				_prototype,
				CultureInfo.CurrentCulture);

		Title = "Insert native effect";
		Width = 520;
		Height = 430;
		CanResize = false;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;
		Content = BuildContent();
		RebuildForm();

		_typeSelector.SelectionChanged += (_, _) =>
			ChangeCreationType();
	}

	private Control BuildContent()
	{
		_form.ColumnDefinitions.Add(
			new ColumnDefinition(GridLength.Auto));
		_form.ColumnDefinitions.Add(
			new ColumnDefinition(
				new GridLength(1, GridUnitType.Star)));

		StackPanel body =
			new()
			{
				Spacing = 12,
			};

		if (_creationMode)
		{
			Grid typeRow = new();
			typeRow.ColumnDefinitions.Add(
				new ColumnDefinition(GridLength.Auto));
			typeRow.ColumnDefinitions.Add(
				new ColumnDefinition(
					new GridLength(1, GridUnitType.Star)));
			TextBlock label =
				new()
				{
					Text = "Effect type",
					Margin = new Thickness(0, 5, 12, 5),
					VerticalAlignment = VerticalAlignment.Center,
				};
			Grid.SetColumn(label, 0);
			Grid.SetColumn(_typeSelector!, 1);
			typeRow.Children.Add(label);
			typeRow.Children.Add(_typeSelector!);
			body.Children.Add(typeRow);
		}

		body.Children.Add(_form);
		body.Children.Add(_message);

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
				Content = _creationMode ? "Insert" : "Apply",
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
		body.Children.Add(buttons);

		return new Border
		{
			Padding = new Thickness(18),
			Child = body,
		};
	}

	private void ChangeCreationType()
	{
		if (_typeSelector?.SelectedItem
			is not NativePatternEffectChoice selected)
		{
			return;
		}

		_prototype =
			NativePatternEffectEditor.CreateDefault(selected.Kind);
		_model =
			NativePatternEffectEditor.Describe(
				_prototype,
				CultureInfo.CurrentCulture);
		_message.Text = string.Empty;
		RebuildForm();
	}

	private void RebuildForm()
	{
		_inputs.Clear();
		_form.Children.Clear();
		_form.RowDefinitions.Clear();

		int row = 0;
		foreach (NativePatternEffectField field in _model.Fields)
		{
			Control input = BuildInput(field);
			_inputs.Add(field.Key, input);
			AddField(_form, ref row, field.Label, input);
		}
	}

	private static Control BuildInput(
		NativePatternEffectField field)
	{
		if (field.Choices is not null)
		{
			return new ComboBox
			{
				ItemsSource = field.Choices,
				SelectedItem = field.Value,
			};
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
					_prototype,
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
