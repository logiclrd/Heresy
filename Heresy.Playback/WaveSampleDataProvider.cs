using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

using Heresy.Core.Assets;
using Heresy.Core.Samples;
using Heresy.Render.Samples;

namespace Heresy.Playback;

/// <summary>
/// File-backed decoded PCM provider for RIFF/WAVE assets. Decoded data is
/// cached by asset path plus recorded hash so independently cloned playback
/// snapshots can share immutable PCM safely.
/// </summary>
public sealed class WaveSampleDataProvider
	: ISampleDataProvider
{
	private readonly object _gate = new();
	private readonly Dictionary<AssetKey, ISampleData> _cache = [];

	public ISampleData GetSampleData(
		SampleDefinition sample)
	{
		ArgumentNullException.ThrowIfNull(sample);

		ExternalAssetReference asset =
			sample.Asset
				?? throw new InvalidOperationException(
					"WaveSampleDataProvider requires a persisted asset reference.");
		AssetKey key =
			new(
				asset.FullPath,
				asset.Sha256);

		lock (_gate)
		{
			if (_cache.TryGetValue(
					key,
					out ISampleData? cached))
			{
				return cached;
			}
		}

		ISampleData decoded =
			Decode(asset.FullPath);

		lock (_gate)
		{
			if (_cache.TryGetValue(
					key,
					out ISampleData? cached))
			{
				return cached;
			}

			_cache.Add(key, decoded);
			return decoded;
		}
	}

	private static ISampleData Decode(
		string fullPath)
	{
		using Stream stream =
			ExternalAssetIntegrity.OpenRead(fullPath);

		byte[] header = ReadUpTo(stream, 12);
		if (header.Length < 12
			|| !header.AsSpan(0, 4)
				.SequenceEqual(
					"RIFF"u8)
			|| !header.AsSpan(8, 4)
				.SequenceEqual(
					"WAVE"u8))
		{
			throw new NotSupportedException(
				$"Realtime playback currently decodes RIFF/WAVE sample assets; '{fullPath}' is not a supported WAVE file.");
		}

		WaveFormat? format = null;
		byte[]? data = null;

		while (true)
		{
			byte[] chunkHeader =
				ReadUpTo(stream, 8);
			if (chunkHeader.Length == 0)
				break;
			if (chunkHeader.Length != 8)
				throw new InvalidDataException(
					"The WAVE file ends inside a chunk header.");

			string id =
				Encoding.ASCII.GetString(
					chunkHeader,
					0,
					4);
			uint chunkSize =
				BinaryPrimitives.ReadUInt32LittleEndian(
					chunkHeader.AsSpan(4, 4));
			if (chunkSize > int.MaxValue)
				throw new NotSupportedException(
					"WAVE chunks larger than 2 GiB are not supported.");

			byte[] chunk =
				ReadExactly(
					stream,
					checked((int)chunkSize));

			if (id == "fmt ")
				format = ParseFormat(chunk);
			else if (id == "data")
				data = chunk;

			if ((chunkSize & 1U) != 0)
			{
				if (stream.ReadByte() < 0)
				throw new InvalidDataException(
					"The WAVE file is missing chunk padding.");
			}

			if (format.HasValue && data is not null)
				break;
		}

		if (!format.HasValue)
			throw new InvalidDataException(
				"The WAVE file does not contain a fmt chunk.");
		if (data is null)
			throw new InvalidDataException(
				"The WAVE file does not contain a data chunk.");

		return DecodeSamples(
			format.Value,
			data);
	}

	private static WaveFormat ParseFormat(
		ReadOnlySpan<byte> bytes)
	{
		if (bytes.Length < 16)
			throw new InvalidDataException(
				"The WAVE fmt chunk is too short.");

		ushort formatTag =
			BinaryPrimitives.ReadUInt16LittleEndian(
				bytes.Slice(0, 2));
		ushort channels =
			BinaryPrimitives.ReadUInt16LittleEndian(
				bytes.Slice(2, 2));
		uint sampleRate =
			BinaryPrimitives.ReadUInt32LittleEndian(
				bytes.Slice(4, 4));
		ushort blockAlign =
			BinaryPrimitives.ReadUInt16LittleEndian(
				bytes.Slice(12, 2));
		ushort bitsPerSample =
			BinaryPrimitives.ReadUInt16LittleEndian(
				bytes.Slice(14, 2));

		if (formatTag == 0xFFFE)
		{
			if (bytes.Length < 40)
				throw new InvalidDataException(
					"The extensible WAVE fmt chunk is too short.");

			uint subFormat =
				BinaryPrimitives.ReadUInt32LittleEndian(
					bytes.Slice(24, 4));
			formatTag =
				subFormat switch
				{
					1U => 1,
					3U => 3,
					_ => throw new NotSupportedException(
						$"WAVE extensible subformat {subFormat} is not supported."),
				};
		}

		if (channels == 0)
			throw new InvalidDataException(
				"The WAVE file declares zero channels.");
		if (sampleRate == 0
			|| sampleRate > int.MaxValue)
		{
			throw new InvalidDataException(
				"The WAVE file declares an invalid sample rate.");
		}

		int bytesPerSample =
			(bitsPerSample + 7) / 8;
		if (bytesPerSample == 0
			|| blockAlign
				!= checked(
					channels * bytesPerSample))
		{
			throw new InvalidDataException(
				"The WAVE block alignment does not match its channel/sample format.");
		}

		if (formatTag == 1
			&& bitsPerSample is not (8 or 16 or 24 or 32))
		{
			throw new NotSupportedException(
				$"{bitsPerSample}-bit PCM WAVE samples are not supported.");
		}
		if (formatTag == 3
			&& bitsPerSample is not (32 or 64))
		{
			throw new NotSupportedException(
				$"{bitsPerSample}-bit floating-point WAVE samples are not supported.");
		}
		if (formatTag is not (1 or 3))
		{
			throw new NotSupportedException(
				$"WAVE format tag {formatTag} is not supported.");
		}

		return new WaveFormat(
			formatTag,
			channels,
			checked((int)sampleRate),
			blockAlign,
			bitsPerSample);
	}

	private static ISampleData DecodeSamples(
		WaveFormat format,
		ReadOnlySpan<byte> bytes)
	{
		if (bytes.Length % format.BlockAlign != 0)
		{
			throw new InvalidDataException(
				"The WAVE data chunk is not an integral number of sample frames.");
		}

		int frameCount =
			bytes.Length / format.BlockAlign;
		int sampleCount =
			checked(
				frameCount
					* format.Channels);
		float[] samples =
			new float[sampleCount];
		int bytesPerSample =
			format.BlockAlign
				/ format.Channels;

		for (int index = 0;
			index < sampleCount;
			index++)
		{
			ReadOnlySpan<byte> value =
				bytes.Slice(
					index * bytesPerSample,
					bytesPerSample);
			samples[index] =
				format.FormatTag == 1
					? DecodeInteger(
						value,
						format.BitsPerSample)
					: DecodeFloat(
						value,
						format.BitsPerSample);
		}

		return new MemorySampleData(
			format.SampleRate,
			format.Channels,
			samples);
	}

	private static float DecodeInteger(
		ReadOnlySpan<byte> value,
		ushort bitsPerSample)
		=> bitsPerSample switch
		{
			8 =>
				(value[0] - 128)
					/ 128.0f,
			16 =>
				BinaryPrimitives.ReadInt16LittleEndian(value)
					/ 32768.0f,
			24 =>
				DecodeInt24(value)
					/ 8388608.0f,
			32 =>
				(float)(
					BinaryPrimitives.ReadInt32LittleEndian(value)
						/ 2147483648.0),
			_ => throw new NotSupportedException(),
		};

	private static int DecodeInt24(
		ReadOnlySpan<byte> value)
	{
		int result =
			value[0]
				| (value[1] << 8)
				| (value[2] << 16);
		if ((result & 0x00800000) != 0)
			result |= unchecked((int)0xFF000000);
		return result;
	}

	private static float DecodeFloat(
		ReadOnlySpan<byte> value,
		ushort bitsPerSample)
		=> bitsPerSample switch
		{
			32 =>
				BitConverter.Int32BitsToSingle(
					BinaryPrimitives.ReadInt32LittleEndian(
						value)),
			64 =>
				(float)BitConverter.Int64BitsToDouble(
					BinaryPrimitives.ReadInt64LittleEndian(
						value)),
			_ => throw new NotSupportedException(),
		};

	private static byte[] ReadExactly(
		Stream stream,
		int count)
	{
		byte[] result =
			new byte[count];
		int offset = 0;

		while (offset < count)
		{
			int read =
				stream.Read(
					result,
					offset,
					count - offset);
			if (read == 0)
			{
				throw new InvalidDataException(
					"The WAVE file ended unexpectedly.");
			}

			offset += read;
		}

		return result;
	}

	private static byte[] ReadUpTo(
		Stream stream,
		int count)
	{
		byte[] result =
			new byte[count];
		int offset = 0;
		while (offset < count)
		{
			int read =
				stream.Read(
					result,
					offset,
					count - offset);
			if (read == 0)
				break;
			offset += read;
		}

		if (offset == count)
			return result;

		Array.Resize(
			ref result,
			offset);
		return result;
	}

	private readonly record struct AssetKey(
		string FullPath,
		string? Sha256);

	private readonly record struct WaveFormat(
		ushort FormatTag,
		ushort Channels,
		int SampleRate,
		ushort BlockAlign,
		ushort BitsPerSample);
}
