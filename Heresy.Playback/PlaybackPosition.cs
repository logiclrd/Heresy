using System;
using System.Collections.Generic;
using System.Threading;

using Heresy.Core.Objects;
using Heresy.Core.Sequences;
using Heresy.Render.Realtime;

namespace Heresy.Playback;

public sealed record PlaybackPatternPosition
{
	public PlaybackPatternPosition(
		ObjectId patternId,
		int patternRow,
		ObjectId? sequenceId,
		int? sequenceEntryIndex)
	{
		if (patternId.IsNone)
			throw new ArgumentException(
				"A playback pattern position requires a concrete pattern ID.",
				nameof(patternId));
		if (patternRow < 0)
			throw new ArgumentOutOfRangeException(nameof(patternRow));
		if (sequenceId.HasValue
			&& sequenceId.Value.IsNone)
		{
			throw new ArgumentException(
				"A sequence context ID may not be ObjectId.None.",
				nameof(sequenceId));
		}
		if (sequenceEntryIndex.HasValue
			&& sequenceEntryIndex.Value < 0)
		{
			throw new ArgumentOutOfRangeException(
				nameof(sequenceEntryIndex));
		}

		PatternId = patternId;
		PatternRow = patternRow;
		SequenceId = sequenceId;
		SequenceEntryIndex = sequenceEntryIndex;
	}

	public ObjectId PatternId { get; }
	public int PatternRow { get; }
	public ObjectId? SequenceId { get; }
	public int? SequenceEntryIndex { get; }

	/// <summary>Optional editor-F6 provenance. The ID distinguishes playback
	/// sessions even if the same source is recompiled or edited in place.</summary>
	public long? EditorCallsiteSession { get; init; }
	public SequenceCallsite? EditorPlayCallsite { get; init; }
	public SequenceCallsite? EditorPatternCallsite { get; init; }

	/// <summary>All distinct authored actions activated in the current
	/// Pattern row. Each timeline entry owns an immutable snapshot; this
	/// transient editor metadata is never stored in the song.</summary>
	public IReadOnlyList<SequenceCallsite>? EditorPatternCallsites { get; init; }

	/// <summary>Actual currently sounding Note invocation spans, separate
	/// from short-lived scheduled command activation highlights.</summary>
	public IReadOnlyList<SequenceCallsite>? EditorActiveNoteCallsites { get; init; }
}

public sealed record PlaybackPositionTimelineEntry(
	TimeSpan Offset,
	PlaybackPatternPosition Position);

public sealed class PlaybackPositionTimeline
{
	private readonly IReadOnlyList<PlaybackPositionTimelineEntry> _entries;
	private readonly object _streamGate = new();
	private readonly List<PlaybackPositionTimelineEntry>? _streamEntries;
	private TimeSpan _streamEnd = TimeSpan.MaxValue;
	private int _editorPendingPositionPeak;

	/// <summary>A timeline populated incrementally by the real playback cursor,
	/// rather than by eagerly traversing all future sequence orders.</summary>
	public PlaybackPositionTimeline()
	{
		_streamEntries = [];
		_entries = _streamEntries;
		Duration = TimeSpan.MaxValue;
		Repeat = false;
	}

	/// <summary>Largest unpublished F6 Pattern position queue depth observed
	/// during this playback timeline. Diagnostic only: no event-dropping
	/// policy, no additional allocations, and zero on untraced playback.
	/// Written by the dedicated producer and safe to sample from the UI.</summary>
	public int EditorPendingPositionPeak =>
		Volatile.Read(ref _editorPendingPositionPeak);

	internal void RecordEditorPendingPositionCount(int count)
	{
		// Only the editor-F6 Pattern queue calls this (one integer comparison
		// per row/action notification). No lock or allocation on the PCM worker.
		if (count > _editorPendingPositionPeak)
			Volatile.Write(ref _editorPendingPositionPeak, count);
	}

	public void Append(PlaybackPositionTimelineEntry entry)
	{
		ArgumentNullException.ThrowIfNull(entry);
		if (_streamEntries is null)
			throw new InvalidOperationException("Static position timelines cannot be extended.");
		lock (_streamGate)
		{
			if (_streamEnd != TimeSpan.MaxValue)
				return;
			if (_streamEntries.Count != 0
				&& entry.Offset < _streamEntries[^1].Offset)
				throw new InvalidOperationException("Streamed Pattern rows must be chronological.");
			_streamEntries.Add(entry);
			// Only the recent playback cursor history is useful to the UI.
			// Infinite scripted songs must not accumulate position records.
			if (_streamEntries.Count > 1024)
				_streamEntries.RemoveRange(0, 512);
		}
	}

	public void Finish(TimeSpan duration)
	{
		if (_streamEntries is null)
			throw new InvalidOperationException("Static position timelines cannot be finished.");
		lock (_streamGate)
		{
			_streamEnd = duration;
			Duration = duration;
		}
	}


	public PlaybackPositionTimeline(
		IEnumerable<PlaybackPositionTimelineEntry> entries,
		TimeSpan duration,
		bool repeat)
	{
		ArgumentNullException.ThrowIfNull(entries);
		if (duration < TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(duration));
		if (repeat && duration <= TimeSpan.Zero)
		{
			throw new ArgumentException(
				"A repeating playback timeline requires a positive duration.",
				nameof(duration));
		}

		PlaybackPositionTimelineEntry[] copy = [.. entries];
		TimeSpan previous = TimeSpan.MinValue;
		foreach (PlaybackPositionTimelineEntry entry in copy)
		{
			ArgumentNullException.ThrowIfNull(entry);
			ArgumentNullException.ThrowIfNull(entry.Position);
			if (entry.Offset < TimeSpan.Zero)
			{
				throw new ArgumentOutOfRangeException(
					nameof(entries),
					"Playback position offsets may not be negative.");
			}
			if (entry.Offset < previous)
			{
				throw new ArgumentException(
					"Playback position entries must be ordered by offset.",
					nameof(entries));
			}
			if (entry.Offset >= duration && duration > TimeSpan.Zero)
			{
				throw new ArgumentException(
					"Playback position entries must start before the timeline duration.",
					nameof(entries));
			}
			previous = entry.Offset;
		}

		_entries = Array.AsReadOnly(copy);
		Duration = duration;
		Repeat = repeat;
	}

	public IReadOnlyList<PlaybackPositionTimelineEntry> Entries => _entries;

	public TimeSpan Duration { get; private set; }

	public bool Repeat { get; }

	public PlaybackPatternPosition? GetPositionAt(
		TimeSpan elapsed)
	{
		lock (_streamGate)
			return GetPositionAtCore(elapsed);
	}

	private PlaybackPatternPosition? GetPositionAtCore(TimeSpan elapsed)
	{
		if (elapsed < TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(elapsed));
		if (_entries.Count == 0
			|| Duration <= TimeSpan.Zero)
		{
			return null;
		}

		TimeSpan local;
		if (Repeat)
		{
			long ticks =
				elapsed.Ticks
					% Duration.Ticks;
			local = TimeSpan.FromTicks(ticks);
		}
		else
		{
			if (elapsed >= Duration)
				return null;
			local = elapsed;
		}

		int low = 0;
		int high = _entries.Count - 1;
		int match = -1;
		while (low <= high)
		{
			int middle =
				low + ((high - low) / 2);
			if (_entries[middle].Offset <= local)
			{
				match = middle;
				low = middle + 1;
			}
			else
			{
				high = middle - 1;
			}
		}

		return match >= 0
			? _entries[match].Position
			: null;
	}

	public bool IsComplete(TimeSpan elapsed)
	{
		lock (_streamGate)
			return !Repeat && elapsed >= Duration;
	}
}

public sealed class PlaybackPositionChangedEventArgs : EventArgs
{
	public PlaybackPositionChangedEventArgs(
		PlaybackPatternPosition? position)
	{
		Position = position;
	}

	public PlaybackPatternPosition? Position { get; }
}

/// <summary>
/// Optional collaboration point between a request source factory and
/// SongPlaybackTransport. A factory which compiles a request can attach the
/// exact pattern-row timeline produced during that same compilation.
/// </summary>
public interface IPlaybackPositionTimelineProvider
{
	bool TryTakePlaybackPositionTimeline(
		PlaybackRequest request,
		out PlaybackPositionTimeline? timeline);
}
