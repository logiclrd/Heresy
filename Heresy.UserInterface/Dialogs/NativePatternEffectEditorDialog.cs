using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Heresy.Core.Filters;
using Heresy.Core.Objects;

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
	private readonly SongDocument? _document;
	private readonly StackPanel _assistant = new() { Spacing = 7 };
	private sealed record StageChoice(FilterStageDefinition Stage)
	{
		public override string ToString() =>
			$"{Stage.Name} ({Stage.Kind})";
	}

	private PatternEffect _prototype;
	private NativePatternEffectEditModel _model;

	public NativePatternEffectEditorDialog(
		PatternEffect effect, SongDocument? document = null)
	{
		_document = document;
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

	public NativePatternEffectEditorDialog(SongDocument? document = null)
	{
		_document = document;
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
		body.Children.Add(_assistant);
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
		DialogActionLayout.ConfigureButtons(apply, cancel);

		StackPanel buttons =
			new()
			{
				Orientation = Orientation.Horizontal,
				HorizontalAlignment = HorizontalAlignment.Right,
				Spacing = 8,
			};
		buttons.Children.Add(cancel);
		buttons.Children.Add(apply);

		return DialogActionLayout.Create(
			body,
			buttons,
			new Thickness(18));
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
		_assistant.Children.Clear();
		bool isFilterText = _prototype is
			SetChannelFilterAssignmentsPatternEffect
			or SetNoteFilterAssignmentsPatternEffect;
		Height = isFilterText ? 780 : _creationMode ? 430 : 380;
		Width = isFilterText ? 700 : 520;
		CanResize = isFilterText;

		int row = 0;
		foreach (NativePatternEffectField field in _model.Fields)
		{
			Control input = BuildInput(field);
			_inputs.Add(field.Key, input);
			AddField(_form, ref row, field.Label, input);
		}
		if (isFilterText)
			BuildFilterAssistant();
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
			AcceptsReturn = field.Multiline,
			TextWrapping = field.Multiline
				? Avalonia.Media.TextWrapping.Wrap
				: Avalonia.Media.TextWrapping.NoWrap,
			MinHeight = field.Multiline ? 145 : 0,
		};
	}

	/// <summary>Descriptor-driven stage assistant for native Filter text.
	/// Unlike shared Filter editing, this changes the command's own text
	/// only. Rewriting does not alter the SongDocument or Filter definition.</summary>
	private void BuildFilterAssistant()
	{
		if (_document is null)
		{
			_assistant.Children.Add(new TextBlock
			{
				Text = "Filter stage assistance requires a song document.",
			});
			return;
		}
		FilterDefinition[] filters = _document.Objects.Values
			.OfType<FilterDefinition>()
			.OrderBy(filter => filter.Name, StringComparer.OrdinalIgnoreCase)
			.ToArray();
		if (filters.Length == 0)
		{
			_assistant.Children.Add(new TextBlock
			{
				Text = "Create a shared Filter definition to configure parameter commands.",
			});
			return;
		}

		StackPanel selector = new()
		{
			Orientation = Orientation.Horizontal,
			Spacing = 8,
		};
		selector.Children.Add(new TextBlock
		{
			Text = "Configuration assistant · Filter",
			VerticalAlignment = VerticalAlignment.Center,
		});
		ComboBox filterPicker = new()
		{
			ItemsSource = filters.Select(x =>
				new FilterChoice(x.Id, x.Name)).ToArray(),
			MinWidth = 180,
		};
		selector.Children.Add(filterPicker);
		selector.Children.Add(new TextBlock
		{
			Text = "Stage",
			VerticalAlignment = VerticalAlignment.Center,
		});
		ComboBox stagePicker = new() { MinWidth = 165 };
		selector.Children.Add(stagePicker);
		_assistant.Children.Add(selector);

		TextBlock guidance = new()
		{
			TextWrapping = Avalonia.Media.TextWrapping.Wrap,
		};
		_assistant.Children.Add(guidance);
		StackPanel parameterFields = new() { Spacing = 4 };
		_assistant.Children.Add(parameterFields);
		TextBox annotation = new()
		{
			PlaceholderText = "Describe the Filter behavior or target",
		};
		_assistant.Children.Add(annotation);
		Button replace = new()
		{
			Content = "Replace this stage's assignments",
			HorizontalAlignment = HorizontalAlignment.Left,
		};
		_assistant.Children.Add(replace);

		Dictionary<string, Control> parameters = [];
		FilterDefinition? selectedFilter = null;
		void LoadStage()
		{
			parameterFields.Children.Clear();
			parameters.Clear();
			if (selectedFilter is null
				|| stagePicker.SelectedItem is not StageChoice choice)
				return;
			FilterStageDefinition stage = choice.Stage;
			annotation.Text = stage.Kind.ToString();
			foreach (FilterParameterDescriptor descriptor in
				FilterCatalogue.GetParameters(stage.Kind))
			{
				string defaultText = descriptor.DefaultValue.Kind
					== FilterParameterValueKind.Number
					? stage.DefaultParameters[descriptor.Name].NumericValue.ToString(
						"G17", CultureInfo.InvariantCulture)
					: stage.DefaultParameters[descriptor.Name].ChoiceValue!;
				Control input = descriptor.Kind == FilterParameterValueKind.Choice
					? new ComboBox
					{
						ItemsSource = descriptor.Choices,
						SelectedItem = defaultText,
						MinWidth = 155,
					}
					: new TextBox
					{
						Text = defaultText,
						MinWidth = 155,
					};
				string limits = descriptor.Kind == FilterParameterValueKind.Number
					? $" [{descriptor.Minimum:G}..{descriptor.Maximum:G}]{descriptor.Units}"
					: $" ({string.Join(", ", descriptor.Choices)})";
				string modifier = descriptor.IsStructural ? " · structural" : "";
				ToolTip.SetTip(input,
					descriptor.Name + limits + modifier);
				StackPanel line = new()
				{
					Orientation = Orientation.Horizontal,
					Spacing = 7,
				};
				line.Children.Add(new TextBlock
				{
					Text = descriptor.Name + limits + modifier,
					Width = 325,
					VerticalAlignment = VerticalAlignment.Center,
				});
				line.Children.Add(input);
				parameters.Add(descriptor.Name, input);
				parameterFields.Children.Add(line);
			}
			guidance.Text = $"{stage.Name}: {stage.Kind}. "
				+ "Values and option lists come from this Filter type's actual "
				+ "parameter descriptors. Only this stage's block is replaced; "
				+ "all other stage assignments and comments remain untouched.";
		}
		filterPicker.SelectionChanged += (_, _) =>
		{
			if (filterPicker.SelectedItem is not FilterChoice selected
				|| !_document.TryGet(selected.Id, out SongObject? obj)
				|| obj is not FilterDefinition definition)
				return;
			selectedFilter = definition;
			if (_inputs["filterId"] is TextBox idInput)
				idInput.Text = selected.Id.ToString();
			stagePicker.ItemsSource =
				definition.Stages.Select(stage => new StageChoice(stage)).ToArray();
			stagePicker.SelectedIndex = 0;
		};
		stagePicker.SelectionChanged += (_, _) => LoadStage();
		replace.Click += (_, _) =>
		{
			try
			{
				if (selectedFilter is null
					|| stagePicker.SelectedItem is not StageChoice selection
					|| _inputs["assignments"] is not TextBox sourceInput)
					throw new ArgumentException("Select a Filter and stage.");
				List<KeyValuePair<string, FilterParameterValue>> assignments = [];
				foreach (FilterParameterDescriptor descriptor in
					FilterCatalogue.GetParameters(selection.Stage.Kind))
				{
					Control control = parameters[descriptor.Name];
					string value = control switch
					{
						TextBox text => text.Text ?? "",
						ComboBox combo => combo.SelectedItem as string ?? "",
						_ => "",
					};
					FilterParameterValue typed;
					if (descriptor.Kind == FilterParameterValueKind.Choice)
						typed = FilterParameterValue.FromChoice(value);
					else if (double.TryParse(value, NumberStyles.Float,
						CultureInfo.InvariantCulture, out double number))
						typed = FilterParameterValue.FromNumber(number);
					else
						throw new ArgumentException(
							$"{descriptor.Name} is not a valid invariant-culture number.");
					assignments.Add(new(descriptor.Name, descriptor.Normalize(typed)));
				}
				FilterInstance selected = new(selectedFilter);
				string rewritten = FilterAssignmentText.RewriteStage(
					selected, sourceInput.Text ?? "", selection.Stage.StageId,
					assignments, annotation.Text ?? "");
				FilterAssignmentText.ParseAndValidate(selected, rewritten);
				sourceInput.Text = rewritten;
				_message.Text = $"Replaced {selection.Stage.Name} assignments.";
			}
			catch (Exception ex)
			{
				_message.Text = ex.Message;
			}
		};
		string? hintId = (_inputs["filterId"] as TextBox)?.Text;
		ObjectId? hinted = uint.TryParse(hintId,
			NumberStyles.None, CultureInfo.InvariantCulture, out uint id)
			? (ObjectId)id : null;
		FilterChoice[] available = (FilterChoice[])filterPicker.ItemsSource!;
		filterPicker.SelectedItem = available.FirstOrDefault(choice =>
			choice.Id == hinted) ?? available[0];
	}

	private sealed record FilterChoice(ObjectId Id, string Name)
	{
		public override string ToString() => $"{Name} (#{Id.Value})";
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
					CultureInfo.CurrentCulture,
					_document);
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
