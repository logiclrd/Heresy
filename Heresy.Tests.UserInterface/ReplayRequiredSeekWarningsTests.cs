using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.UserInterface.PatternEditing;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class ReplayRequiredSeekWarningsTests
{
	[Test]
	public void OxxOnMixdownStartIsFlaggedButSameEffectWithoutStartOnlyRemembers()
	{
		SongDocument doc = new();
		ObjectId child = doc.AllocateObjectId();
		doc.Add(new DataPatternDefinition(child, "Child"));
		ObjectId parentId = doc.AllocateObjectId();
		DataPatternDefinition pattern = new(parentId, "Parent")
			{ RowCount = 5, ChannelCount = 1 };
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new SampleOffsetPatternEffect(0x12));
		PatternCell first = pattern.Grid.GetOrCreateCell(1, 0);
		first.Note = new StartPatternNote(child, mixdown: true);
		first.Effects.Add(new SampleOffsetPatternEffect(0));
		pattern.Grid.GetOrCreateCell(2, 0).Effects.Add(
			new SampleOffsetPatternEffect(0x21));
		pattern.Grid.GetOrCreateCell(3, 0).Effects.Add(
			new RetriggerPatternEffect(0xA3));
		pattern.Grid.GetOrCreateCell(4, 0).Note = new PatternNoteCut();
		doc.Add(pattern);

		var warnings = ReplayRequiredSeekWarnings.Analyze(doc,
			PatternEditorContext.ForPattern(doc, pattern));
		Assert.That(warnings.ContainsKey((0, 0)), Is.False);
		Assert.That(warnings[(1, 0)].Message, Does.Contain("Oxx"));
		Assert.That(warnings.ContainsKey((2, 0)), Is.False);
		Assert.That(warnings[(3, 0)].Message, Does.Contain("Qxy"));
		Assert.That(warnings[(1, 0)].IsConditional, Is.False);
		Assert.That(first.Effects, Has.Count.EqualTo(1));
	}

	[Test]
	public void DirectSampleAndNonMixdownFlattenedNotesDoNotWarn()
	{
		SongDocument doc = new();
		ObjectId child = doc.AllocateObjectId();
		doc.Add(new DataPatternDefinition(child, "Child"));
		ObjectId parentId = doc.AllocateObjectId();
		DataPatternDefinition pattern = new(parentId, "Parent")
			{ RowCount = 3, ChannelCount = 1 };
		pattern.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(child, mixdown: false);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new SampleOffsetPatternEffect(3));
		pattern.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new RetriggerPatternEffect(0xA3));
		PatternCell other = pattern.Grid.GetOrCreateCell(2, 0);
		other.Note = new StartPatternNote((ObjectId)999U, mixdown: true);
		other.Effects.Add(new SampleOffsetPatternEffect(3));
		doc.Add(pattern);
		var warnings = ReplayRequiredSeekWarnings.Analyze(doc,
			PatternEditorContext.ForPattern(doc, pattern));
		Assert.That(warnings, Is.Empty);
	}

	[Test]
	public void InheritedSelectionAndSequenceOccurrencesAreAnalyzedSeparately()
	{
		SongDocument doc = new();
		ObjectId child = doc.AllocateObjectId();
		doc.Add(new DataPatternDefinition(child, "Child"));
		ObjectId introId = doc.AllocateObjectId();
		DataPatternDefinition intro = new(introId, "Intro")
			{ RowCount = 2, ChannelCount = 1 };
		intro.Grid.GetOrCreateCell(0, 0).SourceId = child;
		doc.Add(intro);
		ObjectId bodyId = doc.AllocateObjectId();
		DataPatternDefinition body = new(bodyId, "Body")
			{ RowCount = 1, ChannelCount = 1 };
		body.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote(mixdown: true);
		body.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new SampleOffsetPatternEffect(3));
		doc.Add(body);
		ObjectId seqId = doc.AllocateObjectId();
		DataSequenceDefinition seq = new(seqId, "Sequence");
		seq.Entries.Add(new SequenceEntry(introId));
		seq.Entries.Add(new SequenceEntry(bodyId));
		doc.Add(seq);
		var warnings = ReplayRequiredSeekWarnings.Analyze(doc,
			PatternEditorContext.ForSequence(doc, seq, 1));
		Assert.That(warnings[(2, 0)].IsConditional, Is.False);
	}

	[Test]
	public void InstrumentWithPotentialNestedPatternIsConditionalNotCertain()
	{
		SongDocument doc = new();
		ObjectId inner = doc.AllocateObjectId();
		doc.Add(new DataPatternDefinition(inner, "Inner"));
		ObjectId instrumentId = doc.AllocateObjectId();
		InstrumentDefinition instrument = new(instrumentId, "Instrument");
		instrument.ToneSpecifications.Add(new ToneSpecification { SourceId = inner });
		instrument.ToneTable.Add(0);
		doc.Add(instrument);
		ObjectId parentId = doc.AllocateObjectId();
		DataPatternDefinition pattern = new(parentId, "Parent")
			{ RowCount = 1, ChannelCount = 1 };
		pattern.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote(instrumentId);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new SampleOffsetPatternEffect(1));
		doc.Add(pattern);
		var warnings = ReplayRequiredSeekWarnings.Analyze(doc,
			PatternEditorContext.ForPattern(doc, pattern));
		Assert.That(warnings[(0, 0)].IsConditional, Is.True);
	}

	[Test]
	public void SessionPreferenceDefaultsToShowingHintsAndCanHideThem()
	{
		Heresy.UserInterface.UserInterfaceConfiguration settings = new();
		Assert.That(settings.ShowReplayRequiredSeekHints, Is.True);
		settings.ShowReplayRequiredSeekHints = false;
		Assert.That(settings.ShowReplayRequiredSeekHints, Is.False);
		Assert.That(settings.ShouldDisplayRuntimeDiagnostic("HRSEQ005"), Is.False);
		Assert.That(settings.ShouldDisplayRuntimeDiagnostic("HRSEQ006"), Is.False);
		Assert.That(settings.ShouldDisplayRuntimeDiagnostic("HRSEQ001"), Is.True);
		settings.ShowReplayRequiredSeekHints = true;
		Assert.That(settings.ShouldDisplayRuntimeDiagnostic("HRSEQ005"), Is.True);
	}

	[Test]
	public void OZeroWithoutRecalledOffsetAndSaxAloneAreNotSeeks()
	{
		SongDocument doc = new();
		ObjectId inner = doc.AllocateObjectId();
		doc.Add(new DataPatternDefinition(inner, "Inner"));
		ObjectId patternId = doc.AllocateObjectId();
		DataPatternDefinition pattern = new(patternId, "Parent")
			{ RowCount = 2, ChannelCount = 1 };
		PatternCell first = pattern.Grid.GetOrCreateCell(0, 0);
		first.Note = new StartPatternNote(inner, mixdown: true);
		first.Effects.Add(new SampleOffsetPatternEffect(0));
		PatternCell second = pattern.Grid.GetOrCreateCell(1, 0);
		second.Effects.Add(new SampleOffsetHighPatternEffect(2));
		doc.Add(pattern);
		Assert.That(ReplayRequiredSeekWarnings.Analyze(doc,
			PatternEditorContext.ForPattern(doc, pattern)), Is.Empty);
	}
}
