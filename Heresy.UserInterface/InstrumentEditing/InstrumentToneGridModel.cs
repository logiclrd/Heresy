using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Envelopes;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.PatternEditing;

namespace Heresy.UserInterface.InstrumentEditing;

/// <summary>
/// One Instrument-editor-lifetime view model, independent of Avalonia.
/// The Core ToneTable and ToneSpecifications remain the persisted format,
/// but the editor presents and edits one specification *per tone index*.
/// Shared Core specifications are copy-on-write; equivalent specs can
/// then be interned back into the shared list without changing semantics.
/// </summary>
public sealed class InstrumentToneGridModel
{
	private readonly DocumentWorkspace _workspace;
	private readonly InstrumentDefinition _instrument;
	private readonly Dictionary<int, InstrumentToneGridCells> _drafts = [];

	public InstrumentToneGridModel(
		DocumentWorkspace workspace,
		InstrumentDefinition instrument)
	{
		_workspace = workspace
			?? throw new ArgumentNullException(nameof(workspace));
		_instrument = instrument
			?? throw new ArgumentNullException(nameof(instrument));
		ValidateOwnership();
	}

	public int? EntryIndex { get; private set; }
	public InstrumentToneGridCells EntryCells { get; private set; } = new();

	public IReadOnlyList<InstrumentToneGridRow> Rows
	{
		get
		{
			ValidateOwnership();
			return InstrumentToneGridProjection.Project(_instrument, _drafts);
		}
	}

	public void SetEntryDraft(
		int? index,
		InstrumentToneGridCells cells)
	{
		EntryIndex = index;
		EntryCells = cells;
	}

	/// <summary>
	/// Invoked when the top index field loses focus. An empty/negative index
	/// leaves it available for correction. An accepted index overwrites the
	/// corresponding existing row and clears the entry fields.
	/// </summary>
	public bool CommitEntry()
	{
		if (EntryIndex is not int index || index < 0)
			return false;
		SetRow(index, EntryCells);
		EntryIndex = null;
		EntryCells = new();
		return true;
	}

	public void OnDivisionsChanged()
	{
		// Reprojection uses the current instrument lookup parameters, while
		// editor-only drafts survive throughout this editor's lifetime.
		// Reset the reserved entry row as requested for grid reinitialization.
		ValidateOwnership();
		EntryIndex = null;
		EntryCells = new();
	}

	public void SetRow(int index, InstrumentToneGridCells cells)
	{
		ValidateOwnership();
		if (index < 0)
			throw new ArgumentOutOfRangeException(nameof(index));

		InstrumentToneGridCells canonical = NormalizeCells(cells);
		// Validate first; a failed edit must change neither the Core song
		// nor the editor's prior drafts.
		if (canonical.HasSource)
			ValidateAssignedCells(canonical);

		Dictionary<int, InstrumentToneGridCells> mapped = ReadMappings();
		InstrumentToneGridCells? previous =
			mapped.TryGetValue(index, out InstrumentToneGridCells value)
				? value : null;

		if (canonical.HasSource)
			mapped[index] = canonical;
		else
			mapped.Remove(index);

		// Build and commit the Core replacement first. If validation or
		// table construction throws, the editor's prior drafts remain
		// intact too (no half-applied transaction).
		if (previous != (canonical.HasSource ? canonical : null))
			PublishMappedCells(mapped);

		if (canonical.HasSource)
			_drafts.Remove(index);
		else
			_drafts[index] = canonical;
	}

	/// <summary>
	/// Delete discards any unsaved cells in the selected row and removes its
	/// effective saved tone mapping. It never acts on the reserved entry row.
	/// An extra out-of-range row disappears if nothing else references it.
	/// </summary>
	public void DeleteRow(int index)
	{
		ValidateOwnership();
		if (index < 0)
			throw new ArgumentOutOfRangeException(nameof(index));

		Dictionary<int, InstrumentToneGridCells> mapped = ReadMappings();
		if (mapped.Remove(index))
			PublishMappedCells(mapped);
		// Discard the draft only after a successful Core transaction.
		_drafts.Remove(index);
	}

	private Dictionary<int, InstrumentToneGridCells> ReadMappings()
	{
		Dictionary<int, InstrumentToneGridCells> mapped = [];
		for (int index = 0; index < _instrument.ToneTable.Count; index++)
		{
			int specIndex = _instrument.ToneTable[index];
			if (specIndex == -1)
				continue;
			if ((uint)specIndex >= (uint)_instrument.ToneSpecifications.Count)
			{
				throw new InvalidOperationException(
					$"Tone-table index {index} has invalid specification {specIndex}.");
			}
			mapped[index] = InstrumentToneGridCells.FromTone(
				_instrument.ToneSpecifications[specIndex]);
		}
		return mapped;
	}

	private void PublishMappedCells(
		Dictionary<int, InstrumentToneGridCells> mapped)
	{
		// Construct both lists before mutating Core so allocation/validation
		// failure cannot leave a partially rewritten instrument.
		List<ToneSpecification> specifications = [];
		Dictionary<InstrumentToneGridCells, int> interned = [];
		int length = _instrument.ToneTable.Count;
		if (mapped.Count > 0)
		{
			int largest = mapped.Keys.Max();
			if (largest == int.MaxValue)
				throw new ArgumentOutOfRangeException(nameof(mapped));
			length = Math.Max(length, checked(largest + 1));
		}
		// The visual grid has an explicit supported upper size; very large
		// ToneTable allocations are rejected, never silently truncated.
		if (length > InstrumentToneGridProjection.MaxRegularRows)
		{
			throw new InvalidOperationException(
				$"A tone table with {length} entries exceeds the editor limit of {InstrumentToneGridProjection.MaxRegularRows}.");
		}
		List<int> toneTable = Enumerable.Repeat(-1, length).ToList();
		foreach ((int index, InstrumentToneGridCells cells) in mapped
			.OrderBy(pair => pair.Key))
		{
			if (!interned.TryGetValue(cells, out int specIndex))
			{
				specIndex = specifications.Count;
				specifications.Add(cells.ToTone());
				interned.Add(cells, specIndex);
			}
			toneTable[index] = specIndex;
		}
		// Assign from complete snapshots, then mark one audio-affecting
		// revision. Existing shared spec indices never become dangling;
		// changes to one row cannot unexpectedly affect another.
		_instrument.ToneSpecifications.Clear();
		_instrument.ToneSpecifications.AddRange(specifications);
		_instrument.ToneTable.Clear();
		_instrument.ToneTable.AddRange(toneTable);
		_workspace.Document.MarkChanged(affectsAudio: true);
	}

	private void ValidateOwnership()
	{
		if (!_workspace.Document.TryGet(
			_instrument.Id, out SongObject? live)
			|| !ReferenceEquals(live, _instrument))
		{
			throw new InvalidOperationException(
				"The instrument must belong to the active song document.");
		}
	}

	private void ValidateAssignedCells(
		InstrumentToneGridCells cells)
	{
		ObjectId id = cells.SourceId!.Value;
		if (!_workspace.Document.TryGet(id, out SongObject? source)
			|| source is null
			|| !PatternSourceCatalog.IsSoundSource(source.Kind))
		{
			throw new InvalidOperationException(
				$"Object {id.Value} is not a live sound source in this song.");
		}
		if (!(cells.PitchMultiplier > 0.0)
			|| !double.IsFinite(cells.PitchMultiplier))
		{
			throw new ArgumentOutOfRangeException(nameof(cells),
				"Tone pitch multiplier must be positive and finite.");
		}
		ValidateEnvelope(cells.VolumeEnvelopeId);
		ValidateEnvelope(cells.PitchEnvelopeId);
		ValidateEnvelope(cells.PanningEnvelopeId);
		ValidateEnvelope(cells.FilterEnvelopeId);
	}

	private void ValidateEnvelope(ObjectId? id)
	{
		if (id is null)
			return;
		if (!_workspace.Document.TryGet(id.Value, out SongObject? o)
			|| o is not EnvelopeDefinition)
		{
			throw new InvalidOperationException(
				$"Object {id.Value.Value} is not a live Envelope in this song.");
		}
	}

	private static InstrumentToneGridCells NormalizeCells(
		InstrumentToneGridCells cells)
		=> cells with
		{
			SourceId = cells.SourceId is ObjectId src && src.IsNone
				? null : cells.SourceId,
			VolumeEnvelopeId = Normalize(cells.VolumeEnvelopeId),
			PitchEnvelopeId = Normalize(cells.PitchEnvelopeId),
			PanningEnvelopeId = Normalize(cells.PanningEnvelopeId),
			FilterEnvelopeId = Normalize(cells.FilterEnvelopeId),
		};

	private static ObjectId? Normalize(ObjectId? id)
		=> id is ObjectId value && value.IsNone ? null : id;
}
