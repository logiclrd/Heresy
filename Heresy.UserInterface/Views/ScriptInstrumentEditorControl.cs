using System;
using System.Globalization;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

using Heresy.Core.Envelopes;
using Heresy.Core.Filters;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.PatternEditing;

namespace Heresy.UserInterface.Views;

/// <summary>
/// One deterministic expression chooses from an explicit, ordered list
/// of source/pitch/Envelope tone definitions. Referenced song objects remain
/// discoverable even when the expression does not select them.
/// </summary>
public sealed class ScriptInstrumentEditorControl : UserControl
{
    private sealed record Choice(ObjectId? Id, string Text)
    {
        public override string ToString() => Text;
    }
    private sealed record ToneRow(int Index, string Text)
    {
        public override string ToString() => Text;
    }

    private readonly DocumentWorkspace _workspace;
    private readonly ScriptInstrumentDefinition _definition;
    private readonly Action _close;
    private readonly Action<string> _changed;
    private readonly TextBox _expression;
    private readonly TextBox _pitchExpression;
    private readonly TextBox _ratio;
    private readonly ComboBox _sound;
    private readonly ComboBox[] _envelopes;
    private readonly ComboBox _filter;
    private readonly Button _editFilter;
    private FilterInstance? _toneFilter;
    private bool _loadingFilter;
    private readonly ListBox _tones;
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap };

    public ScriptInstrumentEditorControl(
        DocumentWorkspace workspace, ScriptInstrumentDefinition definition,
        Action close, Action<string> changed)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _close = close ?? throw new ArgumentNullException(nameof(close));
        _changed = changed ?? throw new ArgumentNullException(nameof(changed));
        _expression = new TextBox
        {
            Text = definition.Source, MinWidth = 420, MinHeight = 75,
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        };
        _pitchExpression = new TextBox
        {
            Text = definition.PitchExpression,
            MinWidth = 420,
        };
        _ratio = new TextBox { Text = "1", MinWidth = 115 };
        _sound = new ComboBox { MinWidth = 260 };
        _envelopes = Enumerable.Range(0, 4)
            .Select(_ => new ComboBox { MinWidth = 260 })
            .ToArray();
        _filter = new ComboBox { MinWidth = 260 };
        _editFilter = new Button { Content = "Edit Filter...", IsEnabled = false };
        _filter.SelectionChanged += (_, _) =>
        {
            if (_loadingFilter)
                return;
            ObjectId? id = (_filter.SelectedItem as Choice)?.Id;
            if (id == _toneFilter?.DefinitionId)
                return;
            _toneFilter = id.HasValue
                ? new FilterInstance((FilterDefinition)_workspace.Document.Objects[id.Value])
                : null;
            _editFilter.IsEnabled = _toneFilter is not null;
        };
        _editFilter.Click += (_, _) => EditToneFilter();
        _tones = new ListBox { MinWidth = 420, MinHeight = 145 };
        _tones.SelectionChanged += (_, _) => PopulateSelection();
        Content = BuildContent();
        RefreshChoices();
        RefreshRows();
    }

    private Control BuildContent()
    {
        StackPanel panel = new()
        {
            Margin = new Thickness(18, 14),
            Spacing = 8,
            MaxWidth = 800,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        Button back = new() { Content = "← Document" };
        back.Click += (_, _) => _close();
        panel.Children.Add(back);
        panel.Children.Add(new TextBlock
        {
            Text = _definition.Name + " — Script Instrument",
            FontSize = 20, FontWeight = FontWeight.SemiBold,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Restricted C# integer expression of pitchMultiplier (C4 = 1). " +
                "Return a zero-based tone index, or -1 for silence.",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(_expression);
        panel.Children.Add(new TextBlock
        {
            Text = "Optional pitch scaling expression of pitchMultiplier; " +
                "multiplied by the selected tone's fixed ratio.",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(_pitchExpression);
        Button apply = new() { Content = "Apply expressions" };
        apply.Click += (_, _) => Execute(() =>
            ScriptInstrumentDocumentEditor.SetExpressions(
                _workspace, _definition, _expression.Text ?? string.Empty,
                _pitchExpression.Text ?? string.Empty));
        panel.Children.Add(apply);
        panel.Children.Add(new TextBlock
        {
            Text = "Declared tones (the index is returned by the selector)",
            FontWeight = FontWeight.SemiBold,
        });
        panel.Children.Add(_tones);
        panel.Children.Add(new TextBlock { Text = "Sound source" });
        panel.Children.Add(_sound);
        panel.Children.Add(new TextBlock { Text = "Pitch multiplier" });
        panel.Children.Add(_ratio);
        string[] names = ["Volume Envelope", "Pitch Envelope",
            "Panning Envelope", "Filter Envelope"];
        for (int i = 0; i < _envelopes.Length; i++)
        {
            panel.Children.Add(new TextBlock { Text = names[i] });
            panel.Children.Add(_envelopes[i]);
        }
        panel.Children.Add(new TextBlock
        {
            Text = "Tone Filter (distinct from Filter Envelope)",
        });
        panel.Children.Add(_filter);
        panel.Children.Add(_editFilter);
        StackPanel actions = new()
        { Orientation = Orientation.Horizontal, Spacing = 10 };
        Button add = new() { Content = "Add tone" };
        add.Click += (_, _) => Execute(() =>
        {
            AddTone();
            RefreshRows();
        });
        Button update = new() { Content = "Update selected" };
        update.Click += (_, _) => Execute(() =>
        {
            UpdateTone();
            RefreshRows();
        });
        Button remove = new() { Content = "Remove selected" };
        remove.Click += (_, _) => Execute(() =>
        {
            int index = SelectedIndex();
            ScriptInstrumentDocumentEditor.RemoveTone(
                _workspace, _definition, index);
            RefreshRows();
        });
        actions.Children.Add(add);
        actions.Children.Add(update);
        actions.Children.Add(remove);
        panel.Children.Add(actions);
        panel.Children.Add(_message);
        return new ScrollViewer { Content = panel };
    }

    private void RefreshChoices()
    {
        Choice[] sources = _workspace.Document.Objects.Values
            .Where(o => PatternSourceCatalog.IsSoundSource(o.Kind))
            .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(o => o.Id.Value)
            .Select(o => new Choice(o.Id, $"{o.Name} [{o.Id.Value}]"))
            .ToArray();
        _sound.ItemsSource = sources;
        _sound.SelectedItem = sources.FirstOrDefault();
        Choice[] envelopeChoices =
        [
            new Choice(null, "(None)"),
            .. _workspace.Document.Objects.Values
                .OfType<EnvelopeDefinition>()
                .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(o => o.Id.Value)
                .Select(o => new Choice(o.Id, $"{o.Name} [{o.Id.Value}]")),
        ];
        foreach (ComboBox box in _envelopes)
        {
            box.ItemsSource = envelopeChoices;
            box.SelectedItem = envelopeChoices[0];
        }
        Choice[] filters = [
            new Choice(null, "(None)"),
            .. _workspace.Document.Objects.Values
                .OfType<FilterDefinition>()
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Id.Value)
                .Select(x => new Choice(x.Id, $"{x.Name} [{x.Id.Value}]")),
        ];
        _loadingFilter = true;
        try
        {
            _filter.ItemsSource = filters;
            _filter.SelectedItem = filters[0];
            _toneFilter = null;
            _editFilter.IsEnabled = false;
        }
        finally { _loadingFilter = false; }
    }

    private void RefreshRows()
    {
        ToneRow[] rows = _definition.ToneSpecifications
            .Select((tone, index) => new ToneRow(index,
                $"{index}: source {tone.SourceId.Value}, pitch {tone.PitchMultiplier:R}" +
                $" | V:{Label(tone.VolumeEnvelopeId)}" +
                $" P:{Label(tone.PitchEnvelopeId)}" +
                $" Pan:{Label(tone.PanningEnvelopeId)}" +
                $" F:{Label(tone.FilterEnvelopeId)}" +
                $" Filter:{Label(tone.FilterConfiguration?.DefinitionId)}"))
            .ToArray();
        _tones.ItemsSource = rows;
    }

    private static string Label(ObjectId? id) =>
        id.HasValue ? id.Value.Value.ToString(CultureInfo.InvariantCulture) : "—";

    private void PopulateSelection()
    {
        if (_tones.SelectedItem is not ToneRow row
            || (uint)row.Index >= (uint)_definition.ToneSpecifications.Count)
            return;
        ToneSpecification tone = _definition.ToneSpecifications[row.Index];
        _sound.SelectedItem = (_sound.ItemsSource as Choice[])?
            .FirstOrDefault(x => x.Id == tone.SourceId);
        _ratio.Text = tone.PitchMultiplier.ToString("R", CultureInfo.InvariantCulture);
        ObjectId?[] ids = [tone.VolumeEnvelopeId, tone.PitchEnvelopeId,
            tone.PanningEnvelopeId, tone.FilterEnvelopeId];
        for (int i = 0; i < 4; i++)
            _envelopes[i].SelectedItem = (_envelopes[i].ItemsSource as Choice[])?
                .FirstOrDefault(c => c.Id == ids[i]);
        _loadingFilter = true;
        try
        {
            _toneFilter = tone.FilterConfiguration?.Clone();
            _filter.SelectedItem = (_filter.ItemsSource as Choice[])?
                .FirstOrDefault(c => c.Id == _toneFilter?.DefinitionId);
            _editFilter.IsEnabled = _toneFilter is not null;
        }
        finally { _loadingFilter = false; }
    }

    private int SelectedIndex() =>
        _tones.SelectedItem is ToneRow row ? row.Index
        : throw new InvalidOperationException("Select a tone first.");

    private ObjectId SelectedSource() =>
        (_sound.SelectedItem as Choice)?.Id
        ?? throw new InvalidOperationException("Choose a sound source.");

    private ObjectId? Envelope(int index) =>
        (_envelopes[index].SelectedItem as Choice)?.Id;

    private double Pitch() =>
        double.Parse(_ratio.Text ?? string.Empty, CultureInfo.InvariantCulture);

    private void AddTone() =>
        ScriptInstrumentDocumentEditor.AddTone(_workspace, _definition,
            SelectedSource(), Pitch(), Envelope(0), Envelope(1), Envelope(2), Envelope(3),
            _toneFilter);

    private void UpdateTone() =>
        ScriptInstrumentDocumentEditor.UpdateTone(_workspace, _definition,
            SelectedIndex(), SelectedSource(), Pitch(),
            Envelope(0), Envelope(1), Envelope(2), Envelope(3), _toneFilter);

    private void EditToneFilter()
    {
        try
        {
            if (_toneFilter is null)
                return;
            Window? owner = TopLevel.GetTopLevel(this) as Window;
            if (owner is null)
                throw new InvalidOperationException(
                    "Open the Script Instrument editor in a window to edit a tone Filter.");
            int? selectedIndex = (_tones.SelectedItem as ToneRow)?.Index;
            FilterInstanceEditSession session = new(
                () => _toneFilter ?? throw new InvalidOperationException(
                    "This tone no longer has a Filter."),
                proposed =>
                {
                    _toneFilter = proposed;
                    if (selectedIndex is int index
                        && (uint)index < (uint)_definition.ToneSpecifications.Count
                        && _definition.ToneSpecifications[index]
                            .FilterConfiguration?.DefinitionId == proposed.DefinitionId)
                    {
                        ScriptInstrumentDocumentEditor.SetToneFilterConfiguration(
                            _workspace, _definition, index, proposed);
                        _changed($"Updated tone Filter in {_definition.Name}");
                    }
                });
            Window window = new()
            {
                Title = "Tone Filter — " + _definition.Name,
                Width = 910,
                Height = 600,
                Content = new FilterInstanceEditorControl(session,
                    message => _message.Text = message),
            };
            window.Show(owner);
        }
        catch (Exception error) { _message.Text = error.Message; }
    }

    private void Execute(Action action)
    {
        try
        {
            action();
            _message.Text = "Script Instrument updated.";
            _changed("Updated script Instrument " + _definition.Name);
        }
        catch (Exception error)
        {
            _message.Text = error.Message;
        }
    }
}
