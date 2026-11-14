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
	uint AudioRevision,
	int OlderAudioVoiceCount = 0)
{
	public bool IsActive => SourceDocument is not null;
}

public enum PlaybackSnapshotIndicatorState
{
	Inactive,
	Current,
	OlderAudio,
	DifferentDocument,
}

/// <summary>Warn only about active notes that definitively missed an applicable edit.
/// The original frozen base revision is metadata, not proof of stale audio.</summary>
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
		return snapshot.OlderAudioVoiceCount > 0
			? PlaybackSnapshotIndicatorState.OlderAudio
			: PlaybackSnapshotIndicatorState.Current;
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

/// <summary>Immutable currency observation from the PCM producer, sampled
/// outside realtime via its request-bound source. A failed incompatible
/// publication is counted until each affected voice actually retires.</summary>
public interface IPlaybackOlderAudioReportProvider
{
	bool TryGetOlderAudioVoiceCount(PlaybackRequest request, out int count);
}
