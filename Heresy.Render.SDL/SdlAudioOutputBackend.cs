using System;
using System.Collections.Generic;

using Heresy.Render.Realtime;

using SDL3;

namespace Heresy.Render.SDL;

public sealed class SdlAudioOutputBackend
	: IAudioOutputBackend
{
	private readonly object _gate = new();
	private readonly HashSet<SdlAudioOutputSession> _sessions = [];
	private bool _disposed;

	public SdlAudioOutputBackend()
	{
		if (!SDL.InitSubSystem(SDL.InitFlags.Audio))
		{
			throw new InvalidOperationException(
				$"SDL audio initialization failed: {SDL.GetError()}");
		}
	}

	public IAudioOutputSession Open(
		AudioOutputFormat format,
		IAudioOutputSource source)
	{
		ArgumentNullException.ThrowIfNull(source);

		lock (_gate)
		{
			ThrowIfDisposed();

			if (source.Format != format)
			{
				throw new ArgumentException(
					"Audio source format must match the requested output format.",
					nameof(source));
			}

			SdlAudioOutputSession session =
				new(
					format,
					source,
					OnSessionDisposed);
			_sessions.Add(session);
			return session;
		}
	}

	public void Dispose()
	{
		SdlAudioOutputSession[] sessions;
		lock (_gate)
		{
			if (_disposed)
				return;

			_disposed = true;
			sessions = [.. _sessions];
			_sessions.Clear();
		}

		foreach (SdlAudioOutputSession session in sessions)
			session.Dispose();

		SDL.QuitSubSystem(SDL.InitFlags.Audio);
		GC.SuppressFinalize(this);
	}

	private void OnSessionDisposed(
		SdlAudioOutputSession session)
	{
		lock (_gate)
			_sessions.Remove(session);
	}

	private void ThrowIfDisposed()
	{
		if (_disposed)
			throw new ObjectDisposedException(nameof(SdlAudioOutputBackend));
	}
}
