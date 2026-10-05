using System;
using System.Linq;

using Heresy.Core.Envelopes;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class TrackerEnvelopeControlPatternEffectTests
{
	[TestCase(TrackerEnvelopeControlTarget.Volume, false)]
	[TestCase(TrackerEnvelopeControlTarget.Volume, true)]
	[TestCase(TrackerEnvelopeControlTarget.Panning, false)]
	[TestCase(TrackerEnvelopeControlTarget.Panning, true)]
	[TestCase(TrackerEnvelopeControlTarget.PitchOrFilter, false)]
	[TestCase(TrackerEnvelopeControlTarget.PitchOrFilter, true)]
	public void DataGridPreservesTrackerEnvelopeControl(
		TrackerEnvelopeControlTarget target,
		bool enabled)
	{
		DataPatternDefinition pattern = Pattern(1, 1);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerEnvelopeControlPatternEffect(
				target,
				enabled));

		NoteScheduleBuilder output = new();
		pattern.GenerateRawNotes(
			new SequencingContext(),
			output,
			out _);

		Assert.That(
			output.Freeze()
				.SelectMany(noteEvent => noteEvent.Commands)
				.OfType<ApplyTrackerEnvelopeControlCommand>()
				.Single(),
			Is.EqualTo(
				new ApplyTrackerEnvelopeControlCommand(
					target,
					enabled)));
	}

	[TestCase(TrackerEnvelopeControlTarget.Volume, EnvelopeTarget.Volume)]
	[TestCase(TrackerEnvelopeControlTarget.Panning, EnvelopeTarget.Panning)]
	public void S7EnvelopeControlResolvesToSemanticEnvelopeCommand(
		TrackerEnvelopeControlTarget trackerTarget,
		EnvelopeTarget semanticTarget)
	{
		DataPatternDefinition pattern = Pattern(1, 1);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerEnvelopeControlPatternEffect(
				trackerTarget,
				enabled: false));

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out _);

		Assert.That(
			output.Freeze()
				.SelectMany(noteEvent => noteEvent.Commands)
				.OfType<SetEnvelopeEnabledCommand>()
				.Single(),
			Is.EqualTo(
				new SetEnvelopeEnabledCommand(
					semanticTarget,
					false)));
	}

	[Test]
	public void S7BPitchFilterDisableControlsBothHeresyEnvelopeSlots()
	{
		DataPatternDefinition pattern = Pattern(1, 1);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerEnvelopeControlPatternEffect(
				TrackerEnvelopeControlTarget.PitchOrFilter,
				enabled: false));

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out _);

		SetEnvelopeEnabledCommand[] commands =
			output.Freeze()
				.SelectMany(noteEvent => noteEvent.Commands)
				.OfType<SetEnvelopeEnabledCommand>()
				.ToArray();

		Assert.That(
			commands,
			Is.EqualTo(
				new[]
				{
					new SetEnvelopeEnabledCommand(
						EnvelopeTarget.Pitch,
						false),
					new SetEnvelopeEnabledCommand(
						EnvelopeTarget.Filter,
						false),
				}));
	}

	[Test]
	public void S7CPitchFilterEnableControlsBothHeresyEnvelopeSlots()
	{
		DataPatternDefinition pattern = Pattern(1, 1);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerEnvelopeControlPatternEffect(
				TrackerEnvelopeControlTarget.PitchOrFilter,
				enabled: true));

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out _);

		SetEnvelopeEnabledCommand[] commands =
			output.Freeze()
				.SelectMany(noteEvent => noteEvent.Commands)
				.OfType<SetEnvelopeEnabledCommand>()
				.ToArray();

		Assert.That(
			commands,
			Is.EqualTo(
				new[]
				{
					new SetEnvelopeEnabledCommand(
						EnvelopeTarget.Pitch,
						true),
					new SetEnvelopeEnabledCommand(
						EnvelopeTarget.Filter,
						true),
				}));
	}

	[Test]
	public void SameCellNoteStartsBeforeS77DisablesItsVolumeEnvelope()
	{
		ObjectId sourceId = (ObjectId)10U;
		DataPatternDefinition pattern = Pattern(1, 1);
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Note = new StartPatternNote(sourceId);
		cell.Effects.Add(
			new TrackerEnvelopeControlPatternEffect(
				TrackerEnvelopeControlTarget.Volume,
				enabled: false));

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out _);

		NoteCommand[] commands =
			output.Freeze()
				.SelectMany(noteEvent => noteEvent.Commands)
				.ToArray();

		Assert.That(commands[0], Is.EqualTo(new StartNoteCommand(sourceId)));
		Assert.That(
			commands[1],
			Is.EqualTo(
				new SetEnvelopeEnabledCommand(
					EnvelopeTarget.Volume,
					false)));
	}

	[Test]
	public void StartRowDoesNotExecuteSkippedS7EnvelopeControl()
	{
		DataPatternDefinition pattern = Pattern(2, 1);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerEnvelopeControlPatternEffect(
				TrackerEnvelopeControlTarget.Volume,
				enabled: false));
		pattern.Grid.GetOrCreateCell(1, 0).Note =
			new StartPatternNote((ObjectId)10U);

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			startRow: 1,
			out _);

		Assert.That(
			output.Freeze()
				.SelectMany(noteEvent => noteEvent.Commands)
				.OfType<SetEnvelopeEnabledCommand>(),
			Is.Empty);
	}

	private static DataPatternDefinition Pattern(
		int rows,
		int channels)
		=> new((ObjectId)1U, "Pattern")
		{
			RowCount = rows,
			ChannelCount = channels,
		};
}
