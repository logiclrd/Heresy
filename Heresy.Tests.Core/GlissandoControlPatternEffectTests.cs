using System;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class GlissandoControlPatternEffectTests
{
	[Test]
	public void DataGridTranslatesS1xToRawGlissandoCommand()
	{
		DataPatternDefinition pattern = PatternWithEffect(
			new TrackerGlissandoControlPatternEffect(1));
		NoteScheduleBuilder output = new();

		pattern.GenerateRawNotes(
			new SequencingContext(),
			output,
			out _);

		Assert.That(
			output.Freeze()[0].Commands[0],
			Is.EqualTo(new ApplyTrackerGlissandoControlCommand(1)));
	}

	[Test]
	public void S11PersistsIntoLaterTonePortamento()
	{
		SequencingContext context = new();

		PatternNoteProcessor.GenerateNotes(
			PatternWithEffect(
				new TrackerGlissandoControlPatternEffect(1)),
			context,
			new NoteScheduleBuilder(),
			out _);

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			PatternWithEffect(
				new TonePortamentoPatternEffect(0x04),
				pitchMultiplier: 2.0),
			context,
			output,
			out _);

		SetTonePortamentoCommand command =
			(SetTonePortamentoCommand)output.Freeze()[0].Commands[0];

		Assert.That(command.Glissando, Is.True);
	}

	[Test]
	public void AnyNonZeroS1xEnablesGlissando()
	{
		SequencingContext context = new();

		PatternNoteProcessor.GenerateNotes(
			PatternWithEffect(
				new TrackerGlissandoControlPatternEffect(0x0F)),
			context,
			new NoteScheduleBuilder(),
			out _);

		Assert.That(
			context.GetPhysicalChannelState(0).GlissandoEnabled,
			Is.True);
	}

	[Test]
	public void S10DisablesPreviouslyEnabledGlissando()
	{
		SequencingContext context = new();

		PatternNoteProcessor.GenerateNotes(
			PatternWithEffect(
				new TrackerGlissandoControlPatternEffect(1)),
			context,
			new NoteScheduleBuilder(),
			out _);

		PatternNoteProcessor.GenerateNotes(
			PatternWithEffect(
				new TrackerGlissandoControlPatternEffect(0)),
			context,
			new NoteScheduleBuilder(),
			out _);

		Assert.That(
			context.GetPhysicalChannelState(0).GlissandoEnabled,
			Is.False);
	}

	[Test]
	public void SameCellSelectionAffectsLaterTonePortamentoCommand()
	{
		DataPatternDefinition pattern = new((ObjectId)1U, "Pattern")
		{
			RowCount = 1,
			ChannelCount = 1,
		};
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Note = new StartPatternNote(
			(ObjectId)10U,
			pitchMultiplier: 2.0);
		cell.Effects.Add(new TrackerGlissandoControlPatternEffect(1));
		cell.Effects.Add(new TonePortamentoPatternEffect(0x04));

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out _);

		SetTonePortamentoCommand command =
			(SetTonePortamentoCommand)output.Freeze()[0].Commands[0];

		Assert.That(command.Glissando, Is.True);
	}

	[Test]
	public void StartRowDoesNotExecuteSkippedGlissandoControl()
	{
		DataPatternDefinition pattern = new((ObjectId)1U, "Pattern")
		{
			RowCount = 2,
			ChannelCount = 1,
		};
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerGlissandoControlPatternEffect(1));

		PatternCell row1 = pattern.Grid.GetOrCreateCell(1, 0);
		row1.Note = new StartPatternNote(
			(ObjectId)10U,
			pitchMultiplier: 2.0);
		row1.Effects.Add(new TonePortamentoPatternEffect(0x04));

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			startRow: 1,
			out _);

		SetTonePortamentoCommand command =
			(SetTonePortamentoCommand)output.Freeze()[0].Commands[0];

		Assert.That(command.Glissando, Is.False);
	}

	[Test]
	public void FlattenedChildSharesMappedGlissandoState()
	{
		SequencingContext parent =
			new(physicalChannelBase: 3);

		PatternNoteProcessor.GenerateNotes(
			PatternWithEffect(
				new TrackerGlissandoControlPatternEffect(1),
				channel: 4),
			parent,
			new NoteScheduleBuilder(),
			out _);

		SequencingContext child =
			parent.FlattenedChild(physicalChannelOffset: 4);

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			PatternWithEffect(
				new TonePortamentoPatternEffect(0x04),
				pitchMultiplier: 2.0),
			child,
			output,
			out _);

		Assert.That(
			((SetTonePortamentoCommand)output.Freeze()[0].Commands[0])
				.Glissando,
			Is.True);
	}

	[Test]
	public void MixdownChildHasIndependentGlissandoState()
	{
		SequencingContext parent = new();

		PatternNoteProcessor.GenerateNotes(
			PatternWithEffect(
				new TrackerGlissandoControlPatternEffect(1)),
			parent,
			new NoteScheduleBuilder(),
			out _);

		SequencingContext mixdown = parent.MixdownChild();

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			PatternWithEffect(
				new TonePortamentoPatternEffect(0x04),
				pitchMultiplier: 2.0),
			mixdown,
			output,
			out _);

		Assert.That(
			((SetTonePortamentoCommand)output.Freeze()[0].Commands[0])
				.Glissando,
			Is.False);
	}

	[Test]
	public void GlissandoNibbleMustFitTrackerRange()
	{
		Assert.Throws<ArgumentOutOfRangeException>(
			() => new TrackerGlissandoControlPatternEffect(16));
	}

	private static DataPatternDefinition PatternWithEffect(
		PatternEffect effect,
		double? pitchMultiplier = null,
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
				(ObjectId)10U,
				pitchMultiplier.Value);
		}

		cell.Effects.Add(effect);
		return pattern;
	}
}
