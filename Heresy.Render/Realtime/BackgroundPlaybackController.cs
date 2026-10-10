using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Heresy.Core.Sequencing;

namespace Heresy.Render.Realtime;

/// <summary>
/// Serializes transport changes on a dedicated background thread. Playback
/// requests already own isolated SongDocument snapshots when they enter this
/// controller, so source construction cannot observe later authoring edits.
/// </summary>
public sealed class BackgroundPlaybackController
	: IDisposable
{
	private abstract record Command(
		TaskCompletionSource Completion);

	private sealed record PlayCommand(
		PlaybackRequest Request,
		TaskCompletionSource Completion)
		: Command(Completion);

	private sealed record LiveEventCommand(
		ChannelTarget Target,
		IReadOnlyList<NoteCommand> Commands,
		TaskCompletionSource Completion)
		: Command(Completion);

	private sealed record StopCommand(
		TaskCompletionSource Completion)
		: Command(Completion);

	private sealed record ShutdownCommand(
		TaskCompletionSource Completion)
		: Command(Completion);

	private sealed record AudioSession(long Id, IAudioOutputSession? Session);

	private readonly object _gate = new();
	private readonly IAudioOutputBackend _backend;
	private readonly IBackgroundPlaybackSourceFactory _sourceFactory;
	private readonly BlockingCollection<Command> _commands = new();
	private readonly Thread _worker;

	private IAudioOutputSession? _session;
	private AudioSession _audioSession = new(0, null);
	private long _nextAudioSessionId;
	private IAudioOutputSource? _source;
	private bool _disposed;

	public BackgroundPlaybackController(
		IAudioOutputBackend backend,
		IBackgroundPlaybackSourceFactory sourceFactory)
	{
		_backend =
			backend
				?? throw new ArgumentNullException(nameof(backend));
		_sourceFactory =
			sourceFactory
				?? throw new ArgumentNullException(nameof(sourceFactory));

		_worker =
			new Thread(WorkerLoop)
			{
				IsBackground = true,
				Name = "Heresy Playback",
			};
		_worker.Start();
	}

	/// <summary>
	/// Readable on a transport timer thread. The SDL callback only updates
	/// atomic counters/faults; subscribers and UI are never invoked there.
	/// </summary>
	public PlaybackAudioHealthSnapshot GetAudioHealth()
	{
		AudioSession state = Volatile.Read(ref _audioSession);
		IAudioOutputSession? session = state.Session;
		return new PlaybackAudioHealthSnapshot(
			state.Id,
			session is not null,
			session is IAudioOutputUnderrunCounter counter
				? counter.UnderrunCount : 0,
			session?.Fault);
	}

	public Task PlayAsync(PlaybackRequest request)
	{
		ArgumentNullException.ThrowIfNull(request);

		TaskCompletionSource completion =
			NewCompletion();
		Enqueue(
			new PlayCommand(
				request,
				completion));
		return completion.Task;
	}

	public Task SendLiveEventAsync(
		ChannelTarget target,
		IReadOnlyList<NoteCommand> commands)
	{
		ArgumentNullException.ThrowIfNull(commands);

		TaskCompletionSource completion =
			NewCompletion();
		Enqueue(
			new LiveEventCommand(
				target,
				commands,
				completion));
		return completion.Task;
	}

	public Task StopAsync()
	{
		TaskCompletionSource completion =
			NewCompletion();
		Enqueue(
			new StopCommand(completion));
		return completion.Task;
	}

	public void Dispose()
	{
		TaskCompletionSource completion;
		lock (_gate)
		{
			if (_disposed)
				return;

			_disposed = true;
			completion = NewCompletion();
			_commands.Add(
				new ShutdownCommand(completion));
		}

		completion.Task.GetAwaiter().GetResult();
		_worker.Join();
		_commands.Dispose();
		GC.SuppressFinalize(this);
	}

	private void Enqueue(Command command)
	{
		lock (_gate)
		{
			if (_disposed)
				throw new ObjectDisposedException(
					nameof(BackgroundPlaybackController));

			_commands.Add(command);
		}
	}

	private void WorkerLoop()
	{
		foreach (Command command in _commands.GetConsumingEnumerable())
		{
			try
			{
				switch (command)
				{
					case PlayCommand play:
						ReplacePlayback(play.Request);
						command.Completion.SetResult();
						break;

					case LiveEventCommand live:
						SendLiveEvent(
							live.Target,
							live.Commands);
						command.Completion.SetResult();
						break;

					case StopCommand:
						StopCurrent();
						command.Completion.SetResult();
						break;

					case ShutdownCommand:
						StopCurrent();
						_backend.Dispose();
						command.Completion.SetResult();
						return;

					default:
						throw new InvalidOperationException(
							$"Unsupported playback command {command.GetType().Name}.");
				}
			}
			catch (Exception ex)
			{
				command.Completion.SetException(ex);
				if (command is ShutdownCommand)
					return;
			}
		}
	}

	private void ReplacePlayback(
		PlaybackRequest request)
	{
		StopCurrent();

		IAudioOutputSource source = _sourceFactory.Create(request);
		IAudioOutputSession session;
		try
		{
			session = _backend.Open(source.Format, source);
		}
		catch
		{
			(source as IDisposable)?.Dispose();
			throw;
		}

		try
		{
			session.Start();
			_source = source;
			_session = session;
			Volatile.Write(ref _audioSession, new AudioSession(
				Interlocked.Increment(ref _nextAudioSessionId), session));
		}
		catch
		{
			session.Dispose();
			(source as IDisposable)?.Dispose();
			throw;
		}
	}

	private void SendLiveEvent(
		ChannelTarget target,
		IReadOnlyList<NoteCommand> commands)
	{
		if (_source is not ILiveAudioOutputSource liveSource)
		{
			throw new InvalidOperationException(
				"The active playback source does not accept live events.");
		}

		liveSource.EnqueueLiveEvent(
			target,
			commands);
	}

	private void StopCurrent()
	{
		// Withdraw the session before disposal so transport observers cannot
		// confuse a stopped/replaced session with the next one.
		Volatile.Write(ref _audioSession, new AudioSession(
			Interlocked.Increment(ref _nextAudioSessionId), null));
		IAudioOutputSession? session = _session;
		IAudioOutputSource? source = _source;
		_session = null;
		_source = null;
		try
		{
			if (session is not null)
			{
				try { session.Stop(); }
				finally { session.Dispose(); }
			}
		}
		finally
		{
			(source as IDisposable)?.Dispose();
		}
	}

	private static TaskCompletionSource NewCompletion()
		=> new(
			TaskCreationOptions.RunContinuationsAsynchronously);
}
