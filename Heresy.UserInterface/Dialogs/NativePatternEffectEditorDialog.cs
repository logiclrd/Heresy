using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Heresy.Core.Filters;
using Heresy.Core.Objects;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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
	private (Type EffectType, string Assignments, string FilterId)? _warnedDraft;

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

		// Tunnel so a focused multiline editor cannot consume Ctrl+Enter
		// before the dialog sees the explicit Apply/Insert gesture.
		AddHandler(InputElement.KeyDownEvent, OnDialogKeyDown,
			RoutingStrategies.Tunnel);

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
		_warnedDraft = null;
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
			// A confirmation is permission for one *unchanged editing gesture*,
			// not a standing exemption for text that happens to compare equal.
			// Editing and undoing back to the same bytes must warn again.
			if (isFilterText && (field.Key is "assignments" or "filterId")
				&& input is TextBox text)
				text.TextChanged += (_, _) => InvalidateFilterWarningAcknowledgement();
			AddField(_form, ref row, field.Label, input);
		}
		if (isFilterText)
			BuildFilterAssistant();
		else if (_prototype is SetChannelFilterPatternEffect
			or SetNoteFilterPatternEffect)
			BuildFilterSelectionAssistant();
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
	/// <summary>Use the song's actual Filter objects instead of making
	/// users memorize numeric IDs when selecting or clearing a Filter.</summary>
	private void BuildFilterSelectionAssistant()
	{
		if (_document is null)
			return;
		FilterChoice[] choices =
		[
			new(ObjectId.None, "(Clear current Filter)"),
			.. _document.Objects.Values.OfType<FilterDefinition>()
				.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
				.Select(x => new FilterChoice(x.Id, x.Name)),
		];
		ComboBox picker = new()
		{
			ItemsSource = choices,
			MinWidth = 260,
		};
		_assistant.Children.Add(new TextBlock
		{
			Text = "Choose an available shared Filter definition, or clear:",
		});
		_assistant.Children.Add(picker);
		picker.SelectionChanged += (_, _) =>
		{
			if (picker.SelectedItem is FilterChoice choice
				&& _inputs["filterId"] is TextBox id)
				id.Text = choice.Id.IsNone ? "" : choice.Id.ToString();
		};
		string hint = (_inputs["filterId"] as TextBox)?.Text ?? "";
		picker.SelectedItem = uint.TryParse(hint, NumberStyles.None,
			CultureInfo.InvariantCulture, out uint id)
			? choices.FirstOrDefault(x => x.Id == (ObjectId)id) ?? choices[0]
			: choices[0];
	}

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
			Content = "Update this stage's assignments",
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
			// Populate directly from the current authoring text, never from
			// a cached preview that could have become stale after manual edits.
			var preview = FilterAssignmentText.PreviewStage(
				new FilterInstance(selectedFilter),
				(_inputs["assignments"] as TextBox)?.Text ?? "",
				stage.StageId);
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
				if (preview.TryGetValue(descriptor.Name, out FilterParameterValue? authored)
					&& authored is not null)
				{
					string presented = authored.Kind == FilterParameterValueKind.Number
						? authored.NumericValue.ToString("G17", CultureInfo.InvariantCulture)
						: authored.ChoiceValue!;
					if (input is TextBox authoredText)
						authoredText.Text = presented;
					else if (input is ComboBox authoredChoice)
						authoredChoice.SelectedItem = presented;
				}
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
				+ "Parameters start with valid values from the current script, "
				+ "otherwise with the definition's defaults. Updating edits "
				+ "existing rows in place and adds missing parameters; "
				+ "unrelated lines and comments remain untouched.";
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
				// Update the text draft regardless of unrelated malformed rows.
				// Apply/Insert warns on first invalid draft and confirms unchanged text on second.
				sourceInput.Text = rewritten;
				try
				{
					FilterAssignmentEnvelopeText.ParseAndValidate(selected, rewritten,
						FilterAssignmentEnvelopeNames.Capture(_document));
					_message.Text = $"Updated {selection.Stage.Name} assignments.";
				}
				catch (ArgumentException ex)
				{
					_message.Text = $"Updated draft; remaining validation issue: {ex.Message}";
				}
			}
			catch (Exception ex)
			{
				_message.Text = ex.Message;
			}
		};
		string source = (_inputs["assignments"] as TextBox)?.Text ?? "";
		string? hintId = (_inputs["filterId"] as TextBox)?.Text;
		ObjectId? hinted = uint.TryParse(hintId,
			NumberStyles.None, CultureInfo.InvariantCulture, out uint id)
			? (ObjectId)id : null;
		(FilterDefinition? fromComment, string? heading) =
			FindLastRecognizedFilterComment(filters, source);
		FilterChoice[] available = (FilterChoice[])filterPicker.ItemsSource!;
		ObjectId wanted = fromComment?.Id ?? hinted ?? available[0].Id;
		filterPicker.SelectedItem = available.FirstOrDefault(choice =>
			choice.Id == wanted) ?? available[0];
		if (selectedFilter is not null)
		{
			string? stageName = heading
				?? FindLastStageInScript(selectedFilter, source);
			if (stageName is not null
				&& stagePicker.ItemsSource is IEnumerable<StageChoice> stages)
			{
				StageChoice? item = stages.FirstOrDefault(stage =>
					string.Equals(stage.Stage.Name, stageName,
						StringComparison.OrdinalIgnoreCase));
				if (item is not null)
					stagePicker.SelectedItem = item;
			}
		}
	}

	// Explicit Filter comments and stage headings that uniquely identify
	// a Filter take precedence over the optional authoring hint; the latest
	// recognized comment wins. Ambiguous labels never select arbitrarily.
	private static (FilterDefinition?, string?) FindLastRecognizedFilterComment(
		FilterDefinition[] filters, string source)
	{
		foreach (string line in source.Replace("\r\n", "\n")
			.Replace('\r', '\n').Split('\n').Reverse())
		{
			string trimmed = line.TrimStart();
			if (!trimmed.StartsWith('#')) continue;
			string comment = trimmed[1..].TrimStart();
			int colon = comment.IndexOf(':');
			if (colon < 0) continue;
			string key = comment[..colon].Trim();
			string value = comment[(colon + 1)..].Trim();
			string name = key.Equals("Filter", StringComparison.OrdinalIgnoreCase)
				? value : key;
			FilterDefinition[] named = filters.Where(f =>
				string.Equals(f.Name, name,
					StringComparison.OrdinalIgnoreCase)).ToArray();
			if (named.Length == 1) return (named[0], null);
			if (named.Length > 1
				|| key.Equals("Filter", StringComparison.OrdinalIgnoreCase))
				continue;
			FilterDefinition[] owners = filters.Where(filter =>
				filter.Stages.Any(stage => string.Equals(stage.Name, key,
					StringComparison.OrdinalIgnoreCase))).ToArray();
			if (owners.Length == 1) return (owners[0], key);
		}
		return (null, null);
	}

	private static string? FindLastStageInScript(
		FilterDefinition filter, string source)
	{
		foreach (string line in source.Replace("\r\n", "\n")
			.Replace('\r', '\n').Split('\n').Reverse())
		{
			string trimmed = line.Trim();
			string key;
			if (trimmed.StartsWith('#'))
			{
				string comment = trimmed[1..].TrimStart();
				int colon = comment.IndexOf(':');
				if (colon < 0) continue;
				key = comment[..colon].Trim();
			}
			else
			{
				int equals = trimmed.IndexOf('=');
				if (equals < 0) continue;
				int dot = trimmed.IndexOf('.');
				if (dot <= 0 || dot >= equals) continue;
				key = trimmed[..dot].Trim();
			}
			if (filter.Stages.Any(stage => string.Equals(stage.Name, key,
					StringComparison.OrdinalIgnoreCase)))
				return key;
		}
		return null;
	}

	private sealed record FilterChoice(ObjectId Id, string Name)
	{
		public override string ToString() => $"{Name} (#{Id.Value})";
	}

	private void OnDialogKeyDown(object? sender, KeyEventArgs e)
	{
		if (e.Handled || e.Key != Key.Enter
			|| e.KeyModifiers != KeyModifiers.Control)
			return;

		e.Handled = true;
		Apply();
	}

	/// <summary>An intervening edit invalidates acknowledgement even when
	/// the user later restores exactly the previously warned text/selection.
	/// Keep all user-authored bytes; this only revokes the pending permission.</summary>
	private void InvalidateFilterWarningAcknowledgement()
	{
		if (_warnedDraft is null)
			return;
		_warnedDraft = null;
		if (_message.Text?.StartsWith("Assignment warning:",
			StringComparison.Ordinal) == true)
			_message.Text = string.Empty;
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

			bool isFilterText = _prototype is
				SetChannelFilterAssignmentsPatternEffect
				or SetNoteFilterAssignmentsPatternEffect;
			var draft = (_prototype.GetType(),
				values.GetValueOrDefault("assignments", ""),
				values.GetValueOrDefault("filterId", ""));
			// Value equality is necessary, but edits also proactively revoke
			// _warnedDraft. An edit-then-undo cannot silently reuse permission.
			bool confirmed = isFilterText && _warnedDraft == draft;
			if (isFilterText && _document is not null && !confirmed)
			{
				try
				{
					// Strictly validate the exact first-save draft.
					NativePatternEffectEditor.Rebuild(_prototype, values,
						CultureInfo.CurrentCulture, _document);
				}
				catch (ArgumentException error)
				{
					// Only offer confirmation when the Filter selection and the
					// rest of the command are valid; the strict parser alone failed.
					NativePatternEffectEditor.Rebuild(_prototype, values,
						CultureInfo.CurrentCulture, _document,
						allowInvalidFilterAssignments: true);
					_warnedDraft = draft;
					_message.Text =
						$"Assignment warning: {error.Message} " +
						"Invalid lines will be ignored during playback. " +
						"Press Apply/Insert again without editing to save this draft.";
					return;
				}
			}
			PatternEffect rebuilt =
				NativePatternEffectEditor.Rebuild(
					_prototype, values, CultureInfo.CurrentCulture,
					_document, allowInvalidFilterAssignments: confirmed);
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
