using System;
using System.Threading;

using Heresy.Render.Realtime;
using Heresy.Render.Timing;

namespace Heresy.Playback;

/// <summary>
/// Experimental bounded, asynchronous preparation in front of a
/// sample-accurate incremental renderer. Only the worker executes
/// source generation; the audio consumer never waits for it.
/// </summary>
/// <remarks>
/// A missing whole-block preparation horizon produces a silent output block,
/// leaves the musical playback frame unchanged and increments UnderrunCount.
/// This is a deliberate, opt-in dropout policy, not production transport
/// integration. The caller must stop its audio callbacks before Dispose.
/// Explicit invocation cancellation during lookahead is not yet supported.
/// </remarks>
public sealed class AsyncPreparedIncrementalAudioSource : IAudioOutputSource, IDisposable
{
	private readonly PreparedIncrementalAudioSource _source;
	private readonly int _lookaheadFrames;
	private readonly AutoResetEvent _wake = new(false);
	private readonly Thread _producer;
	private Exception? _preparationError;
	private long _playbackHead;
	private long _underrunCount;
	private int _disposed;

	public AsyncPreparedIncrementalAudioSource(
		PreparedIncrementalAudioSource source, int lookaheadFrames)
	{
		_source = source ?? throw new ArgumentNullException(nameof(source));
		if (lookaheadFrames <= 0)
			throw new ArgumentOutOfRangeException(nameof(lookaheadFrames));
		_lookaheadFrames = lookaheadFrames;
		_playbackHead = source.NextFrame;
		Format = source.Format;
		_producer = new Thread(PrepareLoop)
		{
			IsBackground = true,
			Name = "Heresy incremental lookahead",
		};
		_producer.Start();
	}

	public AudioOutputFormat Format { get; }

	public int LookaheadFrames => _lookaheadFrames;

	/// <summary>
	/// Number of whole callback blocks replaced with silence because
	/// they were not prepared. The first nonzero value is the first
	/// observed dropout. The timeline does not skip any musical frames.
	/// </summary>
	public long UnderrunCount => Interlocked.Read(ref _underrunCount);

	/// <summary>
	/// Producer exception, published for handling outside the audio callback.
	/// A failed producer stops preparing; missing blocks remain silent.
	/// </summary>
	public Exception? PreparationError => Volatile.Read(ref _preparationError);

	public void Render(int frameCount, Span<float> destination)
	{
		ObjectDisposedException.ThrowIf(
			Volatile.Read(ref _disposed) != 0, this);
		if (frameCount < 0 || frameCount > _lookaheadFrames)
			throw new ArgumentOutOfRangeException(nameof(frameCount),
				"A render block must fit within the bounded lookahead horizon.");
		int samples = checked(frameCount * Format.ChannelCount);
		if (destination.Length != samples)
			throw new ArgumentException(
				"Destination length must match the requested output frames.",
				nameof(destination));
		if (frameCount == 0)
			return;

		long head = Volatile.Read(ref _playbackHead);
		long end = checked(head + frameCount);
		long neededTicks = FrameTime.FrameStartTime(
			end, Format.SampleRate).Ticks;
		if (_source.IsPreparedToEnd
			|| _source.PreparedThrough.Ticks >= neededTicks)
		{
			_source.Render(frameCount, destination);
			Volatile.Write(ref _playbackHead, _source.NextFrame);
		}
		else
		{
			destination.Clear();
			Interlocked.Increment(ref _underrunCount);
		}
		// AutoResetEvent.Set is a non-waiting wake-up; no producer-side
		// locking, script evaluation, or backpressure on the PCM callback.
		_wake.Set();
	}

	private void PrepareLoop()
	{
		while (Volatile.Read(ref _disposed) == 0)
		{
			try
			{
				if (!_source.IsPreparedToEnd)
				{
					long head = Volatile.Read(ref _playbackHead);
					long end = checked(head + _lookaheadFrames);
					TimeSpan target = FrameTime.FrameStartTime(
						end, Format.SampleRate);
					if (_source.PreparedThrough < target)
					{
						_source.PrepareThrough(target);
						// The consumer may have advanced while preparing.
						// Re-evaluate its published head before sleeping.
						continue;
					}
				}
			}
			catch (Exception error)
			{
				Volatile.Write(ref _preparationError, error);
				return;
			}
			_wake.WaitOne();
		}
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
			return;
		_wake.Set();
		if (Thread.CurrentThread == _producer)
			throw new InvalidOperationException(
				"The preparation worker cannot dispose itself.");
		_producer.Join();
		_wake.Dispose();
	}
}
