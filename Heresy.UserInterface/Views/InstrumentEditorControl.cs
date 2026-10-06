using System;
using System.Globalization;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

using Heresy.Core.Envelopes;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.PatternEditing;

namespace Heresy.UserInterface.Views;

/// <summary>
/// Main-workspace editor for recursive instruments. The control projects the
/// existing Core object directly; reusable tone specifications and tone-table
/// mappings remain separate just as they are in InstrumentDefinition.
/// </summary>
public sealed class InstrumentEditorControl : UserControl
{
	private readonly DocumentWorkspace _workspace;
	private readonly InstrumentDefinition _instrument;
	private readonly Action _close;
	private readonly Action<string> _changed;
	private readonly TextBox _divisions;
	private readonly TextBox _offset;
	private readonly TextBox _toneTableLength;
	private readonly StackPanel _specificationRows = new() { Spacing = 4 };
	private readonly StackPanel _toneTableRows = new() { Spacing = 4 };
	private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap };

	private PatternSourceOption[] _sources = [];
	private EnvelopeOption[] _envelopes = [];

	public InstrumentEditorControl(
		DocumentWorkspace workspace,
		InstrumentDefinition instrument,
		Action close,
		Action<string> changed)
	{
		_workspace = workspace
			?? throw new ArgumentNullException(nameof(workspace));
		_instrument = instrument
			?? throw new ArgumentNullException(nameof(instrument));
		_close = close
			?? throw new ArgumentNullException(nameof(close));
		_changed = changed
			?? throw new ArgumentNullException(nameof(changed));

		_divisions = NumberBox(instrument.Divisions);
		_offset = NumberBox(instrument.Offset);
		_toneTableLength = NumberBox(instrument.ToneTable.Count);

		Content = BuildContent();
		Refresh();
	}

	private Control BuildContent()
	{
		Button back =
			new()
			{
				Content = "← Document",
				MinWidth = 100,
			};
		back.Click += (_, _) => _close();

		TextBlock title =
			new()
			{
				Text = _instrument.Name,
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

		Button applyLookup =
			new()
			{
				Content = "Apply lookup",
			};
		applyLookup.Click += (_, _) => ApplyLookup();

		StackPanel lookup =
			new()
			{
				Orientation = Orientation.Horizontal,
				Spacing = 6,
				Margin = new Thickness(10, 6),
				VerticalAlignment = VerticalAlignment.Center,
			};
		lookup.Children.Add(Label("Divisions"));
		lookup.Children.Add(_divisions);
		lookup.Children.Add(Label("Offset"));
		lookup.Children.Add(_offset);
		lookup.Children.Add(applyLookup);

		StackPanel specifications = new()
		{
			Margin = new Thickness(10, 8, 10, 4),
			Spacing = 6,
		};
		specifications.Children.Add(SectionHeading("Tone specifications"));
		specifications.Children.Add(
			new TextBlock
			{
				Text =
					"Each specification chooses any sound-producing song object, composes a pitch multiplier, and may override any of the four envelope paths.",
				TextWrapping = TextWrapping.Wrap,
			});
		specifications.Children.Add(BuildAddSpecificationRow());
		specifications.Children.Add(_specificationRows);

		Button resizeToneTable =
			new()
			{
				Content = "Resize",
			};
		resizeToneTable.Click += (_, _) => ResizeToneTable();

		StackPanel toneTableHeader =
			new()
			{
				Orientation = Orientation.Horizontal,
				Spacing = 6,
				VerticalAlignment = VerticalAlignment.Center,
			};
		toneTableHeader.Children.Add(SectionHeading("Tone table"));
		toneTableHeader.Children.Add(Label("Length"));
		toneTableHeader.Children.Add(_toneTableLength);
		toneTableHeader.Children.Add(resizeToneTable);

		StackPanel toneTable = new()
		{
			Margin = new Thickness(10, 8),
			Spacing = 6,
		};
		toneTable.Children.Add(toneTableHeader);
		toneTable.Children.Add(
			new TextBlock
			{
				Text =
					"Tone indices map to reusable specifications. Silent entries remain -1 in the Core model.",
				TextWrapping = TextWrapping.Wrap,
			});
		toneTable.Children.Add(_toneTableRows);

		StackPanel body = new()
		{
			Spacing = 4,
		};
		body.Children.Add(lookup);
		body.Children.Add(specifications);
		body.Children.Add(toneTable);

		ScrollViewer scroll =
			new()
			{
				Content = body,
				HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
				VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			};

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
		root.Children.Add(scroll);
		return root;
	}

	private Control BuildAddSpecificationRow()
	{
		ComboBox source = SourceBox(ObjectId.None);
		TextBox pitch = NumberBox(1.0);
		ComboBox volume = EnvelopeBox(null);
		ComboBox pitchEnvelope = EnvelopeBox(null);
		ComboBox panning = EnvelopeBox(null);
		ComboBox filter = EnvelopeBox(null);

		Button add = new() { Content = "Add specification" };
		add.Click += (_, _) =>
		{
			try
			{
				if (source.SelectedItem is not PatternSourceOption selectedSource)
				{
					_message.Text =
						"Create or select a sound source before adding a tone specification.";
					return;
				}

				InstrumentDocumentEditor.AddToneSpecification(
					_workspace,
					_instrument,
					selectedSource.Id,
					ParsePositiveDouble(pitch, "Pitch multiplier"),
					SelectedEnvelope(volume),
					SelectedEnvelope(pitchEnvelope),
					SelectedEnvelope(panning),
					SelectedEnvelope(filter));
				Refresh();
				_changed($"Added tone specification to {_instrument.Name}");
			}
			catch (Exception ex)
			{
				_message.Text = ex.Message;
			}
		};

		return BuildSpecificationFields(
			"#",
			source,
			pitch,
			volume,
			pitchEnvelope,
			panning,
			filter,
			add,
			null);
	}

	private Control BuildSpecificationRow(int index)
	{
		ToneSpecification tone = _instrument.ToneSpecifications[index];
		ComboBox source = SourceBox(tone.SourceId);
		TextBox pitch = NumberBox(tone.PitchMultiplier);
		ComboBox volume = EnvelopeBox(tone.VolumeEnvelopeId);
		ComboBox pitchEnvelope = EnvelopeBox(tone.PitchEnvelopeId);
		ComboBox panning = EnvelopeBox(tone.PanningEnvelopeId);
		ComboBox filter = EnvelopeBox(tone.FilterEnvelopeId);

		Button apply = new() { Content = "Apply" };
		apply.Click += (_, _) =>
		{
			try
			{
				ObjectId sourceId =
					source.SelectedItem is PatternSourceOption selected
						? selected.Id
						: tone.SourceId;
				InstrumentDocumentEditor.UpdateToneSpecification(
					_workspace,
					_instrument,
					index,
					sourceId,
					ParsePositiveDouble(pitch, "Pitch multiplier"),
					SelectedEnvelopeOrExisting(volume, tone.VolumeEnvelopeId),
					SelectedEnvelopeOrExisting(pitchEnvelope, tone.PitchEnvelopeId),
					SelectedEnvelopeOrExisting(panning, tone.PanningEnvelopeId),
					SelectedEnvelopeOrExisting(filter, tone.FilterEnvelopeId));
				Refresh();
				_changed($"Updated tone specification {index}");
			}
			catch (Exception ex)
			{
				_message.Text = ex.Message;
			}
		};

		Button remove = new() { Content = "Remove" };
		remove.Click += (_, _) =>
		{
			InstrumentDocumentEditor.RemoveToneSpecification(
				_workspace,
				_instrument,
				index);
			Refresh();
			_changed($"Removed tone specification {index}");
		};

		return BuildSpecificationFields(
			index.ToString(CultureInfo.InvariantCulture),
			source,
			pitch,
			volume,
			pitchEnvelope,
			panning,
			filter,
			apply,
			remove);
	}

	private static Control BuildSpecificationFields(
		string index,
		ComboBox source,
		TextBox pitch,
		ComboBox volume,
		ComboBox pitchEnvelope,
		ComboBox panning,
		ComboBox filter,
		Button primary,
		Button? secondary)
	{
		Grid row = new()
		{
			MinHeight = 36,
		};
		row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(38)));
		row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(220)));
		row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(90)));
		row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(155)));
		row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(155)));
		row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(155)));
		row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(155)));
		row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(170)));

		AddAt(
			row,
			new TextBlock
			{
				Text = index,
				VerticalAlignment = VerticalAlignment.Center,
				Margin = new Thickness(4),
			},
			0);
		AddAt(row, source, 1);
		AddAt(row, pitch, 2);
		AddAt(row, volume, 3);
		AddAt(row, pitchEnvelope, 4);
		AddAt(row, panning, 5);
		AddAt(row, filter, 6);

		StackPanel actions = new()
		{
			Orientation = Orientation.Horizontal,
			Spacing = 4,
		};
		actions.Children.Add(primary);
		if (secondary is not null)
			actions.Children.Add(secondary);
		AddAt(row, actions, 7);

		return new Border
		{
			BorderBrush = Brushes.Gray,
			BorderThickness = new Thickness(0, 0, 0, 1),
			Child = row,
		};
	}

	private Control BuildToneTableRow(int toneIndex)
	{
		ToneMappingOption[] options = GetToneMappingOptions();
		ComboBox mapping =
			new()
			{
				ItemsSource = options,
				Width = 360,
			};
		int current = _instrument.ToneTable[toneIndex];
		mapping.SelectedItem =
			options.FirstOrDefault(option =>
				option.SpecificationIndex == current)
			?? options[0];

		Button apply = new() { Content = "Apply" };
		apply.Click += (_, _) =>
		{
			if (mapping.SelectedItem is not ToneMappingOption selected)
				return;

			InstrumentDocumentEditor.SetToneMapping(
				_workspace,
				_instrument,
				toneIndex,
				selected.SpecificationIndex);
			_changed($"Updated tone-table index {toneIndex}");
		};

		StackPanel row = new()
		{
			Orientation = Orientation.Horizontal,
			Spacing = 8,
			VerticalAlignment = VerticalAlignment.Center,
		};
		row.Children.Add(
			new TextBlock
			{
				Text = toneIndex.ToString(CultureInfo.InvariantCulture),
				Width = 54,
				VerticalAlignment = VerticalAlignment.Center,
			});
		row.Children.Add(mapping);
		row.Children.Add(apply);
		return row;
	}

	private void ApplyLookup()
	{
		try
		{
			InstrumentDocumentEditor.UpdateLookup(
				_workspace,
				_instrument,
				ParsePositiveDouble(_divisions, "Divisions"),
				ParseInt(_offset, "Offset"));
			_changed($"Updated lookup for {_instrument.Name}");
		}
		catch (Exception ex)
		{
			_message.Text = ex.Message;
		}
	}

	private void ResizeToneTable()
	{
		try
		{
			int length = ParseInt(_toneTableLength, "Tone-table length");
			if (length < 0)
				throw new ArgumentOutOfRangeException(
					nameof(length),
					"Tone-table length must be non-negative.");

			InstrumentDocumentEditor.ResizeToneTable(
				_workspace,
				_instrument,
				length);
			Refresh();
			_changed($"Resized tone table to {length}");
		}
		catch (Exception ex)
		{
			_message.Text = ex.Message;
		}
	}

	private void Refresh()
	{
		_sources =
			PatternSourceCatalog.GetSources(_workspace.Document);
		_envelopes =
			new[]
			{
				new EnvelopeOption(null, "— inherit / unspecified"),
			}
			.Concat(
				_workspace.Document.Objects.Values
					.OfType<EnvelopeDefinition>()
					.OrderBy(
						envelope => envelope.Name,
						StringComparer.OrdinalIgnoreCase)
					.ThenBy(envelope => envelope.Id.Value)
					.Select(envelope =>
						new EnvelopeOption(
							envelope.Id,
							$"{envelope.Name} <{envelope.Id.Value}>")))
			.ToArray();

		_divisions.Text =
			_instrument.Divisions.ToString(CultureInfo.CurrentCulture);
		_offset.Text =
			_instrument.Offset.ToString(CultureInfo.CurrentCulture);
		_toneTableLength.Text =
			_instrument.ToneTable.Count.ToString(CultureInfo.CurrentCulture);

		_specificationRows.Children.Clear();
		for (int index = 0;
			index < _instrument.ToneSpecifications.Count;
			index++)
		{
			_specificationRows.Children.Add(
				BuildSpecificationRow(index));
		}
		if (_instrument.ToneSpecifications.Count == 0)
		{
			_specificationRows.Children.Add(
				new TextBlock
				{
					Text = "No tone specifications yet.",
					Margin = new Thickness(4, 6),
				});
		}

		_toneTableRows.Children.Clear();
		for (int index = 0; index < _instrument.ToneTable.Count; index++)
			_toneTableRows.Children.Add(BuildToneTableRow(index));
		if (_instrument.ToneTable.Count == 0)
		{
			_toneTableRows.Children.Add(
				new TextBlock
				{
					Text = "The tone table is empty; the instrument is currently silent.",
					Margin = new Thickness(4, 6),
				});
		}
	}

	private ComboBox SourceBox(ObjectId selectedId)
	{
		PatternSourceOption[] choices = _sources;
		PatternSourceOption? selected =
			choices.FirstOrDefault(option => option.Id == selectedId);

		if (selected is null && !selectedId.IsNone)
		{
			string name =
				_workspace.Document.Tombstones.TryGetValue(
					selectedId,
					out ObjectTombstone? tombstone)
					? $"⚠ {tombstone.LastKnownName}"
					: "⚠ Missing source";
			selected =
				new PatternSourceOption(
					selectedId,
					name,
					SongObjectKind.Unknown);
			choices = new[] { selected }.Concat(choices).ToArray();
		}

		ComboBox box =
			new()
			{
				ItemsSource = choices,
				Width = 210,
				SelectedItem =
					selected ?? choices.FirstOrDefault(),
			};
		return box;
	}

	private ComboBox EnvelopeBox(ObjectId? selectedId)
	{
		EnvelopeOption[] choices = _envelopes;
		EnvelopeOption? selected =
			choices.FirstOrDefault(option =>
				option.Id == selectedId);

		if (selected is null
			&& selectedId.HasValue
			&& !selectedId.Value.IsNone)
		{
			string name =
				_workspace.Document.Tombstones.TryGetValue(
					selectedId.Value,
					out ObjectTombstone? tombstone)
					? $"⚠ {tombstone.LastKnownName} <{selectedId.Value.Value}>"
					: $"⚠ <{selectedId.Value.Value}>";
			selected = new EnvelopeOption(selectedId, name);
			choices = new[] { choices[0], selected }
				.Concat(choices.Skip(1))
				.ToArray();
		}

		return new ComboBox
		{
			ItemsSource = choices,
			Width = 145,
			SelectedItem = selected ?? choices[0],
		};
	}

	private ToneMappingOption[] GetToneMappingOptions()
	{
		ToneMappingOption[] result =
			new ToneMappingOption[
				_instrument.ToneSpecifications.Count + 1];
		result[0] =
			new ToneMappingOption(
				-1,
				"— Silent");
		for (int index = 0;
			index < _instrument.ToneSpecifications.Count;
			index++)
		{
			ToneSpecification tone =
				_instrument.ToneSpecifications[index];
			string source =
				_sources.FirstOrDefault(option =>
					option.Id == tone.SourceId)?.DisplayName
				?? $"⚠ <{tone.SourceId.Value}>";
			result[index + 1] =
				new ToneMappingOption(
					index,
					$"#{index}: {source}");
		}
		return result;
	}

	private static ObjectId? SelectedEnvelope(ComboBox box)
		=> box.SelectedItem is EnvelopeOption option
			? option.Id
			: null;

	private static ObjectId? SelectedEnvelopeOrExisting(
		ComboBox box,
		ObjectId? existing)
		=> box.SelectedItem is EnvelopeOption option
			? option.Id
			: existing;

	private static TextBlock Label(string text)
		=> new()
		{
			Text = text,
			VerticalAlignment = VerticalAlignment.Center,
		};

	private static TextBlock SectionHeading(string text)
		=> new()
		{
			Text = text,
			FontSize = 17,
			FontWeight = FontWeight.SemiBold,
			VerticalAlignment = VerticalAlignment.Center,
		};

	private static TextBox NumberBox(double value)
		=> new()
		{
			Text = value.ToString(CultureInfo.CurrentCulture),
			Width = 80,
		};

	private static TextBox NumberBox(int value)
		=> new()
		{
			Text = value.ToString(CultureInfo.CurrentCulture),
			Width = 80,
		};

	private static double ParsePositiveDouble(
		TextBox box,
		string label)
	{
		if (!double.TryParse(
			box.Text,
			NumberStyles.Float,
			CultureInfo.CurrentCulture,
			out double value)
			|| !(value > 0.0)
			|| double.IsNaN(value)
			|| double.IsInfinity(value))
		{
			throw new ArgumentException(
				$"{label} must be a finite number greater than zero.");
		}
		return value;
	}

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
				$"{label} is not a valid integer.");
		}
		return value;
	}

	private static void AddAt(
		Grid grid,
		Control control,
		int column)
	{
		Grid.SetColumn(control, column);
		grid.Children.Add(control);
	}

	private sealed record EnvelopeOption(
		ObjectId? Id,
		string DisplayName)
	{
		public override string ToString() => DisplayName;
	}

	private sealed record ToneMappingOption(
		int SpecificationIndex,
		string DisplayName)
	{
		public override string ToString() => DisplayName;
	}
}
