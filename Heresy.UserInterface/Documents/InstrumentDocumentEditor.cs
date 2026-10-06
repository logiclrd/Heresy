using System;
using System.Linq;

using Heresy.Core.Envelopes;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.UserInterface.PatternEditing;

namespace Heresy.UserInterface.Documents;

/// <summary>
/// Framework-independent instrument authoring operations used by the Avalonia UI.
/// Tone specifications are immutable value snapshots from the editor's point of
/// view, so updates replace list entries rather than mutating them in place.
/// </summary>
public static class InstrumentDocumentEditor
{
	public static InstrumentDefinition CreateInstrument(
		DocumentWorkspace workspace,
		string name,
		double divisions = 12.0,
		int offset = 0)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		ValidateDivisions(divisions);

		ObjectId id = workspace.Document.AllocateObjectId();
		InstrumentDefinition instrument =
			new(id, name.Trim())
			{
				Divisions = divisions,
				Offset = offset,
			};
		workspace.Document.Add(instrument, affectsAudio: true);
		return instrument;
	}

	public static void UpdateLookup(
		DocumentWorkspace workspace,
		InstrumentDefinition instrument,
		double divisions,
		int offset)
	{
		ValidateInstrument(workspace, instrument);
		ValidateDivisions(divisions);

		if (instrument.Divisions == divisions
			&& instrument.Offset == offset)
		{
			return;
		}

		instrument.Divisions = divisions;
		instrument.Offset = offset;
		workspace.Document.MarkChanged(affectsAudio: true);
	}

	public static int AddToneSpecification(
		DocumentWorkspace workspace,
		InstrumentDefinition instrument,
		ObjectId sourceId,
		double pitchMultiplier = 1.0,
		ObjectId? volumeEnvelopeId = null,
		ObjectId? pitchEnvelopeId = null,
		ObjectId? panningEnvelopeId = null,
		ObjectId? filterEnvelopeId = null)
	{
		ValidateInstrument(workspace, instrument);
		ToneSpecification tone =
			CreateToneSpecification(
				workspace.Document,
				sourceId,
				pitchMultiplier,
				volumeEnvelopeId,
				pitchEnvelopeId,
				panningEnvelopeId,
				filterEnvelopeId);

		instrument.ToneSpecifications.Add(tone);
		workspace.Document.MarkChanged(affectsAudio: true);
		return instrument.ToneSpecifications.Count - 1;
	}

	public static void UpdateToneSpecification(
		DocumentWorkspace workspace,
		InstrumentDefinition instrument,
		int specificationIndex,
		ObjectId sourceId,
		double pitchMultiplier = 1.0,
		ObjectId? volumeEnvelopeId = null,
		ObjectId? pitchEnvelopeId = null,
		ObjectId? panningEnvelopeId = null,
		ObjectId? filterEnvelopeId = null)
	{
		ValidateInstrument(workspace, instrument);
		ValidateSpecificationIndex(instrument, specificationIndex);

		ToneSpecification replacement =
			CreateToneSpecification(
				workspace.Document,
				sourceId,
				pitchMultiplier,
				volumeEnvelopeId,
				pitchEnvelopeId,
				panningEnvelopeId,
				filterEnvelopeId);
		ToneSpecification current =
			instrument.ToneSpecifications[specificationIndex];

		if (Equivalent(current, replacement))
			return;

		instrument.ToneSpecifications[specificationIndex] = replacement;
		workspace.Document.MarkChanged(affectsAudio: true);
	}

	public static void RemoveToneSpecification(
		DocumentWorkspace workspace,
		InstrumentDefinition instrument,
		int specificationIndex)
	{
		ValidateInstrument(workspace, instrument);
		ValidateSpecificationIndex(instrument, specificationIndex);

		instrument.ToneSpecifications.RemoveAt(specificationIndex);
		for (int index = 0; index < instrument.ToneTable.Count; index++)
		{
			int mapping = instrument.ToneTable[index];
			if (mapping == specificationIndex)
				instrument.ToneTable[index] = -1;
			else if (mapping > specificationIndex)
				instrument.ToneTable[index] = mapping - 1;
		}

		workspace.Document.MarkChanged(affectsAudio: true);
	}

	public static void ResizeToneTable(
		DocumentWorkspace workspace,
		InstrumentDefinition instrument,
		int length)
	{
		ValidateInstrument(workspace, instrument);
		if (length < 0)
			throw new ArgumentOutOfRangeException(nameof(length));

		if (instrument.ToneTable.Count == length)
			return;

		if (instrument.ToneTable.Count > length)
		{
			instrument.ToneTable.RemoveRange(
				length,
				instrument.ToneTable.Count - length);
		}
		else
		{
			while (instrument.ToneTable.Count < length)
				instrument.ToneTable.Add(-1);
		}

		workspace.Document.MarkChanged(affectsAudio: true);
	}

	public static void SetToneMapping(
		DocumentWorkspace workspace,
		InstrumentDefinition instrument,
		int toneIndex,
		int specificationIndex)
	{
		ValidateInstrument(workspace, instrument);
		if ((uint)toneIndex >= (uint)instrument.ToneTable.Count)
			throw new ArgumentOutOfRangeException(nameof(toneIndex));
		if (specificationIndex < -1
			|| specificationIndex >= instrument.ToneSpecifications.Count)
		{
			throw new ArgumentOutOfRangeException(nameof(specificationIndex));
		}

		if (instrument.ToneTable[toneIndex] == specificationIndex)
			return;

		instrument.ToneTable[toneIndex] = specificationIndex;
		workspace.Document.MarkChanged(affectsAudio: true);
	}

	private static ToneSpecification CreateToneSpecification(
		SongDocument document,
		ObjectId sourceId,
		double pitchMultiplier,
		ObjectId? volumeEnvelopeId,
		ObjectId? pitchEnvelopeId,
		ObjectId? panningEnvelopeId,
		ObjectId? filterEnvelopeId)
	{
		ValidateSoundSource(document, sourceId);
		ValidatePitchMultiplier(pitchMultiplier);
		volumeEnvelopeId = NormalizeEnvelope(document, volumeEnvelopeId);
		pitchEnvelopeId = NormalizeEnvelope(document, pitchEnvelopeId);
		panningEnvelopeId = NormalizeEnvelope(document, panningEnvelopeId);
		filterEnvelopeId = NormalizeEnvelope(document, filterEnvelopeId);

		return new ToneSpecification
		{
			SourceId = sourceId,
			PitchMultiplier = pitchMultiplier,
			VolumeEnvelopeId = volumeEnvelopeId,
			PitchEnvelopeId = pitchEnvelopeId,
			PanningEnvelopeId = panningEnvelopeId,
			FilterEnvelopeId = filterEnvelopeId,
		};
	}

	private static void ValidateInstrument(
		DocumentWorkspace workspace,
		InstrumentDefinition instrument)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(instrument);

		if (!workspace.Document.TryGet(
			instrument.Id,
			out SongObject? stored)
			|| !ReferenceEquals(stored, instrument))
		{
			throw new InvalidOperationException(
				"The instrument is not part of the active song document.");
		}
	}

	private static void ValidateSoundSource(
		SongDocument document,
		ObjectId sourceId)
	{
		if (sourceId.IsNone
			|| !document.TryGet(sourceId, out SongObject? source)
			|| source is null
			|| !PatternSourceCatalog.IsSoundSource(source.Kind))
		{
			throw new InvalidOperationException(
				$"Object {sourceId.Value} is not a live sound source in the active song.");
		}
	}

	private static ObjectId? NormalizeEnvelope(
		SongDocument document,
		ObjectId? envelopeId)
	{
		if (!envelopeId.HasValue || envelopeId.Value.IsNone)
			return null;

		if (!document.TryGet(
			envelopeId.Value,
			out SongObject? songObject)
			|| songObject is not EnvelopeDefinition)
		{
			throw new InvalidOperationException(
				$"Object {envelopeId.Value.Value} is not a live envelope in the active song.");
		}

		return envelopeId;
	}

	private static void ValidateDivisions(double divisions)
	{
		if (!(divisions > 0.0)
			|| double.IsNaN(divisions)
			|| double.IsInfinity(divisions))
		{
			throw new ArgumentOutOfRangeException(nameof(divisions));
		}
	}

	private static void ValidatePitchMultiplier(double pitchMultiplier)
	{
		if (!(pitchMultiplier > 0.0)
			|| double.IsNaN(pitchMultiplier)
			|| double.IsInfinity(pitchMultiplier))
		{
			throw new ArgumentOutOfRangeException(nameof(pitchMultiplier));
		}
	}

	private static void ValidateSpecificationIndex(
		InstrumentDefinition instrument,
		int specificationIndex)
	{
		if ((uint)specificationIndex
			>= (uint)instrument.ToneSpecifications.Count)
		{
			throw new ArgumentOutOfRangeException(nameof(specificationIndex));
		}
	}

	private static bool Equivalent(
		ToneSpecification left,
		ToneSpecification right)
		=> left.SourceId == right.SourceId
			&& left.PitchMultiplier == right.PitchMultiplier
			&& left.VolumeEnvelopeId == right.VolumeEnvelopeId
			&& left.PitchEnvelopeId == right.PitchEnvelopeId
			&& left.PanningEnvelopeId == right.PanningEnvelopeId
			&& left.FilterEnvelopeId == right.FilterEnvelopeId;
}
