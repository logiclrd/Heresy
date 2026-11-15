using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

using Heresy.Core.Filters;
using Heresy.UserInterface.Documents;

namespace Heresy.UserInterface.Views;

/// <summary>
/// Embeddable editor of exactly one configured Filter use. Each checkbox
/// controls whether a field inherits its shared definition default or
/// stores an explicit per-use override. The owning editor supplies a
/// FilterInstanceEditSession; this control does not write SongObjects.
/// </summary>
public sealed class FilterInstanceEditorControl : UserControl
{
	private readonly FilterInstanceEditSession _session;
	private readonly Action<string> _changed;
	private readonly ListBox _stages = new() { MinHeight = 170 };
	// One shared grid: Auto-width Override column, remaining width for values.
	// Headers, descriptions, stage list and help rows span both columns.
	private readonly Grid _parameters = new()
	{
		ColumnDefinitions = new ColumnDefinitions("Auto,*"),
		ColumnSpacing = 8,
		RowSpacing = 5,
		Margin = new Thickness(12),
	};
	private readonly Button _apply = new() { Content = "Apply overrides" };
	private readonly CheckBox _batchChanges = new() { Content = "Batch changes" };
	private readonly bool _synthBatchMode;
	private readonly Button _reload = new() { Content = "Reload configuration" };
	private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap };
	private readonly TextBlock _stageLabel = new() { FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
	private readonly Dictionary<string, CheckBox> _overrideToggles =
		new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, Control> _editors =
		new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, string> _loadedValues =
		new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, bool> _loadedOverrideFlags =
		new(StringComparer.OrdinalIgnoreCase);
	private FilterStageDefinition? _loadedStage;
	private bool _loading;

	public FilterInstanceEditorControl(
		FilterInstanceEditSession session, Action<string> changed,
		bool synthBatchMode = false, bool initialBatchChanges = false,
		Action<bool>? batchChangesChanged = null)
	{
		_session = session ?? throw new ArgumentNullException(nameof(session));
		_changed = changed ?? throw new ArgumentNullException(nameof(changed));
		_synthBatchMode = synthBatchMode;
		_batchChanges.IsChecked = initialBatchChanges;
		_batchChanges.PropertyChanged += (_, change) =>
		{
			if (change.Property == ToggleButton.IsCheckedProperty)
				batchChangesChanged?.Invoke(BatchChanges);
		};
		_stages.SelectionChanged += (_, _) => OnStageSelected();
		_apply.Click += (_, _) => ApplyDraft();
		_reload.Click += (_, _) => Reload();
		Content = BuildView();
		RefreshStages();
	}

	public bool BatchChanges => _batchChanges.IsChecked == true;

	/// <summary>Returns false and retains the original controls if a dirty
	/// draft cannot be safely committed before its owner navigates away.</summary>
	public bool TryCommitPendingChanges()
		=> !HasUnsavedDraft() || TryApplyDraft();

	private void ApplyOnFocusLoss()
	{
		if (!_loading && _synthBatchMode && !BatchChanges && HasUnsavedDraft())
			TryApplyDraft();
	}

	private Control BuildView()
	{
		// Every inspector row belongs to the same two-column grid.
		// Stages and explanatory text span both columns; only the Override
		// checkbox and its value editor occupy separate adjacent cells.
		ScrollViewer content = new()
		{
			Content = _parameters,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
		};
		DockPanel root = new();
		Border status = new()
		{
			Padding = new Thickness(10, 6),
			Child = _message,
		};
		DockPanel.SetDock(status, Dock.Bottom);
		root.Children.Add(status);
		root.Children.Add(content);
		return root;
	}

	private void AddFullWidthRow(Control control)
	{
		int row = _parameters.RowDefinitions.Count;
		_parameters.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
		Grid.SetRow(control, row);
		Grid.SetColumn(control, 0);
		Grid.SetColumnSpan(control, 2);
		_parameters.Children.Add(control);
	}

	private void AddOverrideValueRow(CheckBox toggle, Control editor)
	{
		int row = _parameters.RowDefinitions.Count;
		_parameters.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
		Grid.SetRow(toggle, row);
		Grid.SetColumn(toggle, 0);
		Grid.SetColumnSpan(toggle, 1);
		Grid.SetRow(editor, row);
		Grid.SetColumn(editor, 1);
		Grid.SetColumnSpan(editor, 1);
		_parameters.Children.Add(toggle);
		_parameters.Children.Add(editor);
	}


	private void RefreshStages(int? selectedId = null)
	{
		_loading = true;
		try
		{
			ListBoxItem[] items = _session.Stages.Select(stage =>
				new ListBoxItem
				{
					Tag = stage.StageId,
					Content = $"{stage.Name} — {stage.Kind}",
				}).ToArray();
			_stages.ItemsSource = items;
			_stages.SelectedItem = items.FirstOrDefault(item =>
				item.Tag is int id && id == selectedId)
				?? items.FirstOrDefault();
		}
		finally { _loading = false; }
		LoadSelectedStage();
	}

	private void OnStageSelected()
	{
		if (_loading)
			return;
		int? requestedId = (_stages.SelectedItem as ListBoxItem)?.Tag as int?;
		if (requestedId == _loadedStage?.StageId)
			return;
		if (HasUnsavedDraft())
		{
			if (_synthBatchMode && TryApplyDraft())
			{
				RefreshStages(requestedId);
				return;
			}
			if (!_synthBatchMode)
				_message.Text = "Apply or reload the current stage draft before switching.";
			_loading = true;
			try
			{
				_stages.SelectedItem = _stages.Items.OfType<ListBoxItem>()
					.FirstOrDefault(item => item.Tag is int id
						&& id == _loadedStage!.StageId);
			}
			finally { _loading = false; }
			return;
		}
		if (_session.IsStale)
		{
			_session.Reload();
			RefreshStages(requestedId);
			return;
		}
		LoadSelectedStage();
	}

	private void LoadSelectedStage()
	{
		int? id = (_stages.SelectedItem as ListBoxItem)?.Tag as int?;
		FilterStageDefinition? stage = _session.Stages
			.FirstOrDefault(item => item.StageId == id);
		_loading = true;
		try
		{
			_loadedStage = stage;
			_parameters.Children.Clear();
			_parameters.RowDefinitions.Clear();
			_editors.Clear();
			_overrideToggles.Clear();
			_loadedValues.Clear();
			_loadedOverrideFlags.Clear();
			_stageLabel.Text = stage is null
				? "No stage selected"
				: $"{stage.Name} — {stage.Kind}";
			AddFullWidthRow(new TextBlock
			{
				Text = "Configured stages (serial order)",
				FontWeight = FontWeight.SemiBold,
			});
			AddFullWidthRow(_stages);
			AddFullWidthRow(new TextBlock
			{
				Text = "The shared Filter defines stage names, types and defaults. "
					+ "Overrides here affect only this use.",
				TextWrapping = TextWrapping.Wrap,
			});
			AddFullWidthRow(_stageLabel);
			AddFullWidthRow(new TextBlock
			{
				Text = "Check Override to customize this use; uncheck to inherit the "
					+ "definition's current default. Structural options do not "
					+ "produce audio-rate FM modulation ports.",
				TextWrapping = TextWrapping.Wrap,
			});
			if (stage is not null)
			{
			IReadOnlyDictionary<string, FilterParameterValue> overrides =
				_session.Current.GetOverrides(stage.StageId);
			foreach (FilterParameterDescriptor descriptor in
				FilterCatalogue.GetParameters(stage.Kind))
			{
				bool overridden = overrides.TryGetValue(
					descriptor.Name, out FilterParameterValue? explicitValue);
				FilterParameterValue defaultValue =
					stage.DefaultParameters[descriptor.Name];
				FilterParameterValue value = overridden
					? explicitValue! : defaultValue;
				string defaultText = Format(defaultValue);
				TextBlock title = new()
				{
					Text = descriptor.Name
						+ (string.IsNullOrWhiteSpace(descriptor.Units)
							? "" : $" ({descriptor.Units})")
						+ (descriptor.IsStructural ? " [structural]" : ""),
					TextWrapping = TextWrapping.Wrap,
					VerticalAlignment = VerticalAlignment.Center,
				};
				CheckBox toggle = new()
				{
					Content = "Override",
					IsChecked = overridden,
					VerticalAlignment = VerticalAlignment.Center,
				};
				Control editor;
				if (descriptor.Kind == FilterParameterValueKind.Choice)
				{
					editor = new ComboBox
					{
						ItemsSource = descriptor.Choices,
						SelectedItem = value.ChoiceValue,
						HorizontalAlignment = HorizontalAlignment.Stretch,
						IsEnabled = overridden,
					};
				}
				else
				{
					editor = new TextBox
					{
						Text = Format(value),
						HorizontalAlignment = HorizontalAlignment.Stretch,
						IsEnabled = overridden,
					};
				}
				string rangeText = descriptor.Kind == FilterParameterValueKind.Number
					? $"Permitted: {descriptor.Minimum!.Value:G17} to "
						+ $"{descriptor.Maximum!.Value:G17}"
						+ (descriptor.IsInteger ? "; integer" : "")
						+ (descriptor.IsOddOnly ? "; odd" : "")
					: "Choose one of the listed values";
				string help = $"{rangeText}. Definition default: {defaultText}"
					+ (string.IsNullOrWhiteSpace(descriptor.Units)
						? "" : $" {descriptor.Units}")
					+ (descriptor.IsStructural
						? ". Structural: changes require filter reconfiguration."
						: ". Continuously modulatable numeric parameter.");
				ToolTip.SetTip(editor, help);
				ToolTip.SetTip(toggle, "Unchecked means inherit the shared definition default.");
				toggle.PropertyChanged += (_, change) =>
				{
					if (change.Property == ToggleButton.IsCheckedProperty)
						editor.IsEnabled = toggle.IsChecked == true;
				};
				if (_synthBatchMode)
				{
					toggle.LostFocus += (_, _) => ApplyOnFocusLoss();
					editor.LostFocus += (_, _) => ApplyOnFocusLoss();
				}
				_overrideToggles.Add(descriptor.Name, toggle);
				_editors.Add(descriptor.Name, editor);
				_loadedValues.Add(descriptor.Name, Format(value));
				_loadedOverrideFlags.Add(descriptor.Name, overridden);

				// The label and help are full-width. Only Override and its
				// matching input share a two-column row.
				title.Margin = new Thickness(0, 8, 0, 0);
				AddFullWidthRow(title);
				AddOverrideValueRow(toggle, editor);
				AddFullWidthRow(new TextBlock
				{
					Text = $"Inherited default: {defaultText}"
						+ (string.IsNullOrWhiteSpace(descriptor.Units)
							? "" : $" {descriptor.Units}"),
					FontStyle = FontStyle.Italic,
					Foreground = Brushes.Gray,
					TextWrapping = TextWrapping.Wrap,
				});
				AddFullWidthRow(new TextBlock
				{
					Text = rangeText,
					Foreground = Brushes.Gray,
					TextWrapping = TextWrapping.Wrap,
				});
			}
			}
			if (_synthBatchMode)
				AddFullWidthRow(_batchChanges);
			AddFullWidthRow(_apply);
			AddFullWidthRow(_reload);
			_message.Text = "";
		}
		finally { _loading = false; }
	}

	private static string Format(FilterParameterValue value)
		=> value.Kind == FilterParameterValueKind.Number
			? value.NumericValue.ToString("G17", CultureInfo.InvariantCulture)
			: value.ChoiceValue ?? "";

	private string CurrentText(string name)
		=> _editors[name] switch
		{
			TextBox numeric => numeric.Text ?? "",
			ComboBox choice => choice.SelectedItem as string ?? "",
			_ => "",
		};

	private bool HasUnsavedDraft()
	{
		if (_loadedStage is null)
			return false;
		foreach ((string name, CheckBox toggle) in _overrideToggles)
		{
			bool isOverridden = toggle.IsChecked == true;
			if (isOverridden != _loadedOverrideFlags[name]
				|| (isOverridden && CurrentText(name) != _loadedValues[name]))
				return true;
		}
		return false;
	}

	private void ApplyDraft() => TryApplyDraft();

	private bool TryApplyDraft()
	{
		try
		{
			if (_loadedStage is null)
				throw new InvalidOperationException("Select a Filter stage.");
			if (_session.IsStale)
				throw new InvalidOperationException(
					"This Filter use or shared definition changed. Reload before applying.");
			List<KeyValuePair<string, FilterParameterValue>> requested = [];
			foreach (FilterParameterDescriptor descriptor in
				FilterCatalogue.GetParameters(_loadedStage.Kind))
			{
				if (_overrideToggles[descriptor.Name].IsChecked != true)
					continue;
				FilterParameterValue value;
				if (descriptor.Kind == FilterParameterValueKind.Choice)
				{
					string choice = CurrentText(descriptor.Name);
					value = FilterParameterValue.FromChoice(choice);
				}
				else
				{
					if (!double.TryParse(CurrentText(descriptor.Name),
						NumberStyles.Float, CultureInfo.InvariantCulture,
						out double number) || !double.IsFinite(number))
						throw new ArgumentException(
							$"{descriptor.Name} must be a finite number.");
					value = FilterParameterValue.FromNumber(number);
				}
				requested.Add(new(descriptor.Name, descriptor.Normalize(value)));
			}
			int stageId = _loadedStage.StageId;
			bool changed = _session.ApplyStage(stageId, requested);
			RefreshStages(stageId);
			_message.Text = changed
				? "This Filter use was updated."
				: "No configuration changes.";
			if (changed)
				_changed("Updated configured Filter instance.");
			return true;
		}
		catch (Exception error)
		{
			_message.Text = error.Message;
			return false;
		}
	}

	private void Reload()
	{
		try
		{
			int? selected = _loadedStage?.StageId;
			_session.Reload();
			RefreshStages(selected);
			_message.Text = "Filter configuration reloaded.";
		}
		catch (Exception error) { _message.Text = error.Message; }
	}

	/// <summary>The owner calls this after an external edit. Clean fields
	/// reload in place; dirty fields retain their exact previous-version draft
	/// while the stage list itself displays the current shared definition.</summary>
	public void NotifyConfigurationChanged()
	{
		if (!_session.IsStale)
			return;
		if (!HasUnsavedDraft())
		{
			Reload();
			return;
		}

		// Rebuilding the editors would lose a tentative per-use override.
		// Refresh only the stage choices against the *live* shared definition.
		// The old controls stay visibly labelled as a stale draft until Reload.
		int? draftStageId = _loadedStage?.StageId;
		_loading = true;
		try
		{
			ListBoxItem[] items = _session.Current.Definition.Stages
				.Select(stage => new ListBoxItem
				{
					Tag = stage.StageId,
					Content = $"{stage.Name} — {stage.Kind}",
				}).ToArray();
			_stages.ItemsSource = items;
			_stages.SelectedItem = items.FirstOrDefault(item =>
				item.Tag is int id && id == draftStageId);
			if (_loadedStage is FilterStageDefinition old)
				_stageLabel.Text = $"Unapplied older-version draft: "
					+ $"{old.Name} — {old.Kind} (stage #{old.StageId})";
		}
		finally { _loading = false; }
		_message.Text = "This configured Filter or shared definition "
			+ "changed in another editor. Your draft is preserved; "
			+ "the stage list shows the new definition. "
			+ "Reload before applying.";
	}
}
