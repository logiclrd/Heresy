using System;
using System.Linq;

using Heresy.Core.Diagnostics;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;
using Heresy.Core.Timing;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class SequencingDiagnosticsTests
{
	[Test]
	public void EagerRawReceiverPostsWarningPerDroppedNoteAndStillEmitsChronologically()
	{
		SequencingContext context = new();
		NoteScheduleBuilder receiver = new();
		ChronologicalRawPatternNoteReceiver raw =
			new(receiver, context.Diagnostics);
		raw.Append(At(4));
		raw.Append(At(2));
		raw.Append(At(3));
		raw.Append(At(4));

		Assert.That(receiver.Freeze().Select(n => n.Offset.RowOffset),
			Is.EqualTo(new[] { 4d, 4d }));
		Assert.That(context.Diagnostics.DroppedOutOfOrderNotes, Is.EqualTo(2));
		SequencingDiagnostic[] messages = context.Diagnostics.Drain();
		Assert.That(messages, Has.Length.EqualTo(2));
		Assert.That(messages.All(m => m.Code == "HRSEQ001"), Is.True);
		Assert.That(messages.Select(m => m.Row), Is.EqualTo(new[] { 2d, 3d }));
		Assert.That(context.Diagnostics.Drain(), Is.Empty);
	}

	[Test]
	public void WarningQueueIsBoundedAcrossFlattenedAndMixdownChildren()
	{
		SequencingContext parent = new();
		SequencingContext flattened = parent.FlattenedChild();
		SequencingContext mixdown = parent.MixdownChild();
		Assert.That(flattened.Diagnostics, Is.SameAs(parent.Diagnostics));
		Assert.That(mixdown.Diagnostics, Is.SameAs(parent.Diagnostics));

		ChronologicalRawPatternNoteReceiver raw = new(
			new NoteScheduleBuilder(), flattened.Diagnostics);
		raw.Append(At(10));
		int attempts = SequencingDiagnosticLog.MaximumIndividualMessages + 1000;
		for (int i = 0; i < attempts; i++)
			raw.Append(At(1));

		Assert.That(parent.Diagnostics.DroppedOutOfOrderNotes, Is.EqualTo(attempts));
		SequencingDiagnostic[] messages = parent.Diagnostics.Drain();
		Assert.That(messages, Has.Length.EqualTo(
			SequencingDiagnosticLog.MaximumIndividualMessages + 1));
		Assert.That(messages.Take(SequencingDiagnosticLog.MaximumIndividualMessages)
			.All(m => m.Code == "HRSEQ001"), Is.True);
		Assert.That(messages.Last().Code, Is.EqualTo("HRSEQ002"));
		Assert.That(parent.Diagnostics.SuppressedWarnings,
			Is.EqualTo(attempts - SequencingDiagnosticLog.MaximumIndividualMessages));
		// A drained queue must not restart the message rate cap.
		ChronologicalRawPatternNoteReceiver later = new(
			new NoteScheduleBuilder(), mixdown.Diagnostics);
		later.Append(At(10));
		later.Append(At(0));
		Assert.That(parent.Diagnostics.Drain(), Is.Empty);
		Assert.That(parent.Diagnostics.DroppedOutOfOrderNotes,
			Is.EqualTo(attempts + 1));
	}

	[Test]
	public void ConcurrentProducerAndConsumerPreserveBoundedDiagnostics()
	{
		SequencingDiagnosticLog log = new();
		System.Collections.Generic.List<SequencingDiagnostic> received = [];
		System.Threading.Tasks.Task producer = System.Threading.Tasks.Task.Run(() =>
		{
			for (int i = 0; i < 4000; i++)
				log.ReportDroppedOutOfOrderNote(1, 4);
		});

		while (!producer.IsCompleted)
			received.AddRange(log.Drain());
		producer.GetAwaiter().GetResult();
		received.AddRange(log.Drain());

		Assert.That(log.DroppedOutOfOrderNotes, Is.EqualTo(4000));
		Assert.That(received, Has.Count.EqualTo(
			SequencingDiagnosticLog.MaximumIndividualMessages + 1));
		Assert.That(received.Count(m => m.Code == "HRSEQ001"),
			Is.EqualTo(SequencingDiagnosticLog.MaximumIndividualMessages));
		Assert.That(received.Count(m => m.Code == "HRSEQ002"),
			Is.EqualTo(1));
		Assert.That(log.Drain(), Is.Empty);
	}

	[Test]
	public void SharedTimelineReportsDiscardedTimingCommandWithoutExecutingIt()
	{
		SequencingContext context = new();
		using IncrementalPatternTimeline timeline = new(context);
		timeline.Add(new TestRawSteps(), 4, context);
		while (timeline.TryStep(out _)) { }

		Assert.That(context.State.Tempo, Is.EqualTo(125));
		Assert.That(context.Diagnostics.DroppedOutOfOrderNotes, Is.EqualTo(1));
		var messages = context.Diagnostics.Drain();
		Assert.That(messages, Has.Length.EqualTo(1));
		Assert.That(messages[0].Code, Is.EqualTo("HRSEQ001"));
		Assert.That(messages[0].Row, Is.EqualTo(1));
	}

	private static NoteEvent At(double row)
		=> new(new MusicalTime(TimeSpan.Zero, row),
			ChannelTarget.Physical(0), [new NoteCutCommand()]);

	private sealed class TestRawSteps : IIncrementalRawPatternNoteGenerator
	{
		public System.Collections.Generic.IEnumerable<RawPatternStep>
			EnumerateRawSteps(SequencingContext context)
		{
			yield return new RawPatternStep.Emit(At(2));
			yield return new RawPatternStep.Emit(new NoteEvent(
				new MusicalTime(TimeSpan.Zero, 1),
				ChannelTarget.Global, [new SetTempoCommand(250)]));
			yield return new RawPatternStep.Emit(At(3));
		}
	}
}
