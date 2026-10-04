using System;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class FineVibratoPatternEffectTests
{
	[Test]
	public void GridFineVibratoTranslatesToRawMemoryAwareCommand()
	{
		DataPatternDefinition pattern = PatternWithFineVibrato(1, 0, 0x53);
		NoteScheduleBuilder output = new();

		pattern.GenerateRawNotes(
			new SequencingContext(),
			output,
			out _);

		Assert.That(
			output.Freeze()[0].Commands[0],
			Is.EqualTo(new ApplyFineVibratoCommand(0x53)));
	}

	[Test]
	public void ProcessorResolvesFineVibratoToQuarterDepthScale()
	{
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(
			PatternWithFineVibrato(1, 0, 0x53),
			new SequencingContext(),
			output,
			out _);

		Assert.That(
			output.Freeze()[0].Commands[0],
			Is.EqualTo(
				new SetVibratoCommand(
					5,
					3,
					TrackerWaveform.Sine,
					DepthScale: 0.25)));
	}

	[Test]
	public void HxxAndUxxShareNibbleMemory()
	{
		SequencingContext context = new();

		PatternNoteProcessor.GenerateNotes(
			PatternWithNormalVibrato(1, 0, 0x53),
			context,
			new NoteScheduleBuilder(),
			out _);

		NoteScheduleBuilder fineOutput = new();
		PatternNoteProcessor.GenerateNotes(
			PatternWithFineVibrato(1, 0, 0x70),
			context,
			fineOutput,
			out _);

		SetVibratoCommand fine =
			(SetVibratoCommand)fineOutput.Freeze()[0].Commands[0];

		Assert.That(
			fine,
			Is.EqualTo(
				new SetVibratoCommand(
					7,
					3,
					TrackerWaveform.Sine,
					DepthScale: 0.25)));

		PatternNoteProcessor.GenerateNotes(
			PatternWithFineVibrato(1, 0, 0x24),
			context,
			new NoteScheduleBuilder(),
			out _);

		NoteScheduleBuilder normalOutput = new();
		PatternNoteProcessor.GenerateNotes(
			PatternWithNormalVibrato(1, 0, 0x60),
			context,
			normalOutput,
			out _);

		SetVibratoCommand normal =
			(SetVibratoCommand)normalOutput.Freeze()[0].Commands[0];

		Assert.That(
			normal,
			Is.EqualTo(new SetVibratoCommand(6, 4)));
	}

	[Test]
	public void FineVibratoUsesPersistentVibratoWaveform()
	{
		DataPatternDefinition pattern = new((ObjectId)1U, "Pattern")
		{
			RowCount = 1,
			ChannelCount = 1,
		};

		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Effects.Add(
			new TrackerVibratoWaveformPatternEffect(2));
		cell.Effects.Add(
			new FineVibratoPatternEffect(0x53));

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out _);

		SetVibratoCommand command =
			(SetVibratoCommand)output.Freeze()[0].Commands[0];

		Assert.That(command.Waveform, Is.EqualTo(TrackerWaveform.Square));
		Assert.That(command.DepthScale, Is.EqualTo(0.25));
	}

	[Test]
	public void StartRowDoesNotLetSkippedFineVibratoChangeSharedMemory()
	{
		DataPatternDefinition pattern = new((ObjectId)1U, "Pattern")
		{
			RowCount = 2,
			ChannelCount = 1,
		};

		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new FineVibratoPatternEffect(0x53));
		pattern.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new VibratoPatternEffect(0x00));

		SequencingContext context = new();
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(
			pattern,
			context,
			output,
			startRow: 1,
			out _);

		SetVibratoCommand command =
			(SetVibratoCommand)output.Freeze()[0].Commands[0];

		Assert.That(command, Is.EqualTo(new SetVibratoCommand(0, 0)));
		Assert.That(
			context.GetPhysicalChannelState(0)
				.TryGetEffectParameter(
					EffectMemorySlot.Vibrato,
					out _),
			Is.False);
	}

	private static DataPatternDefinition PatternWithFineVibrato(
		int rowCount,
		int channel,
		byte parameter)
	{
		DataPatternDefinition pattern = new((ObjectId)1U, "Pattern")
		{
			RowCount = rowCount,
			ChannelCount = Math.Max(channel + 1, 1),
		};
		pattern.Grid.GetOrCreateCell(0, channel).Effects.Add(
			new FineVibratoPatternEffect(parameter));
		return pattern;
	}

	private static DataPatternDefinition PatternWithNormalVibrato(
		int rowCount,
		int channel,
		byte parameter)
	{
		DataPatternDefinition pattern = new((ObjectId)1U, "Pattern")
		{
			RowCount = rowCount,
			ChannelCount = Math.Max(channel + 1, 1),
		};
		pattern.Grid.GetOrCreateCell(0, channel).Effects.Add(
			new VibratoPatternEffect(parameter));
		return pattern;
	}
}
