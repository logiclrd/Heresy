using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

using Heresy.Render.Realtime;

namespace Heresy.Render.File;

/// <summary>
/// Incremental 16-bit FLAC encoder for offline rendering. It buffers at most
/// one 4096-frame PCM block, emits completed frames immediately, and uses only
/// bounded per-frame scratch storage. STREAMINFO totals are patched when the
/// destination is seekable; non-seekable destinations remain valid with an
/// unknown total-sample count.
/// </summary>
public sealed class FlacFileSink : IAudioFileSink
{
	private const int BlockFrameCount = 4096;
	private const int BitsPerSample = 16;
	private const ulong MaximumTotalFrames = 0x0FFFFFFFFFUL;

	private readonly Stream _stream;
	private readonly bool _leaveOpen;
	private readonly long? _streamStart;
	private readonly short[] _block;
	private int _bufferedFrames;
	private uint _frameNumber;
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
		if (format.SampleRate > 0xFFFFF)
		{
			throw new ArgumentOutOfRangeException(
				nameof(format),
				"FLAC STREAMINFO stores sample rates in 20 bits.");
		}

		Format = format;
		_leaveOpen = leaveOpen;
		_streamStart =
			stream.CanSeek
				? stream.Position
				: null;
		_block =
			new short[
				checked(
					BlockFrameCount
						* format.ChannelCount)];

		WriteStreamHeader(
			totalFrames: 0);
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

		int inputFrames =
			interleavedSamples.Length
				/ Format.ChannelCount;
		int sourceFrame = 0;
		while (sourceFrame < inputFrames)
		{
			int copyFrames =
				Math.Min(
					BlockFrameCount
						- _bufferedFrames,
					inputFrames
						- sourceFrame);
			int sampleCount =
				checked(
					copyFrames
						* Format.ChannelCount);
			int sourceSample =
				checked(
					sourceFrame
						* Format.ChannelCount);
			int destinationSample =
				checked(
					_bufferedFrames
						* Format.ChannelCount);

			for (int index = 0;
				index < sampleCount;
				index++)
			{
				_block[destinationSample + index] =
					Quantize(
						interleavedSamples[
							sourceSample + index]);
			}

			sourceFrame += copyFrames;
			_bufferedFrames += copyFrames;
			if (_bufferedFrames == BlockFrameCount)
				FlushBlock(BlockFrameCount);
		}
	}

	public void Complete()
	{
		ThrowIfDisposed();
		if (_completed)
			return;

		if (_bufferedFrames != 0)
			FlushBlock(_bufferedFrames);

		if (_streamStart.HasValue)
			PatchStreamInfo();

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

	private void FlushBlock(
		int frameCount)
	{
		if (frameCount <= 0
			|| frameCount > BlockFrameCount)
		{
			throw new ArgumentOutOfRangeException(
				nameof(frameCount));
		}

		ulong newTotal =
			checked(
				(ulong)FramesWritten
					+ (uint)frameCount);
		if (newTotal > MaximumTotalFrames)
		{
			throw new InvalidOperationException(
				"FLAC total sample count exceeds the 36-bit STREAMINFO field.");
		}

		byte[] frame =
			BuildFrame(
				frameCount,
				_frameNumber);
		_stream.Write(frame);

		FramesWritten =
			checked(
				FramesWritten
					+ frameCount);
		_frameNumber++;
		_bufferedFrames = 0;
	}

	private byte[] BuildFrame(
		int frameCount,
		uint frameNumber)
	{
		using MemoryStream frame = new();
		WriteFrameHeader(
			frame,
			frameCount,
			frameNumber);

		BitWriter bits = new(frame);
		for (int channel = 0;
			channel < Format.ChannelCount;
			channel++)
		{
			WriteSubframe(
				bits,
				frameCount,
				channel);
		}
		bits.AlignToByte();

		byte[] withoutFooter =
			frame.ToArray();
		ushort crc =
			ComputeCrc16(withoutFooter);
		Span<byte> footer =
			stackalloc byte[2];
		BinaryPrimitives.WriteUInt16BigEndian(
			footer,
			crc);
		frame.Write(footer);
		return frame.ToArray();
	}

	private void WriteFrameHeader(
		Stream destination,
		int frameCount,
		uint frameNumber)
	{
		using MemoryStream header = new();

		// 15-bit sync 0x7FFC plus fixed-blocking strategy 0.
		header.WriteByte(0xFF);
		header.WriteByte(0xF8);

		int blockSizeCode =
			frameCount == BlockFrameCount
				? 0xC
				: frameCount <= 256
					? 0x6
					: 0x7;
		SampleRateEncoding rate =
			GetSampleRateEncoding(
				Format.SampleRate);
		header.WriteByte(
			(byte)(
				(blockSizeCode << 4)
					| rate.Code));

		int channelAssignment =
			Format.ChannelCount - 1;
		int bitDepthCode = 0b100;
		header.WriteByte(
			(byte)(
				(channelAssignment << 4)
					| (bitDepthCode << 1)));

		WriteCodedFrameNumber(
			header,
			frameNumber);

		if (blockSizeCode == 0x6)
		{
			header.WriteByte(
				checked(
					(byte)(frameCount - 1)));
		}
		else if (blockSizeCode == 0x7)
		{
			Span<byte> blockSize =
				stackalloc byte[2];
			BinaryPrimitives.WriteUInt16BigEndian(
				blockSize,
				checked(
					(ushort)(frameCount - 1)));
			header.Write(blockSize);
		}

		if (rate.ExtraByteCount == 1)
		{
			header.WriteByte(
				checked(
					(byte)rate.ExtraValue));
		}
		else if (rate.ExtraByteCount == 2)
		{
			Span<byte> sampleRate =
				stackalloc byte[2];
			BinaryPrimitives.WriteUInt16BigEndian(
				sampleRate,
				checked(
					(ushort)rate.ExtraValue));
			header.Write(sampleRate);
		}

		byte[] bytes =
			header.ToArray();
		destination.Write(bytes);
		destination.WriteByte(
			ComputeCrc8(bytes));
	}

	private void WriteSubframe(
		BitWriter bits,
		int frameCount,
		int channel)
	{
		short first =
			_block[channel];
		bool constant = true;
		for (int frame = 1;
			frame < frameCount;
			frame++)
		{
			if (_block[
				checked(
					frame
						* Format.ChannelCount
						+ channel)] != first)
			{
				constant = false;
				break;
			}
		}

		if (constant)
		{
			// zero pad bit + constant type (000000) + no wasted bits.
			bits.WriteBits(0, 8);
			bits.WriteBits(
				unchecked(
					(ushort)first),
				BitsPerSample);
			return;
		}

		if (frameCount >= 2
			&& TryChooseFixedOrderOneRiceParameter(
				frameCount,
				channel,
				out int riceParameter,
				out long fixedBits)
			&& fixedBits
				< 8L
					+ ((long)frameCount
						* BitsPerSample))
		{
			WriteFixedOrderOneSubframe(
				bits,
				frameCount,
				channel,
				riceParameter);
			return;
		}

		// zero pad bit + verbatim type (000001) + no wasted bits.
		bits.WriteBits(0x02, 8);
		for (int frame = 0;
			frame < frameCount;
			frame++)
		{
			short sample =
				_block[
					checked(
						frame
							* Format.ChannelCount
							+ channel)];
			bits.WriteBits(
				unchecked(
					(ushort)sample),
				BitsPerSample);
		}
	}

	private bool TryChooseFixedOrderOneRiceParameter(
		int frameCount,
		int channel,
		out int bestParameter,
		out long bestBits)
	{
		bestParameter = 0;
		bestBits = long.MaxValue;

		for (int parameter = 0;
			parameter <= 14;
			parameter++)
		{
			// 8 subframe header + 16 warmup + 2 coding method
			// + 4 partition order + 4 Rice parameter.
			long bits = 34;
			short previous =
				_block[channel];
			for (int frame = 1;
				frame < frameCount;
				frame++)
			{
				short current =
					_block[
						checked(
							frame
								* Format.ChannelCount
								+ channel)];
				int residual =
					current - previous;
				uint folded =
					FoldSigned(residual);
				bits =
					checked(
						bits
							+ (folded >> parameter)
							+ 1
							+ parameter);
				if (bits >= bestBits)
					break;
				previous = current;
			}

			if (bits < bestBits)
			{
				bestBits = bits;
				bestParameter = parameter;
			}
		}

		return bestBits != long.MaxValue;
	}

	private void WriteFixedOrderOneSubframe(
		BitWriter bits,
		int frameCount,
		int channel,
		int riceParameter)
	{
		// zero pad + fixed-predictor type order 1 (001001) + no wasted bits.
		bits.WriteBits(0x12, 8);

		short previous =
			_block[channel];
		bits.WriteBits(
			unchecked(
				(ushort)previous),
			BitsPerSample);

		// Partitioned Rice coding method 00, partition order 0000,
		// then one 4-bit Rice parameter for the whole residual.
		bits.WriteBits(0, 2);
		bits.WriteBits(0, 4);
		bits.WriteBits(
			(uint)riceParameter,
			4);

		for (int frame = 1;
			frame < frameCount;
			frame++)
		{
			short current =
				_block[
					checked(
						frame
							* Format.ChannelCount
							+ channel)];
			int residual =
				current - previous;
			uint folded =
				FoldSigned(residual);
			uint quotient =
				folded
					>> riceParameter;
			bits.WriteUnary(quotient);
			if (riceParameter != 0)
			{
				uint remainder =
					folded
						& ((1U << riceParameter) - 1U);
				bits.WriteBits(
					remainder,
					riceParameter);
			}
			previous = current;
		}
	}

	private static uint FoldSigned(
		int value)
		=> value >= 0
			? checked(
				(uint)value * 2U)
			: checked(
				(uint)(-value) * 2U - 1U);

	private void WriteStreamHeader(
		ulong totalFrames)
	{
		_stream.Write(
			"fLaC"u8);
		// Last metadata block flag + STREAMINFO type, followed by 34-byte size.
		_stream.Write(
			new byte[]
			{
				0x80,
				0x00,
				0x00,
				0x22,
			});
		WriteStreamInfoPayload(
			_stream,
			totalFrames);
	}

	private void PatchStreamInfo()
	{
		if (!_streamStart.HasValue)
			return;

		long returnPosition =
			_stream.Position;
		_stream.Position =
			checked(
				_streamStart.Value
					+ 8);
		WriteStreamInfoPayload(
			_stream,
			checked(
				(ulong)FramesWritten));
		_stream.Position =
			returnPosition;
	}

	private void WriteStreamInfoPayload(
		Stream destination,
		ulong totalFrames)
	{
		if (totalFrames > MaximumTotalFrames)
			throw new ArgumentOutOfRangeException(nameof(totalFrames));

		Span<byte> payload =
			stackalloc byte[34];
		BinaryPrimitives.WriteUInt16BigEndian(
			payload[..2],
			(ushort)BlockFrameCount);
		BinaryPrimitives.WriteUInt16BigEndian(
			payload.Slice(2, 2),
			(ushort)BlockFrameCount);
		// min/max frame sizes remain zero (unknown).

		ulong combined =
			((ulong)Format.SampleRate << 44)
				| ((ulong)(Format.ChannelCount - 1) << 41)
				| ((ulong)(BitsPerSample - 1) << 36)
				| totalFrames;
		BinaryPrimitives.WriteUInt64BigEndian(
			payload.Slice(10, 8),
			combined);
		// MD5 remains all zero, which FLAC defines as unknown.

		destination.Write(payload);
	}

	private static SampleRateEncoding GetSampleRateEncoding(
		int sampleRate)
		=> sampleRate switch
		{
			88200 => new SampleRateEncoding(0x1, 0, 0),
			176400 => new SampleRateEncoding(0x2, 0, 0),
			192000 => new SampleRateEncoding(0x3, 0, 0),
			8000 => new SampleRateEncoding(0x4, 0, 0),
			16000 => new SampleRateEncoding(0x5, 0, 0),
			22050 => new SampleRateEncoding(0x6, 0, 0),
			24000 => new SampleRateEncoding(0x7, 0, 0),
			32000 => new SampleRateEncoding(0x8, 0, 0),
			44100 => new SampleRateEncoding(0x9, 0, 0),
			48000 => new SampleRateEncoding(0xA, 0, 0),
			96000 => new SampleRateEncoding(0xB, 0, 0),
			_ when sampleRate % 1000 == 0
				&& sampleRate / 1000 <= byte.MaxValue =>
				new SampleRateEncoding(
					0xC,
					sampleRate / 1000,
					1),
			_ when sampleRate <= ushort.MaxValue =>
				new SampleRateEncoding(
					0xD,
					sampleRate,
					2),
			_ when sampleRate % 10 == 0
				&& sampleRate / 10 <= ushort.MaxValue =>
				new SampleRateEncoding(
					0xE,
					sampleRate / 10,
					2),
			_ =>
				// Valid FLAC, but outside the streamable subset: obtain the
				// unusual rate from STREAMINFO.
				new SampleRateEncoding(0x0, 0, 0),
		};

	private static void WriteCodedFrameNumber(
		Stream destination,
		uint value)
	{
		if (value > 0x7FFFFFFF)
		{
			throw new InvalidOperationException(
				"FLAC fixed-block frame number exceeds 31 bits.");
		}

		int byteCount =
			value switch
			{
				<= 0x7F => 1,
				<= 0x7FF => 2,
				<= 0xFFFF => 3,
				<= 0x1FFFFF => 4,
				<= 0x3FFFFFF => 5,
				_ => 6,
			};

		if (byteCount == 1)
		{
			destination.WriteByte(
				(byte)value);
			return;
		}

		Span<byte> bytes =
			stackalloc byte[6];
		uint remaining = value;
		for (int index = byteCount - 1;
			index >= 1;
			index--)
		{
			bytes[index] =
				(byte)(
					0x80
						| (remaining & 0x3F));
			remaining >>= 6;
		}

		byte prefix =
			byteCount switch
			{
				2 => 0xC0,
				3 => 0xE0,
				4 => 0xF0,
				5 => 0xF8,
				6 => 0xFC,
				_ => throw new InvalidOperationException(),
			};
		int firstPayloadBits =
			7 - byteCount;
		byte firstMask =
			(byte)(
				(1 << firstPayloadBits) - 1);
		bytes[0] =
			(byte)(
				prefix
					| (remaining & firstMask));
		destination.Write(
			bytes[..byteCount]);
	}

	private static short Quantize(
		float value)
	{
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
							value * 32768.0,
							MidpointRounding.AwayFromZero));
		return checked(
			(short)Math.Clamp(
				quantized,
				short.MinValue,
				short.MaxValue));
	}

	private static byte ComputeCrc8(
		ReadOnlySpan<byte> bytes)
	{
		byte crc = 0;
		foreach (byte value in bytes)
		{
			crc ^= value;
			for (int bit = 0; bit < 8; bit++)
			{
				crc =
					(crc & 0x80) != 0
						? (byte)((crc << 1) ^ 0x07)
						: (byte)(crc << 1);
			}
		}
		return crc;
	}

	private static ushort ComputeCrc16(
		ReadOnlySpan<byte> bytes)
	{
		ushort crc = 0;
		foreach (byte value in bytes)
		{
			crc ^=
				(ushort)(value << 8);
			for (int bit = 0; bit < 8; bit++)
			{
				crc =
					(crc & 0x8000) != 0
						? (ushort)((crc << 1) ^ 0x8005)
						: (ushort)(crc << 1);
			}
		}
		return crc;
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

	private readonly record struct SampleRateEncoding(
		int Code,
		int ExtraValue,
		int ExtraByteCount);

	private sealed class BitWriter
	{
		private readonly Stream _stream;
		private byte _pending;
		private int _pendingBits;

		public BitWriter(
			Stream stream)
			=> _stream =
				stream
					?? throw new ArgumentNullException(
						nameof(stream));

		public void WriteBits(
			ulong value,
			int bitCount)
		{
			if (bitCount is < 0 or > 64)
				throw new ArgumentOutOfRangeException(nameof(bitCount));

			for (int bit = bitCount - 1;
				bit >= 0;
				bit--)
			{
				_pending =
					(byte)(
						(_pending << 1)
							| (byte)((value >> bit) & 1UL));
				_pendingBits++;
				if (_pendingBits == 8)
				{
					_stream.WriteByte(_pending);
					_pending = 0;
					_pendingBits = 0;
				}
			}
		}

		public void WriteUnary(
			uint value)
		{
			for (uint index = 0;
				index < value;
				index++)
			{
				WriteBits(0, 1);
			}
			WriteBits(1, 1);
		}

		public void AlignToByte()
		{
			if (_pendingBits == 0)
				return;

			_pending =
				(byte)(
					_pending
						<< (8 - _pendingBits));
			_stream.WriteByte(_pending);
			_pending = 0;
			_pendingBits = 0;
		}
	}
}
