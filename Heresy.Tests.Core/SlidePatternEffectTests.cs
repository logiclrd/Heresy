using System;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class SlidePatternEffectTests
{
	[Test]
	public void DataPatternSlidesEmitRowEndFreezeCommands()
	{
		DataPatternDefinition pattern = new((ObjectId)1U, "Pattern")
		{
			RowCount = 1,
			ChannelCount = 1,
		};
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Effects.Add(new PitchSlidePatternEffect(48.0));
		cell.Effects.Add(new NoteVolumeSlidePatternEffect(-4.0));
		NoteScheduleBuilder output = new();

		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out TimeSpan duration);

		NoteSchedule schedule = output.Freeze();

		Assert.That(duration, Is.EqualTo(TimeSpan.FromMilliseconds(120)));
		Assert.That(schedule.Count, Is.EqualTo(2));
		Assert.That(
			schedule[0].Commands,
			Does.Contain(new SetPitchSlideCommand(48.0)));
		Assert.That(
			schedule[0].Commands,
			Does.Contain(new SetNoteVolumeSlideCommand(-4.0)));
		Assert.That(schedule[1].Offset.TimeOffset, Is.EqualTo(duration));
		Assert.That(
			schedule[1].Commands,
			Does.Contain(new ClearPitchSlideCommand()));
		Assert.That(
			schedule[1].Commands,
			Does.Contain(new ClearNoteVolumeSlideCommand()));
	}
}
