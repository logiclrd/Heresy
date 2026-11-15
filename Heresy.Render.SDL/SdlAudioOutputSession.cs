using System;
using System.Buffers;
using System.Diagnostics;
using System.Threading;

using Heresy.Render.Realtime;

using SdlApi = SDL3.SDL;

namespace Heresy.Render.SDL;

internal sealed class SdlAudioOutputSession
	: IAudioOutputSession, IAudioOutputUnderrunCounter
{
	private readonly object _gate = new();
	private readonly BufferedAudioOutputSource _source;
	private readonly Action<SdlAudioOutputSession> _onDisposed;
	private readonly SdlApi.AudioStreamCallback _callback;
	private readonly AudioOutputWaveformCapture? _waveformCapture;
	private readonly AudioOutputWaveformCapture.Session? _captureSession;
	private readonly AudioDeviceQualificationCsvRecorder? _qualificationRecorder;
	private long _callbackCount;
	private long _callbackRequestedFrames;
	private long _callbackPeakRequestedFrames;
	private long _callbackPeakTicks;
	private long _callbackOverNominal;

	private IntPtr _stream;
	private Exception? _fault;
	private bool _isRunning;
	private bool _disposed;

	public SdlAudioOutputSession(
		AudioOutputFormat format,
		IAudioOutputSource source,
		Action<SdlAudioOutputSession> onDisposed,
		AudioOutputWaveformCapture? waveformCapture = null)
	{
		Format = format;
		ArgumentNullException.ThrowIfNull(source);
		_onDisposed =
			onDisposed
				?? throw new ArgumentNullException(nameof(onDisposed));
		_callback = FeedAudio;

		SdlApi.AudioSpec spec =
			new()
			{
				Channels = format.ChannelCount,
				Format =
					BitConverter.IsLittleEndian
						? SdlApi.AudioFormat.AudioF32LE
						: SdlApi.AudioFormat.AudioF32BE,
				Freq = format.SampleRate,
			};

		_stream =
			SdlApi.OpenAudioDeviceStream(
				SdlApi.AudioDeviceDefaultPlayback,
				in spec,
				_callback,
				IntPtr.Zero);
		if (_stream == IntPtr.Zero)
		{
			throw new InvalidOperationException(
				$"SDL could not open the default playback device: {SdlApi.GetError()}");
		}

		// The SDL callback never calls a sequencer or renderer. A dedicated
		// worker owns the source and publishes bounded interleaved PCM.
		// Qualification CSV is strictly opt-in. Enable producer timestamps
		// only for these manual device runs, never for normal playback.
		string? csvPath = Environment.GetEnvironmentVariable(
			AudioDeviceQualificationCsvRecorder.EnvironmentVariable);
		bool qualify = !string.IsNullOrWhiteSpace(csvPath);
		_source = source as BufferedAudioOutputSource
			?? new BufferedAudioOutputSource(source,
				profileProducerRender: qualify);
		_waveformCapture = waveformCapture;
		_captureSession = waveformCapture?.BeginSession(format);
		if (qualify)
		{
			try
			{
				_qualificationRecorder = new AudioDeviceQualificationCsvRecorder(
					csvPath!, _source, ObserveCallbacks, () => Fault);
			}
			catch (Exception error) when (error is System.IO.IOException
				or UnauthorizedAccessException or ArgumentException
				or NotSupportedException)
			{
				Console.Error.WriteLine(
					$"C5(e) CSV recording could not start: {error.Message}");
			}
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

	public long UnderrunCount => _source.UnderrunCount;

	public Exception? Fault =>
		Volatile.Read(ref _fault) ?? _source.RenderingFault;

	public void Start()
	{
		lock (_gate)
		{
			ThrowIfDisposed();
			if (_isRunning)
				return;

			if (!SdlApi.ResumeAudioStreamDevice(_stream))
			{
				throw new InvalidOperationException(
					$"SDL could not resume audio playback: {SdlApi.GetError()}");
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

			if (!SdlApi.PauseAudioStreamDevice(_stream))
			{
				throw new InvalidOperationException(
					$"SDL could not pause audio playback: {SdlApi.GetError()}");
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
			SdlApi.DestroyAudioStream(stream);
		if (_captureSession is { } capture)
			_waveformCapture!.EndSession(capture);
		_qualificationRecorder?.Dispose();
		_source.Dispose();

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

		// A few atomic counters and timestamps are enabled only in the
		// C5(e) opt-in mode. Never log, allocate, or invoke UI here.
		bool profile = _qualificationRecorder is not null;
		long began = profile ? Stopwatch.GetTimestamp() : 0;
		int requestedFrames = 0;
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
			requestedFrames = frameCount;
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

				if (!SdlApi.PutAudioStreamData(
						audioStream,
						bytes,
						byteCount))
				{
					SetFault(
						new InvalidOperationException(
							$"SDL could not queue audio data: {SdlApi.GetError()}"));
				}
				else if (_captureSession is { } capture)
				{
					// This is the actual post-mix/filter/underrun-pad PCM that
					// SDL accepted, not an earlier synthesized source buffer.
					_waveformCapture!.Publish(capture, destination);
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
		finally
		{
			if (profile)
				RecordCallback(requestedFrames,
					Stopwatch.GetTimestamp() - began);
		}
	}

	private AudioDeviceCallbackObservation ObserveCallbacks() => new(
		Interlocked.Read(ref _callbackCount),
		Interlocked.Read(ref _callbackRequestedFrames),
		Interlocked.Read(ref _callbackPeakRequestedFrames),
		Interlocked.Read(ref _callbackPeakTicks),
		Interlocked.Read(ref _callbackOverNominal));

	private void RecordCallback(int frames, long elapsedTicks)
	{
		Interlocked.Increment(ref _callbackCount);
		Interlocked.Add(ref _callbackRequestedFrames, frames);
		UpdateMaximum(ref _callbackPeakRequestedFrames, frames);
		UpdateMaximum(ref _callbackPeakTicks, elapsedTicks);
		if (frames > 0 && (double)elapsedTicks / Stopwatch.Frequency
			> (double)frames / Format.SampleRate)
			Interlocked.Increment(ref _callbackOverNominal);
	}

	private static void UpdateMaximum(ref long target, long measured)
	{
		long prior;
		while ((prior = Interlocked.Read(ref target)) < measured
			&& Interlocked.CompareExchange(
				ref target, measured, prior) != prior)
		{
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
