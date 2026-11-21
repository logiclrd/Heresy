using System;
using System.Collections.Generic;
using System.Globalization;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

using AvaloniaEdit;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.Scripting.Analysis;
using Heresy.UserInterface.Documents;

namespace Heresy.UserInterface.Views;

/// <summary>
/// Shared AvaloniaEdit source editor for scripted patterns and sequences.
/// The TextDocument always contains canonical restricted-C# source; semantic
/// object references are replaced only in the visual-line layer.
/// </summary>
public sealed class ScriptEditorControl : UserControl, IScriptSourceEditor, IScriptSourceShortcutTarget
{
	private readonly DocumentWorkspace _workspace;
	private readonly ScriptPatternDefinition? _pattern;
	private readonly ScriptSequenceDefinition? _sequence;
	private readonly Action _close;
	private readonly Action<string> _changed;
	private readonly string _backLabel;
	private readonly Action? _testRequested;
	private readonly Action? _documentSaveRequested;

	private readonly TextEditor _source;
	private string _appliedSource = string.Empty;
	private readonly Button _saveSource = new()
	{
		Content = "Save",
		MinWidth = 120,
		HorizontalAlignment = HorizontalAlignment.Left,
		IsEnabled = false,
	};
	private readonly ScriptObjectReferenceElementGenerator _referenceGenerator =
		new();
	private readonly ScriptSyntaxColorizer _syntaxColorizer =
		new();
	private readonly ScriptDiagnosticRenderer _diagnosticRenderer =
		new();
	private readonly ComboBox _reference;
	private readonly TextBlock _message = new()
	{
		TextWrapping = TextWrapping.Wrap,
	};

	private readonly StackPanel _analysis = new()
	{
		Spacing = 4,
	};

	private IReadOnlyList<ScriptDiagnosticVisualMarker> _diagnosticMarkers = [];
	private IReadOnlyList<ScriptReferenceVisualToken> _referenceTokens = [];
	private string? _diagnosticHoverText;

	private readonly TextBox? _rowCount;
	private readonly TextBox? _channelCount;
	private readonly TextBox? _minorHighlight;
	private readonly TextBox? _majorHighlight;

	private ScriptReferenceAnalysisSnapshot? _syntaxSnapshot;

	public ScriptEditorControl(
		DocumentWorkspace workspace,
		ScriptPatternDefinition pattern,
		Action close,
		Action<string> changed,
		string backLabel = "← Document",
		Action? testRequested = null,
		Action? documentSaveRequested = null)
		: this(
			workspace,
			pattern,
			null,
			close,
			changed,
			backLabel,
			testRequested,
			documentSaveRequested)
	{
	}

	public ScriptEditorControl(
		DocumentWorkspace workspace,
		ScriptSequenceDefinition sequence,
		Action close,
		Action<string> changed,
		string backLabel = "← Document",
		Action? testRequested = null,
		Action? documentSaveRequested = null)
		: this(
			workspace,
			null,
			sequence,
			close,
			changed,
			backLabel,
			testRequested,
			documentSaveRequested)
	{
	}

	private ScriptEditorControl(
		DocumentWorkspace workspace,
		ScriptPatternDefinition? pattern,
		ScriptSequenceDefinition? sequence,
		Action close,
		Action<string> changed,
		string backLabel,
		Action? testRequested,
		Action? documentSaveRequested)
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
		_testRequested = testRequested;
		_documentSaveRequested = documentSaveRequested;

		string source =
			pattern?.Source
				?? sequence!.Source;
		_appliedSource = source;

		_source =
			new TextEditor
			{
				Text = source,
				WordWrap = false,
				ShowLineNumbers = true,
				Height = 420,
				MinWidth = 520,
			};
		ScriptEditorFonts.ApplyTo(_source);
		_source.TextArea.TextView.ElementGenerators.Add(
			_referenceGenerator);
		_source.TextArea.TextView.LineTransformers.Add(
			_syntaxColorizer);
		_source.TextArea.TextView.BackgroundRenderers.Add(
			_diagnosticRenderer);
		// Intercept token-edge deletion before AvaloniaEdit treats a whole
		// one-column visual reference as an indivisible character.
		_source.TextArea.AddHandler(
			InputElement.KeyDownEvent,
			OnScriptTokenKeyDown,
			RoutingStrategies.Tunnel,
			handledEventsToo: true);
		// AvaloniaEdit owns an internal TextArea. Its key events can be
		// consumed before the host sees a bubbling event. The window handles
		// shortcuts first when attached; this fallback covers the text area
		// itself without double-dispatching handled keys.
		_source.TextArea.AddHandler(
			InputElement.KeyDownEvent,
			OnSourceShortcutKeyDown,
			RoutingStrategies.Tunnel,
			handledEventsToo: true);
		_source.TextChanged += (_, _) =>
		{
			RefreshAnalysis();
			_saveSource.IsEnabled = HasUnsavedScriptSource;
		};
		_saveSource.Click += (_, _) => TrySaveScriptSource();
		_source.PointerMoved += OnSourcePointerMoved;
		_source.PointerExited += (_, _) =>
			ClearDiagnosticHover();
		ToolTip.SetPlacement(
			_source,
			PlacementMode.Pointer);

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
		RefreshAnalysis();
	}

	private SongObject ScriptObject =>
		(SongObject?)_pattern
			?? _sequence!;

	public string ScriptSourceDisplayName => ScriptObject.Name;

	public ObjectId? ScriptPatternId => _pattern?.Id;
	public ObjectId? ScriptSequenceId => _sequence?.Id;

	public ScriptSourceSaveShortcutRoute TryHandleSourceSaveShortcut(Visual? keySource)
	{
		if (!ScriptSourceShortcutRouting.IsWithin(keySource, _source)
			&& !ScriptSourceShortcutRouting.IsWithin(keySource, _saveSource))
			return ScriptSourceSaveShortcutRoute.NotApplicable;

		if (!HasUnsavedScriptSource)
			return ScriptSourceSaveShortcutRoute.SaveDocument;

		// Capture the pre-save dirty state. A successful first press
		// must not immediately forward to disk in the same key event.
		TrySaveScriptSource();
		return ScriptSourceSaveShortcutRoute.SaveLocally;
	}

	public bool HasUnsavedScriptSource =>
		!string.Equals(_source.Text ?? string.Empty,
			_appliedSource, StringComparison.Ordinal);

	public void DiscardUnsavedScriptSource()
	{
		_source.Text = _appliedSource;
		_saveSource.IsEnabled = false;
	}

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

		// Keep the source commit action visible before the long reference pane.
		body.Children.Add(_saveSource);

		body.Children.Add(new TextBlock
		{
			Text = "Script API reference",
			FontWeight = FontWeight.SemiBold,
		});
		body.Children.Add(ScriptEditorHelp.CreateReferenceViewer(
			_pattern is not null
				? ScriptEditorHelp.PatternReference
				: ScriptEditorHelp.SequenceReference));

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

	private void InsertReference()
	{
		if (_reference.SelectedItem
			is not ScriptObjectReferenceOption option)
		{
			_message.Text =
				"Choose an object before inserting a reference.";
			return;
		}

		int start = _source.SelectionStart;
		int length = _source.SelectionLength;
		_source.Document.Replace(
			start,
			length,
			option.ReferenceText);
		int caret =
			start + option.ReferenceText.Length;
		_source.Select(caret, 0);
		_source.Focus();
		_message.Text =
			$"Inserted {option.DisplayName} as {option.ReferenceText}.";
	}

	private void RefreshAnalysis()
	{
		try
		{
			ScriptSourceDocumentAnalysis analysis =
				AnalyzeSource(
					_source.Text
						?? string.Empty);
			IReadOnlyList<ScriptReferenceVisualToken> referenceTokens =
				ScriptReferenceVisualTokenCatalog.Create(
					analysis.References);
			_referenceTokens = referenceTokens;
			_referenceGenerator.SetTokens(
				referenceTokens);
			_syntaxColorizer.SetSpans(
				ScriptSyntaxHighlightCatalog.Create(
					analysis.SyntaxSnapshot,
					referenceTokens));
			_diagnosticMarkers =
				ScriptDiagnosticVisualMarkerCatalog.Create(
					_source.Document.TextLength,
					analysis.Diagnostics);
			_diagnosticRenderer.SetMarkers(
				_diagnosticMarkers);
			ClearDiagnosticHover();
			_source.TextArea.TextView.Redraw();
			RenderAnalysis(analysis);
		}
		catch (Exception ex)
		{
			_analysis.Children.Clear();
			_analysis.Children.Add(
				new TextBlock
				{
					Text =
						$"Script analysis failed: {ex.Message}",
					TextWrapping = TextWrapping.Wrap,
				});
		}
	}

	private void OnSourceShortcutKeyDown(object? sender, KeyEventArgs e)
	{
		if (e.Handled)
			return;

		if (e.Key == Key.S && e.KeyModifiers == KeyModifiers.Control)
		{
			// AvaloniaEdit's TextArea can swallow the bubbling KeyDown.
			// The normal window tunnel runs first when available; this
			// fallback also supports an already-clean second Ctrl+S.
			if (HasUnsavedScriptSource)
			{
				e.Handled = true;
				TrySaveScriptSource();
			}
			else if (_documentSaveRequested is not null)
			{
				e.Handled = true;
				_documentSaveRequested();
			}
		}
		else if (e.Key == Key.F6 && e.KeyModifiers == KeyModifiers.None
			&& _testRequested is not null)
		{
			e.Handled = true;
			_testRequested();
		}
	}

	private void OnScriptTokenKeyDown(object? sender, KeyEventArgs e)
	{
		if (e.Handled
			|| e.KeyModifiers != KeyModifiers.None
			|| _source.SelectionLength != 0
			|| e.Key is not (Key.Back or Key.Delete))
			return;

		if (!ScriptReferenceTokenEditing.TryPlan(
				_referenceTokens, _source.CaretOffset,
				backspace: e.Key == Key.Back,
				out ScriptReferenceTokenEditing.Deletion edit))
			return;

		_source.Document.Remove(edit.Offset, 1);
		_source.CaretOffset = edit.CaretAfter;
		e.Handled = true;
	}

	private void OnSourcePointerMoved(
		object? sender,
		PointerEventArgs e)
	{
		Point point =
			e.GetPosition(_source);
		TextViewPosition? position =
			_source.GetPositionFromPoint(point);
		if (position is null)
		{
			ClearDiagnosticHover();
			return;
		}

		int offset =
			_source.Document.GetOffset(
				position.Value.Location);
		ScriptDiagnosticHoverInfo? hover =
			ScriptDiagnosticHoverCatalog.FindAtOffset(
				_diagnosticMarkers,
				offset);
		ShowDiagnosticHover(hover);
	}

	private void ShowDiagnosticHover(
		ScriptDiagnosticHoverInfo? hover)
	{
		string? text = hover?.Text;
		if (string.Equals(
				text,
				_diagnosticHoverText,
				StringComparison.Ordinal))
		{
			return;
		}

		ToolTip.SetIsOpen(_source, false);
		_diagnosticHoverText = text;

		if (text is null)
		{
			ToolTip.SetTip(_source, null);
			return;
		}

		ToolTip.SetTip(
			_source,
			new TextBlock
			{
				Text = text,
				TextWrapping = TextWrapping.Wrap,
				MaxWidth = 560,
			});
		ToolTip.SetIsOpen(_source, true);
	}

	private void ClearDiagnosticHover()
		=> ShowDiagnosticHover(null);

	private ScriptSourceDocumentAnalysis AnalyzeSource(
		string source)
	{
		ScriptSourceDocumentAnalysis analysis =
			_pattern is not null
				? ScriptSourceDocumentAnalyzer.Analyze(
					_workspace,
					_pattern,
					source,
					_syntaxSnapshot)
				: ScriptSourceDocumentAnalyzer.Analyze(
					_workspace,
					_sequence!,
					source,
					_syntaxSnapshot);
		_syntaxSnapshot =
			analysis.SyntaxSnapshot;
		return analysis;
	}

	private void RenderAnalysis(
		ScriptSourceDocumentAnalysis analysis)
	{
		_analysis.Children.Clear();

		if (_diagnosticMarkers.Count == 0)
		{
			_analysis.Children.Add(
				new TextBlock
				{
					Text = "Analysis: no compiler diagnostics.",
				});
		}
		else
		{
			_analysis.Children.Add(
				new TextBlock
				{
					Text = "Diagnostics:",
					FontWeight = FontWeight.SemiBold,
				});

			foreach (ScriptDiagnosticVisualMarker marker
				in _diagnosticMarkers)
			{
				Button diagnostic =
					new()
					{
						// String Button.Content becomes AccessText and treats
						// underscores as Alt-key mnemonics. Script diagnostics
						// must display _O(137) literally, without accelerators.
						Content = new TextBlock
						{
							Text = $"{marker.Severity} {marker.Code} "
								+ $"at {marker.OriginalSpan.Start}: "
								+ marker.Message,
							TextWrapping = TextWrapping.Wrap,
						},
						HorizontalAlignment = HorizontalAlignment.Stretch,
						HorizontalContentAlignment = HorizontalAlignment.Left,
					};
				diagnostic.Click += (_, _) =>
					NavigateToDiagnostic(marker);
				_analysis.Children.Add(diagnostic);
			}
		}

		_analysis.Children.Add(
			new TextBlock
			{
				Text =
					analysis.References.Count == 0
						? "Object references: none."
						: "Object references:",
				FontWeight =
					analysis.References.Count == 0
						? FontWeight.Normal
						: FontWeight.SemiBold,
			});

		foreach (ProjectedScriptObjectReference reference
			in analysis.References)
		{
			_analysis.Children.Add(
				new TextBlock
				{
					Text =
						$"{reference.DisplayName} — {reference.Kind} "
							+ $"<{reference.Id.Value}> "
							+ $"[{reference.Resolution}]",
					TextWrapping = TextWrapping.Wrap,
				});
		}
	}

	private void NavigateToDiagnostic(
		ScriptDiagnosticVisualMarker marker)
	{
		ScriptDiagnosticNavigationTarget target =
			ScriptDiagnosticNavigation.GetTarget(
				_source.Document.TextLength,
				marker);

		_source.Select(
			target.SelectionStart,
			target.SelectionLength);
		_source.CaretOffset =
			target.CaretOffset;

		var location =
			_source.Document.GetLocation(
				target.CaretOffset);
		_source.ScrollTo(
			location.Line,
			location.Column);
		_source.Focus();
	}

	public bool TrySaveScriptSource()
	{
		if (!HasUnsavedScriptSource)
			return true;
		try
		{
			string source =
				_source.Text
					?? string.Empty;
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

			_appliedSource = source;
			_saveSource.IsEnabled = false;
			_message.Text = "Script source saved to the song.";
			_changed(
				$"Updated script source for {ScriptObject.Name}");
			return true;
		}
		catch (Exception ex)
		{
			_message.Text = ex.Message;
			return false;
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
