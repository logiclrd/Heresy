using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.Core.Sequencing;
using Heresy.Core.Timing;
using Heresy.Scripting.Compilation;

namespace Heresy.Playback;

/// <summary>
/// Prepares the first flattened-schedule integration slice before playback.
/// A flattened pattern/sequence contributes events to its caller's physical
/// channels; it does not create a separate audio-rendering voice or session.
/// </summary>
internal static class FlattenedNestedScheduleExpander
{
	internal sealed record Result(NoteSchedule Schedule, TimeSpan Duration);

	private sealed record OrderedEvent(NoteEvent Event, long Order);

	public static Result Expand(
		SongDocument document,
		NoteSchedule schedule,
		TimeSpan duration,
		ObjectId rootSourceId = default)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(schedule);
		if (duration < TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(duration));

		List<OrderedEvent> output = [];
		HashSet<ObjectId> path = [];
		if (!rootSourceId.IsNone)
			path.Add(rootSourceId);

		TimeSpan end = duration;
		long order = 0;
		SequencingContext context = new();
		ExpandSchedule(
			document, schedule, TimeSpan.Zero,
			context, path, output, ref order, ref end);

		NoteScheduleBuilder builder = new();
		foreach (OrderedEvent item in output
			.OrderBy(item => item.Event.Offset.TimeOffset)
			.ThenBy(item => item.Order))
		{
			builder.Append(item.Event);
		}
		return new Result(builder.Freeze(), end);
	}

	private static void ExpandSchedule(
		SongDocument document,
		NoteSchedule schedule,
		TimeSpan origin,
		SequencingContext context,
		HashSet<ObjectId> path,
		List<OrderedEvent> output,
		ref long order,
		ref TimeSpan duration)
	{
		foreach (NoteEvent entry in schedule)
		{
			TimeSpan startTime = origin + entry.Offset.TimeOffset;
			List<NoteCommand> remaining = [];
			List<(ObjectId Source, StartNoteCommand Note)> nested = [];

			foreach (NoteCommand command in entry.Commands)
			{
				if (command is not StartNoteCommand start
					|| start.Mixdown
					|| !document.TryGet(start.SourceId, out SongObject? obj)
					|| obj is not (PatternDefinition or SequenceDefinition))
				{
					remaining.Add(command);
					continue;
				}

				if (entry.Target.Kind != ChannelTargetKind.Physical)
				{
					throw new NotSupportedException(
						"Flattened Pattern/Sequence notes currently require a physical parent channel.");
				}
				if (start.PitchMultiplier != 1.0
					|| start.PlaybackSpeedMultiplier != 1.0
					|| start.Volume.HasValue)
				{
					throw new NotSupportedException(
						"Pitch, speed and initial-volume transforms of flattened nested sources are not yet implemented.");
				}
				nested.Add((start.SourceId, start));
			}

			if (remaining.Count > 0)
			{
				output.Add(new OrderedEvent(
					entry with
					{
						Offset = new MusicalTime(
							startTime,
							entry.Offset.RowOffset),
						Commands = remaining,
					},
					order++));
				CheckLimit(order);
			}

			foreach ((ObjectId sourceId, _) in nested)
			{
				if (!path.Add(sourceId))
				{
					throw new InvalidOperationException(
						$"Flattened Pattern/Sequence sound source cycle includes object {sourceId.Value}.");
				}
				try
				{
					int channelOffset =
						entry.Target.PhysicalChannel - context.PhysicalChannelBase;
					if (channelOffset < 0)
						throw new InvalidOperationException(
							"Flattened child channels cannot precede their parent context.");

					SequencingContext childContext =
						context.FlattenedChild(physicalChannelOffset: channelOffset);
					SongScheduleCompilationResult compiled =
						document.TryGet(sourceId, out SongObject? obj)
							&& obj is PatternDefinition
							? SongScheduleCompiler.CompilePattern(
								document, sourceId, context: childContext)
							: SongScheduleCompiler.CompileSequence(
								document, sourceId, context: childContext);

					if (!compiled.Success || compiled.Schedule is null)
					{
						throw new PlaybackSourceCompilationException(
							$"Could not compile flattened sound source {sourceId.Value}.",
							compiled.Diagnostics);
					}

					TimeSpan childEnd = startTime + compiled.Duration;
					if (childEnd > duration)
						duration = childEnd;
					ExpandSchedule(
						document, compiled.Schedule, startTime,
						childContext, path, output, ref order, ref duration);
				}
				finally
				{
					path.Remove(sourceId);
				}
			}
		}
	}

	private static void CheckLimit(long count)
	{
		if (count > NoteScheduleBuilder.MaximumGeneratedNotes)
		{
			throw new InvalidOperationException(
				$"Flattened sources exceeded the {NoteScheduleBuilder.MaximumGeneratedNotes:N0} event limit.");
		}
	}
}
