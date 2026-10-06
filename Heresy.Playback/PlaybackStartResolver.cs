using System;

using Heresy.Core.Objects;

namespace Heresy.Playback;

public abstract record PlaybackStartLocation;

public sealed record SequencePlaybackStartLocation(
	ObjectId SequenceId,
	int Order,
	int Row)
	: PlaybackStartLocation;

public sealed record PatternPlaybackStartLocation(
	ObjectId PatternId,
	int Row)
	: PlaybackStartLocation;

public static class PlaybackStartResolver
{
	public static PlaybackStartLocation ResolveFromPattern(
		SongDocument document,
		ObjectId patternId,
		int row,
		ObjectId? preferredSequenceId = null,
		int? preferredOrder = null)
		=> throw new NotImplementedException();
}
