using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

using Heresy.Core.Filters;
using Heresy.Core.Objects;
using Heresy.UserInterface.Documents;

namespace Heresy.UserInterface.Views;

/// <summary>
/// Standalone editor of one reusable Filter definition. Stage edits are
/// draft-first; applying name, type and parameters is atomic, and changes
/// originating in another editor never overwrite a dirty draft.
/// </summary>
public sealed class FilterEditorControl : UserControl
{
	private readonly DocumentWorkspace _workspace;
	private readonly FilterDefinition _filter;
	private readonly Action _close;
	private readonly Action<string> _changed;
	private readonly ListBox _stages = new() { MinWidth = 245, MinHeight = 220 };
	private readonly TextBox _name = new() { MinWidth = 185 };
	private readonly ComboBox _kind = new() { MinWidth = 275 };
	private readonly StackPanel _parameters = new() { Spacing = 6 };
	private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap };
	private readonly TextBlock _title;
	private readonly Dictionary<string, Control> _editors =
		new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, string> _loadedTexts =
		new(StringComparer.OrdinalIgnoreCase);
	private FilterStageDefinition? _baseline;
	private FilterKind _loadedKind;
	private string _loadedName = "";
	private bool _loading;
	private bool _checkingDocumentChange;

	public FilterEditorControl(
		DocumentWorkspace workspace, FilterDefinition filter,
		Action close, Action<string> changed)
	{
		_workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
		_filter = filter ?? throw new ArgumentNullException(nameof(filter));
		_close = close ?? throw new ArgumentNullException(nameof(close));
		_changed = changed ?? throw new ArgumentNullException(nameof(changed));
		FilterDocumentEditor.ValidateAttached(workspace, filter);

		_title = new TextBlock
		{
			Text = filter.Name,
			FontSize = 20,
			FontWeight = FontWeight.SemiBold,
			VerticalAlignment = VerticalAlignment.Center,
		};

		BuildFilterTypeMenu();
		_kind.SelectionChanged += (_, _) => OnKindChanged();
		_stages.SelectionChanged += (_, _) => OnStageSelected();
		AttachedToVisualTree += OnAttached;
		DetachedFromVisualTree += OnDetached;

		Content = BuildView();
		RefreshStageList(_filter.Stages[0].StageId);
	}

	private Control BuildView()
	{
		Button back = new() { Content = "← Document", MinWidth = 100 };
		back.Click += (_, _) => _close();
		DockPanel header = new() { Margin = new Thickness(12, 8) };
		DockPanel.SetDock(back, Dock.Left);
		header.Children.Add(back);
		header.Children.Add(_title);

		Button add = new() { Content = "+ Stage" };
		add.Click += (_, _) => AddStage();
		Button remove = new() { Content = "Remove stage" };
		remove.Click += (_, _) => RemoveStage();
		Button up = new() { Content = "↑" };
		up.Click += (_, _) => MoveStage(-1);
		Button down = new() { Content = "↓" };
		down.Click += (_, _) => MoveStage(1);
		StackPanel stageActions = new()
		{
			Orientation = Orientation.Horizontal,
			Spacing = 6,
		};
		stageActions.Children.Add(add);
		stageActions.Children.Add(remove);
		stageActions.Children.Add(up);
		stageActions.Children.Add(down);

		StackPanel left = new() { Spacing = 8, Margin = new Thickness(10) };
		left.Children.Add(new TextBlock
		{
			Text = "Serial stages (top to bottom)",
			FontWeight = FontWeight.SemiBold,
		});
		left.Children.Add(_stages);
		left.Children.Add(stageActions);
		left.Children.Add(new TextBlock
		{
			Text = "Stages are evaluated in this order. At least one stage is required.",
			TextWrapping = TextWrapping.Wrap,
		});

		StackPanel right = new() { Spacing = 9, Margin = new Thickness(14) };
		right.Children.Add(new TextBlock
		{
			Text = "Selected stage",
			FontWeight = FontWeight.SemiBold,
		});
		right.Children.Add(new TextBlock { Text = "Stage name" });
		right.Children.Add(_name);
		right.Children.Add(new TextBlock { Text = "Filter type" });
		right.Children.Add(_kind);
		right.Children.Add(new TextBlock
		{
			Text = "Stage defaults",
			FontWeight = FontWeight.SemiBold,
		});
		right.Children.Add(new TextBlock
		{
			Text = "Parameters here belong to the shared definition. "
				+ "Individual uses may override them independently. "
				+ "Structural settings are applied at configuration boundaries.",
			TextWrapping = TextWrapping.Wrap,
		});
		right.Children.Add(_parameters);

		Button apply = new() { Content = "Apply stage", MinWidth = 130 };
		apply.Click += (_, _) => ApplyDraft();
		Button reload = new() { Content = "Reload shared stage" };
		reload.Click += (_, _) => ReloadStage();
		StackPanel actions = new()
		{
			Orientation = Orientation.Horizontal,
			Spacing = 8,
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

		ScrollViewer body = new()
		{
			Content = columns,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
		};
		DockPanel root = new();
		DockPanel.SetDock(header, Dock.Top);
		root.Children.Add(header);
		Border status = new()
		{
			Padding = new Thickness(10, 6),
			Child = _message,
		};
		DockPanel.SetDock(status, Dock.Bottom);
		root.Children.Add(status);
		root.Children.Add(body);
		return root;
	}

	private void BuildFilterTypeMenu()
	{
		foreach (FilterMenuItem descriptor in FilterCatalogue.MenuItems)
		{
			ComboBoxItem option = new()
			{
				Tag = descriptor.Kind,
				IsEnabled = descriptor.IsSelectable,
				Content = new TextBlock
				{
					Text = descriptor.Label,
					Margin = new Thickness(descriptor.Depth * 14, 0, 0, 0),
					FontWeight = descriptor.IsSelectable
						? FontWeight.Normal
						: FontWeight.SemiBold,
				},
			};
			_kind.Items.Add(option);
		}
	}

	private FilterKind? SelectedKind
		=> (_kind.SelectedItem as ComboBoxItem)?.Tag is FilterKind kind
			? kind
			: null;

	private void SelectKind(FilterKind kind)
	{
		foreach (ComboBoxItem option in _kind.Items.OfType<ComboBoxItem>())
			if (option.Tag is FilterKind candidate && candidate == kind)
			{
				_kind.SelectedItem = option;
				return;
			}
		throw new ArgumentOutOfRangeException(nameof(kind));
	}

	private void RefreshStageList(int? selectId)
	{
		_loading = true;
		try
		{
			ListBoxItem[] items = _filter.Stages
				.Select(stage => new ListBoxItem
				{
					Tag = stage.StageId,
					Content = $"{stage.Name} — {stage.Kind}",
				}).ToArray();
			_stages.ItemsSource = items;
			_stages.SelectedItem = items.FirstOrDefault(item =>
				item.Tag is int id && id == selectId) ?? items.FirstOrDefault();
		}
		finally { _loading = false; }
		LoadSelected();
	}

	private void OnStageSelected()
	{
		if (_loading)
			return;
		int? selectedId = (_stages.SelectedItem as ListBoxItem)?.Tag as int?;
		if (selectedId == _baseline?.StageId)
			return;
		if (HasUnsavedDraft())
		{
			_message.Text = "Apply or reload the unsaved stage draft before switching stages.";
			_loading = true;
			try
			{
				_stages.SelectedItem = _stages.Items
					.OfType<ListBoxItem>()
					.FirstOrDefault(item => item.Tag is int id
						&& id == _baseline!.StageId);
			}
			finally { _loading = false; }
			return;
		}
		LoadSelected();
	}

	private void LoadSelected()
	{
		int? id = (_stages.SelectedItem as ListBoxItem)?.Tag as int?;
		FilterStageDefinition? stage = id.HasValue
			? _filter.Stages.FirstOrDefault(item => item.StageId == id.Value)
			: null;
		_loading = true;
		try
		{
			_baseline = stage;
			_loadedName = stage?.Name ?? "";
			_name.Text = _loadedName;
			_loadedKind = stage?.Kind ?? FilterKind.OnePole;
			SelectKind(_loadedKind);
			BuildParameterFields(_loadedKind,
				stage?.DefaultParameters);
			_message.Text = "";
		}
		finally { _loading = false; }
	}

	private void OnKindChanged()
	{
		if (_loading || SelectedKind is not FilterKind kind)
			return;
		BuildParameterFields(kind, null);
	}

	private void BuildParameterFields(
		FilterKind kind,
		IReadOnlyDictionary<string, FilterParameterValue>? values)
	{
		_parameters.Children.Clear();
		_editors.Clear();
		_loadedTexts.Clear();
		foreach (FilterParameterDescriptor descriptor in
			FilterCatalogue.GetParameters(kind))
		{
			FilterParameterValue value = values is not null
				&& values.TryGetValue(descriptor.Name, out FilterParameterValue? configured)
				? configured
				: descriptor.DefaultValue;
			TextBlock label = new()
			{
				Text = descriptor.Name + (string.IsNullOrEmpty(descriptor.Units)
					? "" : $" ({descriptor.Units})")
					+ (descriptor.IsStructural ? " [structural]" : ""),
				MinWidth = 180,
				VerticalAlignment = VerticalAlignment.Center,
			};
			Control control;
			if (descriptor.Kind == FilterParameterValueKind.Choice)
			{
				ComboBox choices = new()
				{
					ItemsSource = descriptor.Choices,
					SelectedItem = value.ChoiceValue,
					MinWidth = 150,
				};
				control = choices;
				_loadedTexts[descriptor.Name] = value.ChoiceValue ?? "";
			}
			else
			{
				TextBox numeric = new()
				{
					Text = value.NumericValue.ToString("G17",
						CultureInfo.InvariantCulture),
					Width = 155,
				};
				ToolTip.SetTip(numeric,
					$"Range: {descriptor.Minimum:G17}–{descriptor.Maximum:G17}"
					+ (descriptor.IsInteger ? " (integer)" : "")
					+ (descriptor.IsOddOnly ? " (odd)" : ""));
				control = numeric;
				_loadedTexts[descriptor.Name] = numeric.Text ?? "";
			}
			_editors.Add(descriptor.Name, control);
			StackPanel field = new()
			{
				Orientation = Orientation.Horizontal,
				Spacing = 6,
			};
			field.Children.Add(label);
			field.Children.Add(control);
			_parameters.Children.Add(field);
		}
	}

	private bool HasUnsavedDraft()
	{
		if (_baseline is null)
			return false;
		if (_name.Text != _loadedName || SelectedKind != _loadedKind)
			return true;
		foreach ((string name, Control editor) in _editors)
		{
			string current = editor switch
			{
				TextBox input => input.Text ?? "",
				ComboBox box => box.SelectedItem as string ?? "",
				_ => "",
			};
			if (!_loadedTexts.TryGetValue(name, out string? loaded)
				|| current != loaded)
				return true;
		}
		return false;
	}

	private void ReloadStage()
	{
		try
		{
			FilterDocumentEditor.ValidateAttached(_workspace, _filter);
			RefreshStageList(_baseline?.StageId);
			_message.Text = "Shared Filter stage reloaded.";
		}
		catch (Exception error) { _message.Text = error.Message; }
	}

	private void ApplyDraft()
	{
		try
		{
			if (_baseline is null || SelectedKind is not FilterKind kind)
				throw new InvalidOperationException("Select a Filter stage and type.");
			int stageId = _baseline.StageId;
			// Parse all fields before changing the definition. An invalid field
			// must never commit any earlier fields from this draft.
			List<KeyValuePair<string, FilterParameterValue>> defaults = [];
			foreach (FilterParameterDescriptor descriptor in
				FilterCatalogue.GetParameters(kind))
			{
				Control editor = _editors[descriptor.Name];
				FilterParameterValue value;
				if (descriptor.Kind == FilterParameterValueKind.Choice)
				{
					string choice = (editor as ComboBox)?.SelectedItem as string
						?? throw new ArgumentException(
							$"Select a value for {descriptor.Name}.");
					value = FilterParameterValue.FromChoice(choice);
				}
				else
				{
					string text = (editor as TextBox)?.Text ?? "";
					if (!double.TryParse(text, NumberStyles.Float,
							CultureInfo.InvariantCulture, out double number)
						|| !double.IsFinite(number))
						throw new ArgumentException(
							$"{descriptor.Name} must be a finite number.");
					value = FilterParameterValue.FromNumber(number);
				}
				defaults.Add(new(descriptor.Name, descriptor.Normalize(value)));
			}

			bool changed = FilterDocumentEditor.ApplyStage(
				_workspace, _filter, stageId, _baseline,
				_name.Text ?? "", kind, defaults);
			RefreshStageList(stageId);
			_message.Text = changed ? "Shared Filter stage updated." : "No changes.";
			if (changed)
				_changed($"Updated Filter {_filter.Name}");
		}
		catch (Exception error) { _message.Text = error.Message; }
	}

	private void AddStage()
	{
		try
		{
			if (HasUnsavedDraft())
				throw new InvalidOperationException(
					"Apply or reload the current draft before adding a stage.");
			FilterStageDefinition stage = FilterDocumentEditor.AddStage(
				_workspace, _filter);
			RefreshStageList(stage.StageId);
			_changed($"Added stage to Filter {_filter.Name}");
		}
		catch (Exception error) { _message.Text = error.Message; }
	}

	private void RemoveStage()
	{
		try
		{
			if (_baseline is null)
				return;
			if (HasUnsavedDraft())
				throw new InvalidOperationException(
					"Apply or reload the current draft before removing a stage.");
			int oldIndex = _filter.Stages.ToList()
				.FindIndex(stage => stage.StageId == _baseline.StageId);
			FilterDocumentEditor.RemoveStage(_workspace, _filter,
				_baseline.StageId, _baseline);
			int nextId = _filter.Stages[Math.Min(oldIndex,
				_filter.Stages.Count - 1)].StageId;
			RefreshStageList(nextId);
			_changed($"Removed stage from Filter {_filter.Name}");
		}
		catch (Exception error) { _message.Text = error.Message; }
	}

	private void MoveStage(int direction)
	{
		try
		{
			if (_baseline is null)
				return;
			if (HasUnsavedDraft())
				throw new InvalidOperationException(
					"Apply or reload the current draft before moving a stage.");
			int index = _filter.Stages.ToList()
				.FindIndex(stage => stage.StageId == _baseline.StageId);
			int target = index + direction;
			if (target < 0 || target >= _filter.Stages.Count)
				return;
			if (FilterDocumentEditor.MoveStage(_workspace, _filter,
				_baseline.StageId, target, _baseline))
			{
				int id = _baseline.StageId;
				RefreshStageList(id);
				_changed($"Reordered stages in Filter {_filter.Name}");
			}
		}
		catch (Exception error) { _message.Text = error.Message; }
	}

	private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
	{
		_workspace.Document.Changed += OnDocumentChanged;
		if (_baseline is not null && !IsBaselineCurrent())
			HandleExternalEdit();
	}

	private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
		=> _workspace.Document.Changed -= OnDocumentChanged;

	private bool IsBaselineCurrent()
		=> _baseline is null ||
			_filter.Stages.Any(stage =>
				stage.StageId == _baseline.StageId
					&& ReferenceEquals(stage, _baseline));

	private void OnDocumentChanged(object? sender, SongDocumentChangedEventArgs e)
	{
		if (_checkingDocumentChange)
			return;
		if (!IsBaselineCurrent())
			HandleExternalEdit();
	}

	private void HandleExternalEdit()
	{
		if (_checkingDocumentChange)
			return;
		_checkingDocumentChange = true;
		try
		{
			if (HasUnsavedDraft())
				_message.Text = "The shared Filter stage changed in another editor. "
					+ "Your draft is preserved; reload before applying.";
			else
			{
				RefreshStageList(_baseline?.StageId);
				_message.Text = "Shared Filter definition refreshed.";
			}
		}
		finally { _checkingDocumentChange = false; }
	}
}
