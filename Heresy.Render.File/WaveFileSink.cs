using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

using Heresy.Render.Realtime;

namespace Heresy.Render.File;

/// <summary>
/// Classic RIFF/WAVE integer PCM writer. Mono/stereo use the canonical
/// 44-byte PCM header. Surround layouts use WAVEFORMATEXTENSIBLE with
/// a channel mask, so speaker feeds have an unambiguous interpretation.
/// RIFF size limits and even-byte data chunk padding are enforced.
/// </summary>
public sealed class WaveFileSink : IAudioFileSink
{
	private const ushort PcmFormatTag = 1;
	private const ushort ExtensibleFormatTag = 0xfffe;
	private const int BasicHeaderSize = 44;
	private const int SurroundHeaderSize = 68;

	private readonly Stream _stream;
	private readonly bool _leaveOpen;
	private readonly long _headerStart;
	private readonly int _headerSize;
	private readonly int _bytesPerSample;
	private bool _completed;
	private bool _disposed;
	private long _dataBytes;

	public WaveFileSink(
		Stream stream,
		AudioOutputFormat format,
		bool leaveOpen = false,
		WavePcmBitDepth bitDepth = WavePcmBitDepth.Pcm16)
	{
		_stream = stream ?? throw new ArgumentNullException(nameof(stream));
		if (!stream.CanWrite)
			throw new ArgumentException("The WAVE destination stream must be writable.",
				nameof(stream));
		if (!stream.CanSeek)
			throw new ArgumentException(
				"The WAVE destination stream must be seekable so RIFF sizes can be finalized.",
				nameof(stream));
		if (!IsSupportedBitDepth(bitDepth))
			throw new ArgumentOutOfRangeException(nameof(bitDepth));
		if (format.ChannelCount is not (1 or 2 or 6 or 8))
			throw new ArgumentOutOfRangeException(nameof(format),
				"WAV export supports mono, stereo, 5.1 or 7.1.");
		_bytesPerSample = (int)bitDepth / 8;
		long blockAlign = checked((long)format.ChannelCount * _bytesPerSample);
		if (blockAlign > ushort.MaxValue)
			throw new ArgumentOutOfRangeException(nameof(format),
				"WAVE PCM block alignment exceeds the RIFF field width.");
		long byteRate = checked((long)format.SampleRate * blockAlign);
		if (byteRate > uint.MaxValue)
			throw new ArgumentOutOfRangeException(nameof(format),
				"WAVE PCM byte rate exceeds the RIFF field width.");

		Format = format;
		BitDepth = bitDepth;
		_leaveOpen = leaveOpen;
		_headerSize = format.ChannelCount > 2
			? SurroundHeaderSize : BasicHeaderSize;
		_headerStart = stream.Position;
		WriteHeader(0);
	}

	public AudioOutputFormat Format { get; }
	public WavePcmBitDepth BitDepth { get; }
	public long FramesWritten { get; private set; }

	public void Write(ReadOnlySpan<float> interleavedSamples)
	{
		ThrowIfUnavailable();
		if (interleavedSamples.Length % Format.ChannelCount != 0)
			throw new ArgumentException(
				"Interleaved PCM length must be a whole number of output frames.",
				nameof(interleavedSamples));

		long added = checked((long)interleavedSamples.Length * _bytesPerSample);
		long newDataBytes = checked(_dataBytes + added);
		ValidateRiffSize(newDataBytes);
		byte[] encoded = new byte[checked(interleavedSamples.Length * _bytesPerSample)];
		for (int index = 0; index < interleavedSamples.Length; index++)
		{
			float sample = interleavedSamples[index];
			if (!float.IsFinite(sample))
				throw new InvalidOperationException(
					"Offline rendering produced a non-finite PCM sample.");

			int offset = checked(index * _bytesPerSample);
			if (BitDepth == WavePcmBitDepth.Pcm8)
			{
				long signed = Quantize(sample, 8);
				encoded[offset] = (byte)(signed + 128);
			}
			else if (BitDepth == WavePcmBitDepth.Pcm16)
			{
				BinaryPrimitives.WriteInt16LittleEndian(
					encoded.AsSpan(offset, 2), (short)Quantize(sample, 16));
			}
			else if (BitDepth == WavePcmBitDepth.Pcm24)
			{
				long quantized = Quantize(sample, 24);
				encoded[offset] = (byte)quantized;
				encoded[offset + 1] = (byte)(quantized >> 8);
				encoded[offset + 2] = (byte)(quantized >> 16);
			}
			else
			{
				BinaryPrimitives.WriteInt32LittleEndian(
					encoded.AsSpan(offset, 4), (int)Quantize(sample, 32));
			}
		}

		_stream.Write(encoded);
		_dataBytes = newDataBytes;
		FramesWritten = checked(FramesWritten +
			interleavedSamples.Length / Format.ChannelCount);
	}

	public void Complete()
	{
		ThrowIfDisposed();
		if (_completed)
			return;

		ValidateRiffSize(_dataBytes);
		if ((_dataBytes & 1) != 0)
			_stream.WriteByte(0); // RIFF word-alignment, excluded from data size.
		long endPosition = _stream.Position;
		WriteHeader(_dataBytes);
		_stream.Position = endPosition;
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

	private void ValidateRiffSize(long dataBytes)
	{
		long riffLength = checked(_headerSize - 8L + dataBytes +
			(dataBytes & 1));
		if (dataBytes < 0 || dataBytes > uint.MaxValue
			|| riffLength > uint.MaxValue)
			throw new InvalidOperationException(
				"Classic RIFF/WAVE size exceeds 4 GiB.");
	}

	private void WriteHeader(long dataBytes)
	{
		ValidateRiffSize(dataBytes);
		Span<byte> header = stackalloc byte[SurroundHeaderSize];
		header.Clear();
		Span<byte> actual = header[.._headerSize];
		Encoding.ASCII.GetBytes("RIFF", actual[..4]);
		BinaryPrimitives.WriteUInt32LittleEndian(actual.Slice(4, 4),
			(uint)(_headerSize - 8L + dataBytes + (dataBytes & 1)));
		Encoding.ASCII.GetBytes("WAVE", actual.Slice(8, 4));
		Encoding.ASCII.GetBytes("fmt ", actual.Slice(12, 4));
		bool surround = Format.ChannelCount > 2;
		BinaryPrimitives.WriteUInt32LittleEndian(actual.Slice(16, 4),
			surround ? 40u : 16u);
		BinaryPrimitives.WriteUInt16LittleEndian(actual.Slice(20, 2),
			surround ? ExtensibleFormatTag : PcmFormatTag);
		BinaryPrimitives.WriteUInt16LittleEndian(actual.Slice(22, 2),
			(ushort)Format.ChannelCount);
		BinaryPrimitives.WriteUInt32LittleEndian(actual.Slice(24, 4),
			(uint)Format.SampleRate);
		BinaryPrimitives.WriteUInt32LittleEndian(actual.Slice(28, 4),
			(uint)((long)Format.SampleRate * Format.ChannelCount * _bytesPerSample));
		BinaryPrimitives.WriteUInt16LittleEndian(actual.Slice(32, 2),
			(ushort)(Format.ChannelCount * _bytesPerSample));
		BinaryPrimitives.WriteUInt16LittleEndian(actual.Slice(34, 2),
			(ushort)BitDepth);

		int dataOffset = 36;
		if (surround)
		{
			BinaryPrimitives.WriteUInt16LittleEndian(actual.Slice(36, 2), 22);
			BinaryPrimitives.WriteUInt16LittleEndian(actual.Slice(38, 2),
				(ushort)BitDepth);
			// FL FR FC LFE BL BR, optionally SL SR for 7.1.
			BinaryPrimitives.WriteUInt32LittleEndian(actual.Slice(40, 4),
				Format.ChannelCount == 6 ? 0x3fu : 0x63fu);
			// KSDATAFORMAT_SUBTYPE_PCM GUID in little-endian byte layout.
			ReadOnlySpan<byte> pcmSubtype =
			[
				0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x10, 0x00,
				0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71,
			];
			pcmSubtype.CopyTo(actual.Slice(44, 16));
			dataOffset = 60;
		}
		Encoding.ASCII.GetBytes("data", actual.Slice(dataOffset, 4));
		BinaryPrimitives.WriteUInt32LittleEndian(actual.Slice(dataOffset + 4, 4),
			(uint)dataBytes);

		_stream.Position = _headerStart;
		_stream.Write(actual);
	}

	private static bool IsSupportedBitDepth(WavePcmBitDepth depth)
		=> depth is WavePcmBitDepth.Pcm8 or WavePcmBitDepth.Pcm16
			or WavePcmBitDepth.Pcm24 or WavePcmBitDepth.Pcm32;

	private static long Quantize(float sample, int bits)
	{
		long magnitude = 1L << (bits - 1);
		if (sample <= -1f)
			return -magnitude;
		if (sample >= 1f)
			return magnitude - 1;
		long quantized = checked((long)Math.Round(
			(double)sample * magnitude, MidpointRounding.AwayFromZero));
		return Math.Clamp(quantized, -magnitude, magnitude - 1);
	}

	private void ThrowIfUnavailable()
	{
		ThrowIfDisposed();
		if (_completed)
			throw new InvalidOperationException(
				"The WAVE sink has already been completed.");
	}

	private void ThrowIfDisposed()
	{
		if (_disposed)
			throw new ObjectDisposedException(nameof(WaveFileSink));
	}
}
