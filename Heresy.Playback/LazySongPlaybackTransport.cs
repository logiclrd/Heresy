using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Heresy.Core.Objects;
using Heresy.Core.Sequencing;
using Heresy.Render.Realtime;

namespace Heresy.Playback;

/// <summary>
/// Defers construction of the platform playback stack until the first command
/// which actually requires audio. Stop remains a no-op before initialization.
/// </summary>
public sealed class LazySongPlaybackTransport
	: ISongPlaybackTransport,
		IPlaybackPositionTransport,
		IPlaybackRuntimeDiagnosticsTransport
{
	private readonly object _gate = new();
	private readonly Func<ISongPlaybackTransport> _factory;
	private ISongPlaybackTransport? _inner;
	private EventHandler<PlaybackPositionChangedEventArgs>? _positionChanged;
	private EventHandler<PlaybackRuntimeDiagnosticsEventArgs>? _runtimeDiagnostics;
	private bool _disposed;

	public LazySongPlaybackTransport(
		Func<ISongPlaybackTransport> factory)
	{
		_factory =
			factory
				?? throw new ArgumentNullException(nameof(factory));
	}

	public event EventHandler<PlaybackPositionChangedEventArgs>?
		PlaybackPositionChanged
	{
		add
		{
			lock (_gate)
			{
				ThrowIfDisposed();
				_positionChanged += value;
				if (_inner is IPlaybackPositionTransport positions)
					positions.PlaybackPositionChanged += value;
			}
		}
		remove
		{
			lock (_gate)
			{
				if (_disposed)
					return;

				_positionChanged -= value;
				if (_inner is IPlaybackPositionTransport positions)
					positions.PlaybackPositionChanged -= value;
			}
		}
	}

	public event EventHandler<PlaybackRuntimeDiagnosticsEventArgs>?
		RuntimeDiagnostics
	{
		add
		{
			lock (_gate)
			{
				ThrowIfDisposed();
				_runtimeDiagnostics += value;
				if (_inner is IPlaybackRuntimeDiagnosticsTransport diagnostics)
					diagnostics.RuntimeDiagnostics += value;
			}
		}
		remove
		{
			lock (_gate)
			{
				if (_disposed)
					return;
				_runtimeDiagnostics -= value;
				if (_inner is IPlaybackRuntimeDiagnosticsTransport diagnostics)
					diagnostics.RuntimeDiagnostics -= value;
			}
		}
	}

	public PlaybackPatternPosition? CurrentPlaybackPosition
	{
		get
		{
			lock (_gate)
			{
				ThrowIfDisposed();
				return (_inner as IPlaybackPositionTransport)
					?.CurrentPlaybackPosition;
			}
		}
	}

	public Task PlaySongAsync(
		SongDocument document)
		=> GetInner().PlaySongAsync(document);

	public Task PlaySequenceAsync(
		SongDocument document,
		ObjectId sequenceId,
		SequencePlaybackPosition? startPosition = null)
		=> GetInner().PlaySequenceAsync(
			document,
			sequenceId,
			startPosition);

	public Task PlayPatternAsync(
		SongDocument document,
		ObjectId patternId,
		int startRow = 0,
		bool repeat = false)
		=> GetInner().PlayPatternAsync(
			document,
			patternId,
			startRow,
			repeat);

	public Task PlayAdHocAsync(
		SongDocument document,
		NoteSchedule schedule)
		=> GetInner().PlayAdHocAsync(
			document,
			schedule);

	public Task SendLiveEventAsync(
		SongDocument document,
		ChannelTarget target,
		IReadOnlyList<NoteCommand> commands)
		=> GetInner().SendLiveEventAsync(
			document,
			target,
			commands);

	public Task StopAsync()
	{
		lock (_gate)
		{
			ThrowIfDisposed();
			return _inner?.StopAsync()
				?? Task.CompletedTask;
		}
	}

	public void Dispose()
	{
		ISongPlaybackTransport? inner;
		lock (_gate)
		{
			if (_disposed)
				return;

			_disposed = true;
			inner = _inner;
			_inner = null;
		}

		inner?.Dispose();
		GC.SuppressFinalize(this);
	}

	private ISongPlaybackTransport GetInner()
	{
		lock (_gate)
		{
			ThrowIfDisposed();
			if (_inner is null)
			{
				_inner =
					_factory()
						?? throw new InvalidOperationException(
							"The playback transport factory returned null.");
				if (_inner is IPlaybackPositionTransport positions
					&& _positionChanged is not null)
				{
					positions.PlaybackPositionChanged +=
						_positionChanged;
				}
				if (_inner is IPlaybackRuntimeDiagnosticsTransport diagnostics
					&& _runtimeDiagnostics is not null)
					diagnostics.RuntimeDiagnostics += _runtimeDiagnostics;
			}
			return _inner;
		}
	}

	private void ThrowIfDisposed()
	{
		if (_disposed)
			throw new ObjectDisposedException(nameof(LazySongPlaybackTransport));
	}
}
