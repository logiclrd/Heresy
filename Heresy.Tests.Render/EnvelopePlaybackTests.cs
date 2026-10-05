using System;
using System.Collections.Generic;
using System.Numerics;

using Heresy.Core.Assets;
using Heresy.Core.Envelopes;
using Heresy.Core.Objects;
using Heresy.Core.Samples;
using Heresy.Core.Sequencing;
using Heresy.Core.Timing;
using Heresy.Render.Configuration;
using Heresy.Render.Envelopes;
using Heresy.Render.Playback;
using Heresy.Render.Sounds;
using Heresy.Render.Timing;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class EnvelopePlaybackTests
{
	[Test]
	public void AdsrCurveRendersAttackDecaySustainAndRelease()
	{
		AdsrEnvelopeDefinition definition =
			new((ObjectId)1U, "Envelope")
			{
				Attack = TimeSpan.FromSeconds(1),
				Decay = TimeSpan.FromSeconds(1),
				SustainLevel = 0.25,
				Release = TimeSpan.FromSeconds(2),
			};
		AdsrEnvelopeCurve curve = new(definition);

		Assert.That(curve.GetValue(0, 4, null), Is.EqualTo(0.0));
		Assert.That(curve.GetValue(2, 4, null), Is.EqualTo(0.5));
		Assert.That(curve.GetValue(4, 4, null), Is.EqualTo(1.0));
		Assert.That(curve.GetValue(6, 4, null), Is.EqualTo(0.625));
		Assert.That(curve.GetValue(8, 4, null), Is.EqualTo(0.25));

		Assert.That(
			curve.GetValue(12, 4, noteOffActiveFrame: 8),
			Is.EqualTo(0.125).Within(1e-12));
		Assert.That(
			curve.GetValue(16, 4, noteOffActiveFrame: 8),
			Is.EqualTo(0.0));
	}

	[Test]
	public void AdsrCurveDoesNotClampNegativeEnvelopeValues()
	{
		AdsrEnvelopeDefinition definition =
			new((ObjectId)1U, "Envelope")
			{
				Attack = TimeSpan.Zero,
				Decay = TimeSpan.Zero,
				SustainLevel = -0.5,
				Release = TimeSpan.FromSeconds(1),
			};
		AdsrEnvelopeCurve curve = new(definition);

		Assert.That(curve.GetValue(0, 4, null), Is.EqualTo(-0.5));
		Assert.That(
			curve.GetValue(2, 4, noteOffActiveFrame: 0),
			Is.EqualTo(-0.25).Within(1e-12));
	}

	[Test]
	public void DisabledEnvelopeHoldsItsPositionAndResumes()
	{
		EnvelopePlaybackState state = new(
			new ActiveFrameEnvelopeCurve(),
			startFrame: 10,
			sampleRate: 100);

		Assert.That(state.GetValue(20), Is.EqualTo(10.0));

		state.SetEnabled(20, false);
		Assert.That(state.GetValue(30), Is.EqualTo(10.0));
		Assert.That(state.ActiveFrame, Is.EqualTo(10));

		state.SetEnabled(30, true);
		Assert.That(state.GetValue(35), Is.EqualTo(15.0));
		Assert.That(state.ActiveFrame, Is.EqualTo(15));
	}

	[Test]
	public void NoteOffWhileDisabledStartsReleaseFromHeldEnvelopePosition()
	{
		EnvelopePlaybackState state = new(
			new ReleaseProbeEnvelopeCurve(),
			startFrame: 0,
			sampleRate: 100);

		state.GetValue(20);
		state.SetEnabled(20, false);
		state.NoteOff(50);

		Assert.That(state.NoteOffActiveFrame, Is.EqualTo(20));
		Assert.That(state.GetValue(80), Is.EqualTo(20_020.0));

		state.SetEnabled(80, true);
		Assert.That(state.GetValue(85), Is.EqualTo(20_025.0));
	}

	[Test]
	public void HistoricalEnvelopeQueriesRemainStableAfterPauseResume()
	{
		EnvelopePlaybackState state = new(
			new ActiveFrameEnvelopeCurve(),
			startFrame: 0,
			sampleRate: 100);

		Assert.That(state.GetValue(10), Is.EqualTo(10.0));
		state.SetEnabled(10, false);
		state.SetEnabled(20, true);
		Assert.That(state.GetValue(30), Is.EqualTo(20.0));

		Assert.That(state.GetValue(5), Is.EqualTo(5.0));
		Assert.That(state.ActiveFrame, Is.EqualTo(20));
	}

	[Test]
	public void VolumeEnvelopeMultipliesVoiceOutput()
	{
		ObjectId sourceId = (ObjectId)10U;
		AdsrEnvelopeCurve volumeEnvelope = new(
			new AdsrEnvelopeDefinition((ObjectId)1U, "Volume")
			{
				Attack = TimeSpan.FromSeconds(1),
				Decay = TimeSpan.Zero,
				SustainLevel = 1.0,
				Release = TimeSpan.Zero,
			});

		PlaybackSession session = Session(
			4,
			Schedule(
				Event(
					Frame(0, 4),
					new StartNoteCommand(sourceId))),
			new TestResolver(
				(sourceId,
					new ConstantSound(
						1.0f,
						new EnvelopeConfigurationSnapshot(
							Volume: volumeEnvelope)))));

		float[] output = new float[4];
		session.Render(0, 4, output);

		Assert.That(
			output,
			Is.EqualTo(new[] { 0.0f, 0.25f, 0.5f, 0.75f })
				.Within(1e-6));
	}

	[Test]
	public void VolumeEnvelopeMayInvertPhase()
	{
		ObjectId sourceId = (ObjectId)10U;
		AdsrEnvelopeCurve volumeEnvelope = new(
			new AdsrEnvelopeDefinition((ObjectId)1U, "Volume")
			{
				Attack = TimeSpan.Zero,
				Decay = TimeSpan.Zero,
				SustainLevel = -0.5,
				Release = TimeSpan.Zero,
			});

		PlaybackSession session = Session(
			4,
			Schedule(Event(TimeSpan.Zero, new StartNoteCommand(sourceId))),
			new TestResolver(
				(sourceId,
					new ConstantSound(
						1.0f,
						new EnvelopeConfigurationSnapshot(
							Volume: volumeEnvelope)))));

		float[] output = new float[1];
		session.Render(0, 1, output);

		Assert.That(output[0], Is.EqualTo(-0.5f).Within(1e-6));
	}

	[Test]
	public void SemanticEnvelopeEnableCommandPausesAndResumesVolumeEnvelope()
	{
		ObjectId sourceId = (ObjectId)10U;
		AdsrEnvelopeCurve volumeEnvelope = new(
			new AdsrEnvelopeDefinition((ObjectId)1U, "Volume")
			{
				Attack = TimeSpan.FromSeconds(1),
				Decay = TimeSpan.Zero,
				SustainLevel = 1.0,
				Release = TimeSpan.Zero,
			});

		PlaybackSession session = Session(
			4,
			Schedule(
				Event(
					Frame(0, 4),
					new StartNoteCommand(sourceId)),
				Event(
					Frame(2, 4),
					new SetEnvelopeEnabledCommand(
						EnvelopeTarget.Volume,
						false)),
				Event(
					Frame(4, 4),
					new SetEnvelopeEnabledCommand(
						EnvelopeTarget.Volume,
						true))),
			new TestResolver(
				(sourceId,
					new ConstantSound(
						1.0f,
						new EnvelopeConfigurationSnapshot(
							Volume: volumeEnvelope)))));

		float[] output = new float[6];
		session.Render(0, 6, output);

		Assert.That(
			output,
			Is.EqualTo(
				new[] { 0.0f, 0.25f, 0.5f, 0.5f, 0.5f, 0.75f })
				.Within(1e-6));
	}

	[Test]
	public void NoteOffStartsVolumeEnvelopeRelease()
	{
		ObjectId sourceId = (ObjectId)10U;
		AdsrEnvelopeCurve volumeEnvelope = new(
			new AdsrEnvelopeDefinition((ObjectId)1U, "Volume")
			{
				Attack = TimeSpan.Zero,
				Decay = TimeSpan.Zero,
				SustainLevel = 1.0,
				Release = TimeSpan.FromSeconds(1),
			});

		PlaybackSession session = Session(
			4,
			Schedule(
				Event(
					Frame(0, 4),
					new StartNoteCommand(sourceId)),
				Event(
					Frame(2, 4),
					new NoteOffCommand())),
			new TestResolver(
				(sourceId,
					new ConstantSound(
						1.0f,
						new EnvelopeConfigurationSnapshot(
							Volume: volumeEnvelope)))));

		float[] output = new float[7];
		session.Render(0, 7, output);

		Assert.That(
			output,
			Is.EqualTo(
				new[] { 1.0f, 1.0f, 1.0f, 0.75f, 0.5f, 0.25f, 0.0f })
				.Within(1e-6));
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
		=> FrameTime.FrameStartTime(frame, sampleRate);

	private sealed class ActiveFrameEnvelopeCurve : IEnvelopeCurve
	{
		public double GetValue(
			long activeFrame,
			int sampleRate,
			long? noteOffActiveFrame)
			=> activeFrame;
	}

	private sealed class ReleaseProbeEnvelopeCurve : IEnvelopeCurve
	{
		public double GetValue(
			long activeFrame,
			int sampleRate,
			long? noteOffActiveFrame)
			=> activeFrame
				+ 1000.0 * (noteOffActiveFrame ?? 0);
	}

	private sealed class ConstantSound : ISound
	{
		private readonly float _value;
		private readonly EnvelopeConfigurationSnapshot _envelopes;

		public ConstantSound(
			float value,
			EnvelopeConfigurationSnapshot envelopes)
		{
			_value = value;
			_envelopes = envelopes;
		}

		public NoteConfigurationSnapshot SnapshotNoteConfiguration()
			=> new(
				NewNotePolicy.Cut,
				envelopes: _envelopes);

		public SoundState CreateState()
			=> new ConstantSoundState();

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
			for (int i = 0; i < destination.Length; i++)
				destination[i] += _value;
		}
	}

	private sealed class ConstantSoundState : SoundState
	{
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
