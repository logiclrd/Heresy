using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

using Heresy.Render.Realtime;

namespace Heresy.Render.File;

/// <summary>
/// Classic RIFF/WAVE PCM writer. The first file-rendering slice deliberately
/// supports 16-bit integer PCM only; later encoded sinks share IAudioFileSink.
/// </summary>
public sealed class WaveFileSink : IAudioFileSink
{
	private const int HeaderSize = 44;
	private const ushort PcmFormatTag = 1;
	private const ushort BitsPerSample = 16;

	private readonly Stream _stream;
	private readonly bool _leaveOpen;
	private readonly long _headerStart;
	private bool _completed;
	private bool _disposed;
	private long _dataBytes;

	public WaveFileSink(
		Stream stream,
		AudioOutputFormat format,
		bool leaveOpen = false)
	{
		_stream =
			stream
				?? throw new ArgumentNullException(nameof(stream));
		if (!stream.CanWrite)
			throw new ArgumentException(
				"The WAVE destination stream must be writable.",
				nameof(stream));
		if (!stream.CanSeek)
			throw new ArgumentException(
				"The WAVE destination stream must be seekable so RIFF sizes can be finalized.",
				nameof(stream));
		if (format.ChannelCount > ushort.MaxValue)
			throw new ArgumentOutOfRangeException(
				nameof(format),
				"WAVE PCM channel count exceeds the RIFF field width.");

		long blockAlign =
			checked(
				format.ChannelCount
					* (BitsPerSample / 8));
		if (blockAlign > ushort.MaxValue)
			throw new ArgumentOutOfRangeException(
				nameof(format),
				"WAVE PCM block alignment exceeds the RIFF field width.");

		long byteRate =
			checked(
				(long)format.SampleRate
					* blockAlign);
		if (byteRate > uint.MaxValue)
			throw new ArgumentOutOfRangeException(
				nameof(format),
				"WAVE PCM byte rate exceeds the RIFF field width.");

		Format = format;
		_leaveOpen = leaveOpen;
		_headerStart = stream.Position;
		WriteHeader(
			dataBytes: 0);
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

		long additionalBytes =
			checked(
				(long)interleavedSamples.Length
					* sizeof(short));
		long newDataBytes =
			checked(
				_dataBytes
					+ additionalBytes);
		if (newDataBytes > uint.MaxValue)
		{
			throw new InvalidOperationException(
				"Classic RIFF/WAVE PCM data may not exceed 4 GiB.");
		}

		byte[] encoded =
			new byte[
				checked(
					interleavedSamples.Length
						* sizeof(short))];

		for (int index = 0;
			index < interleavedSamples.Length;
			index++)
		{
			float value =
				interleavedSamples[index];
			if (!float.IsFinite(value))
			{
				throw new InvalidOperationException(
					"Offline rendering produced a non-finite PCM sample.");
			}

			int quantized =
				value <= -1.0f
					? short.MinValue
					: value >= 1.0f
						? short.MaxValue
						: checked(
							(int)Math.Round(
								value
									* 32768.0,
								MidpointRounding.AwayFromZero));
			quantized =
				Math.Clamp(
					quantized,
					short.MinValue,
					short.MaxValue);

			BinaryPrimitives.WriteInt16LittleEndian(
				encoded.AsSpan(
					index * sizeof(short),
					sizeof(short)),
				(short)quantized);
		}

		_stream.Write(encoded);
		_dataBytes = newDataBytes;
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

		long returnPosition =
			_stream.Position;
		WriteHeader(
			_dataBytes);
		_stream.Position =
			returnPosition;
		_stream.Flush();
		_completed = true;
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

	private void WriteHeader(
		long dataBytes)
	{
		if (dataBytes < 0
			|| dataBytes > uint.MaxValue)
		{
			throw new ArgumentOutOfRangeException(
				nameof(dataBytes));
		}

		long blockAlign =
			Format.ChannelCount
				* (BitsPerSample / 8);
		long byteRate =
			(long)Format.SampleRate
				* blockAlign;
		long riffSize =
			36
				+ dataBytes;
		if (riffSize > uint.MaxValue)
		{
			throw new InvalidOperationException(
				"Classic RIFF/WAVE size exceeds 4 GiB.");
		}

		Span<byte> header =
			stackalloc byte[HeaderSize];
		Encoding.ASCII.GetBytes(
			"RIFF",
			header[..4]);
		BinaryPrimitives.WriteUInt32LittleEndian(
			header.Slice(4, 4),
			(uint)riffSize);
		Encoding.ASCII.GetBytes(
			"WAVE",
			header.Slice(8, 4));
		Encoding.ASCII.GetBytes(
			"fmt ",
			header.Slice(12, 4));
		BinaryPrimitives.WriteUInt32LittleEndian(
			header.Slice(16, 4),
			16);
		BinaryPrimitives.WriteUInt16LittleEndian(
			header.Slice(20, 2),
			PcmFormatTag);
		BinaryPrimitives.WriteUInt16LittleEndian(
			header.Slice(22, 2),
			(ushort)Format.ChannelCount);
		BinaryPrimitives.WriteUInt32LittleEndian(
			header.Slice(24, 4),
			(uint)Format.SampleRate);
		BinaryPrimitives.WriteUInt32LittleEndian(
			header.Slice(28, 4),
			(uint)byteRate);
		BinaryPrimitives.WriteUInt16LittleEndian(
			header.Slice(32, 2),
			(ushort)blockAlign);
		BinaryPrimitives.WriteUInt16LittleEndian(
			header.Slice(34, 2),
			BitsPerSample);
		Encoding.ASCII.GetBytes(
			"data",
			header.Slice(36, 4));
		BinaryPrimitives.WriteUInt32LittleEndian(
			header.Slice(40, 4),
			(uint)dataBytes);

		_stream.Position =
			_headerStart;
		_stream.Write(header);
	}

	private void ThrowIfUnavailable()
	{
		ThrowIfDisposed();
		if (_completed)
		{
			throw new InvalidOperationException(
				"The WAVE sink has already been completed.");
		}
	}

	private void ThrowIfDisposed()
	{
		if (_disposed)
			throw new ObjectDisposedException(nameof(WaveFileSink));
	}
}
