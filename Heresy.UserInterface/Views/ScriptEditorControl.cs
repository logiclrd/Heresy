using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.Scripting.Analysis;
using Heresy.UserInterface.Documents;

namespace Heresy.UserInterface.Views;

/// <summary>
/// Shared source editor for scripted patterns and scripted sequences.
/// Persisted source remains ordinary restricted-C# text; semantic object
/// references are presented as atomic named tokens without rewriting the
/// underlying _O(id) expressions.
/// </summary>
public sealed class ScriptEditorControl : UserControl
{
	private readonly record struct EditorSnapshot(
		string Source,
		int SourceCaret);

	private readonly DocumentWorkspace _workspace;
	private readonly ScriptPatternDefinition? _pattern;
	private readonly ScriptSequenceDefinition? _sequence;
	private readonly Action _close;
	private readonly Action<string> _changed;
	private readonly string _backLabel;

	private readonly TextBox _source;
	private readonly ComboBox _reference;
	private readonly TextBlock _message = new()
	{
		TextWrapping = TextWrapping.Wrap,
	};

	private readonly TextBlock _analysis = new()
	{
		TextWrapping = TextWrapping.Wrap,
	};

	private readonly TextBox? _rowCount;
	private readonly TextBox? _channelCount;
	private readonly TextBox? _minorHighlight;
	private readonly TextBox? _majorHighlight;

	private readonly Stack<EditorSnapshot> _undo = [];
	private readonly Stack<EditorSnapshot> _redo = [];

	private ScriptSourceProjection _projection;
	private bool _updatingProjectedText;
	private EditorSnapshot? _pendingTextEditSnapshot;

	public ScriptEditorControl(
		DocumentWorkspace workspace,
		ScriptPatternDefinition pattern,
		Action close,
		Action<string> changed,
		string backLabel = "← Document")
		: this(
			workspace,
			pattern,
			null,
			close,
			changed,
			backLabel)
	{
	}

	public ScriptEditorControl(
		DocumentWorkspace workspace,
		ScriptSequenceDefinition sequence,
		Action close,
		Action<string> changed,
		string backLabel = "← Document")
		: this(
			workspace,
			null,
			sequence,
			close,
			changed,
			backLabel)
	{
	}

	private ScriptEditorControl(
		DocumentWorkspace workspace,
		ScriptPatternDefinition? pattern,
		ScriptSequenceDefinition? sequence,
		Action close,
		Action<string> changed,
		string backLabel)
	{
		_workspace = workspace
			?? throw new ArgumentNullException(nameof(workspace));
		if ((pattern is null) == (sequence is null))
		{
			throw new ArgumentException(
				"Exactly one scripted object must be supplied.");
		}

		_pattern = pattern;
		_sequence = sequence;
		_close = close
			?? throw new ArgumentNullException(nameof(close));
		_changed = changed
			?? throw new ArgumentNullException(nameof(changed));
		_backLabel = backLabel
			?? throw new ArgumentNullException(nameof(backLabel));

		string source =
			pattern?.Source
				?? sequence!.Source;
		_projection =
			ScriptSourceProjection.Create(
				source,
				[]);
		_source =
			new TextBox
			{
				Text = source,
				AcceptsReturn = true,
				AcceptsTab = true,
				TextWrapping = TextWrapping.NoWrap,
				MinHeight = 360,
				MinWidth = 520,
				UndoLimit = 0,
			};

		_source.TextChanging += (_, _) =>
		{
			if (!_updatingProjectedText)
				_pendingTextEditSnapshot = CaptureSnapshot();
		};
		_source.TextChanged += (_, _) => OnProjectedTextChanged();
		_source.KeyDown += async (_, e) =>
			await OnSourceKeyDownAsync(e);
		_source.ContextMenu = BuildSourceContextMenu();

		_reference =
			new ComboBox
			{
				ItemsSource =
					ScriptDocumentEditor.GetObjectReferences(
						workspace.Document),
				MinWidth = 320,
			};

		if (pattern is not null)
		{
			_rowCount = NumberBox(pattern.RowCount);
			_channelCount = NumberBox(pattern.ChannelCount);
			_minorHighlight = NumberBox(pattern.MinorHighlightRows);
			_majorHighlight = NumberBox(pattern.MajorHighlightRows);
		}

		Content = BuildContent();
		RebuildProjection(
			source,
			sourceCaret: 0);
	}

	private SongObject ScriptObject =>
		(SongObject?)_pattern
			?? _sequence!;

	private Control BuildContent()
	{
		Button back =
			new()
			{
				Content = _backLabel,
				MinWidth = 110,
			};
		back.Click += (_, _) => _close();

		TextBlock title =
			new()
			{
				Text =
					_pattern is not null
						? $"{_pattern.Name} — Script Pattern"
						: $"{_sequence!.Name} — Script Sequence",
				FontSize = 20,
				FontWeight = FontWeight.SemiBold,
				VerticalAlignment = VerticalAlignment.Center,
			};

		DockPanel header =
			new()
			{
				Margin = new Thickness(10, 8),
			};
		DockPanel.SetDock(back, Dock.Left);
		header.Children.Add(back);
		header.Children.Add(title);

		StackPanel body =
			new()
			{
				Margin = new Thickness(16, 10),
				Spacing = 12,
			};

		if (_pattern is not null)
			body.Children.Add(BuildPatternLayout());
		else
			body.Children.Add(BuildSequenceControls());

		body.Children.Add(BuildReferenceToolbar());

		body.Children.Add(
			new TextBlock
			{
				Text = "Restricted C# source",
				FontWeight = FontWeight.SemiBold,
			});
		body.Children.Add(_source);

		Border analysisBorder =
			new()
			{
				Padding = new Thickness(10, 8),
				Child = _analysis,
			};
		body.Children.Add(analysisBorder);

		TextBlock note =
			new()
			{
				Text =
					"Object references are persisted as _O(id) but displayed as atomic ⟦name⟧ tokens. Renames update the projection rather than the stored source; deleting, replacing, copying or cutting any part of a token operates on the complete canonical reference.",
				TextWrapping = TextWrapping.Wrap,
				MaxWidth = 900,
			};
		body.Children.Add(note);

		Button apply =
			new()
			{
				Content = "Apply source",
				MinWidth = 120,
				HorizontalAlignment = HorizontalAlignment.Left,
			};
		apply.Click += (_, _) => ApplySource();
		body.Children.Add(apply);

		Border messageBorder =
			new()
			{
				Padding = new Thickness(10, 5),
				Child = _message,
			};

		DockPanel root = new();
		DockPanel.SetDock(header, Dock.Top);
		DockPanel.SetDock(messageBorder, Dock.Bottom);
		root.Children.Add(header);
		root.Children.Add(messageBorder);
		root.Children.Add(
			new ScrollViewer
			{
				Content = body,
			});
		return root;
	}

	private Control BuildPatternLayout()
	{
		Grid grid =
			new()
			{
				ColumnSpacing = 8,
				RowSpacing = 8,
				HorizontalAlignment = HorizontalAlignment.Left,
			};
		grid.ColumnDefinitions.Add(
			new ColumnDefinition(GridLength.Auto));
		grid.ColumnDefinitions.Add(
			new ColumnDefinition(new GridLength(90)));
		grid.ColumnDefinitions.Add(
			new ColumnDefinition(GridLength.Auto));
		grid.ColumnDefinitions.Add(
			new ColumnDefinition(new GridLength(90)));

		for (int row = 0; row < 3; row++)
			grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

		AddField(grid, "Rows", _rowCount!, 0, 0);
		AddField(grid, "Channels", _channelCount!, 0, 2);
		AddField(grid, "Minor highlight", _minorHighlight!, 1, 0);
		AddField(grid, "Major highlight", _majorHighlight!, 1, 2);

		Button applyLayout =
			new()
			{
				Content = "Apply layout",
				MinWidth = 110,
			};
		applyLayout.Click += (_, _) => ApplyPatternLayout();
		Grid.SetRow(applyLayout, 2);
		Grid.SetColumn(applyLayout, 0);
		Grid.SetColumnSpan(applyLayout, 4);
		grid.Children.Add(applyLayout);
		return grid;
	}

	private Control BuildSequenceControls()
	{
		Button root =
			new()
			{
				Content = "Set as root sequence",
				MinWidth = 150,
				HorizontalAlignment = HorizontalAlignment.Left,
			};
		root.Click += (_, _) =>
		{
			try
			{
				ScriptDocumentEditor.SetRootSequence(
					_workspace,
					_sequence!);
				_message.Text =
					_workspace.Document.RootSequenceId == _sequence!.Id
						? "This script sequence is the song root."
						: "Root sequence was not changed.";
				_changed(
					$"Root sequence set to {_sequence.Name}");
			}
			catch (Exception ex)
			{
				_message.Text = ex.Message;
			}
		};
		return root;
	}

	private Control BuildReferenceToolbar()
	{
		Button insert =
			new()
			{
				Content = "Insert object",
				MinWidth = 110,
			};
		insert.Click += (_, _) => InsertReference();

		StackPanel panel =
			new()
			{
				Orientation = Orientation.Horizontal,
				Spacing = 8,
				VerticalAlignment = VerticalAlignment.Center,
			};
		panel.Children.Add(
			new TextBlock
			{
				Text = "Object reference:",
				VerticalAlignment = VerticalAlignment.Center,
			});
		panel.Children.Add(_reference);
		panel.Children.Add(insert);
		return panel;
	}

	private ContextMenu BuildSourceContextMenu()
	{
		MenuItem undo =
			new()
			{
				Header = "Undo",
			};
		undo.Click += (_, _) => UndoSource();

		MenuItem redo =
			new()
			{
				Header = "Redo",
			};
		redo.Click += (_, _) => RedoSource();

		MenuItem cut =
			new()
			{
				Header = "Cut",
			};
		cut.Click += async (_, _) =>
			await CutSelectionAsync();

		MenuItem copy =
			new()
			{
				Header = "Copy",
			};
		copy.Click += async (_, _) =>
			await CopySelectionAsync();

		MenuItem paste =
			new()
			{
				Header = "Paste",
			};
		paste.Click += async (_, _) =>
			await PasteAsync();

		MenuItem selectAll =
			new()
			{
				Header = "Select All",
			};
		selectAll.Click += (_, _) =>
		{
			_source.SelectionStart = 0;
			_source.SelectionEnd = _projection.Text.Length;
			_source.CaretIndex = _projection.Text.Length;
		};

		return new ContextMenu
		{
			ItemsSource =
				new object[]
				{
					undo,
					redo,
					new Separator(),
					cut,
					copy,
					paste,
					new Separator(),
					selectAll,
				},
		};
	}

	private async Task OnSourceKeyDownAsync(
		KeyEventArgs e)
	{
		bool clipboardModifier =
			(e.KeyModifiers
				& (KeyModifiers.Control | KeyModifiers.Meta))
				!= 0
			&& (e.KeyModifiers & KeyModifiers.Alt) == 0;

		if (clipboardModifier && e.Key == Key.Z)
		{
			if ((e.KeyModifiers & KeyModifiers.Shift) != 0)
				RedoSource();
			else
				UndoSource();
			e.Handled = true;
			return;
		}

		if (clipboardModifier && e.Key == Key.Y)
		{
			RedoSource();
			e.Handled = true;
			return;
		}

		if (clipboardModifier && e.Key == Key.C)
		{
			await CopySelectionAsync();
			e.Handled = true;
			return;
		}

		if (clipboardModifier && e.Key == Key.X)
		{
			await CutSelectionAsync();
			e.Handled = true;
			return;
		}

		if (clipboardModifier && e.Key == Key.V)
		{
			await PasteAsync();
			e.Handled = true;
			return;
		}

		if (e.Key == Key.Back)
		{
			ApplyProjectionEdit(
				_projection.DeleteBackward(
					_source.SelectionStart,
					_source.SelectionEnd),
				CaptureSnapshot());
			e.Handled = true;
			return;
		}

		if (e.Key == Key.Delete)
		{
			ApplyProjectionEdit(
				_projection.DeleteForward(
					_source.SelectionStart,
					_source.SelectionEnd),
				CaptureSnapshot());
			e.Handled = true;
			return;
		}

		bool ordinaryHorizontalMove =
			(e.KeyModifiers
				& (KeyModifiers.Control
					| KeyModifiers.Meta
					| KeyModifiers.Alt))
				== 0
			&& e.Key is Key.Left or Key.Right;
		if (ordinaryHorizontalMove)
		{
			MoveProjectedCaret(
				e.Key == Key.Left ? -1 : 1,
				(e.KeyModifiers & KeyModifiers.Shift) != 0);
			e.Handled = true;
		}
	}

	private void MoveProjectedCaret(
		int direction,
		bool extendSelection)
	{
		int target;
		if (!extendSelection
			&& _source.SelectionStart
				!= _source.SelectionEnd)
		{
			target =
				direction < 0
					? Math.Min(
						_source.SelectionStart,
						_source.SelectionEnd)
					: Math.Max(
						_source.SelectionStart,
						_source.SelectionEnd);
		}
		else
		{
			target =
				_projection.MoveCaret(
					_source.CaretIndex,
					direction);
		}

		if (!extendSelection)
		{
			_source.SelectionStart = target;
			_source.SelectionEnd = target;
			_source.CaretIndex = target;
			return;
		}

		int anchor =
			_source.SelectionStart
				== _source.SelectionEnd
				? _source.CaretIndex
				: _source.CaretIndex
					== _source.SelectionStart
					? _source.SelectionEnd
					: _source.SelectionStart;
		_source.SelectionStart =
			Math.Min(
				anchor,
				target);
		_source.SelectionEnd =
			Math.Max(
				anchor,
				target);
		_source.CaretIndex = target;
	}

	private void OnProjectedTextChanged()
	{
		if (_updatingProjectedText)
			return;

		string changedText =
			_source.Text
				?? string.Empty;
		ScriptProjectionEdit edit =
			_projection.ReconcileTextChange(
				changedText);
		EditorSnapshot before =
			_pendingTextEditSnapshot
				?? CaptureSnapshot();
		_pendingTextEditSnapshot = null;
		ApplyProjectionEdit(
			edit,
			before);
	}

	private void InsertReference()
	{
		if (_reference.SelectedItem
			is not ScriptObjectReferenceOption option)
		{
			_message.Text =
				"Choose an object before inserting a reference.";
			return;
		}

		ScriptProjectionEdit edit =
			_projection.Replace(
				_source.SelectionStart,
				_source.SelectionEnd,
				option.ReferenceText);
		ApplyProjectionEdit(
			edit,
			CaptureSnapshot());
		_source.Focus();
		_message.Text =
			$"Inserted {option.DisplayName} as {option.ReferenceText}.";
	}

	private async Task CopySelectionAsync()
	{
		string text =
			_projection.GetSourceText(
				_source.SelectionStart,
				_source.SelectionEnd);
		if (text.Length == 0)
			return;

		var clipboard =
			TopLevel.GetTopLevel(this)?.Clipboard;
		if (clipboard is not null)
			await clipboard.SetTextAsync(text);
	}

	private async Task CutSelectionAsync()
	{
		if (_source.SelectionStart
			== _source.SelectionEnd)
		{
			return;
		}

		await CopySelectionAsync();
		ApplyProjectionEdit(
			_projection.Replace(
				_source.SelectionStart,
				_source.SelectionEnd,
				string.Empty),
			CaptureSnapshot());
	}

	private async Task PasteAsync()
	{
		var clipboard =
			TopLevel.GetTopLevel(this)?.Clipboard;
		if (clipboard is null)
			return;

		string? text =
			await clipboard.TryGetTextAsync();
		if (text is null)
			return;

		ApplyProjectionEdit(
			_projection.Replace(
				_source.SelectionStart,
				_source.SelectionEnd,
				text),
			CaptureSnapshot());
	}

	private EditorSnapshot CaptureSnapshot()
		=> new(
			_projection.Source,
			_projection.SourcePositionFromDisplay(
				_source.CaretIndex));

	private void ApplyProjectionEdit(
		ScriptProjectionEdit edit,
		EditorSnapshot before)
	{
		if (edit.Source != _projection.Source)
		{
			_undo.Push(before);
			_redo.Clear();
		}

		RebuildProjection(
			edit.Source,
			edit.SourceCaret);
	}

	private void UndoSource()
	{
		if (_undo.Count == 0)
			return;

		EditorSnapshot current =
			CaptureSnapshot();
		EditorSnapshot previous =
			_undo.Pop();
		_redo.Push(current);
		RebuildProjection(
			previous.Source,
			previous.SourceCaret);
	}

	private void RedoSource()
	{
		if (_redo.Count == 0)
			return;

		EditorSnapshot current =
			CaptureSnapshot();
		EditorSnapshot next =
			_redo.Pop();
		_undo.Push(current);
		RebuildProjection(
			next.Source,
			next.SourceCaret);
	}

	private void RebuildProjection(
		string source,
		int sourceCaret)
	{
		try
		{
			ScriptSourceDocumentAnalysis analysis =
				AnalyzeSource(source);
			_projection =
				ScriptSourceProjection.Create(
					source,
					analysis.References);

			int displayCaret =
				_projection.DisplayPositionFromSource(
					sourceCaret);
			_updatingProjectedText = true;
			try
			{
				_source.Text = _projection.Text;
				_source.SelectionStart = displayCaret;
				_source.SelectionEnd = displayCaret;
				_source.CaretIndex = displayCaret;
			}
			finally
			{
				_updatingProjectedText = false;
				_pendingTextEditSnapshot = null;
			}

			RenderAnalysis(analysis);
		}
		catch (Exception ex)
		{
			_analysis.Text =
				$"Script analysis failed: {ex.Message}";
		}
	}

	private ScriptSourceDocumentAnalysis AnalyzeSource(
		string source)
		=> _pattern is not null
			? ScriptSourceDocumentAnalyzer.Analyze(
				_workspace,
				_pattern,
				source)
			: ScriptSourceDocumentAnalyzer.Analyze(
				_workspace,
				_sequence!,
				source);

	private void RenderAnalysis(
		ScriptSourceDocumentAnalysis analysis)
	{
		List<string> lines = [];
		if (analysis.Diagnostics.Count == 0)
		{
			lines.Add("Analysis: no compiler diagnostics.");
		}
		else
		{
			lines.Add("Diagnostics:");
			foreach (ScriptAnalysisDiagnostic diagnostic
				in analysis.Diagnostics)
			{
				int displayStart =
					_projection.DisplayPositionFromSource(
						diagnostic.Span.Start);
				lines.Add(
					$"{diagnostic.Severity} {diagnostic.Code} "
						+ $"at {displayStart}: "
						+ diagnostic.Message);
			}
		}

		if (analysis.References.Count == 0)
		{
			lines.Add("Object references: none.");
		}
		else
		{
			lines.Add("Object references:");
			foreach (ProjectedScriptObjectReference reference
				in analysis.References)
			{
				lines.Add(
					$"{reference.DisplayName} — {reference.Kind} "
						+ $"<{reference.Id.Value}> "
						+ $"[{reference.Resolution}]");
			}
		}

		_analysis.Text =
			string.Join(
				Environment.NewLine,
				lines);
	}

	private void ApplySource()
	{
		try
		{
			string source = _projection.Source;
			if (_pattern is not null)
			{
				ScriptDocumentEditor.UpdateSource(
					_workspace,
					_pattern,
					source);
			}
			else
			{
				ScriptDocumentEditor.UpdateSource(
					_workspace,
					_sequence!,
					source);
			}

			_message.Text = "Script source updated.";
			_changed(
				$"Updated script source for {ScriptObject.Name}");
		}
		catch (Exception ex)
		{
			_message.Text = ex.Message;
		}
	}

	private void ApplyPatternLayout()
	{
		try
		{
			int rows = ParseInt(_rowCount!, "Rows");
			int channels = ParseInt(_channelCount!, "Channels");
			int minor = ParseInt(_minorHighlight!, "Minor highlight");
			int major = ParseInt(_majorHighlight!, "Major highlight");

			ScriptDocumentEditor.UpdatePatternLayout(
				_workspace,
				_pattern!,
				rows,
				channels,
				minor,
				major);

			_rowCount!.Text =
				_pattern!.RowCount.ToString(
					CultureInfo.CurrentCulture);
			_channelCount!.Text =
				_pattern.ChannelCount.ToString(
					CultureInfo.CurrentCulture);
			_minorHighlight!.Text =
				_pattern.MinorHighlightRows.ToString(
					CultureInfo.CurrentCulture);
			_majorHighlight!.Text =
				_pattern.MajorHighlightRows.ToString(
					CultureInfo.CurrentCulture);

			_message.Text = "Script-pattern layout updated.";
			_changed(
				$"Updated script-pattern layout for {_pattern.Name}");
		}
		catch (Exception ex)
		{
			_message.Text = ex.Message;
		}
	}

	private static TextBox NumberBox(int value)
		=> new()
		{
			Text = value.ToString(CultureInfo.CurrentCulture),
			Width = 80,
		};

	private static int ParseInt(
		TextBox box,
		string label)
	{
		if (!int.TryParse(
			box.Text,
			NumberStyles.Integer,
			CultureInfo.CurrentCulture,
			out int value))
		{
			throw new ArgumentException(
				$"{label} must be an integer.");
		}
		return value;
	}

	private static void AddField(
		Grid grid,
		string label,
		Control control,
		int row,
		int column)
	{
		TextBlock text =
			new()
			{
				Text = label,
				VerticalAlignment = VerticalAlignment.Center,
			};
		Grid.SetRow(text, row);
		Grid.SetColumn(text, column);
		grid.Children.Add(text);

		Grid.SetRow(control, row);
		Grid.SetColumn(control, column + 1);
		grid.Children.Add(control);
	}
}
