using System;

using Heresy.Core.Objects;
using Heresy.Core.Sequencing;

namespace Heresy.Render.Realtime;

public readonly record struct SequencePlaybackPosition(
	int Order,
	int Row);

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
		=> throw new NotImplementedException();
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
		=> throw new NotImplementedException();
}

public sealed record AdHocPlaybackRequest(
	SongDocumentSnapshot Snapshot,
	NoteSchedule Schedule)
	: PlaybackRequest(Snapshot)
{
	public static AdHocPlaybackRequest Create(
		SongDocument document,
		NoteSchedule schedule)
		=> throw new NotImplementedException();
}

public interface IBackgroundPlaybackSourceFactory
{
	IAudioOutputSource Create(
		PlaybackRequest request);
}
