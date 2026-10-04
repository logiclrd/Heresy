using System;
using System.Linq;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;
using Heresy.Core.Timing;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class TonePortamentoVolumeSlidePatternEffectTests
{
	[Test]
	public void NoteWithLxxBecomesPortamentoTargetInsteadOfOrdinaryStart()
	{
		ObjectId sourceId = (ObjectId)10U;
		DataPatternDefinition pattern = PatternWithLxx(
			parameter: 0x04,
			pitchMultiplier: 2.0,
			sourceId: sourceId);
		NoteScheduleBuilder output = new();

		pattern.GenerateRawNotes(
			new SequencingContext(),
			output,
			out _);

		NoteSchedule schedule = output.Freeze();
		Assert.That(schedule, Has.Count.EqualTo(1));
		Assert.That(
			schedule[0].Commands,
			Has.None.TypeOf<StartNoteCommand>());

		ApplyTonePortamentoVolumeSlideCommand command =
			(ApplyTonePortamentoVolumeSlideCommand)
				schedule[0].Commands[0];

		Assert.That(command.Parameter, Is.EqualTo(0x04));
		Assert.That(command.TargetNote, Is.Not.Null);
		Assert.That(command.TargetNote!.SourceId, Is.EqualTo(sourceId));
		Assert.That(command.TargetNote.PitchMultiplier, Is.EqualTo(2.0));
	}

	[Test]
	public void LxxWithoutNoteHasNoNewTarget()
	{
		NoteScheduleBuilder output = new();

		PatternWithLxx(
			parameter: 0x04,
			pitchMultiplier: null)
			.GenerateRawNotes(
				new SequencingContext(),
				output,
				out _);

		ApplyTonePortamentoVolumeSlideCommand command =
			(ApplyTonePortamentoVolumeSlideCommand)
				output.Freeze()[0].Commands[0];

		Assert.That(command.TargetNote, Is.Null);
	}

	[Test]
	public void LxxUsesRememberedGxxSpeedAndThenAppliesVolumeSlide()
	{
		SequencingContext context = new();

		Generate(
			PatternWithGxx(0x05, 2.0),
			context);

		NoteSchedule schedule = Generate(
			PatternWithLxx(0x04, 4.0),
			context);

		Assert.That(
			schedule[0].Commands,
			Is.EqualTo(
				new NoteCommand[]
				{
					new SetTonePortamentoCommand(
						20.0,
						new StartNoteCommand(
							(ObjectId)10U,
							4.0)),
					new SetNoteVolumeSlideCommand(-4.0),
				}));
	}

	[Test]
	public void LxxDoesNotOverwriteTonePortamentoSpeedMemory()
	{
		SequencingContext context = new();

		Generate(
			PatternWithGxx(0x05, 2.0),
			context);

		Generate(
			PatternWithLxx(0x40, pitchMultiplier: null),
			context);

		NoteSchedule recalled = Generate(
			PatternWithGxx(0x00, pitchMultiplier: null),
			context);

		Assert.That(
			recalled.SelectMany(e => e.Commands)
				.OfType<SetTonePortamentoCommand>()
				.Single()
				.LinearUnitsPerTick,
			Is.EqualTo(20.0));
	}

	[Test]
	public void LxxWithNoGxxMemoryCanStillEstablishTargetAtZeroSpeed()
	{
		SequencingContext context = new();

		NoteSchedule schedule = Generate(
			PatternWithLxx(0x04, 2.0),
			context);

		SetTonePortamentoCommand tone =
			schedule.SelectMany(e => e.Commands)
				.OfType<SetTonePortamentoCommand>()
				.Single();

		Assert.That(tone.LinearUnitsPerTick, Is.EqualTo(0.0));
		Assert.That(tone.TargetNote, Is.Not.Null);
		Assert.That(tone.TargetNote!.PitchMultiplier, Is.EqualTo(2.0));
	}

	[Test]
	public void L00SharesDxxKxxLxxVolumeSlideMemory()
	{
		SequencingContext context = new();

		Generate(
			PatternWithDxx(0x40),
			context);

		NoteSchedule schedule = Generate(
			PatternWithLxx(0x00, pitchMultiplier: null),
			context);

		Assert.That(
			schedule.SelectMany(e => e.Commands)
				.OfType<SetNoteVolumeSlideCommand>()
				.Single()
				.TrackerUnitsPerTick,
			Is.EqualTo(4.0));
	}

	[Test]
	public void LxxUpdatesSharedVolumeSlideMemoryForK00()
	{
		SequencingContext context = new();

		Generate(
			PatternWithLxx(0x04, pitchMultiplier: null),
			context);

		NoteSchedule schedule = Generate(
			PatternWithKxx(0x00),
			context);

		Assert.That(
			schedule.SelectMany(e => e.Commands)
				.OfType<SetNoteVolumeSlideCommand>()
				.Single()
				.TrackerUnitsPerTick,
			Is.EqualTo(-4.0));
	}

	[Test]
	public void FineLxxVolumeSlideIsImmediateAndTonePortamentoStillRuns()
	{
		SequencingContext context = new();

		Generate(
			PatternWithGxx(0x05, 2.0),
			context);

		NoteSchedule schedule = Generate(
			PatternWithLxx(0x2F, 4.0),
			context);

		Assert.That(
			schedule[0].Commands,
			Is.EqualTo(
				new NoteCommand[]
				{
					new SetTonePortamentoCommand(
						20.0,
						new StartNoteCommand(
							(ObjectId)10U,
							4.0)),
					new AdjustNoteVolumeCommand(2.0),
				}));
		Assert.That(
			schedule.SelectMany(e => e.Commands)
				.OfType<ClearTonePortamentoCommand>()
				.Count(),
			Is.EqualTo(1));
		Assert.That(
			schedule.SelectMany(e => e.Commands)
				.OfType<ClearNoteVolumeSlideCommand>(),
			Is.Empty);
	}

	[Test]
	public void FinePatternDelayExtendsBothContinuousPartsOfLxx()
	{
		SequencingContext context = new();
		Generate(
			PatternWithGxx(0x05, 2.0),
			context);

		DataPatternDefinition pattern = PatternWithLxx(
			0x40,
			4.0);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerFinePatternDelayPatternEffect(2));

		NoteSchedule schedule = Generate(pattern, context);

		SetTonePortamentoCommand tone =
			schedule.SelectMany(e => e.Commands)
				.OfType<SetTonePortamentoCommand>()
				.Single();
		SetNoteVolumeSlideCommand volume =
			schedule.SelectMany(e => e.Commands)
				.OfType<SetNoteVolumeSlideCommand>()
				.Single();

		Assert.That(
			tone.TicksPerRow,
			Is.EqualTo(SequencingConstants.DefaultSpeed + 2));
		Assert.That(
			volume.TicksPerRow,
			Is.EqualTo(SequencingConstants.DefaultSpeed + 2));
	}

	[Test]
	public void PatternDelayRepeatsLxxWithoutReestablishingTarget()
	{
		SequencingContext context = new();
		Generate(
			PatternWithGxx(0x05, 2.0),
			context);

		DataPatternDefinition pattern = PatternWithLxx(
			0x40,
			4.0);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerPatternDelayPatternEffect(1));

		NoteSchedule schedule = Generate(pattern, context);

		SetTonePortamentoCommand[] tones =
			schedule.SelectMany(e => e.Commands)
				.OfType<SetTonePortamentoCommand>()
				.ToArray();

		Assert.That(tones, Has.Length.EqualTo(2));
		Assert.That(tones[0].TargetNote, Is.Not.Null);
		Assert.That(tones[1].TargetNote, Is.Null);
	}

	[Test]
	public void StartRowDoesNotLetSkippedLxxSeedSharedVolumeMemory()
	{
		DataPatternDefinition pattern = new((ObjectId)1U, "Pattern")
		{
			RowCount = 2,
			ChannelCount = 1,
		};

		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TonePortamentoVolumeSlidePatternEffect(0x40));
		pattern.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new TrackerVolumeSlidePatternEffect(0x00));

		SequencingContext context = new();
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(
			pattern,
			context,
			output,
			startRow: 1,
			out _);

		Assert.That(
			output.Freeze()
				.SelectMany(e => e.Commands)
				.Any(c => c is SetNoteVolumeSlideCommand
					or AdjustNoteVolumeCommand),
			Is.False);
	}

	private static NoteSchedule Generate(
		DataPatternDefinition pattern,
		SequencingContext context)
	{
		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			context,
			output,
			out _);
		return output.Freeze();
	}

	private static DataPatternDefinition PatternWithLxx(
		byte parameter,
		double? pitchMultiplier,
		ObjectId? sourceId = null)
	{
		DataPatternDefinition pattern = Pattern();
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);

		if (pitchMultiplier.HasValue)
		{
			cell.Note = new StartPatternNote(
				sourceId ?? (ObjectId)10U,
				pitchMultiplier.Value);
		}

		cell.Effects.Add(
			new TonePortamentoVolumeSlidePatternEffect(parameter));
		return pattern;
	}

	private static DataPatternDefinition PatternWithGxx(
		byte parameter,
		double? pitchMultiplier)
	{
		DataPatternDefinition pattern = Pattern();
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		if (pitchMultiplier.HasValue)
		{
			cell.Note = new StartPatternNote(
				(ObjectId)10U,
				pitchMultiplier.Value);
		}
		cell.Effects.Add(
			new TonePortamentoPatternEffect(parameter));
		return pattern;
	}

	private static DataPatternDefinition PatternWithDxx(byte parameter)
	{
		DataPatternDefinition pattern = Pattern();
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerVolumeSlidePatternEffect(parameter));
		return pattern;
	}

	private static DataPatternDefinition PatternWithKxx(byte parameter)
	{
		DataPatternDefinition pattern = Pattern();
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new VibratoVolumeSlidePatternEffect(parameter));
		return pattern;
	}

	private static DataPatternDefinition Pattern()
		=> new((ObjectId)1U, "Pattern")
		{
			RowCount = 1,
			ChannelCount = 1,
		};
}
