using System;
using System.Numerics;

using Heresy.Core.Samples;
using Heresy.Render.Configuration;
using Heresy.Render.Sounds;
using Heresy.Render.Spatial;
using Heresy.Render.Timing;

namespace Heresy.Render.Samples;

/// <summary>
/// Executable renderer for a SampleDefinition and its decoded PCM.
/// </summary>
public sealed class SampleSound : ISound
{
	private readonly SampleDefinition _definition;
	private readonly ISampleData _data;
	private readonly Vector3[] _sourceChannelPositions;

	public SampleSound(
		SampleDefinition definition,
		ISampleData data,
		NewNotePolicy? newNotePolicy = null)
	{
		_definition = definition ?? throw new ArgumentNullException(nameof(definition));
		_data = data ?? throw new ArgumentNullException(nameof(data));

		if (_data.SampleRate <= 0)
			throw new ArgumentException("Sample data has an invalid sample rate.", nameof(data));
		if (_data.ChannelCount <= 0)
			throw new ArgumentException("Sample data has an invalid channel count.", nameof(data));
		if (_data.FrameCount < 0)
			throw new ArgumentException("Sample data has an invalid frame count.", nameof(data));

		ValidateLoop(_definition.Loop, _data.FrameCount);
		_sourceChannelPositions = ResolveSourceChannelPositions(
			_definition,
			_data.ChannelCount);

		NewNotePolicy = newNotePolicy ?? Heresy.Render.Sounds.NewNotePolicy.Cut;
	}

	/// <summary>
	/// Current configuration used for notes started after this value changes.
	/// Already-playing voices retain the value captured at note start.
	/// </summary>
	public NewNotePolicy NewNotePolicy { get; set; }

	public NoteConfigurationSnapshot SnapshotNoteConfiguration()
		=> new(NewNotePolicy);

	public SoundState CreateState()
		=> new SampleSoundState();

	public long? GetEndFrameExclusive(RenderContext context, SoundState state)
	{
		SampleSoundState sampleState = ValidateState(context, state);

		long? noteOffEnd = sampleState.NoteOffTime.HasValue
			? FrameTime.Ceiling(
				sampleState.NoteOffTime.Value,
				context.Configuration.SampleRate)
			: null;

		if (_definition.Loop.Mode != SampleLoopMode.None)
			return noteOffEnd;

		double sourceOffsetFrames =
			sampleState.PlaybackOffset.TotalSeconds * _data.SampleRate;
		double remainingSourceFrames = _data.FrameCount - sourceOffsetFrames;

		if (!(remainingSourceFrames > 0.0))
			return 0;

		if (!sampleState.PitchTrajectory.CanProjectEndEfficiently)
			return noteOffEnd;

		double baseStep = GetSourceFramesPerOutputFrame(context, sampleState);
		double requiredPitchPosition = remainingSourceFrames / baseStep;
		long naturalEnd = sampleState.PitchTrajectory.FindFrameAtOrAfterPosition(
			requiredPitchPosition);

		return noteOffEnd.HasValue
			? Math.Min(naturalEnd, noteOffEnd.Value)
			: naturalEnd;
	}

	public void Render(
		RenderContext context,
		SoundState state,
		long startFrame,
		int frameCount,
		Span<float> destination)
	{
		SampleSoundState sampleState = ValidateState(context, state);

		if (startFrame < 0)
			throw new ArgumentOutOfRangeException(nameof(startFrame));
		if (frameCount < 0)
			throw new ArgumentOutOfRangeException(nameof(frameCount));

		int outputChannelCount = context.Configuration.OutputChannelCount;
		int requiredSamples = checked(frameCount * outputChannelCount);
		if (destination.Length != requiredSamples)
		{
			throw new ArgumentException(
				"Destination length must exactly match frame count and output channel count.",
				nameof(destination));
		}

		if (frameCount == 0)
			return;

		if (_data.FrameCount == 0)
		{
			if (_definition.Loop.Mode == SampleLoopMode.None)
				sampleState.MarkNaturalEndReached(0);
			return;
		}

		long? endFrame = GetEndFrameExclusive(context, sampleState);
		if (endFrame.HasValue && startFrame >= endFrame.Value)
			return;

		int activeFrames = endFrame.HasValue
			? (int)Math.Min(frameCount, endFrame.Value - startFrame)
			: frameCount;

		double step = GetSourceFramesPerOutputFrame(context, sampleState);
		double sourceOffsetFrames =
			sampleState.PlaybackOffset.TotalSeconds * _data.SampleRate;

		int gainCount = checked(_data.ChannelCount * outputChannelCount);
		Span<float> gains = gainCount <= 128
			? stackalloc float[gainCount]
			: new float[gainCount];
		BuildSpatialGains(context.Configuration, sampleState.Position, gains);

		double pitchPosition =
			sampleState.PitchTrajectory.GetPosition(startFrame);

		for (int outputFrame = 0; outputFrame < activeFrames; outputFrame++)
		{
			long invocationFrame = startFrame + outputFrame;
			double sourcePosition =
				sourceOffsetFrames + pitchPosition * step;

			if (!IsSourcePositionActive(sourcePosition))
			{
				if (_definition.Loop.Mode == SampleLoopMode.None
					&& sourcePosition >= _data.FrameCount)
				{
					sampleState.MarkNaturalEndReached(invocationFrame);
					break;
				}

				pitchPosition +=
					sampleState.PitchTrajectory.GetMultiplier(invocationFrame);
				continue;
			}

			int destinationBase = outputFrame * outputChannelCount;

			for (int sourceChannel = 0; sourceChannel < _data.ChannelCount; sourceChannel++)
			{
				float sourceValue = ReadInterpolated(sourcePosition, sourceChannel);
				int gainBase = sourceChannel * outputChannelCount;

				for (int outputChannel = 0; outputChannel < outputChannelCount; outputChannel++)
				{
					destination[destinationBase + outputChannel] +=
						sourceValue * gains[gainBase + outputChannel];
				}
			}

			pitchPosition +=
				sampleState.PitchTrajectory.GetMultiplier(invocationFrame);
		}
	}


	private SampleSoundState ValidateState(RenderContext context, SoundState state)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(state);

		if (state is not SampleSoundState sampleState)
		{
			throw new ArgumentException(
				$"State must be {nameof(SampleSoundState)}.",
				nameof(state));
		}

		return sampleState;
	}

	private double GetSourceFramesPerOutputFrame(
		RenderContext context,
		SampleSoundState state)
		=> (double)_data.SampleRate
			/ context.Configuration.SampleRate
			* state.PitchMultiplier
			* state.PlaybackSpeedMultiplier;

	private void BuildSpatialGains(
		RenderConfiguration configuration,
		Vector3 invocationPosition,
		Span<float> gains)
	{
		int outputChannelCount = configuration.OutputChannelCount;

		for (int sourceChannel = 0; sourceChannel < _data.ChannelCount; sourceChannel++)
		{
			Vector3 sourcePosition =
				invocationPosition + _sourceChannelPositions[sourceChannel];

			for (int outputChannel = 0; outputChannel < outputChannelCount; outputChannel++)
			{
				gains[sourceChannel * outputChannelCount + outputChannel] =
					(float)Spatializer.CalculateGain(
						sourcePosition,
						configuration.OutputChannels[outputChannel]);
			}
		}
	}

	private bool IsSourcePositionActive(double sourcePosition)
	{
		if (!(sourcePosition >= 0.0))
			return false;

		if (_definition.Loop.Mode != SampleLoopMode.None)
			return _data.FrameCount != 0;

		return sourcePosition < _data.FrameCount;
	}

	private float ReadInterpolated(double sourcePosition, int channel)
	{
		double mapped = MapPosition(sourcePosition);
		long frame0 = (long)Math.Floor(mapped);
		double fraction = mapped - frame0;

		float sample0 = ReadFrame(frame0, channel);
		if (fraction == 0.0)
			return sample0;

		float sample1 = ReadFrame(frame0 + 1, channel);
		return sample0 + (sample1 - sample0) * (float)fraction;
	}

	private double MapPosition(double sourcePosition)
	{
		SampleLoop loop = _definition.Loop;

		if (loop.Mode == SampleLoopMode.None || sourcePosition < loop.EndFrameExclusive)
			return sourcePosition;

		long loopLength = loop.EndFrameExclusive - loop.StartFrame;

		if (loop.Mode == SampleLoopMode.Forward)
		{
			if (loopLength == 1)
				return loop.StartFrame;

			return loop.StartFrame + PositiveModulo(
				sourcePosition - loop.StartFrame,
				loopLength);
		}

		if (loopLength == 1)
			return loop.StartFrame;

		double endpointDistance = loopLength - 1.0;
		double period = endpointDistance * 2.0;
		double phase = PositiveModulo(
			sourcePosition - loop.StartFrame,
			period);

		return phase <= endpointDistance
			? loop.StartFrame + phase
			: loop.StartFrame + (period - phase);
	}

	private float ReadFrame(long frame, int channel)
	{
		SampleLoop loop = _definition.Loop;

		if (loop.Mode == SampleLoopMode.None)
		{
			if (frame < 0)
				return 0.0f;
			if (frame >= _data.FrameCount)
				return _data.FrameCount == 0
					? 0.0f
					: _data.GetSample(_data.FrameCount - 1, channel);
			return _data.GetSample(frame, channel);
		}

		if (frame < loop.EndFrameExclusive)
			return _data.GetSample(frame, channel);

		double mapped = MapPosition(frame);
		long mappedFrame = (long)Math.Round(mapped);

		if (mappedFrame < 0 || mappedFrame >= _data.FrameCount)
			return 0.0f;

		return _data.GetSample(mappedFrame, channel);
	}

	private static double PositiveModulo(double value, double modulus)
	{
		double result = value % modulus;
		return result < 0.0 ? result + modulus : result;
	}

	private static void ValidateLoop(SampleLoop loop, long frameCount)
	{
		if (loop.Mode == SampleLoopMode.None)
			return;

		if (loop.StartFrame >= frameCount)
			throw new ArgumentException("Sample loop starts beyond the decoded sample data.");
		if (loop.EndFrameExclusive > frameCount)
			throw new ArgumentException("Sample loop ends beyond the decoded sample data.");
	}

	private static Vector3[] ResolveSourceChannelPositions(
		SampleDefinition definition,
		int channelCount)
	{
		if (definition.SourceChannelPositions.Count != 0
			&& definition.SourceChannelPositions.Count != channelCount)
		{
			throw new ArgumentException(
				"Source channel position count must match decoded sample channel count.",
				nameof(definition));
		}

		Vector3[] result = new Vector3[channelCount];

		if (definition.SourceChannelPositions.Count != 0)
		{
			for (int i = 0; i < channelCount; i++)
				result[i] = definition.SourceChannelPositions[i];
			return result;
		}

		if (channelCount == 2)
		{
			result[0] = new Vector3(-1.0f, 0.0f, 0.0f);
			result[1] = new Vector3(+1.0f, 0.0f, 0.0f);
		}

		return result;
	}
}
