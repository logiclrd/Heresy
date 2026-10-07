using System;

using Heresy.Core.Samples;

namespace Heresy.UserInterface.SampleEditing;

public readonly record struct SampleWaveformPeak(
	bool HasData,
	float Minimum,
	float Maximum)
{
	public static SampleWaveformPeak NoData { get; } =
		new(false, 0.0f, 0.0f);
}

public sealed class SampleWaveformEnvelope
{
	private readonly SampleWaveformPeak[] _peaks;

	private SampleWaveformEnvelope(
		long frameCount,
		int channelCount,
		int columnCount,
		SampleWaveformPeak[] peaks)
	{
		FrameCount = frameCount;
		ChannelCount = channelCount;
		ColumnCount = columnCount;
		_peaks = peaks;
	}

	public long FrameCount { get; }

	public int ChannelCount { get; }

	public int ColumnCount { get; }

	public SampleWaveformPeak this[
		int column,
		int channel]
	{
		get
		{
			if ((uint)column >= (uint)ColumnCount)
				throw new ArgumentOutOfRangeException(nameof(column));
			if ((uint)channel >= (uint)ChannelCount)
				throw new ArgumentOutOfRangeException(nameof(channel));

			return _peaks[
				checked(
					(column * ChannelCount)
						+ channel)];
		}
	}

	public static SampleWaveformEnvelope Build(
		SamplePcmData pcm,
		int columnCount)
	{
		ArgumentNullException.ThrowIfNull(pcm);
		if (columnCount <= 0)
			throw new ArgumentOutOfRangeException(nameof(columnCount));

		SampleWaveformPeak[] peaks =
			new SampleWaveformPeak[
				checked(columnCount * pcm.ChannelCount)];

		if (pcm.FrameCount == 0)
		{
			return new SampleWaveformEnvelope(
				0,
				pcm.ChannelCount,
				columnCount,
				peaks);
		}

		for (int column = 0;
			column < columnCount;
			column++)
		{
			long start =
				(long)Math.Floor(
					(double)column
						* pcm.FrameCount
						/ columnCount);
			long end =
				(long)Math.Floor(
					(double)(column + 1)
						* pcm.FrameCount
						/ columnCount);

			if (start >= pcm.FrameCount)
				start = pcm.FrameCount - 1;
			if (end <= start)
				end = start + 1;
			if (end > pcm.FrameCount)
				end = pcm.FrameCount;

			for (int channel = 0;
				channel < pcm.ChannelCount;
				channel++)
			{
				bool haveData = false;
				float minimum = 0.0f;
				float maximum = 0.0f;

				for (long frame = start;
					frame < end;
					frame++)
				{
					float value =
						pcm.GetSample(
							frame,
							channel);
					if (!float.IsFinite(value))
						continue;

					if (!haveData)
					{
						minimum = value;
						maximum = value;
						haveData = true;
					}
					else
					{
						minimum =
							Math.Min(
								minimum,
								value);
						maximum =
							Math.Max(
								maximum,
								value);
					}
				}

				peaks[
					checked(
						(column * pcm.ChannelCount)
							+ channel)] =
					haveData
						? new SampleWaveformPeak(
							true,
							minimum,
							maximum)
						: SampleWaveformPeak.NoData;
			}
		}

		return new SampleWaveformEnvelope(
			pcm.FrameCount,
			pcm.ChannelCount,
			columnCount,
			peaks);
	}

	public static double FrameToFraction(
		long frame,
		long frameCount)
	{
		if (frameCount < 0)
			throw new ArgumentOutOfRangeException(nameof(frameCount));
		if (frame < 0 || frame > frameCount)
			throw new ArgumentOutOfRangeException(nameof(frame));
		if (frameCount == 0)
			return 0.0;

		return (double)frame / frameCount;
	}

	public static long FractionToFrame(
		double fraction,
		long frameCount)
	{
		if (frameCount < 0)
			throw new ArgumentOutOfRangeException(nameof(frameCount));
		if (!double.IsFinite(fraction)
			|| fraction < 0.0
			|| fraction > 1.0)
		{
			throw new ArgumentOutOfRangeException(nameof(fraction));
		}

		return (long)Math.Round(
			fraction * frameCount,
			MidpointRounding.AwayFromZero);
	}
}
