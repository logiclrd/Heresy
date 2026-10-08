using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.Core.Sequencing;
using Heresy.Core.Timing;

namespace Heresy.Scripting.Compilation;

/// <summary>
/// Expands flattened Pattern/Sequence invocations at the exact parent row
/// resolution point, using the same mutable SequencingState and mapped
/// SequencingChannelStateMap. Only the flattened form changes the parent
/// schedule; mixdown continues through an independently rendered ISound.
/// </summary>
internal sealed class DocumentFlattenedNoteSourceExpander
	: IFlattenedNoteSourceExpander
{
	private readonly SongDocument _document;
	private readonly HashSet<ObjectId> _active = [];

	public DocumentFlattenedNoteSourceExpander(SongDocument document)
		=> _document = document
			?? throw new ArgumentNullException(nameof(document));

	public TimeSpan MaximumAbsoluteEnd { get; private set; }

	public IReadOnlyList<NoteEvent>? Expand(
		NoteEvent noteEvent,
		SequencingContext context)
	{
		ArgumentNullException.ThrowIfNull(noteEvent);
		ArgumentNullException.ThrowIfNull(context);

		List<NoteCommand> retained = [];
		List<NoteEvent> inserted = [];
		bool flattened = false;

		foreach (NoteCommand command in noteEvent.Commands)
		{
			if (command is not StartNoteCommand start
				|| start.Mixdown
				|| !_document.TryGet(start.SourceId, out SongObject? obj)
				|| obj is not (PatternDefinition or SequenceDefinition))
			{
				retained.Add(command);
				continue;
			}
			flattened = true;
			if (noteEvent.Target.Kind != ChannelTargetKind.Physical)
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

			ObjectId childId = start.SourceId;
			if (!_active.Add(childId))
			{
				throw new InvalidOperationException(
					$"Flattened Pattern/Sequence sound source cycle includes object {childId.Value}.");
			}
			try
			{
				int channelOffset =
					noteEvent.Target.PhysicalChannel
						- context.PhysicalChannelBase;
				if (channelOffset < 0)
					throw new InvalidOperationException(
						"Flattened child channels cannot precede their parent context.");

				SequencingContext childContext = context.FlattenedChild(
					physicalChannelOffset: channelOffset);
				childContext.TimelineOrigin =
					context.TimelineOrigin + noteEvent.Offset.TimeOffset;

				SongScheduleCompilationResult compilation =
					obj is PatternDefinition
						? SongScheduleCompiler.CompilePattern(
							_document, childId, context: childContext)
						: SongScheduleCompiler.CompileSequence(
							_document, childId, context: childContext);
				if (!compilation.Success || compilation.Schedule is null)
				{
					string diagnostics = string.Join(
						"; ", compilation.Diagnostics.Select(d => d.Message));
					throw new InvalidOperationException(
						$"Cannot compile flattened source {childId.Value}: {diagnostics}");
				}
				TimeSpan absoluteEnd =
					childContext.TimelineOrigin + compilation.Duration;
				if (absoluteEnd > MaximumAbsoluteEnd)
					MaximumAbsoluteEnd = absoluteEnd;

				foreach (NoteEvent childEvent in compilation.Schedule)
				{
					inserted.Add(childEvent with
					{
						Offset = new MusicalTime(
							noteEvent.Offset.TimeOffset
								+ childEvent.Offset.TimeOffset,
							childEvent.Offset.RowOffset),
					});
				}
			}
			finally
			{
				_active.Remove(childId);
			}
		}

		if (!flattened)
			return null;

		List<NoteEvent> expanded = [];
		if (retained.Count > 0)
		{
			expanded.Add(noteEvent with { Commands = retained });
		}
		expanded.AddRange(inserted);

		if (expanded.Count > NoteScheduleBuilder.MaximumGeneratedNotes)
		{
			throw new InvalidOperationException(
				"Flattened source expansion exceeds the note-event limit.");
		}
		return expanded;
	}
}
