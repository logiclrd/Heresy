using System;
using System.Collections.Generic;
using System.Globalization;

using Avalonia;
using Avalonia.Controls;
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
public sealed class ScriptEditorControl : UserControl
{
	private readonly DocumentWorkspace _workspace;
	private readonly ScriptPatternDefinition? _pattern;
	private readonly ScriptSequenceDefinition? _sequence;
	private readonly Action _close;
	private readonly Action<string> _changed;
	private readonly string _backLabel;

	private readonly TextEditor _source;
	private readonly ScriptObjectReferenceElementGenerator _referenceGenerator =
		new();
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

	private ScriptReferenceAnalysisSnapshot? _syntaxSnapshot;

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

		_source =
			new TextEditor
			{
				Text = source,
				WordWrap = false,
				ShowLineNumbers = true,
				Height = 420,
				MinWidth = 520,
			};
		_source.TextArea.TextView.ElementGenerators.Add(
			_referenceGenerator);
		_source.TextChanged += (_, _) =>
			RefreshAnalysis();

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
					"Object references remain canonical _O(id) expressions in the AvaloniaEdit document. Roslyn-recognized references are rendered as single visual object-name tokens without changing raw source offsets, so AvaloniaEdit's native selection, clipboard and undo behavior continues to operate on valid restricted C#.",
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
			_referenceGenerator.SetTokens(
				ScriptReferenceVisualTokenCatalog.Create(
					analysis.References));
			_source.TextArea.TextView.Redraw();
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
				lines.Add(
					$"{diagnostic.Severity} {diagnostic.Code} "
						+ $"at {diagnostic.Span.Start}: "
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
