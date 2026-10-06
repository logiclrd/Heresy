using System;

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

public sealed record SequencePlaybackRequest(
	SongDocumentSnapshot Snapshot,
	ObjectId SequenceId,
	SequencePlaybackPosition? StartPosition)
	: PlaybackRequest(Snapshot)
{
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
	NoteSchedule Schedule)
	: PlaybackRequest(Snapshot)
{
	public static AdHocPlaybackRequest Create(
		SongDocument document,
		NoteSchedule schedule)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(schedule);

		return new AdHocPlaybackRequest(
			SongDocumentSnapshot.Create(document),
			schedule);
	}
}

public interface IBackgroundPlaybackSourceFactory
{
	IAudioOutputSource Create(
		PlaybackRequest request);
}
