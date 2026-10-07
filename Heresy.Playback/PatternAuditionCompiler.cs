using System;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;

namespace Heresy.Playback;

/// <summary>
/// Compiles tracker note/row audition into ordinary immutable NoteSchedules.
/// Earlier rows are sequenced into a discard sink first so source/effect/timing
/// memory at the auditioned row matches ordinary pattern sequencing.
/// </summary>
public static class PatternAuditionCompiler
{
	public static NoteSchedule CompileNote(
		DataPatternDefinition pattern,
		int row,
		int channel)
	{
		Validate(
			pattern,
			row,
			channel);

		PatternCell? source =
			pattern.Grid[row, channel];
		if (source?.Note is null)
			return new NoteScheduleBuilder().Freeze();

		SequencingContext context =
			CreatePrimedContext(
				pattern,
				row);
		DataPatternDefinition slice =
			CreateSlice(
				pattern,
				row,
				channel,
				includeEffects: false);

		return CompileSlice(
			slice,
			context);
	}

	public static NoteSchedule CompileRow(
		DataPatternDefinition pattern,
		int row)
	{
		Validate(
			pattern,
			row,
			channel: null);

		SequencingContext context =
			CreatePrimedContext(
				pattern,
				row);
		DataPatternDefinition slice =
			CreateSlice(
				pattern,
				row,
				channel: null,
				includeEffects: true);

		return CompileSlice(
			slice,
			context);
	}

	private static SequencingContext CreatePrimedContext(
		DataPatternDefinition pattern,
		int row)
	{
		SequencingContext context = new();

		for (int priorRow = 0;
			priorRow < row;
			priorRow++)
		{
			DataPatternDefinition slice =
				CreateSlice(
					pattern,
					priorRow,
					channel: null,
					includeEffects: true);

			PatternNoteProcessor.GenerateNotes(
				slice,
				context,
				DiscardingNoteReceiver.Instance,
				out _);
		}

		return context;
	}

	private static NoteSchedule CompileSlice(
		DataPatternDefinition slice,
		SequencingContext context)
	{
		NoteScheduleBuilder builder = new();
		PatternNoteProcessor.GenerateNotes(
			slice,
			context,
			builder,
			out _);
		return builder.Freeze();
	}

	private static DataPatternDefinition CreateSlice(
		DataPatternDefinition pattern,
		int sourceRow,
		int? channel,
		bool includeEffects)
	{
		DataPatternDefinition slice =
			new(
				ObjectId.None,
				$"{pattern.Name} audition")
			{
				RowCount = 1,
				ChannelCount = pattern.ChannelCount,
			};

		if (channel.HasValue)
		{
			CopyCell(
				pattern.Grid[sourceRow, channel.Value],
				slice,
				channel.Value,
				includeEffects);
			return slice;
		}

		for (int index = 0;
			index < pattern.ChannelCount;
			index++)
		{
			CopyCell(
				pattern.Grid[sourceRow, index],
				slice,
				index,
				includeEffects);
		}

		return slice;
	}

	private static void CopyCell(
		PatternCell? source,
		DataPatternDefinition destination,
		int channel,
		bool includeEffects)
	{
		if (source is null || source.IsEmpty)
			return;

		PatternCell target =
			destination.Grid.GetOrCreateCell(
				0,
				channel);
		target.Note = source.Note;
		target.SourceId = source.SourceId;
		target.Volume = source.Volume;

		if (includeEffects)
			target.Effects.AddRange(source.Effects);
	}

	private static void Validate(
		DataPatternDefinition pattern,
		int row,
		int? channel)
	{
		ArgumentNullException.ThrowIfNull(pattern);

		if ((uint)row >= (uint)pattern.RowCount)
			throw new ArgumentOutOfRangeException(nameof(row));

		if (channel.HasValue
			&& (uint)channel.Value >= (uint)pattern.ChannelCount)
		{
			throw new ArgumentOutOfRangeException(nameof(channel));
		}
	}

	private sealed class DiscardingNoteReceiver
		: INoteReceiver
	{
		public static DiscardingNoteReceiver Instance { get; } =
			new();

		public void Append(
			NoteEvent noteEvent)
		{
			ArgumentNullException.ThrowIfNull(noteEvent);
		}
	}
}
