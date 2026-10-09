using System;

using Heresy.Render.Realtime;
using Heresy.Render.Timing;

namespace Heresy.Playback;

/// <summary>
/// Dedicated single-threaded sequencing + PCM rendering of a recursive
/// incremental source into a bounded SPSC ring. The system audio callback
/// never executes a Pattern, a script, a child session or a PCM renderer.
/// </summary>
/// <remarks>
/// This adapter uses the existing incremental event processor on the *same*
/// worker that renders its PCM; the staged event queue is no longer a
/// producer/consumer boundary. The SDL backend also buffers other sources.
/// The caller must stop its output callbacks before disposing the worker.
/// </remarks>
public sealed class AsyncPreparedIncrementalAudioSource : IAudioOutputSource, IDisposable
{
	private sealed class SynchronousRenderingSource : IAudioOutputSource
	{
		private readonly PreparedIncrementalAudioSource _source;
		public SynchronousRenderingSource(PreparedIncrementalAudioSource source)
		{
			_source = source;
			Format = source.Format;
		}
		public AudioOutputFormat Format { get; }
		public void Render(int frameCount, Span<float> destination)
		{
			// Both operations occur on the same dedicated PCM rendering thread.
			// Scripts and nested mixdown generators are never invoked by SDL.
			long end = checked(_source.NextFrame + frameCount);
			_source.PrepareThrough(
				FrameTime.FrameStartTime(end, Format.SampleRate));
			_source.Render(frameCount, destination);
		}
	}

	private readonly BufferedAudioOutputSource _ring;
	private readonly int _lookaheadFrames;
	private bool _disposed;

	public AsyncPreparedIncrementalAudioSource(
		PreparedIncrementalAudioSource source, int lookaheadFrames)
	{
		ArgumentNullException.ThrowIfNull(source);
		if (lookaheadFrames <= 0)
			throw new ArgumentOutOfRangeException(nameof(lookaheadFrames));
		_lookaheadFrames = lookaheadFrames;
		Format = source.Format;
		_ring = new BufferedAudioOutputSource(
			new SynchronousRenderingSource(source),
			capacityFrames: lookaheadFrames,
			blockFrames: Math.Min(256, lookaheadFrames));
	}

	public AudioOutputFormat Format { get; }
	public int LookaheadFrames => _lookaheadFrames;
	public long UnderrunCount => _ring.UnderrunCount;
	public Exception? PreparationError => _ring.RenderingFault;
	public long BufferedFrames => _ring.BufferedFrames;

	/// <summary>
	/// Callback path: consume prepared PCM, substituting silence on underrun
	/// without advancing the source. No sequencing or rendering here.
	/// </summary>
	public void Render(int frameCount, Span<float> destination)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (frameCount < 0 || frameCount > _lookaheadFrames)
			throw new ArgumentOutOfRangeException(nameof(frameCount),
				"A callback request must fit within the PCM ring capacity.");
		_ring.Render(frameCount, destination);
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		_ring.Dispose();
	}
}
