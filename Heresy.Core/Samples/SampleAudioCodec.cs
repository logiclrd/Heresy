using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

using Codec.Flac;
using Codec.Mp3;
using Codec.Vorbis;

using FileFormat.Aiff;

namespace Heresy.Core.Samples;

/// <summary>
/// Load/import-time decoder for encoded sample assets. Realtime playback must
/// never invoke this layer.
/// </summary>
public static class SampleAudioCodec
{
	public static SamplePcmData Decode(
		ReadOnlySpan<byte> encoded,
		string sourceName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);

		if (encoded.Length >= 12
			&& encoded[..4].SequenceEqual("RIFF"u8)
			&& encoded.Slice(8, 4).SequenceEqual("WAVE"u8))
		{
			return DecodeWave(encoded);
		}

		if (encoded.Length >= 4
			&& encoded[..4].SequenceEqual("fLaC"u8))
		{
			return DecodeFlac(encoded);
		}

		if (encoded.Length >= 4
			&& encoded[..4].SequenceEqual("OggS"u8))
		{
			return DecodeVorbis(encoded);
		}

		if (encoded.Length >= 12
			&& encoded[..4].SequenceEqual("FORM"u8)
			&& (encoded.Slice(8, 4).SequenceEqual("AIFF"u8)
				|| encoded.Slice(8, 4).SequenceEqual("AIFC"u8)))
		{
			return DecodeAiff(encoded);
		}

		if (LooksLikeMp3(encoded, sourceName))
			return DecodeMp3(encoded);

		throw new NotSupportedException(
			$"Sample '{sourceName}' is not in a supported encoded audio format. Supported formats are WAVE, FLAC, MP3, OGG Vorbis and AIFF.");
	}

	private static SamplePcmData DecodeFlac(
		ReadOnlySpan<byte> encoded)
	{
		FlacCodec.AudioProperties properties =
			FlacCodec.ReadAudioProperties(encoded);
		using MemoryStream input =
			new(encoded.ToArray(), writable: false);
		using MemoryStream output = new();
		FlacCodec.Decompress(input, output);
		return DecodeIntegerPcm(
			output.ToArray(),
			properties.SampleRate,
			properties.Channels,
			properties.BitsPerSample,
			littleEndian: true,
			eightBitUnsigned: true);
	}

	private static SamplePcmData DecodeMp3(
		ReadOnlySpan<byte> encoded)
	{
		byte[] bytes = encoded.ToArray();
		using MemoryStream input =
			new(bytes, writable: false);
		Mp3StreamInfo info =
			Mp3Codec.ReadStreamInfo(input);
		input.Position = 0;
		using MemoryStream output = new();
		Mp3Codec.Decompress(input, output);
		return DecodeIntegerPcm(
			output.ToArray(),
			info.SampleRate,
			info.Channels,
			bitsPerSample: 16,
			littleEndian: true,
			eightBitUnsigned: false);
	}

	private static SamplePcmData DecodeVorbis(
		ReadOnlySpan<byte> encoded)
	{
		byte[] bytes = encoded.ToArray();
		using MemoryStream input =
			new(bytes, writable: false);
		VorbisStreamInfo info =
			VorbisCodec.ReadStreamInfo(input);
		input.Position = 0;
		using MemoryStream output = new();
		VorbisCodec.Decompress(input, output);
		return DecodeIntegerPcm(
			output.ToArray(),
			info.SampleRate,
			info.Channels,
			bitsPerSample: 16,
			littleEndian: true,
			eightBitUnsigned: false);
	}

	private static SamplePcmData DecodeAiff(
		ReadOnlySpan<byte> encoded)
	{
		AiffReader.ParsedAiff parsed =
			new AiffReader().Read(encoded);

		if (parsed.NumChannels <= 0)
			throw new InvalidDataException(
				"The AIFF file declares no audio channels.");
		if (parsed.SampleRate <= 0)
			throw new InvalidDataException(
				"The AIFF file declares an invalid sample rate.");

		string compression =
			parsed.CompressionId;
		if (!parsed.IsAifc
			|| compression is "NONE" or "twos")
		{
			return DecodeIntegerPcm(
				parsed.SoundData,
				parsed.SampleRate,
				parsed.NumChannels,
				parsed.BitsPerSample,
				littleEndian: false,
				eightBitUnsigned: false);
		}

		if (compression == "sowt")
		{
			return DecodeIntegerPcm(
				parsed.SoundData,
				parsed.SampleRate,
				parsed.NumChannels,
				parsed.BitsPerSample,
				littleEndian: true,
				eightBitUnsigned: false);
		}

		if (compression is "fl32" or "FL32")
		{
			return DecodeFloatingPcm(
				parsed.SoundData,
				parsed.SampleRate,
				parsed.NumChannels,
				bitsPerSample: 32,
				littleEndian: false);
		}

		if (compression is "fl64" or "FL64")
		{
			return DecodeFloatingPcm(
				parsed.SoundData,
				parsed.SampleRate,
				parsed.NumChannels,
				bitsPerSample: 64,
				littleEndian: false);
		}

		throw new NotSupportedException(
			$"AIFC compression '{compression}' is not supported as a Heresy sample.");
	}

	private static bool LooksLikeMp3(
		ReadOnlySpan<byte> encoded,
		string sourceName)
	{
		if (encoded.Length >= 3
			&& encoded[..3].SequenceEqual("ID3"u8))
		{
			return true;
		}

		if (encoded.Length >= 2
			&& encoded[0] == 0xFF
			&& (encoded[1] & 0xE0) == 0xE0)
		{
			return true;
		}

		return string.Equals(
			Path.GetExtension(sourceName),
			".mp3",
			StringComparison.OrdinalIgnoreCase);
	}

	private static SamplePcmData DecodeIntegerPcm(
		ReadOnlySpan<byte> bytes,
		int sampleRate,
		int channels,
		int bitsPerSample,
		bool littleEndian,
		bool eightBitUnsigned)
	{
		if (sampleRate <= 0)
			throw new InvalidDataException(
				"The sample rate must be positive.");
		if (channels <= 0)
			throw new InvalidDataException(
				"The channel count must be positive.");
		if (bitsPerSample is < 4 or > 32)
		{
			throw new NotSupportedException(
				$"{bitsPerSample}-bit integer PCM is not supported.");
		}

		int bytesPerSample =
			(bitsPerSample + 7) / 8;
		int blockAlign =
			checked(channels * bytesPerSample);
		if (bytes.Length % blockAlign != 0)
		{
			throw new InvalidDataException(
				"The decoded PCM is not an integral number of sample frames.");
		}

		int sampleCount =
			bytes.Length / bytesPerSample;
		float[] samples =
			new float[sampleCount];
		double scale =
			Math.Pow(
				2.0,
				bitsPerSample - 1);

		for (int index = 0; index < sampleCount; index++)
		{
			ReadOnlySpan<byte> value =
				bytes.Slice(
					index * bytesPerSample,
					bytesPerSample);
			long integer =
				ReadSignedInteger(
					value,
					littleEndian,
					bitsPerSample,
					eightBitUnsigned);
			samples[index] =
				(float)(integer / scale);
		}

		return new SamplePcmData(
			sampleRate,
			channels,
			samples);
	}

	private static long ReadSignedInteger(
		ReadOnlySpan<byte> value,
		bool littleEndian,
		int bitsPerSample,
		bool eightBitUnsigned)
	{
		if (bitsPerSample == 8)
		{
			return eightBitUnsigned
				? value[0] - 128
				: unchecked((sbyte)value[0]);
		}

		ulong raw = 0;
		if (littleEndian)
		{
			for (int index = value.Length - 1; index >= 0; index--)
				raw = (raw << 8) | value[index];
		}
		else
		{
			for (int index = 0; index < value.Length; index++)
				raw = (raw << 8) | value[index];
		}

		int storageBits =
			value.Length * 8;
		ulong signBit =
			1UL << (storageBits - 1);
		if ((raw & signBit) != 0)
			raw |= ulong.MaxValue << storageBits;

		return unchecked((long)raw);
	}

	private static SamplePcmData DecodeFloatingPcm(
		ReadOnlySpan<byte> bytes,
		int sampleRate,
		int channels,
		int bitsPerSample,
		bool littleEndian)
	{
		int bytesPerSample =
			bitsPerSample / 8;
		int blockAlign =
			checked(channels * bytesPerSample);
		if (bytes.Length % blockAlign != 0)
		{
			throw new InvalidDataException(
				"The decoded floating-point PCM is not an integral number of sample frames.");
		}

		int sampleCount =
			bytes.Length / bytesPerSample;
		float[] samples =
			new float[sampleCount];

		for (int index = 0; index < sampleCount; index++)
		{
			ReadOnlySpan<byte> value =
				bytes.Slice(
					index * bytesPerSample,
					bytesPerSample);
			samples[index] =
				bitsPerSample switch
				{
					32 => BitConverter.Int32BitsToSingle(
						littleEndian
							? BinaryPrimitives.ReadInt32LittleEndian(value)
							: BinaryPrimitives.ReadInt32BigEndian(value)),
					64 => (float)BitConverter.Int64BitsToDouble(
						littleEndian
							? BinaryPrimitives.ReadInt64LittleEndian(value)
							: BinaryPrimitives.ReadInt64BigEndian(value)),
					_ => throw new NotSupportedException(
						$"{bitsPerSample}-bit floating-point PCM is not supported."),
				};
		}

		return new SamplePcmData(
			sampleRate,
			channels,
			samples);
	}

	private static SamplePcmData DecodeWave(
		ReadOnlySpan<byte> bytes)
	{
		WaveFormat? format = null;
		ReadOnlySpan<byte> data = default;
		bool haveData = false;
		int offset = 12;

		while (offset < bytes.Length)
		{
			if (bytes.Length - offset < 8)
				throw new InvalidDataException(
					"The WAVE file ends inside a chunk header.");

			ReadOnlySpan<byte> chunkHeader = bytes.Slice(offset, 8);
			offset += 8;
			string id = Encoding.ASCII.GetString(chunkHeader[..4]);
			uint chunkSize =
				BinaryPrimitives.ReadUInt32LittleEndian(
					chunkHeader.Slice(4, 4));
			if (chunkSize > int.MaxValue
				|| bytes.Length - offset < chunkSize)
			{
				throw new InvalidDataException(
					"The WAVE file contains an invalid or truncated chunk.");
			}

			ReadOnlySpan<byte> chunk =
				bytes.Slice(offset, checked((int)chunkSize));
			if (id == "fmt ")
				format = ParseFormat(chunk);
			else if (id == "data")
			{
				data = chunk;
				haveData = true;
			}

			offset = checked(offset + (int)chunkSize);
			if ((chunkSize & 1U) != 0)
			{
				if (offset >= bytes.Length)
					throw new InvalidDataException(
						"The WAVE file is missing chunk padding.");
				offset++;
			}

			if (format.HasValue && haveData)
				break;
		}

		if (!format.HasValue)
			throw new InvalidDataException(
				"The WAVE file does not contain a fmt chunk.");
		if (!haveData)
			throw new InvalidDataException(
				"The WAVE file does not contain a data chunk.");

		return DecodeSamples(format.Value, data);
	}

	private static WaveFormat ParseFormat(
		ReadOnlySpan<byte> bytes)
	{
		if (bytes.Length < 16)
			throw new InvalidDataException(
				"The WAVE fmt chunk is too short.");

		ushort formatTag =
			BinaryPrimitives.ReadUInt16LittleEndian(bytes[..2]);
		ushort channels =
			BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(2, 2));
		uint sampleRate =
			BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(4, 4));
		ushort blockAlign =
			BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(12, 2));
		ushort bitsPerSample =
			BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(14, 2));

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
		if (sampleRate == 0 || sampleRate > int.MaxValue)
			throw new InvalidDataException(
				"The WAVE file declares an invalid sample rate.");

		int bytesPerSample = (bitsPerSample + 7) / 8;
		if (bytesPerSample == 0
			|| blockAlign != checked(channels * bytesPerSample))
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
			throw new NotSupportedException(
				$"WAVE format tag {formatTag} is not supported.");

		return new WaveFormat(
			formatTag,
			channels,
			checked((int)sampleRate),
			blockAlign,
			bitsPerSample);
	}

	private static SamplePcmData DecodeSamples(
		WaveFormat format,
		ReadOnlySpan<byte> bytes)
	{
		if (bytes.Length % format.BlockAlign != 0)
			throw new InvalidDataException(
				"The WAVE data chunk is not an integral number of sample frames.");

		int frameCount = bytes.Length / format.BlockAlign;
		int sampleCount = checked(frameCount * format.Channels);
		float[] samples = new float[sampleCount];
		int bytesPerSample = format.BlockAlign / format.Channels;

		for (int index = 0; index < sampleCount; index++)
		{
			ReadOnlySpan<byte> value =
				bytes.Slice(index * bytesPerSample, bytesPerSample);
			samples[index] =
				format.FormatTag == 1
					? DecodeInteger(value, format.BitsPerSample)
					: DecodeFloat(value, format.BitsPerSample);
		}

		return new SamplePcmData(
			format.SampleRate,
			format.Channels,
			samples);
	}

	private static float DecodeInteger(
		ReadOnlySpan<byte> value,
		ushort bitsPerSample)
		=> bitsPerSample switch
		{
			8 => (value[0] - 128) / 128.0f,
			16 => BinaryPrimitives.ReadInt16LittleEndian(value) / 32768.0f,
			24 => DecodeInt24(value) / 8388608.0f,
			32 => (float)(
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
			32 => BitConverter.Int32BitsToSingle(
				BinaryPrimitives.ReadInt32LittleEndian(value)),
			64 => (float)BitConverter.Int64BitsToDouble(
				BinaryPrimitives.ReadInt64LittleEndian(value)),
			_ => throw new NotSupportedException(),
		};

	private readonly record struct WaveFormat(
		ushort FormatTag,
		ushort Channels,
		int SampleRate,
		ushort BlockAlign,
		ushort BitsPerSample);
}
