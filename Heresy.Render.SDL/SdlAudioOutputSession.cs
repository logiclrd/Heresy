using System;
using System.Buffers;
using System.Threading;

using Heresy.Render.Realtime;

using SDL3;

namespace Heresy.Render.SDL;

internal sealed class SdlAudioOutputSession
	: IAudioOutputSession
{
	private readonly object _gate = new();
	private readonly IAudioOutputSource _source;
	private readonly Action<SdlAudioOutputSession> _onDisposed;
	private readonly SDL.AudioStreamCallback _callback;

	private IntPtr _stream;
	private Exception? _fault;
	private bool _isRunning;
	private bool _disposed;

	public SdlAudioOutputSession(
		AudioOutputFormat format,
		IAudioOutputSource source,
		Action<SdlAudioOutputSession> onDisposed)
	{
		Format = format;
		_source = source ?? throw new ArgumentNullException(nameof(source));
		_onDisposed =
			onDisposed
				?? throw new ArgumentNullException(nameof(onDisposed));
		_callback = FeedAudio;

		SDL.AudioSpec spec =
			new()
			{
				Channels = format.ChannelCount,
				Format =
					BitConverter.IsLittleEndian
						? SDL.AudioFormat.AudioF32LE
						: SDL.AudioFormat.AudioF32BE,
				Freq = format.SampleRate,
			};

		_stream =
			SDL.OpenAudioDeviceStream(
				SDL.AudioDeviceDefaultPlayback,
				in spec,
				_callback,
				IntPtr.Zero);
		if (_stream == IntPtr.Zero)
		{
			throw new InvalidOperationException(
				$"SDL could not open the default playback device: {SDL.GetError()}");
		}
	}

	public AudioOutputFormat Format { get; }

	public bool IsRunning
	{
		get
		{
			lock (_gate)
				return _isRunning;
		}
	}

	public Exception? Fault =>
		Volatile.Read(ref _fault);

	public void Start()
	{
		lock (_gate)
		{
			ThrowIfDisposed();
			if (_isRunning)
				return;

			if (!SDL.ResumeAudioStreamDevice(_stream))
			{
				throw new InvalidOperationException(
					$"SDL could not resume audio playback: {SDL.GetError()}");
			}

			_isRunning = true;
		}
	}

	public void Stop()
	{
		lock (_gate)
		{
			ThrowIfDisposed();
			if (!_isRunning)
				return;

			if (!SDL.PauseAudioStreamDevice(_stream))
			{
				throw new InvalidOperationException(
					$"SDL could not pause audio playback: {SDL.GetError()}");
			}

			_isRunning = false;
		}
	}

	public void Dispose()
	{
		IntPtr stream;
		lock (_gate)
		{
			if (_disposed)
				return;

			_disposed = true;
			_isRunning = false;
			stream = _stream;
			_stream = IntPtr.Zero;
		}

		if (stream != IntPtr.Zero)
			SDL.DestroyAudioStream(stream);

		_onDisposed(this);
		GC.SuppressFinalize(this);
	}

	private void FeedAudio(
		IntPtr userdata,
		IntPtr audioStream,
		int additionalAmount,
		int totalAmount)
	{
		_ = userdata;
		_ = totalAmount;

		if (additionalAmount <= 0)
			return;

		try
		{
			int bytesPerFrame =
				checked(
					Format.ChannelCount
						* sizeof(float));
			int frameCount =
				checked(
					(additionalAmount
						+ bytesPerFrame
						- 1)
					/ bytesPerFrame);
			int sampleCount =
				checked(
					frameCount
						* Format.ChannelCount);
			int byteCount =
				checked(
					sampleCount
						* sizeof(float));

			float[] samples =
				ArrayPool<float>.Shared.Rent(sampleCount);
			byte[] bytes =
				ArrayPool<byte>.Shared.Rent(byteCount);
			try
			{
				Span<float> destination =
					samples.AsSpan(
						0,
						sampleCount);

				if (Fault is null)
				RenderOrSilence(destination, frameCount);
				else
					destination.Clear();

				Buffer.BlockCopy(
					samples,
					0,
					bytes,
					0,
					byteCount);

				if (!SDL.PutAudioStreamData(
						audioStream,
						bytes,
						byteCount))
				{
					SetFault(
						new InvalidOperationException(
							$"SDL could not queue audio data: {SDL.GetError()}"));
				}
			}
			finally
			{
				ArrayPool<byte>.Shared.Return(bytes);
				ArrayPool<float>.Shared.Return(samples);
			}
		}
		catch (Exception ex)
		{
			SetFault(ex);
		}
	}

	private void RenderOrSilence(
		Span<float> destination,
		int frameCount)
	{
		try
		{
			_source.Render(
				frameCount,
				destination);
		}
		catch (Exception ex)
		{
			destination.Clear();
			SetFault(ex);
		}
	}

	private void SetFault(Exception exception)
	{
		ArgumentNullException.ThrowIfNull(exception);
		Interlocked.CompareExchange(
			ref _fault,
			exception,
			null);
	}

	private void ThrowIfDisposed()
	{
		if (_disposed)
			throw new ObjectDisposedException(nameof(SdlAudioOutputSession));
	}
}
