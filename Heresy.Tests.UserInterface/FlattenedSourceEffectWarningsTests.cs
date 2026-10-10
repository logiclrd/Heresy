using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.UserInterface.PatternEditing;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class FlattenedSourceEffectWarningsTests
{
	[Test]
	public void ExplicitFlatSourceShowsWarningButPreservesStoredEffects()
	{
		SongDocument doc = new();
		ObjectId innerId = doc.AllocateObjectId();
		doc.Add(new DataPatternDefinition(innerId, "Inner")
			{ RowCount = 1, ChannelCount = 1 });
		ObjectId outerId = doc.AllocateObjectId();
		DataPatternDefinition outer = new(outerId, "Outer")
			{ RowCount = 1, ChannelCount = 1 };
		PatternCell cell = outer.Grid.GetOrCreateCell(0, 0);
		cell.Note = new StartPatternNote(innerId);
		cell.Effects.Add(new RetriggerPatternEffect(0xA3));
		cell.Effects.Add(new TrackerTempoPatternEffect(0x80));
		doc.Add(outer);

		string? warning = FlattenedSourceEffectWarnings.Describe(
			doc, outer, 0, 0);
		Assert.That(warning, Does.Contain("Retrigger"));
		Assert.That(warning, Does.Not.Contain("Tempo"));
		Assert.That(cell.Effects, Has.Count.EqualTo(2));
	}

	[Test]
	public void CombinedNoteVolumeSlidesWarnButChannelVolumeRemainsPermitted()
	{
		SongDocument doc = new();
		ObjectId childId = doc.AllocateObjectId();
		doc.Add(new DataPatternDefinition(childId, "Child")
			{ RowCount = 1, ChannelCount = 1 });
		DataPatternDefinition parent = new((ObjectId)999U, "Parent")
			{ RowCount = 1, ChannelCount = 1 };
		PatternCell start = parent.Grid.GetOrCreateCell(0, 0);
		start.Note = new StartPatternNote(childId);
		start.Effects.Add(new TrackerChannelVolumePatternEffect(32));
		Assert.That(FlattenedSourceEffectWarnings.Describe(
			doc, parent, 0, 0), Is.Null);
		start.Effects.Add(new TonePortamentoVolumeSlidePatternEffect(0x43));
		start.Effects.Add(new VibratoVolumeSlidePatternEffect(0x32));
		start.Effects.Add(new TrackerVolumeSlidePatternEffect(0x21));
		string? warning = FlattenedSourceEffectWarnings.Describe(
			doc, parent, 0, 0);
		Assert.That(warning, Does.Contain("TonePortamentoVolumeSlide"));
		Assert.That(warning, Does.Contain("VibratoVolumeSlide"));
		Assert.That(warning, Does.Not.Contain("TrackerVolumeSlide"),
			"Dxx is a meaningful live note-volume effect on the flattened source.");
		Assert.That(warning, Does.Not.Contain("TrackerChannelVolume"));
		Assert.That(start.Effects, Has.Count.EqualTo(4));
	}

	[Test]
	public void MixdownOrdinaryAndSafeEffectsDoNotWarn()
	{
		SongDocument doc = new();
		ObjectId innerId = doc.AllocateObjectId();
		doc.Add(new DataPatternDefinition(innerId, "Inner")
			{ RowCount = 1, ChannelCount = 1 });
		DataPatternDefinition outer = new((ObjectId)999U, "Outer")
			{ RowCount = 3, ChannelCount = 1 };
		PatternCell first = outer.Grid.GetOrCreateCell(0, 0);
		first.Note = new StartPatternNote(innerId, mixdown: true);
		first.Effects.Add(new RetriggerPatternEffect(0xA3));
		PatternCell second = outer.Grid.GetOrCreateCell(1, 0);
		second.Note = new StartPatternNote(innerId);
		second.Effects.Add(new TrackerTempoPatternEffect(0x80));
		PatternCell third = outer.Grid.GetOrCreateCell(2, 0);
		third.Note = new StartPatternNote();
		third.Effects.Add(new SampleOffsetPatternEffect(0x17));
		Assert.That(FlattenedSourceEffectWarnings.Describe(
			doc, outer, 0, 0), Is.Null);
		Assert.That(FlattenedSourceEffectWarnings.Describe(
			doc, outer, 1, 0), Is.Null);
		Assert.That(FlattenedSourceEffectWarnings.Describe(
			doc, outer, 2, 0), Does.Contain("SampleOffset"));
	}

	[Test]
	public void LaterRowsWarnUntilCutIncludingAfterNoteOffRelease()
	{
		SongDocument doc = new();
		ObjectId childId = doc.AllocateObjectId();
		doc.Add(new DataPatternDefinition(childId, "Child")
			{ RowCount = 1, ChannelCount = 1 });
		DataPatternDefinition parent = new((ObjectId)999U, "Parent")
			{ RowCount = 6, ChannelCount = 2 };
		parent.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote(childId);
		PatternCell effectOnly = parent.Grid.GetOrCreateCell(1, 0);
		effectOnly.Effects.Add(new RetriggerPatternEffect(0xA3));
		effectOnly.Effects.Add(new TrackerVolumeSlidePatternEffect(0x21));
		parent.Grid.GetOrCreateCell(2, 0).Note = new PatternNoteOff();
		parent.Grid.GetOrCreateCell(3, 0).Effects.Add(
			new SampleOffsetPatternEffect(0x17));
		parent.Grid.GetOrCreateCell(4, 0).Note = new PatternNoteCut();
		parent.Grid.GetOrCreateCell(5, 0).Effects.Add(
			new RetriggerPatternEffect(0xA3));
		parent.Grid.GetOrCreateCell(1, 1).Effects.Add(
			new RetriggerPatternEffect(0xA3));

		string? current = FlattenedSourceEffectWarnings.Describe(
			doc, parent, 1, 0);
		Assert.That(current, Does.Contain("Retrigger"));
		Assert.That(current, Does.Not.Contain("TrackerVolumeSlide"));
		Assert.That(FlattenedSourceEffectWarnings.Describe(
			doc, parent, 3, 0), Does.Contain("SampleOffset"),
			"Note Off retains the releasing instigator's volume association.");
		Assert.That(FlattenedSourceEffectWarnings.Describe(
			doc, parent, 5, 0), Is.Null,
			"Cut clears the current logical source.");
		Assert.That(FlattenedSourceEffectWarnings.Describe(
			doc, parent, 1, 1), Is.Null,
			"Unrelated logical channels cannot inherit the flattened source.");
		Assert.That(effectOnly.Effects, Has.Count.EqualTo(2));
	}

	[Test]
	public void SourceColumnRecallDoesNotChangeActiveSourceUntilNextStart()
	{
		SongDocument doc = new();
		ObjectId firstId = doc.AllocateObjectId();
		ObjectId secondId = doc.AllocateObjectId();
		doc.Add(new DataPatternDefinition(firstId, "First")
			{ RowCount = 1, ChannelCount = 1 });
		doc.Add(new DataPatternDefinition(secondId, "Second")
			{ RowCount = 1, ChannelCount = 1 });
		DataPatternDefinition parent = new((ObjectId)999U, "Parent")
			{ RowCount = 4, ChannelCount = 1 };
		parent.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote(firstId);
		PatternCell selected = parent.Grid.GetOrCreateCell(1, 0);
		selected.SourceId = secondId;
		selected.Effects.Add(new ArpeggioPatternEffect(0x23));
		PatternCell mixed = parent.Grid.GetOrCreateCell(2, 0);
		mixed.Note = new StartPatternNote(mixdown: true);
		mixed.Effects.Add(new RetriggerPatternEffect(0xA3));
		parent.Grid.GetOrCreateCell(3, 0).Effects.Add(
			new SampleOffsetPatternEffect(0x17));

		Assert.That(FlattenedSourceEffectWarnings.Describe(
			doc, parent, 1, 0), Does.Contain("Arpeggio"),
			"Selecting a future Source must not displace the current instigator.");
		Assert.That(FlattenedSourceEffectWarnings.Describe(
			doc, parent, 2, 0), Is.Null,
			"A mixdown start is an ordinary single playback voice.");
		Assert.That(FlattenedSourceEffectWarnings.Describe(
			doc, parent, 3, 0), Is.Null,
			"Effects after mixdown replacement cannot target the old source.");
	}
	[Test]
	public void SequenceOrderInheritanceIsOccurrenceSpecificAndSurvivesNoteOffUntilCut()
	{
		SongDocument doc = new();
		ObjectId childId = doc.AllocateObjectId();
		doc.Add(new DataPatternDefinition(childId, "Flattened")
			{ RowCount = 1, ChannelCount = 1 });
		ObjectId firstId = doc.AllocateObjectId();
		DataPatternDefinition first = new(firstId, "Intro")
			{ RowCount = 2, ChannelCount = 1 };
		first.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote(childId);
		doc.Add(first);
		ObjectId sharedId = doc.AllocateObjectId();
		DataPatternDefinition shared = new(sharedId, "Shared")
			{ RowCount = 4, ChannelCount = 1 };
		shared.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new RetriggerPatternEffect(0xA3));
		shared.Grid.GetOrCreateCell(1, 0).Note = new PatternNoteOff();
		shared.Grid.GetOrCreateCell(2, 0).Effects.Add(
			new SampleOffsetPatternEffect(0x17));
		shared.Grid.GetOrCreateCell(3, 0).Note = new PatternNoteCut();
		doc.Add(shared);
		ObjectId seqId = doc.AllocateObjectId();
		DataSequenceDefinition seq = new(seqId, "Song");
		seq.Entries.Add(new SequenceEntry(firstId));
		seq.Entries.Add(new SequenceEntry(sharedId));
		seq.Entries.Add(new SequenceEntry(sharedId));
		doc.Add(seq);
		PatternEditorContext context =
			PatternEditorContext.ForSequence(doc, seq, 1);

		var warnings = FlattenedSourceEffectWarnings.Analyze(doc, context);
		Assert.That(warnings[(2, 0)].Message,
			Does.Contain("inherited from sequence order 0"));
		Assert.That(warnings[(2, 0)].IsConditional, Is.False);
		Assert.That(warnings[(3, 0)].Message, Does.Contain("Note Off"));
		Assert.That(warnings[(4, 0)].Message, Does.Contain("releasing"));
		Assert.That(warnings[(5, 0)].Message, Does.Contain("Note Cut"));
		Assert.That(warnings.ContainsKey((6, 0)), Is.False,
			"Second occurrence of the same Pattern follows a Cut.");
		Assert.That(warnings.ContainsKey((7, 0)), Is.False);
		Assert.That(shared.Grid[0, 0]!.Effects, Has.Count.EqualTo(1));
	}

	[Test]
	public void ScriptOrderProducesConditionalWarningsUntilExplicitSourceResolves()
	{
		SongDocument doc = new();
		ObjectId childId = doc.AllocateObjectId();
		doc.Add(new DataPatternDefinition(childId, "Flat")
			{ RowCount = 1, ChannelCount = 1 });
		ObjectId scriptId = doc.AllocateObjectId();
		doc.Add(new ScriptPatternDefinition(scriptId, "Dynamic")
			{ Source = "" });
		ObjectId patternId = doc.AllocateObjectId();
		DataPatternDefinition pattern = new(patternId, "After script")
			{ RowCount = 5, ChannelCount = 1 };
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new RetriggerPatternEffect(0xA3));
		pattern.Grid.GetOrCreateCell(1, 0).Note = new PatternNoteOff();
		PatternCell explicitlySelected = pattern.Grid.GetOrCreateCell(2, 0);
		explicitlySelected.SourceId = childId;
		explicitlySelected.Note = new StartPatternNote();
		explicitlySelected.Effects.Add(new ArpeggioPatternEffect(0x12));
		pattern.Grid.GetOrCreateCell(3, 0).Note = new PatternNoteCut();
		pattern.Grid.GetOrCreateCell(4, 0).Effects.Add(
			new SampleOffsetPatternEffect(0x17));
		doc.Add(pattern);
		ObjectId seqId = doc.AllocateObjectId();
		DataSequenceDefinition seq = new(seqId, "Song");
		seq.Entries.Add(new SequenceEntry(scriptId));
		seq.Entries.Add(new SequenceEntry(patternId));
		doc.Add(seq);
		var context = PatternEditorContext.ForSequence(doc, seq, 1);

		var warnings = FlattenedSourceEffectWarnings.Analyze(doc, context);
		Assert.That(warnings[(0, 0)].IsConditional, Is.True);
		Assert.That(warnings[(0, 0)].Message, Does.Contain("script"));
		Assert.That(warnings[(1, 0)].IsConditional, Is.True);
		Assert.That(warnings[(1, 0)].Message, Does.Contain("Note Off"));
		Assert.That(warnings[(2, 0)].IsConditional, Is.False);
		Assert.That(warnings[(2, 0)].Message, Does.Contain("Arpeggio"));
		Assert.That(warnings[(3, 0)].Message, Does.Contain("Note Cut"));
		Assert.That(warnings.ContainsKey((4, 0)), Is.False);
	}

	[Test]
	public void RememberedSourceSelectionFromEarlierOrderIsRecognizedAtLaterNoteStart()
	{
		SongDocument doc = new();
		ObjectId innerId = doc.AllocateObjectId();
		doc.Add(new DataPatternDefinition(innerId, "Flat")
			{ RowCount = 1, ChannelCount = 1 });
		ObjectId firstId = doc.AllocateObjectId();
		DataPatternDefinition first = new(firstId, "Select only")
			{ RowCount = 1, ChannelCount = 1 };
		first.Grid.GetOrCreateCell(0, 0).SourceId = innerId;
		doc.Add(first);
		ObjectId secondId = doc.AllocateObjectId();
		DataPatternDefinition second = new(secondId, "Start recalled")
			{ RowCount = 2, ChannelCount = 1 };
		PatternCell recalledStart = second.Grid.GetOrCreateCell(0, 0);
		recalledStart.Note = new StartPatternNote();
		recalledStart.Effects.Add(new RetriggerPatternEffect(0xA3));
		second.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new TrackerVolumeSlidePatternEffect(0x12));
		doc.Add(second);
		ObjectId seqId = doc.AllocateObjectId();
		DataSequenceDefinition seq = new(seqId, "Song");
		seq.Entries.Add(new SequenceEntry(firstId));
		seq.Entries.Add(new SequenceEntry(secondId));
		doc.Add(seq);
		var context = PatternEditorContext.ForSequence(doc, seq, 1);

		var warnings = FlattenedSourceEffectWarnings.Analyze(doc, context);
		Assert.That(warnings[(1, 0)].IsConditional, Is.False);
		Assert.That(warnings[(1, 0)].Message,
			Does.Contain("Source selection was inherited from sequence order 0"));
		Assert.That(warnings.ContainsKey((2, 0)), Is.False,
			"Native note-volume slides remain valid on flattened sources.");

		// A single cell edit changes later warnings without mutating audio
		// state or requiring static runtime-script execution.
		first.Grid.GetOrCreateCell(0, 0).SourceId = ObjectId.None;
		var updated = FlattenedSourceEffectWarnings.Analyze(doc, context);
		Assert.That(updated[(1, 0)].IsConditional, Is.True);
	}

	[Test]
	public void SkippedStartRowsAndFlowEffectsPreventFalseDefiniteWarnings()
	{
		SongDocument doc = new();
		ObjectId childId = doc.AllocateObjectId();
		doc.Add(new DataPatternDefinition(childId, "Flat")
			{ RowCount = 1, ChannelCount = 1 });
		ObjectId aId = doc.AllocateObjectId();
		DataPatternDefinition a = new(aId, "Flow")
			{ RowCount = 2, ChannelCount = 1 };
		PatternCell first = a.Grid.GetOrCreateCell(0, 0);
		first.Note = new StartPatternNote(childId);
		first.Effects.Add(new TrackerOrderJumpPatternEffect(0));
		doc.Add(a);
		ObjectId bId = doc.AllocateObjectId();
		DataPatternDefinition b = new(bId, "Partial")
			{ RowCount = 3, ChannelCount = 1 };
		b.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote(childId);
		b.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new RetriggerPatternEffect(0xA3));
		b.Grid.GetOrCreateCell(2, 0).Effects.Add(
			new ArpeggioPatternEffect(0x12));
		doc.Add(b);
		ObjectId seqId = doc.AllocateObjectId();
		DataSequenceDefinition seq = new(seqId, "Song");
		seq.Entries.Add(new SequenceEntry(aId));
		seq.Entries.Add(new SequenceEntry(bId, startRow: 1));
		doc.Add(seq);

		var context = PatternEditorContext.ForSequence(doc, seq, 1);
		var warnings = FlattenedSourceEffectWarnings.Analyze(doc, context);
		Assert.That(warnings[(2, 0)].IsConditional, Is.True,
			"Bxx makes linear progression unknowable statically.");
		Assert.That(warnings[(3, 0)].IsConditional, Is.True);
		Assert.That(warnings[(2, 0)].Message, Does.Not.Contain(
			"definitely ignored"));
	}


}
