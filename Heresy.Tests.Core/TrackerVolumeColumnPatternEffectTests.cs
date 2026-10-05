using System;
using System.Linq;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class TrackerVolumeColumnPatternEffectTests
{
	[Test]
	public void ParameterMustFitVolumeColumnDigit()
	{
		Assert.That(
			() => new TrackerVolumeColumnPatternEffect(
				TrackerVolumeColumnEffectKind.VolumeSlideUp,
				10),
			Throws.TypeOf<ArgumentOutOfRangeException>());
	}

	[Test]
	public void DataGridPreservesVolumeColumnOperation()
	{
		DataPatternDefinition pattern = PatternWith(
			new TrackerVolumeColumnPatternEffect(
				TrackerVolumeColumnEffectKind.FineVolumeUp,
				3));

		NoteScheduleBuilder output = new();
		pattern.GenerateRawNotes(
			new SequencingContext(),
			output,
			out _);

		Assert.That(
			output.Freeze()[0].Commands.Single(),
			Is.EqualTo(
				new ApplyTrackerVolumeColumnCommand(
					TrackerVolumeColumnEffectKind.FineVolumeUp,
					3)));
	}

	[TestCase(TrackerVolumeColumnEffectKind.FineVolumeUp, 3, 3.0)]
	[TestCase(TrackerVolumeColumnEffectKind.FineVolumeDown, 4, -4.0)]
	public void FineVolumeColumnSlidesAreImmediateCurrentVoiceAdjustments(
		TrackerVolumeColumnEffectKind kind,
		byte parameter,
		double expected)
	{
		Assert.That(
			FirstResolved(
				new TrackerVolumeColumnPatternEffect(
					kind,
					parameter)),
			Is.EqualTo(
				new AdjustCurrentNoteVolumeCommand(expected)));
	}

	[TestCase(TrackerVolumeColumnEffectKind.VolumeSlideUp, 3, 3.0)]
	[TestCase(TrackerVolumeColumnEffectKind.VolumeSlideDown, 4, -4.0)]
	public void OrdinaryVolumeColumnSlidesAreContinuous(
		TrackerVolumeColumnEffectKind kind,
		byte parameter,
		double expected)
	{
		NoteSchedule schedule = Resolved(
			new TrackerVolumeColumnPatternEffect(
				kind,
				parameter));

		Assert.That(
			schedule[0].Commands[0],
			Is.EqualTo(new SetNoteVolumeSlideCommand(expected)));
		Assert.That(
			schedule[^1].Commands,
			Does.Contain(new ClearNoteVolumeSlideCommand()));
	}

	[Test]
	public void VolumeColumnABCDShareMemoryButNotEffectColumnDMemory()
	{
		SequencingContext context = new();

		ResolveInto(
			context,
			new TrackerVolumeColumnPatternEffect(
				TrackerVolumeColumnEffectKind.FineVolumeUp,
				3));

		NoteSchedule recalled = ResolveInto(
			context,
			new TrackerVolumeColumnPatternEffect(
				TrackerVolumeColumnEffectKind.VolumeSlideDown,
				0));
		Assert.That(
			recalled[0].Commands[0],
			Is.EqualTo(new SetNoteVolumeSlideCommand(-3.0)));

		NoteScheduleBuilder effectRecall = new();
		PatternNoteProcessor.GenerateNotes(
			PatternWith(new TrackerVolumeSlidePatternEffect(0x00)),
			context,
			effectRecall,
			out _);
		Assert.That(effectRecall.Freeze(), Is.Empty);

		ResolveInto(
			context,
			new TrackerVolumeSlidePatternEffect(0x40));

		NoteSchedule stillThree = ResolveInto(
			context,
			new TrackerVolumeColumnPatternEffect(
				TrackerVolumeColumnEffectKind.VolumeSlideUp,
				0));
		Assert.That(
			stillThree[0].Commands[0],
			Is.EqualTo(new SetNoteVolumeSlideCommand(3.0)));
	}

	[TestCase(TrackerVolumeColumnEffectKind.PitchSlideDown, 3, -48.0)]
	[TestCase(TrackerVolumeColumnEffectKind.PitchSlideUp, 2, 32.0)]
	public void VolumeColumnPitchSlidesUseFourTimesTheEffectParameter(
		TrackerVolumeColumnEffectKind kind,
		byte parameter,
		double expected)
	{
		Assert.That(
			FirstResolved(
				new TrackerVolumeColumnPatternEffect(
					kind,
					parameter)),
			Is.EqualTo(new SetPitchSlideCommand(expected)));
	}

	[Test]
	public void VolumeColumnPitchSlidesShareRawMemoryWithEffectColumnEF()
	{
		SequencingContext context = new();

		ResolveInto(
			context,
			new TrackerPitchSlideUpPatternEffect(0x05));

		NoteSchedule recalled = ResolveInto(
			context,
			new TrackerVolumeColumnPatternEffect(
				TrackerVolumeColumnEffectKind.PitchSlideDown,
				0));
		Assert.That(
			recalled[0].Commands[0],
			Is.EqualTo(new SetPitchSlideCommand(-20.0)));

		ResolveInto(
			context,
			new TrackerVolumeColumnPatternEffect(
				TrackerVolumeColumnEffectKind.PitchSlideDown,
				4));

		NoteScheduleBuilder effectRecall = new();
		PatternNoteProcessor.GenerateNotes(
			PatternWith(new TrackerPitchSlideUpPatternEffect(0x00)),
			context,
			effectRecall,
			out _);

		Assert.That(
			effectRecall.Freeze()[0].Commands[0],
			Is.EqualTo(new SetPitchSlideCommand(64.0)));
	}

	[Test]
	public void VolumeColumnPitchSlideZeroTreatsFineEffectMemoryAsRawSpeed()
	{
		SequencingContext context = new();

		ResolveInto(
			context,
			new TrackerPitchSlideUpPatternEffect(0xE2));

		NoteSchedule recalled = ResolveInto(
			context,
			new TrackerVolumeColumnPatternEffect(
				TrackerVolumeColumnEffectKind.PitchSlideDown,
				0));

		Assert.That(
			recalled[0].Commands[0],
			Is.EqualTo(new SetPitchSlideCommand(-904.0)));
	}

	[TestCase(1, 4.0)]
	[TestCase(2, 16.0)]
	[TestCase(3, 32.0)]
	[TestCase(4, 64.0)]
	[TestCase(5, 128.0)]
	[TestCase(6, 256.0)]
	[TestCase(7, 384.0)]
	[TestCase(8, 512.0)]
	[TestCase(9, 1020.0)]
	public void VolumeColumnTonePortamentoUsesItCoarseSpeedTable(
		byte parameter,
		double expectedLinearUnits)
	{
		Assert.That(
			FirstResolved(
				new TrackerVolumeColumnPatternEffect(
					TrackerVolumeColumnEffectKind.TonePortamento,
					parameter)),
			Is.EqualTo(
				new SetTonePortamentoCommand(
					expectedLinearUnits)));
	}

	[Test]
	public void VolumeColumnTonePortamentoTurnsSameCellNoteIntoTarget()
	{
		ObjectId sourceId = (ObjectId)10U;
		DataPatternDefinition pattern = PatternWith(
			new TrackerVolumeColumnPatternEffect(
				TrackerVolumeColumnEffectKind.TonePortamento,
				3));
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Note = new StartPatternNote(
			sourceId,
			pitchMultiplier: 2.0);

		NoteScheduleBuilder raw = new();
		pattern.GenerateRawNotes(
			new SequencingContext(),
			raw,
			out _);

		NoteEvent rawEvent = raw.Freeze().Single();
		Assert.That(
			rawEvent.Commands,
			Has.None.TypeOf<StartNoteCommand>());

		ApplyTrackerVolumeColumnCommand command =
			rawEvent.Commands
				.OfType<ApplyTrackerVolumeColumnCommand>()
				.Single();
		Assert.That(command.TargetNote, Is.Not.Null);
		Assert.That(command.TargetNote!.SourceId, Is.EqualTo(sourceId));
		Assert.That(
			command.TargetNote.PitchMultiplier,
			Is.EqualTo(2.0));
	}

	[Test]
	public void VolumeColumnTonePortamentoSharesGxxMemory()
	{
		SequencingContext context = new();

		ResolveInto(
			context,
			new TonePortamentoPatternEffect(0x06));

		NoteSchedule recalled = ResolveInto(
			context,
			new TrackerVolumeColumnPatternEffect(
				TrackerVolumeColumnEffectKind.TonePortamento,
				0));

		Assert.That(
			recalled[0].Commands[0],
			Is.EqualTo(new SetTonePortamentoCommand(24.0)));

		ResolveInto(
			context,
			new TrackerVolumeColumnPatternEffect(
				TrackerVolumeColumnEffectKind.TonePortamento,
				4));

		NoteScheduleBuilder gRecall = new();
		PatternNoteProcessor.GenerateNotes(
			PatternWith(new TonePortamentoPatternEffect(0x00)),
			context,
			gRecall,
			out _);

		Assert.That(
			gRecall.Freeze()[0].Commands[0],
			Is.EqualTo(new SetTonePortamentoCommand(64.0)));
	}

	[Test]
	public void VolumeColumnVibratoChangesDepthWithoutChangingSpeed()
	{
		SequencingContext context = new();

		ResolveInto(
			context,
			new VibratoPatternEffect(0x53));

		NoteSchedule resolved = ResolveInto(
			context,
			new TrackerVolumeColumnPatternEffect(
				TrackerVolumeColumnEffectKind.Vibrato,
				7));

		Assert.That(
			resolved[0].Commands[0],
			Is.EqualTo(new SetVibratoCommand(5, 7)));
	}

	[Test]
	public void VolumeColumnVibratoZeroPreservesFineVibratoDepthScale()
	{
		SequencingContext context = new();

		ResolveInto(
			context,
			new FineVibratoPatternEffect(0x54));

		NoteSchedule resolved = ResolveInto(
			context,
			new TrackerVolumeColumnPatternEffect(
				TrackerVolumeColumnEffectKind.Vibrato,
				0));

		Assert.That(
			resolved[0].Commands[0],
			Is.EqualTo(
				new SetVibratoCommand(
					5,
					4,
					DepthScale: 0.25)));
	}

	[Test]
	public void NonzeroVolumeColumnVibratoDepthRestoresNormalDepthScale()
	{
		SequencingContext context = new();

		ResolveInto(
			context,
			new FineVibratoPatternEffect(0x54));

		NoteSchedule resolved = ResolveInto(
			context,
			new TrackerVolumeColumnPatternEffect(
				TrackerVolumeColumnEffectKind.Vibrato,
				7));

		Assert.That(
			resolved[0].Commands[0],
			Is.EqualTo(new SetVibratoCommand(5, 7)));
	}

	[Test]
	public void StartRowDoesNotSeedVolumeColumnMemory()
	{
		DataPatternDefinition pattern = new((ObjectId)1U, "Pattern")
		{
			RowCount = 2,
			ChannelCount = 1,
		};
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerVolumeColumnPatternEffect(
				TrackerVolumeColumnEffectKind.VolumeSlideUp,
				3));
		pattern.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new TrackerVolumeColumnPatternEffect(
				TrackerVolumeColumnEffectKind.VolumeSlideDown,
				0));

		SequencingContext context = new();
		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			context,
			output,
			startRow: 1,
			out _);

		Assert.That(output.Freeze(), Is.Empty);
		Assert.That(
			context.GetPhysicalChannelState(0)
				.TryGetEffectParameter(
					EffectMemorySlot.VolumeColumnSlide,
					out _),
			Is.False);
	}

	private static NoteCommand FirstResolved(PatternEffect effect)
		=> Resolved(effect)[0].Commands[0];

	private static NoteSchedule Resolved(PatternEffect effect)
		=> ResolveInto(new SequencingContext(), effect);

	private static NoteSchedule ResolveInto(
		SequencingContext context,
		PatternEffect effect)
	{
		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			PatternWith(effect),
			context,
			output,
			out _);
		return output.Freeze();
	}

	private static DataPatternDefinition PatternWith(
		PatternEffect effect)
	{
		DataPatternDefinition pattern = new((ObjectId)1U, "Pattern")
		{
			RowCount = 1,
			ChannelCount = 1,
		};
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(effect);
		return pattern;
	}
}
