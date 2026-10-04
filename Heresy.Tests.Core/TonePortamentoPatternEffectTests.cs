using System;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class TonePortamentoPatternEffectTests
{
	[Test]
	public void NoteWithGxxBecomesTargetInsteadOfOrdinaryStart()
	{
		ObjectId sourceId = (ObjectId)10U;
		DataPatternDefinition pattern = PatternWithTonePortamento(
			parameter: 0x05,
			pitchMultiplier: 2.0,
			sourceId: sourceId);
		NoteScheduleBuilder output = new();

		pattern.GenerateRawNotes(
			new SequencingContext(),
			output,
			out _);

		NoteSchedule schedule = output.Freeze();
		Assert.That(schedule.Count, Is.EqualTo(1));
		Assert.That(
			schedule[0].Commands,
			Has.None.TypeOf<StartNoteCommand>());

		ApplyTonePortamentoCommand command =
			(ApplyTonePortamentoCommand)schedule[0].Commands[0];

		Assert.That(command.Parameter, Is.EqualTo(0x05));
		Assert.That(command.TargetNote, Is.Not.Null);
		Assert.That(command.TargetNote!.SourceId, Is.EqualTo(sourceId));
		Assert.That(command.TargetNote.PitchMultiplier, Is.EqualTo(2.0));
	}

	[Test]
	public void GxxWithoutNoteHasNoNewTarget()
	{
		DataPatternDefinition pattern = PatternWithTonePortamento(
			parameter: 0x05,
			pitchMultiplier: null);
		NoteScheduleBuilder output = new();

		pattern.GenerateRawNotes(
			new SequencingContext(),
			output,
			out _);

		ApplyTonePortamentoCommand command =
			(ApplyTonePortamentoCommand)output.Freeze()[0].Commands[0];

		Assert.That(command.TargetNote, Is.Null);
	}

	[Test]
	public void ProcessorResolvesSpeedAndClearsMovementAtRowEnd()
	{
		DataPatternDefinition pattern = PatternWithTonePortamento(
			parameter: 0x05,
			pitchMultiplier: 2.0);
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out TimeSpan duration);

		NoteSchedule schedule = output.Freeze();

		Assert.That(duration, Is.EqualTo(TimeSpan.FromMilliseconds(120)));
		Assert.That(schedule.Count, Is.EqualTo(2));

		SetTonePortamentoCommand command =
			(SetTonePortamentoCommand)schedule[0].Commands[0];
		Assert.That(command.LinearUnitsPerTick, Is.EqualTo(20.0));
		Assert.That(command.TargetNote!.PitchMultiplier, Is.EqualTo(2.0));

		Assert.That(schedule[1].Offset.TimeOffset, Is.EqualTo(duration));
		Assert.That(
			schedule[1].Commands[0],
			Is.TypeOf<ClearTonePortamentoCommand>());
	}

	[Test]
	public void G00RecallsTonePortamentoSpeed()
	{
		SequencingContext context = new();

		PatternNoteProcessor.GenerateNotes(
			PatternWithTonePortamento(0x07, 2.0),
			context,
			new NoteScheduleBuilder(),
			out _);

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			PatternWithTonePortamento(0x00, pitchMultiplier: null),
			context,
			output,
			out _);

		SetTonePortamentoCommand recalled =
			(SetTonePortamentoCommand)output.Freeze()[0].Commands[0];

		Assert.That(recalled.LinearUnitsPerTick, Is.EqualTo(28.0));
		Assert.That(recalled.TargetNote, Is.Null);
	}

	[Test]
	public void TonePortamentoMemoryIsIndependentFromOrdinaryPitchSlideMemory()
	{
		SequencingContext context = new();

		PatternNoteProcessor.GenerateNotes(
			PatternWithTonePortamento(0x05, 2.0),
			context,
			new NoteScheduleBuilder(),
			out _);

		DataPatternDefinition pitchSlide = new((ObjectId)2U, "Pitch slide")
		{
			RowCount = 1,
			ChannelCount = 1,
		};
		pitchSlide.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerPitchSlideUpPatternEffect(0x03));

		PatternNoteProcessor.GenerateNotes(
			pitchSlide,
			context,
			new NoteScheduleBuilder(),
			out _);

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			PatternWithTonePortamento(0x00, pitchMultiplier: null),
			context,
			output,
			out _);

		SetTonePortamentoCommand recalled =
			(SetTonePortamentoCommand)output.Freeze()[0].Commands[0];

		Assert.That(recalled.LinearUnitsPerTick, Is.EqualTo(20.0));
	}

	[Test]
	public void StartRowDoesNotLetSkippedGxxSeedMemory()
	{
		DataPatternDefinition pattern = new((ObjectId)1U, "Pattern")
		{
			RowCount = 2,
			ChannelCount = 1,
		};

		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TonePortamentoPatternEffect(0x05));
		pattern.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new TonePortamentoPatternEffect(0x00));

		SequencingContext context = new();
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(
			pattern,
			context,
			output,
			startRow: 1,
			out _);

		Assert.That(output.Freeze().Count, Is.EqualTo(0));
		Assert.That(
			context.GetPhysicalChannelState(0).TryGetEffectParameter(
				EffectMemorySlot.TonePortamento,
				out _),
			Is.False);
	}

	[Test]
	public void FlattenedChildSharesMappedTonePortamentoMemory()
	{
		SequencingContext parent = new(physicalChannelBase: 3);

		PatternNoteProcessor.GenerateNotes(
			PatternWithTonePortamento(
				0x06,
				2.0,
				channel: 4),
			parent,
			new NoteScheduleBuilder(),
			out _);

		SequencingContext child =
			parent.FlattenedChild(physicalChannelOffset: 4);

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			PatternWithTonePortamento(
				0x00,
				pitchMultiplier: null),
			child,
			output,
			out _);

		SetTonePortamentoCommand recalled =
			(SetTonePortamentoCommand)output.Freeze()[0].Commands[0];

		Assert.That(recalled.LinearUnitsPerTick, Is.EqualTo(24.0));
	}

	private static DataPatternDefinition PatternWithTonePortamento(
		byte parameter,
		double? pitchMultiplier,
		ObjectId? sourceId = null,
		int channel = 0)
	{
		DataPatternDefinition pattern = new((ObjectId)1U, "Pattern")
		{
			RowCount = 1,
			ChannelCount = Math.Max(channel + 1, 1),
		};

		PatternCell cell = pattern.Grid.GetOrCreateCell(0, channel);
		if (pitchMultiplier.HasValue)
		{
			cell.Note = new StartPatternNote(
				sourceId ?? (ObjectId)10U,
				pitchMultiplier.Value);
		}

		cell.Effects.Add(new TonePortamentoPatternEffect(parameter));
		return pattern;
	}
}
