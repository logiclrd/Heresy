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
	private readonly ListBox _stages = new() { MinWidth = 230, MinHeight = 170 };
	private readonly StackPanel _parameters = new() { Spacing = 8 };
	private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap };
	private readonly TextBlock _stageLabel = new() { FontWeight = FontWeight.SemiBold };
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
		FilterInstanceEditSession session, Action<string> changed)
	{
		_session = session ?? throw new ArgumentNullException(nameof(session));
		_changed = changed ?? throw new ArgumentNullException(nameof(changed));
		_stages.SelectionChanged += (_, _) => OnStageSelected();
		Content = BuildView();
		RefreshStages();
	}

	private Control BuildView()
	{
		StackPanel left = new()
		{
			Spacing = 8, Margin = new Thickness(12),
		};
		left.Children.Add(new TextBlock
		{
			Text = "Configured stages (serial order)",
			FontWeight = FontWeight.SemiBold,
		});
		left.Children.Add(_stages);
		left.Children.Add(new TextBlock
		{
			Text = "The shared Filter defines stage names, types and defaults. "
				+ "Overrides here affect only this use.",
			TextWrapping = TextWrapping.Wrap,
		});

		StackPanel right = new()
		{
			Spacing = 8, Margin = new Thickness(12),
		};
		right.Children.Add(_stageLabel);
		right.Children.Add(new TextBlock
		{
			Text = "Check Override to customize this use; uncheck to inherit the "
				+ "definition's current default. Structural options do not "
				+ "produce audio-rate FM modulation ports.",
			TextWrapping = TextWrapping.Wrap,
		});
		right.Children.Add(_parameters);

		Button apply = new() { Content = "Apply overrides" };
		apply.Click += (_, _) => ApplyDraft();
		Button reload = new() { Content = "Reload configuration" };
		reload.Click += (_, _) => Reload();
		StackPanel actions = new()
		{
			Orientation = Orientation.Horizontal, Spacing = 8,
		};
		actions.Children.Add(apply);
		actions.Children.Add(reload);
		right.Children.Add(actions);

		Grid columns = new()
		{
			ColumnDefinitions = new ColumnDefinitions("Auto,*"),
		};
		Grid.SetColumn(left, 0);
		Grid.SetColumn(right, 1);
		columns.Children.Add(left);
		columns.Children.Add(right);

		ScrollViewer content = new()
		{
			Content = columns,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
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
			_session.Reload();
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
			_editors.Clear();
			_overrideToggles.Clear();
			_loadedValues.Clear();
			_loadedOverrideFlags.Clear();
			_stageLabel.Text = stage is null
				? "No stage selected"
				: $"{stage.Name} — {stage.Kind}";
			if (stage is null)
				return;
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
					MinWidth = 175,
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
						MinWidth = 150,
						IsEnabled = overridden,
					};
				}
				else
				{
					editor = new TextBox
					{
						Text = Format(value),
						Width = 130,
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
				toggle.Checked += (_, _) => editor.IsEnabled = true;
				toggle.Unchecked += (_, _) => editor.IsEnabled = false;
				_overrideToggles.Add(descriptor.Name, toggle);
				_editors.Add(descriptor.Name, editor);
				_loadedValues.Add(descriptor.Name, Format(value));
				_loadedOverrideFlags.Add(descriptor.Name, overridden);

				StackPanel row = new()
				{
					Orientation = Orientation.Horizontal, Spacing = 8,
				};
				row.Children.Add(title);
				row.Children.Add(toggle);
				row.Children.Add(editor);
				StackPanel group = new() { Spacing = 3 };
				group.Children.Add(row);
				group.Children.Add(new TextBlock
				{
					Text = $"Inherited default: {defaultText}"
						+ (string.IsNullOrWhiteSpace(descriptor.Units)
							? "" : $" {descriptor.Units}"),
					FontStyle = FontStyle.Italic,
					Foreground = Brushes.Gray,
				});
				_parameters.Children.Add(group);
			}
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

	private void ApplyDraft()
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
		}
		catch (Exception error) { _message.Text = error.Message; }
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

	/// <summary>The owner calls this after an external edit. Dirty local
	/// drafts remain intact; clean editors reload from the new configuration.</summary>
	public void NotifyConfigurationChanged()
	{
		if (_session.IsStale)
		{
			if (HasUnsavedDraft())
				_message.Text = "This configured Filter or shared definition "
					+ "changed in another editor. Your draft is preserved. "
					+ "Reload before applying.";
			else
				Reload();
		}
	}
}
