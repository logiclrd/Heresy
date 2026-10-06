using System.Linq;

using AwesomeAssertions;

using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class SongReferenceAnalyzerTests
{
	[Test]
	public void AnalyzeReportsStructuralReferenceKinds()
	{
		SongDocument document = new();
		ObjectId targetId = document.AllocateObjectId();
		document.Add(new DataPatternDefinition(targetId, "Target"));
		document.RootSequenceId = targetId;
		document.Root.Children.Add(new SongTreeObject("Target", targetId));

		ObjectId patternId = document.AllocateObjectId();
		DataPatternDefinition pattern = new(patternId, "Pattern");
		PatternCell referenceCell = pattern.Grid.GetOrCreateCell(0, 0);
		referenceCell.SourceId = targetId;
		referenceCell.Note = new StartPatternNote();
		document.Add(pattern);

		ObjectId sequenceId = document.AllocateObjectId();
		DataSequenceDefinition sequence = new(sequenceId, "Sequence");
		sequence.Entries.Add(new SequenceEntry(targetId));
		document.Add(sequence);

		ObjectId instrumentId = document.AllocateObjectId();
		InstrumentDefinition instrument = new(instrumentId, "Instrument");
		instrument.ToneSpecifications.Add(
			new ToneSpecification { SourceId = targetId });
		document.Add(instrument);

		SongReferenceAnalysis analysis = SongReferenceAnalyzer.Analyze(document);
		SongReferenceKind[] kinds = analysis.References
			.Where(reference => reference.TargetId == targetId)
			.Select(reference => reference.Kind)
			.ToArray();

		kinds.Should().Contain(SongReferenceKind.RootSequence);
		kinds.Should().Contain(SongReferenceKind.Tree);
		kinds.Should().Contain(SongReferenceKind.PatternNoteSource);
		kinds.Should().Contain(SongReferenceKind.SequencePattern);
		kinds.Should().Contain(SongReferenceKind.InstrumentSource);
	}

	[Test]
	public void AnalyzeReportsInstrumentEnvelopeSlotsSeparately()
	{
		SongDocument document = new();
		ObjectId envelopeId = document.AllocateObjectId();
		ObjectId instrumentId = document.AllocateObjectId();
		InstrumentDefinition instrument = new(instrumentId, "Instrument");
		instrument.ToneSpecifications.Add(
			new ToneSpecification
			{
				SourceId = new ObjectId(999),
				VolumeEnvelopeId = envelopeId,
				PitchEnvelopeId = envelopeId,
				PanningEnvelopeId = envelopeId,
				FilterEnvelopeId = envelopeId,
			});
		document.Add(instrument);

		SongReferenceAnalysis analysis = SongReferenceAnalyzer.Analyze(document);
		SongReferenceKind[] kinds = analysis.References
			.Where(reference => reference.TargetId == envelopeId)
			.Select(reference => reference.Kind)
			.ToArray();

		kinds.Should().Contain(SongReferenceKind.InstrumentVolumeEnvelope);
		kinds.Should().Contain(SongReferenceKind.InstrumentPitchEnvelope);
		kinds.Should().Contain(SongReferenceKind.InstrumentPanningEnvelope);
		kinds.Should().Contain(SongReferenceKind.InstrumentFilterEnvelope);
	}

	[Test]
	public void AnalyzeMarksScriptSourcesAsOpaqueUntilCompilerAnalysisExists()
	{
		SongDocument document = new();
		ObjectId scriptId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(scriptId, "Script"));

		SongReferenceAnalysis analysis = SongReferenceAnalyzer.Analyze(document);

		analysis.HasOpaqueScriptReferences.Should().BeTrue();
	}
}
