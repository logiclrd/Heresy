using Heresy.Core.Objects;
using Heresy.Core.Patterns;
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
}
