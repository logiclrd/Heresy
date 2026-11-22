using System;
using System.Threading;

using Heresy.Core.Objects;
using Heresy.Core.Sequencing;

namespace Heresy.Render.Realtime;

public readonly record struct SequencePlaybackPosition
{
	public SequencePlaybackPosition(
		int order,
		int row)
	{
		if (order < 0)
			throw new ArgumentOutOfRangeException(nameof(order));
		if (row < 0)
			throw new ArgumentOutOfRangeException(nameof(row));

		Order = order;
		Row = row;
	}

	public int Order { get; }
	public int Row { get; }
}

public abstract record PlaybackRequest(
	SongDocumentSnapshot Snapshot);

/// <summary>One process-wide, positive identifier per editor-F6 playback.
/// No ID survives application restart or is stored in a song. Atomically
/// allocate across editors and potential concurrent callers.</summary>
public static class EditorCallsiteSessionIds
{
	private static long _next;

	public static long Next()
	{
		long id = Interlocked.Increment(ref _next);
		if (id <= 0)
			throw new InvalidOperationException(
				"Editor playback session identifiers are exhausted.");
		return id;
	}
}

public sealed record SequencePlaybackRequest(
	SongDocumentSnapshot Snapshot,
	ObjectId SequenceId,
	SequencePlaybackPosition? StartPosition)
	: PlaybackRequest(Snapshot)
{
	/// <summary>Present only for a Script Sequence launched by F6 in its
	/// source editor. Ordinary playback requests never set this property.</summary>
	public long? EditorCallsiteSession { get; init; }

	public static SequencePlaybackRequest Create(
		SongDocument document,
		ObjectId sequenceId,
		SequencePlaybackPosition? startPosition = null)
	{
		ArgumentNullException.ThrowIfNull(document);
		if (sequenceId.IsNone)
			throw new ArgumentException(
				"A playback sequence ID may not be zero.",
				nameof(sequenceId));

		return new SequencePlaybackRequest(
			SongDocumentSnapshot.Create(document),
			sequenceId,
			startPosition);
	}
}

public sealed record PatternPlaybackRequest(
	SongDocumentSnapshot Snapshot,
	ObjectId PatternId,
	int StartRow,
	bool Repeat)
	: PlaybackRequest(Snapshot)
{
	/// <summary>Only a Script Pattern's F6 audition opts into authored
	/// action provenance.</summary>
	public long? EditorCallsiteSession { get; init; }

	public static PatternPlaybackRequest Create(
		SongDocument document,
		ObjectId patternId,
		int startRow = 0,
		bool repeat = false)
	{
		ArgumentNullException.ThrowIfNull(document);
		if (patternId.IsNone)
			throw new ArgumentException(
				"A playback pattern ID may not be zero.",
				nameof(patternId));
		if (startRow < 0)
			throw new ArgumentOutOfRangeException(nameof(startRow));

		return new PatternPlaybackRequest(
			SongDocumentSnapshot.Create(document),
			patternId,
			startRow,
			repeat);
	}
}

public sealed record AdHocPlaybackRequest(
	SongDocumentSnapshot Snapshot,
	NoteSchedule Schedule,
	bool ReserveSynthMultiHeadroom = false)
	: PlaybackRequest(Snapshot)
{
	/// <summary>Only live Synth audition opts into dormant constituent
	/// capacity. Ordinary ad-hoc playback keeps this disabled.</summary>
	public static AdHocPlaybackRequest Create(
		SongDocument document,
		NoteSchedule schedule,
		bool reserveSynthMultiHeadroom = false)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(schedule);

		return new AdHocPlaybackRequest(
			SongDocumentSnapshot.Create(document),
			schedule,
			reserveSynthMultiHeadroom);
	}
}

public interface IBackgroundPlaybackSourceFactory
{
	IAudioOutputSource Create(
		PlaybackRequest request);
}
