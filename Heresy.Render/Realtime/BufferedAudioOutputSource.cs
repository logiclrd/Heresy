using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Heresy.Core.Sequencing;

namespace Heresy.Render.Realtime;

/// <summary>
/// Single-producer/single-consumer bounded PCM ring. A dedicated worker owns
/// sequencing, scripts, recursive instruments and all source.Render calls.
/// System audio callbacks only copy already rendered interleaved float PCM.
/// Silence on underrun never advances the rendering source's musical clock.
/// </summary>
public sealed class BufferedAudioOutputSource : ILiveAudioOutputSource, IDisposable
{
	private readonly IAudioOutputSource _source;
	private readonly float[] _samples;
	private readonly int _capacityFrames;
	private readonly int _blockFrames;
	private readonly AutoResetEvent _wake = new(false);
	private readonly Thread _worker;
	private long _readFrames;
	private long _writtenFrames;
	private long _underruns;
	private Exception? _fault;
	private int _disposed;

	public BufferedAudioOutputSource(
		IAudioOutputSource source,
		int capacityFrames = 2048,
		int blockFrames = 256)
	{
		_source = source ?? throw new ArgumentNullException(nameof(source));
		if (capacityFrames <= 0)
			throw new ArgumentOutOfRangeException(nameof(capacityFrames));
		if (blockFrames <= 0 || blockFrames > capacityFrames)
			throw new ArgumentOutOfRangeException(nameof(blockFrames));
		Format = source.Format;
		_capacityFrames = capacityFrames;
		_blockFrames = blockFrames;
		_samples = new float[checked(capacityFrames * Format.ChannelCount)];
		_worker = new Thread(Produce)
		{
			IsBackground = true,
			Name = "Heresy PCM rendering",
		};
		_worker.Start();
	}

	public AudioOutputFormat Format { get; }
	public int CapacityFrames => _capacityFrames;
	public int RenderBlockFrames => _blockFrames;
	public long BufferedFrames =>
		Volatile.Read(ref _writtenFrames) - Volatile.Read(ref _readFrames);
	public long UnderrunCount => Interlocked.Read(ref _underruns);
	public Exception? RenderingFault => Volatile.Read(ref _fault);

	/// <summary>
	/// The source's own live-event input must be a thread-safe command queue.
	/// The queued commands are applied by its rendering worker, never here.
	/// </summary>
	public void EnqueueLiveEvent(
		ChannelTarget target,
		IReadOnlyList<NoteCommand> commands)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
		if (_source is not ILiveAudioOutputSource live)
			throw new InvalidOperationException("The underlying playback source does not support live events.");
		ArgumentNullException.ThrowIfNull(commands);
		live.EnqueueLiveEvent(target, commands.ToArray());
		_wake.Set();
	}

	/// <summary>
	/// Callback-only path: copy available PCM, pad a short read with silence
	/// and report an underrun. No wait, lock, allocation, script or synthesis.
	/// A short read consumes only the frames that actually existed.
	/// </summary>
	public void Render(int frameCount, Span<float> destination)
	{
		if (frameCount < 0)
			throw new ArgumentOutOfRangeException(nameof(frameCount));
		if (destination.Length != checked(frameCount * Format.ChannelCount))
			throw new ArgumentException(
				"Destination length must match interleaved frame count.",
				nameof(destination));
		if (frameCount == 0)
			return;
		if (Volatile.Read(ref _disposed) != 0)
		{
			destination.Clear();
			return;
		}

		long read = Volatile.Read(ref _readFrames);
		long written = Volatile.Read(ref _writtenFrames);
		int available = checked((int)Math.Min(frameCount, written - read));
		int channels = Format.ChannelCount;
		int first = (int)(read % _capacityFrames);
		int firstFrames = Math.Min(available, _capacityFrames - first);
		if (firstFrames > 0)
			_samples.AsSpan(first * channels, firstFrames * channels)
				.CopyTo(destination);
		if (available > firstFrames)
			_samples.AsSpan(0, (available - firstFrames) * channels)
				.CopyTo(destination.Slice(firstFrames * channels));
		if (available < frameCount)
		{
			destination.Slice(available * channels).Clear();
			Interlocked.Increment(ref _underruns);
		}
		if (available > 0)
			Volatile.Write(ref _readFrames, read + available);
		_wake.Set();
	}

	private void Produce()
	{
		while (Volatile.Read(ref _disposed) == 0)
		{
			long read = Volatile.Read(ref _readFrames);
			long written = Volatile.Read(ref _writtenFrames);
			long free = _capacityFrames - (written - read);
			if (free == 0)
			{
				_wake.WaitOne();
				continue;
			}
			int position = (int)(written % _capacityFrames);
			int count = (int)Math.Min(_blockFrames,
				Math.Min(free, _capacityFrames - position));
			try
			{
				// The ring reuses physical storage. Every source must receive a
				// clean destination even when it renders additively.
				Span<float> output = _samples.AsSpan(
					position * Format.ChannelCount, count * Format.ChannelCount);
				output.Clear();
				_source.Render(count, output);
			}
			catch (Exception error)
			{
				Volatile.Write(ref _fault, error);
				return;
			}
			Volatile.Write(ref _writtenFrames, written + count);
		}
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
			return;
		_wake.Set();
		if (Thread.CurrentThread == _worker)
			throw new InvalidOperationException(
				"A PCM rendering worker cannot dispose itself.");
		_worker.Join();
		_wake.Dispose();
	}
}
