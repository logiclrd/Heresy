using System;
using System.IO;

using Heresy.Render.Realtime;

using NAudio.SoundFile;
using NAudio.Wave;

namespace Heresy.Render.File;

/// <summary>
/// Streaming FLAC sink backed by libsndfile/libFLAC through NAudio.SoundFile.
/// Heresy owns only the sink contract, PCM validation, and stream lifetime;
/// codec framing, prediction, residual coding, and compression decisions remain
/// entirely inside the mature native codec stack.
/// </summary>
public sealed class FlacFileSink : IAudioFileSink
{
	private readonly Stream _stream;
	private readonly bool _leaveOpen;
	private SoundFileWriter? _writer;
	private bool _completed;
	private bool _disposed;

	public FlacFileSink(
		Stream stream,
		AudioOutputFormat format,
		bool leaveOpen = false)
	{
		_stream =
			stream
				?? throw new ArgumentNullException(
					nameof(stream));
		if (!stream.CanWrite)
		{
			throw new ArgumentException(
				"The FLAC destination stream must be writable.",
				nameof(stream));
		}
		if (format.ChannelCount > 8)
		{
			throw new ArgumentOutOfRangeException(
				nameof(format),
				"FLAC supports at most eight audio channels.");
		}

		Format = format;
		_leaveOpen = leaveOpen;

		WaveFormat sourceFormat =
			WaveFormat.CreateIeeeFloatWaveFormat(
				format.SampleRate,
				format.ChannelCount);
		_writer =
			new SoundFileWriter(
				stream,
				sourceFormat,
				SoundFileMajorFormat.Flac,
				new SoundFileWriterOptions
				{
					Subtype =
						SoundFileSubtype.Pcm16,
					Clipping = true,
				});
	}

	public AudioOutputFormat Format { get; }

	public long FramesWritten { get; private set; }

	public void Write(
		ReadOnlySpan<float> interleavedSamples)
	{
		ThrowIfUnavailable();
		if (interleavedSamples.Length
			% Format.ChannelCount != 0)
		{
			throw new ArgumentException(
				"Interleaved PCM length must be a whole number of output frames.",
				nameof(interleavedSamples));
		}

		foreach (float sample in interleavedSamples)
		{
			if (!float.IsFinite(sample))
			{
				throw new InvalidOperationException(
					"Offline rendering produced a non-finite PCM sample.");
			}
		}

		if (interleavedSamples.IsEmpty)
			return;

		SoundFileWriter writer =
			_writer
				?? throw new InvalidOperationException(
					"The FLAC sink has already been completed.");
		writer.WriteSamples(
			interleavedSamples);
		FramesWritten =
			checked(
				FramesWritten
					+ interleavedSamples.Length
						/ Format.ChannelCount);
	}

	public void Complete()
	{
		ThrowIfDisposed();
		if (_completed)
			return;

		SoundFileWriter? writer = _writer;
		_writer = null;
		try
		{
			writer?.Dispose();
			_stream.Flush();
			_completed = true;
		}
		catch
		{
			_completed = true;
			throw;
		}
	}

	public void Dispose()
	{
		if (_disposed)
			return;

		try
		{
			if (!_completed)
				Complete();
		}
		finally
		{
			_disposed = true;
			if (!_leaveOpen)
				_stream.Dispose();
		}

		GC.SuppressFinalize(this);
	}

	private void ThrowIfUnavailable()
	{
		ThrowIfDisposed();
		if (_completed)
		{
			throw new InvalidOperationException(
				"The FLAC sink has already been completed.");
		}
	}

	private void ThrowIfDisposed()
	{
		if (_disposed)
			throw new ObjectDisposedException(nameof(FlacFileSink));
	}
}
