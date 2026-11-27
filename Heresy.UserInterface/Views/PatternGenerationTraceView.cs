using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;

using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.Core.Sequencing;
using Heresy.Scripting.Compilation;
using Heresy.UserInterface.Documents;

namespace Heresy.UserInterface.Views;

/// <summary>Explicit diagnostic view. Owns an isolated snapshot, not the
/// editable source document or the transport. The UI thread owns publication
/// and selection; the worker owns compilation and raw capture.</summary>
internal sealed class PatternGenerationTraceView : UserControl
{
    private readonly Action _back;
    private readonly Action<int> _navigate;
    private readonly Button _cancel = new() { Content = "Cancel", IsEnabled = false };
    private readonly ComboBox _order = new()
    {
        ItemsSource = new[] { "Original order", "Playback order", "Playback order by channel" },
        SelectedIndex = 0,
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };
    private readonly ListBox _events = new()
    {
        SelectionMode = SelectionMode.Multiple,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
    };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextEditor _snapshot = new()
    {
        IsReadOnly = true, ShowLineNumbers = true, WordWrap = false,
    };
    private readonly ScriptObjectReferenceElementGenerator _references = new();
    private readonly ScriptSyntaxColorizer _colorizer = new();
    private readonly ScriptSequencePlayCallsiteRenderer _highlight = new();
    private CancellationTokenSource? _activeRun;
    private PatternGenerationTracePresentation? _model;
    private bool _changingSelection;

    internal PatternGenerationTraceView(Action back, Action<int> navigate)
    {
        _back = back;
        _navigate = navigate;
        ScriptEditorFonts.ApplyTo(_snapshot);
        _snapshot.TextArea.TextView.ElementGenerators.Add(_references);
        _snapshot.TextArea.TextView.LineTransformers.Add(_colorizer);
        _snapshot.TextArea.TextView.BackgroundRenderers.Add(_highlight);
        _snapshot.AddHandler(InputElement.PointerPressedEvent, OnSnapshotPressed,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        _events.ItemTemplate = new FuncDataTemplate<PatternTraceRow>((row, _) =>
        {
            if (row is null) return null;
            return new TextBlock
            {
                Text = row.Text,
                Margin = new Thickness(8 + Math.Min(row.Depth, 12) * 14, 3, 8, 3),
                TextWrapping = TextWrapping.Wrap,
                FontWeight = row.Kind is PatternTraceRowKind.ChannelHeader
                    or PatternTraceRowKind.GroupOperation ? FontWeight.SemiBold : FontWeight.Normal,
                Opacity = row.Dropped ? 0.7 : 1,
            };
        });
        _events.SelectionChanged += (_, _) => UpdateSelection();
        _order.SelectionChanged += (_, _) => ChangeOrder();
        _cancel.Click += (_, _) =>
        {
            _activeRun?.Cancel();
            _cancel.IsEnabled = false;
            _status.Text = "Cancel requested — retaining the captured prefix.";
        };

        Button backButton = new() { Content = "← Source" };
        backButton.Click += (_, _) => _back();
        StackPanel toolbar = new() { Orientation = Orientation.Horizontal, Spacing = 12 };
        toolbar.Children.Add(backButton);
        toolbar.Children.Add(_cancel);
        toolbar.Children.Add(new TextBlock
        {
            Text = "Generation Trace",
            FontWeight = FontWeight.SemiBold,
            FontSize = 20,
            VerticalAlignment = VerticalAlignment.Center,
        });

        DockPanel left = new() { LastChildFill = true };
        DockPanel.SetDock(_order, Dock.Top);
        left.Children.Add(_order);
        left.Children.Add(_events);
        DockPanel right = new() { LastChildFill = true };
        var explanation = new TextBlock
        {
            Text = "Captured source (read-only) · Double-click to return to source",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        };
        DockPanel.SetDock(explanation, Dock.Top);
        right.Children.Add(explanation);
        right.Children.Add(_snapshot);
        Grid split = new() { ColumnSpacing = 6 };
        split.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(2, GridUnitType.Star)));
        split.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(5)));
        split.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(3, GridUnitType.Star)));
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 2);
        GridSplitter splitter = new()
        {
            ResizeDirection = GridResizeDirection.Columns,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = Brushes.Gray,
        };
        Grid.SetColumn(splitter, 1);
        split.Children.Add(left);
        split.Children.Add(splitter);
        split.Children.Add(right);
        DockPanel root = new() { Margin = new Thickness(12), LastChildFill = true };
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        _status.Margin = new Thickness(0, 8, 0, 0);
        toolbar.Margin = new Thickness(0, 0, 0, 8);
        root.Children.Add(toolbar);
        root.Children.Add(_status);
        root.Children.Add(split);
        Content = root;
    }

    internal PatternGenerationTracePresentation? Model => _model;
    internal string CapturedSource => _snapshot.Text ?? string.Empty;

    /// <summary>Invalidate without disposing a cancellation source still in use
    /// by a worker. A superseded worker cannot publish into another view.</summary>
    internal void Abandon()
    {
        var previous = _activeRun;
        _activeRun = null;
        previous?.Cancel();
        _cancel.IsEnabled = false;
        _model = null;
        _changingSelection = true;
        try
        {
            _events.ItemsSource = null;
            _order.IsEnabled = false;
            _snapshot.Text = string.Empty;
            _references.SetTokens([]);
            _colorizer.SetSpans([]);
            _highlight.SetSpans([], 0);
            _snapshot.TextArea.TextView.Redraw();
        }
        finally { _changingSelection = false; }
    }

    internal async Task RunAsync(ScriptPatternDefinition definition,
        IReadOnlyList<ScriptReferenceVisualToken> tokens,
        IReadOnlyList<ScriptSyntaxHighlightSpan> syntax)
    {
        Abandon();
        using CancellationTokenSource run = new();
        _activeRun = run;
        _model = null;
        _changingSelection = true;
        try
        {
            _events.ItemsSource = null;
            _order.SelectedIndex = 0;
            _order.IsEnabled = false;
            _snapshot.Text = definition.Source;
            _references.SetTokens(tokens);
            _colorizer.SetSpans(syntax);
            _highlight.SetSpans([], _snapshot.Document.TextLength);
            _snapshot.TextArea.TextView.Redraw();
        }
        finally { _changingSelection = false; }
        _cancel.IsEnabled = true;
        _status.Text = "Compiling and generating diagnostic events — no audio is played.";
        try
        {
            var outcome = await Task.Run<(PatternGenerationTracePresentation? Model, string? Error)>(() =>
            {
                var result = ScriptCompiler.CompilePatternGenerationTrace(definition);
                if (!result.Success || result.Program is null)
                    return (Model: (PatternGenerationTracePresentation?)null,
                        Error: string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.Message)));
                var trace = result.Program.Capture(new SequencingCancellation(run.Token));
                var model = new PatternGenerationTracePresentation(trace);
                // Build immutable projections on the worker, not while the
                // user changes the selector in the editor.
                foreach (var order in Enum.GetValues<PatternTraceOrder>())
                    model.GetProjection(order);
                return (Model: (PatternGenerationTracePresentation?)model, Error: (string?)null);
            });
            if (!ReferenceEquals(_activeRun, run)) return;
            _model = outcome.Model;
            if (_model is null)
            {
                _status.Text = "Compilation failed — no audio published. " + outcome.Error;
                return;
            }
            _events.ItemsSource = _model.Rows;
            _order.IsEnabled = true;
            _status.Text = _model.StatusText + $" {_model.Trace.Events.Count} events."
                + (string.IsNullOrWhiteSpace(_model.Trace.Detail) ? "" : " " + _model.Trace.Detail);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ReferenceEquals(_activeRun, run))
                _status.Text = "Diagnostic generation failed — no audio published. " + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_activeRun, run))
            {
                _activeRun = null;
                _cancel.IsEnabled = false;
            }
        }
    }

    private void ChangeOrder()
    {
        if (_changingSelection || _model is null || _order.SelectedIndex < 0) return;
        _changingSelection = true;
        try
        {
            _model.SetOrder((PatternTraceOrder)_order.SelectedIndex);
            var selected = _model.SelectedOccurrenceIds.ToHashSet();
            _events.ItemsSource = _model.Rows;
            if (_events.SelectedItems is { } items)
            {
                items.Clear();
                // Restore the primary event first, then other selected events.
                int primary = _model.PrimaryRowIndex;
                if (primary >= 0)
                    items.Add(_model.Rows[primary]);
                foreach (var row in _model.Rows)
                    if (row.OccurrenceId is long id && selected.Contains(id)
                        && id != _model.PrimaryOccurrenceId)
                        items.Add(row);
            }
            if (_model.PrimaryRowIndex >= 0)
                _events.ScrollIntoView(_model.PrimaryRowIndex);
        }
        finally { _changingSelection = false; }
        ShowSpans(_model.SelectedSourceSpans);
    }

    private void UpdateSelection()
    {
        if (_changingSelection || _model is null) return;
        var rows = _events.SelectedItems?.OfType<PatternTraceRow>().ToArray() ?? [];
        var groupIds = rows.Where(r => r.Kind == PatternTraceRowKind.GroupOperation
            && r.InvocationId.HasValue).Select(r => r.InvocationId!.Value).ToHashSet();
        var ids = rows.Where(r => r.OccurrenceId.HasValue).Select(r => r.OccurrenceId!.Value)
            .Concat(groupIds.Count == 0 ? Enumerable.Empty<long>()
                : _model.Trace.Events.Where(e => e.InvocationId is long id && groupIds.Contains(id))
                    .Select(e => e.OccurrenceId));
        _model.SelectOccurrences(ids, (_events.SelectedItem as PatternTraceRow)?.OccurrenceId);
        var spans = _model.SelectedSourceSpans
            .Concat(rows.Where(r => !r.OccurrenceId.HasValue && r.SourceSpan.HasValue)
                .Select(r => r.SourceSpan!.Value)).Distinct().ToArray();
        ShowSpans(spans);
    }

    private void ShowSpans(IReadOnlyList<SequenceCallsite> spans)
    {
        if (_highlight.SetSpans(spans, _snapshot.Document.TextLength))
            _snapshot.TextArea.TextView.Redraw();
        if (spans.Count > 0 && spans[0].Start >= 0 && spans[0].Start < _snapshot.Document.TextLength)
        {
            var location = _snapshot.Document.GetLocation(spans[0].Start);
            _snapshot.ScrollTo(location.Line, location.Column);
        }
    }

    private void OnSnapshotPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount != 2 || !e.GetCurrentPoint(_snapshot).Properties.IsLeftButtonPressed) return;
        var position = _snapshot.GetPositionFromPoint(e.GetPosition(_snapshot));
        if (position is null)
        {
            e.Handled = true;
            _back();
            return;
        }
        int offset = _snapshot.Document.GetOffset(position.Value.Location);
        e.Handled = true;
        _navigate(offset);
    }
}
