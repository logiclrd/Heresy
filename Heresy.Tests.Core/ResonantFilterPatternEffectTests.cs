using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class ResonantFilterPatternEffectTests
{
	[Test]
	public void GridFilterEffectTranslatesToFilterCommand()
	{
		DataPatternDefinition pattern = new((ObjectId)1U, "Pattern")
		{
			RowCount = 1,
			ChannelCount = 1,
		};
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new SetResonantFilterPatternEffect(0.4, 0.75));
		NoteScheduleBuilder output = new();

		pattern.GenerateRawNotes(
			new SequencingContext(),
			output,
			out _);

		NoteSchedule schedule = output.Freeze();
		Assert.That(schedule.Count, Is.EqualTo(1));
		Assert.That(
			schedule[0].Commands[0],
			Is.EqualTo(new SetResonantFilterCommand(0.4, 0.75)));
	}
}
