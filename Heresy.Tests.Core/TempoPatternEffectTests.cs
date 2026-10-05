using System;
using System.Linq;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;
using Heresy.Core.Timing;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class TempoPatternEffectTests
{
	[Test]
	public void DataGridKeepsTxxOnOriginatingPhysicalChannel()
	{
		DataPatternDefinition pattern = Pattern(1, 1);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerTempoPatternEffect(0x12));
		NoteScheduleBuilder output = new();

		pattern.GenerateRawNotes(
			new SequencingContext(),
			output,
			out _);

		NoteEvent noteEvent = output.Freeze().Single();
		Assert.That(
			noteEvent.Target,
			Is.EqualTo(ChannelTarget.Physical(0)));
		Assert.That(
			noteEvent.Commands.Single(),
			Is.EqualTo(new ApplyTrackerTempoCommand(0x12)));
	}

	[Test]
	public void TfaSetsTempoImmediatelyAndControlsWholeRow()
	{
		DataPatternDefinition pattern = Pattern(1, 1);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerTempoPatternEffect(0xFA));

		SequencingContext context = new();
		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			context,
			output,
			out TimeSpan duration);

		NoteEvent tempoEvent = output.Freeze().Single();
		Assert.That(tempoEvent.Offset.TimeOffset, Is.EqualTo(TimeSpan.Zero));
		Assert.That(tempoEvent.Target, Is.EqualTo(ChannelTarget.Physical(0)));
		Assert.That(
			tempoEvent.Commands.Single(),
			Is.EqualTo(new SetTempoCommand(250)));
		Assert.That(
			duration.TotalSeconds,
			Is.EqualTo(6.0 * 2.5 / 250.0).Within(1e-7));
		Assert.That(context.State.Tempo, Is.EqualTo(250.0));
	}

	[Test]
	public void T12SlidesUpOnEveryPostFirstTick()
	{
		SequencingContext context = new();
		NoteSchedule schedule = Generate(
			new TrackerTempoPatternEffect(0x12),
			context,
			out TimeSpan duration);

		SetTempoRampCommand[] commands =
			schedule.SelectMany(e => e.Commands)
				.OfType<SetTempoRampCommand>()
				.ToArray();

		Assert.That(
			commands.Select(c => c.EndingTempo),
			Is.EqualTo(new double[] { 127, 129, 131, 133, 135 }));
		Assert.That(
			commands.Select(c => c.TrackerTicks),
			Is.EqualTo(new double[] { 1, 1, 1, 1, 1 }));

		double[] expectedTimes =
		{
			0.0,
			RampDuration(125, 127),
			RampDuration(125, 127) + RampDuration(127, 129),
			RampDuration(125, 127) + RampDuration(127, 129)
				+ RampDuration(129, 131),
			RampDuration(125, 127) + RampDuration(127, 129)
				+ RampDuration(129, 131) + RampDuration(131, 133),
		};

		NoteEvent[] events = schedule
			.Where(e => e.Commands.OfType<SetTempoRampCommand>().Any())
			.ToArray();

		Assert.That(events, Has.Length.EqualTo(5));
		for (int i = 0; i < events.Length; i++)
		{
			Assert.That(
				events[i].Offset.TimeOffset.TotalSeconds,
				Is.EqualTo(expectedTimes[i]).Within(1e-7));
		}

		double expectedDuration =
			expectedTimes[^1]
				+ RampDuration(133, 135)
				+ TickDuration(135);
		Assert.That(
			duration.TotalSeconds,
			Is.EqualTo(expectedDuration).Within(1e-7));
		Assert.That(context.State.Tempo, Is.EqualTo(135.0));
	}

	[Test]
	public void T02SlidesDownAndClampsAt32()
	{
		SequencingContext context = new();
		context.State.Tempo = 35;

		Generate(
			new TrackerTempoPatternEffect(0x02),
			context,
			out _);

		Assert.That(context.State.Tempo, Is.EqualTo(32.0));
	}

	[Test]
	public void T1fSlidesUpAndClampsAt255()
	{
		SequencingContext context = new();
		context.State.Tempo = 250;

		Generate(
			new TrackerTempoPatternEffect(0x1F),
			context,
			out _);

		Assert.That(context.State.Tempo, Is.EqualTo(255.0));
	}

	[Test]
	public void T00RecallsWholeByteMemoryPerPhysicalChannel()
	{
		SequencingContext context = new();

		Generate(
			new TrackerTempoPatternEffect(0x12),
			context,
			out _);

		double afterFirstRow = context.State.Tempo;

		Generate(
			new TrackerTempoPatternEffect(0x00),
			context,
			out _);

		Assert.That(
			context.GetPhysicalChannelState(0)
				.TryGetEffectParameter(
					EffectMemorySlot.Tempo,
					out byte remembered),
			Is.True);
		Assert.That(remembered, Is.EqualTo(0x12));
		Assert.That(
			context.State.Tempo,
			Is.EqualTo(afterFirstRow + 10.0));
	}

	[Test]
	public void T00CanRecallImmediateTempoSet()
	{
		SequencingContext context = new();

		Generate(
			new TrackerTempoPatternEffect(0xFA),
			context,
			out _);

		context.State.Tempo = 125;

		NoteSchedule recalled = Generate(
			new TrackerTempoPatternEffect(0x00),
			context,
			out _);

		Assert.That(
			recalled.SelectMany(e => e.Commands)
				.OfType<SetTempoCommand>()
				.First()
				.TicksPerDiachron,
			Is.EqualTo(250.0));
		Assert.That(context.State.Tempo, Is.EqualTo(250.0));
	}

	[Test]
	public void SimultaneousTempoSlidesUsePhysicalChannelOrderAtClamp()
	{
		DataPatternDefinition downThenUp = Pattern(1, 2);
		downThenUp.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerTempoPatternEffect(0x01));
		downThenUp.Grid.GetOrCreateCell(0, 1).Effects.Add(
			new TrackerTempoPatternEffect(0x11));

		SequencingContext first = new();
		first.State.Tempo = 32;
		PatternNoteProcessor.GenerateNotes(
			downThenUp,
			first,
			new NoteScheduleBuilder(),
			out _);

		Assert.That(first.State.Tempo, Is.EqualTo(33.0));

		DataPatternDefinition upThenDown = Pattern(1, 2);
		upThenDown.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerTempoPatternEffect(0x11));
		upThenDown.Grid.GetOrCreateCell(0, 1).Effects.Add(
			new TrackerTempoPatternEffect(0x01));

		SequencingContext second = new();
		second.State.Tempo = 32;
		PatternNoteProcessor.GenerateNotes(
			upThenDown,
			second,
			new NoteScheduleBuilder(),
			out _);

		Assert.That(second.State.Tempo, Is.EqualTo(32.0));
	}

	[Test]
	public void S6xExtendsTempoSlideTickRun()
	{
		DataPatternDefinition pattern = Pattern(1, 1);
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Effects.Add(new TrackerTempoPatternEffect(0x11));
		cell.Effects.Add(new TrackerFinePatternDelayPatternEffect(2));

		SequencingContext context = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			context,
			new NoteScheduleBuilder(),
			out _);

		Assert.That(context.State.Tempo, Is.EqualTo(132.0));
	}

	[TestCase("cut")]
	[TestCase("delay")]
	[TestCase("retrigger")]
	public void TickTimedEffectsUseVariableTempoBoundary(string kind)
	{
		DataPatternDefinition pattern = Pattern(1, 2);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerTempoPatternEffect(0x12));

		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 1);
		cell.Note = new StartPatternNote((ObjectId)10U);

		switch (kind)
		{
			case "cut":
				cell.Effects.Add(new TrackerNoteCutPatternEffect(3));
				break;
			case "delay":
				cell.Effects.Add(new TrackerNoteDelayPatternEffect(3));
				break;
			case "retrigger":
				cell.Effects.Add(new RetriggerPatternEffect(0x03));
				break;
			default:
				throw new AssertionException("Unknown test case.");
		}

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out _);

		double expected =
			RampDuration(125, 127)
			+ RampDuration(127, 129)
			+ RampDuration(129, 131);

		NoteEvent target = kind switch
		{
			"cut" => output.Freeze().Single(
				e => e.Commands.Any(c => c is NoteCutCommand)),
			"delay" => output.Freeze().Single(
				e => e.Commands.Any(c => c is StartNoteCommand)),
			_ => output.Freeze().Single(
				e => e.Commands.Any(
					c => c is RetriggerCurrentVoiceCommand)),
		};

		Assert.That(
			target.Offset.TimeOffset.TotalSeconds,
			Is.EqualTo(expected).Within(1e-7));
	}

	[Test]
	public void StartRowDoesNotExecuteSkippedTxxOrSeedMemory()
	{
		DataPatternDefinition pattern = Pattern(2, 1);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerTempoPatternEffect(0xFA));
		pattern.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new TrackerTempoPatternEffect(0x00));

		SequencingContext context = new();
		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			context,
			output,
			startRow: 1,
			out TimeSpan duration);

		Assert.That(
			context.GetPhysicalChannelState(0)
				.TryGetEffectParameter(
					EffectMemorySlot.Tempo,
					out _),
			Is.False);
		Assert.That(context.State.Tempo, Is.EqualTo(125.0));
		Assert.That(
			duration.TotalSeconds,
			Is.EqualTo(6.0 * TickDuration(125)).Within(1e-7));
	}

	private static NoteSchedule Generate(
		PatternEffect effect,
		SequencingContext context,
		out TimeSpan duration)
	{
		DataPatternDefinition pattern = Pattern(1, 1);
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(effect);
		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			context,
			output,
			out duration);
		return output.Freeze();
	}

	private static double RampDuration(
		double startingTempo,
		double endingTempo)
		=> Math.Abs(endingTempo - startingTempo) <= 1e-12
			? TickDuration(startingTempo)
			: 2.5 / (endingTempo - startingTempo)
				* Math.Log(endingTempo / startingTempo);

	private static double TickDuration(double tempo)
		=> SequencingConstants.Diachron.TotalSeconds / tempo;

	private static DataPatternDefinition Pattern(
		int rows,
		int channels)
		=> new((ObjectId)1U, "Pattern")
		{
			RowCount = rows,
			ChannelCount = channels,
		};
}
