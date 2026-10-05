using System;
using System.Collections.Generic;
using System.Numerics;

using Heresy.Core.Objects;
using Heresy.Core.Sequencing;
using Heresy.Core.Timing;
using Heresy.Render.Configuration;
using Heresy.Render.Envelopes;
using Heresy.Render.Filters;
using Heresy.Render.Playback;
using Heresy.Render.Sounds;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class EnvelopeParameterPlaybackTests
{
	[Test]
	public void PitchEnvelopeIsAdditiveInLog2PitchSpace()
	{
		ObjectId sourceId = (ObjectId)10U;
		PlaybackSession session = Session(
			4,
			Schedule(
				Event(
					TimeSpan.Zero,
					new StartNoteCommand(
						sourceId,
						PitchMultiplier: 1.5))),
			new TestResolver(
				(sourceId,
					new ProbeSound(
						ProbeKind.Pitch,
						new EnvelopeConfigurationSnapshot(
							Pitch: new ConstantCurve(1.0))))));

		float[] output = new float[1];
		session.Render(0, 1, output);

		Assert.That(output[0], Is.EqualTo(3.0f).Within(1e-6));
	}

	[Test]
	public void DisabledPitchEnvelopeHoldsAndThenResumesItsLogarithmicOffset()
	{
		ObjectId sourceId = (ObjectId)10U;
		PlaybackSession session = Session(
			4,
			Schedule(
				Event(
					Frame(0, 4),
					new StartNoteCommand(sourceId)),
				Event(
					Frame(1, 4),
					new SetEnvelopeEnabledCommand(
						Heresy.Core.Envelopes.EnvelopeTarget.Pitch,
						false)),
				Event(
					Frame(3, 4),
					new SetEnvelopeEnabledCommand(
						Heresy.Core.Envelopes.EnvelopeTarget.Pitch,
						true))),
			new TestResolver(
				(sourceId,
					new ProbeSound(
						ProbeKind.Pitch,
						new EnvelopeConfigurationSnapshot(
							Pitch: new ActiveFrameCurve())))));

		float[] output = new float[5];
		session.Render(0, output.Length, output);

		Assert.That(
			output,
			Is.EqualTo(new[] { 1.0f, 2.0f, 2.0f, 2.0f, 4.0f })
				.Within(1e-6));
	}

	[Test]
	public void PanningEnvelopeUsesRelativeExcursionAroundBasePan()
	{
		ObjectId sourceId = (ObjectId)10U;
		PlaybackSession session = Session(
			4,
			Schedule(
				Event(
					TimeSpan.Zero,
					new SetSpatialPositionCommand(
						new Vector3(0.5f, 0.0f, 0.0f)),
					new StartNoteCommand(sourceId))),
			new TestResolver(
				(sourceId,
					new ProbeSound(
						ProbeKind.PositionX,
						new EnvelopeConfigurationSnapshot(
							Panning:
								new OffsetActiveFrameCurve(-1.0))))));

		float[] output = new float[3];
		session.Render(0, output.Length, output);

		Assert.That(
			output,
			Is.EqualTo(new[] { 0.0f, 0.5f, 1.0f })
				.Within(1e-6));
	}

	[Test]
	public void PanningEnvelopeIsIgnoredForSurroundVoice()
	{
		ObjectId sourceId = (ObjectId)10U;
		PlaybackSession session = Session(
			4,
			Schedule(
				Event(
					TimeSpan.Zero,
					new SetSurroundCommand(true),
					new StartNoteCommand(sourceId))),
			new TestResolver(
				(sourceId,
					new ProbeSound(
						ProbeKind.PositionX,
						new EnvelopeConfigurationSnapshot(
							Panning: new ConstantCurve(1.0))))));

		float[] output = new float[1];
		session.Render(0, 1, output);

		Assert.That(output[0], Is.EqualTo(0.0f).Within(1e-6));
	}

	[Test]
	public void FilterEnvelopeSetsEffectiveCutoffWithoutChangingPersistentBaseline()
	{
		ObjectId sourceId = (ObjectId)10U;
		PlaybackSession session = Session(
			48000,
			Schedule(
				Event(
					TimeSpan.Zero,
					new SetResonantFilterCommand(0.8, 0.4),
					new StartNoteCommand(sourceId))),
			new TestResolver(
				(sourceId,
					new ProbeSound(
						ProbeKind.Constant,
						new EnvelopeConfigurationSnapshot(
							Filter: new ConstantCurve(0.25))))));

		session.Render(0, 1, new float[1]);

		PlaybackChannelState channel = session.GetChannelState(0);
		Assert.That(
			channel.FilterParameters,
			Is.EqualTo(new ResonantFilterParameters(0.8, 0.4)));
		Assert.That(
			channel.CurrentVoice!.FilterState.Parameters,
			Is.EqualTo(new ResonantFilterParameters(0.25, 0.4)));
	}

	[Test]
	public void FilterEnvelopeClampsToNormalizedCutoffDomain()
	{
		ObjectId lowId = (ObjectId)10U;
		ObjectId highId = (ObjectId)11U;
		TestResolver resolver = new(
			(lowId,
				new ProbeSound(
					ProbeKind.Constant,
					new EnvelopeConfigurationSnapshot(
						Filter: new ConstantCurve(-2.0)))),
			(highId,
				new ProbeSound(
					ProbeKind.Constant,
					new EnvelopeConfigurationSnapshot(
						Filter: new ConstantCurve(3.0)))));

		PlaybackSession low = Session(
			48000,
			Schedule(
				Event(
					TimeSpan.Zero,
					new SetResonantFilterCommand(0.8, 0.4),
					new StartNoteCommand(lowId))),
			resolver);
		low.Render(0, 1, new float[1]);
		Assert.That(
			low.GetChannelState(0).CurrentVoice!.FilterState.Parameters.Cutoff,
			Is.EqualTo(0.0));

		PlaybackSession high = Session(
			48000,
			Schedule(
				Event(
					TimeSpan.Zero,
					new SetResonantFilterCommand(0.8, 0.4),
					new StartNoteCommand(highId))),
			resolver);
		high.Render(0, 1, new float[1]);
		Assert.That(
			high.GetChannelState(0).CurrentVoice!.FilterState.Parameters.Cutoff,
			Is.EqualTo(1.0));
	}

	private static PlaybackSession Session(
		int sampleRate,
		NoteSchedule schedule,
		ISoundResolver resolver)
		=> new(
			new RenderContext(
				new RenderConfiguration(
					sampleRate,
					new[]
					{
						new OutputChannelConfiguration(
							Vector3.Zero,
							positionalImportance: 0.0),
					})),
			schedule,
			resolver);

	private static NoteSchedule Schedule(params NoteEvent[] events)
	{
		NoteScheduleBuilder builder = new();
		foreach (NoteEvent noteEvent in events)
			builder.Append(noteEvent);
		return builder.Freeze();
	}

	private static NoteEvent Event(
		TimeSpan time,
		params NoteCommand[] commands)
		=> new(
			new MusicalTime(time, 0.0),
			ChannelTarget.Physical(0),
			commands);

	private static TimeSpan Frame(long frame, int sampleRate)
		=> Heresy.Render.Timing.FrameTime.FrameStartTime(
			frame,
			sampleRate);

	private enum ProbeKind
	{
		Constant,
		Pitch,
		PositionX,
	}

	private sealed class ProbeSound : ISound
	{
		private readonly ProbeKind _kind;
		private readonly EnvelopeConfigurationSnapshot _envelopes;

		public ProbeSound(
			ProbeKind kind,
			EnvelopeConfigurationSnapshot envelopes)
		{
			_kind = kind;
			_envelopes = envelopes;
		}

		public NoteConfigurationSnapshot SnapshotNoteConfiguration()
			=> new(
				NewNotePolicy.Cut,
				envelopes: _envelopes);

		public SoundState CreateState()
			=> new ProbeState();

		public long? GetEndFrameExclusive(
			RenderContext context,
			SoundState state)
			=> null;

		public void Render(
			RenderContext context,
			SoundState state,
			long startFrame,
			int frameCount,
			Span<float> destination)
		{
			for (int frame = 0; frame < frameCount; frame++)
			{
				destination[frame] += _kind switch
				{
					ProbeKind.Constant => 1.0f,
					ProbeKind.Pitch =>
						(float)(
							state.PitchMultiplier
							* state.PitchTrajectory.GetMultiplier(
								startFrame + frame)),
					ProbeKind.PositionX => state.Position.X,
					_ => throw new InvalidOperationException(),
				};
			}
		}
	}

	private sealed class ProbeState : SoundState
	{
	}

	private sealed class ConstantCurve : IEnvelopeCurve
	{
		private readonly double _value;

		public ConstantCurve(double value)
			=> _value = value;

		public double GetValue(
			long activeFrame,
			int sampleRate,
			long? noteOffActiveFrame)
			=> _value;
	}

	private sealed class ActiveFrameCurve : IEnvelopeCurve
	{
		public double GetValue(
			long activeFrame,
			int sampleRate,
			long? noteOffActiveFrame)
			=> activeFrame;
	}

	private sealed class OffsetActiveFrameCurve : IEnvelopeCurve
	{
		private readonly double _offset;

		public OffsetActiveFrameCurve(double offset)
			=> _offset = offset;

		public double GetValue(
			long activeFrame,
			int sampleRate,
			long? noteOffActiveFrame)
			=> _offset + activeFrame;
	}

	private sealed class TestResolver : ISoundResolver
	{
		private readonly Dictionary<ObjectId, ISound> _sounds = [];

		public TestResolver(params (ObjectId Id, ISound Sound)[] sounds)
		{
			foreach ((ObjectId id, ISound sound) in sounds)
				_sounds.Add(id, sound);
		}

		public bool TryResolve(
			ObjectId sourceId,
			bool mixdown,
			out ISound? sound)
			=> _sounds.TryGetValue(sourceId, out sound);
	}
}
