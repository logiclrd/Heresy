using System;
using System.Collections.Generic;
using System.Numerics;

using Heresy.Core.Assets;
using Heresy.Core.Objects;
using Heresy.Core.Samples;
using Heresy.Core.Sequencing;
using Heresy.Core.Timing;
using Heresy.Render.Configuration;
using Heresy.Render.Playback;
using Heresy.Render.Samples;
using Heresy.Render.Sounds;
using Heresy.Render.Timing;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class TrackerGlissandoTests
{
	[Test]
	public void ContinuousHiddenPathQuantizesToNextSemitone()
	{
		TrackerTonePortamentoCurve curve = new(
			initialMultiplier: 1.0,
			targetMultiplier: 2.0,
			linearUnitsPerTick: 16.0,
			tickDuration: TimeSpan.FromMilliseconds(20),
			ticksPerRow: 6,
			sampleRate: 1000,
			glissando: true);

		double oneSemitone = Math.Pow(2.0, 1.0 / 12.0);

		Assert.That(curve.GetMultiplier(0), Is.EqualTo(1.0).Within(1e-14));
		Assert.That(
			curve.GetMultiplier(20),
			Is.EqualTo(1.0).Within(1e-14));
		Assert.That(
			curve.GetMultiplier(80),
			Is.EqualTo(1.0).Within(1e-14));
		Assert.That(
			curve.GetMultiplier(100),
			Is.EqualTo(oneSemitone).Within(1e-14));
	}

	[Test]
	public void GlissandoStepOccursWhenContinuousPathCrossesSemitoneBoundary()
	{
		TrackerTonePortamentoCurve curve = new(
			1.0,
			2.0,
			16.0,
			TimeSpan.FromMilliseconds(20),
			6,
			1000,
			glissando: true);

		double oneSemitone = Math.Pow(2.0, 1.0 / 12.0);

		Assert.That(
			curve.GetMultiplier(95),
			Is.EqualTo(1.0).Within(1e-14));
		Assert.That(
			curve.GetMultiplier(96),
			Is.EqualTo(oneSemitone).Within(1e-14));
		Assert.That(
			curve.GetMultiplier(119),
			Is.EqualTo(oneSemitone).Within(1e-14));
	}

	[Test]
	public void GlissandoRetainsContinuousUnderlyingPortamentoPosition()
	{
		TrackerTonePortamentoCurve curve = new(
			1.0,
			2.0,
			16.0,
			TimeSpan.FromMilliseconds(20),
			6,
			1000,
			glissando: true);

		Assert.That(
			curve.GetContinuousMultiplier(100),
			Is.EqualTo(
				Math.Pow(2.0, (80.0 * 5.0 / 6.0) / 768.0))
				.Within(1e-14));

		Assert.That(
			curve.GetMultiplier(100),
			Is.EqualTo(
				Math.Pow(2.0, 1.0 / 12.0))
				.Within(1e-14));
	}

	[Test]
	public void DownwardGlissandoUsesSameNextSemitoneRule()
	{
		TrackerTonePortamentoCurve curve = new(
			2.0,
			1.0,
			16.0,
			TimeSpan.FromMilliseconds(20),
			6,
			1000,
			glissando: true);

		Assert.That(
			curve.GetMultiplier(20),
			Is.EqualTo(2.0).Within(1e-14));
		Assert.That(
			curve.GetMultiplier(80),
			Is.EqualTo(2.0).Within(1e-14));
		Assert.That(
			curve.GetMultiplier(100),
			Is.EqualTo(Math.Pow(2.0, 11.0 / 12.0))
				.Within(1e-14));
	}

	[Test]
	public void G00ContinuationPreservesHiddenContinuousPosition()
	{
		ObjectId sourceId = (ObjectId)10U;
		SampleSound sound = RampSample(2000, 1000);

		PlaybackSession session = Session(
			1000,
			Schedule(
				Event(
					Frame(0, 1000),
					new StartNoteCommand(sourceId),
					new SetTonePortamentoCommand(
						16.0,
						new StartNoteCommand(
							sourceId,
							PitchMultiplier: 2.0),
						Glissando: true)),
				Event(
					Frame(120, 1000),
					new ClearTonePortamentoCommand()),
				Event(
					Frame(120, 1000),
					new SetTonePortamentoCommand(
						16.0,
						Glissando: true)),
				Event(
					Frame(240, 1000),
					new ClearTonePortamentoCommand())),
			new TestResolver((sourceId, false, sound)));

		session.Render(0, 160, new float[160]);

		PitchTrajectory trajectory =
			session.GetChannelState(0).CurrentVoice!
				.SoundState.PitchTrajectory;

		double oneSemitone = Math.Pow(2.0, 1.0 / 12.0);

		Assert.That(
			trajectory.GetMultiplier(100),
			Is.EqualTo(oneSemitone).Within(1e-12));
		Assert.That(
			trajectory.GetMultiplier(140),
			Is.EqualTo(oneSemitone).Within(1e-12));
	}

	[Test]
	public void SplitRenderingWithGlissandoMatchesSingleCall()
	{
		ObjectId sourceId = (ObjectId)10U;
		SampleSound sound = RampSample(2000, 1000);
		NoteSchedule schedule = Schedule(
			Event(
				TimeSpan.Zero,
				new StartNoteCommand(sourceId),
				new SetTonePortamentoCommand(
					16.0,
					new StartNoteCommand(
						sourceId,
						PitchMultiplier: 2.0),
					Glissando: true)),
			Event(
				Frame(120, 1000),
				new ClearTonePortamentoCommand()));
		TestResolver resolver =
			new((sourceId, false, sound));

		PlaybackSession oneSession =
			Session(1000, schedule, resolver);
		float[] one = new float[180];
		oneSession.Render(0, one.Length, one);

		PlaybackSession splitSession =
			Session(1000, schedule, resolver);
		float[] split = new float[180];
		splitSession.Render(
			0,
			17,
			split.AsSpan(0, 17));
		splitSession.Render(
			17,
			43,
			split.AsSpan(17, 43));
		splitSession.Render(
			60,
			60,
			split.AsSpan(60, 60));
		splitSession.Render(
			120,
			60,
			split.AsSpan(120, 60));

		Assert.That(split, Is.EqualTo(one));
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

	private static NoteSchedule Schedule(
		params NoteEvent[] events)
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

	private static TimeSpan Frame(
		long frame,
		int sampleRate)
		=> FrameTime.FrameStartTime(
			frame,
			sampleRate);

	private static SampleSound RampSample(
		int frameCount,
		int sampleRate)
	{
		float[] data = new float[frameCount];
		for (int i = 0; i < data.Length; i++)
			data[i] = i;

		return new SampleSound(
			new SampleDefinition(
				(ObjectId)1U,
				"Sample",
				new ExternalAssetReference("sample.raw")),
			new MemorySampleData(
				sampleRate,
				1,
				data));
	}

	private sealed class TestResolver : ISoundResolver
	{
		private readonly Dictionary<
			(ObjectId Id, bool Mixdown),
			ISound> _sounds = [];

		public TestResolver(
			params (ObjectId Id, bool Mixdown, ISound Sound)[] sounds)
		{
			foreach (
				(ObjectId id, bool mixdown, ISound sound)
				in sounds)
			{
				_sounds.Add(
					(id, mixdown),
					sound);
			}
		}

		public bool TryResolve(
			ObjectId sourceId,
			bool mixdown,
			out ISound? sound)
			=> _sounds.TryGetValue(
				(sourceId, mixdown),
				out sound);
	}
}
