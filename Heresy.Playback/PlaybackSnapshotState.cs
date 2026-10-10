using System;

using Heresy.Core.Objects;

namespace Heresy.Playback;

/// <summary>
/// Immutable playback snapshot identified by the original authoring
/// document, not by its file name or unsaved-file revision.
/// </summary>
public readonly record struct PlaybackSnapshotInfo(
	long Generation,
	SongDocument? SourceDocument,
	uint AudioRevision)
{
	public bool IsActive => SourceDocument is not null;
}

public enum PlaybackSnapshotIndicatorState
{
	Inactive,
	Current,
	AudioEdited,
	DifferentDocument,
}

/// <summary>Compares frozen audio revision, not the persistence baseline.</summary>
public static class PlaybackSnapshotIndicator
{
	public static PlaybackSnapshotIndicatorState GetState(
		PlaybackSnapshotInfo snapshot,
		SongDocument currentDocument)
	{
		ArgumentNullException.ThrowIfNull(currentDocument);
		if (!snapshot.IsActive)
			return PlaybackSnapshotIndicatorState.Inactive;
		if (!ReferenceEquals(snapshot.SourceDocument, currentDocument))
			return PlaybackSnapshotIndicatorState.DifferentDocument;
		return snapshot.AudioRevision == currentDocument.AudioRevision
			? PlaybackSnapshotIndicatorState.Current
			: PlaybackSnapshotIndicatorState.AudioEdited;
	}
}

public interface IPlaybackSnapshotTransport
{
	event EventHandler<PlaybackSnapshotChangedEventArgs>? PlaybackSnapshotChanged;
	PlaybackSnapshotInfo CurrentPlaybackSnapshot { get; }
}

/// <summary>Raised by the transport, never on the PCM or SDL callback.</summary>
public sealed class PlaybackSnapshotChangedEventArgs(
	PlaybackSnapshotInfo snapshot) : EventArgs
{
	public PlaybackSnapshotInfo Snapshot { get; } = snapshot;
}
