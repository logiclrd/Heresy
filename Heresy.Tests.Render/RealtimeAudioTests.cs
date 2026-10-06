using System;

using AwesomeAssertions;

using Heresy.Core.Sequencing;
using Heresy.Render.Configuration;
using Heresy.Render.Playback;
using Heresy.Render.Realtime;
using Heresy.Render.Sounds;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class RealtimeAudioTests
{
	[TestCase(0, 2)]
	[TestCase(-1, 2)]
	public void AudioOutputFormatRejectsInvalidSampleRate(
		int sampleRate,
		int channels)
	{
		Action act = () =>
			_ = new AudioOutputFormat(
				sampleRate,
				channels);

		act.Should().Throw<ArgumentOutOfRangeException>();
	}

	[TestCase(48000, 0)]
	[TestCase(48000, -1)]
	public void AudioOutputFormatRejectsInvalidChannelCount(
		int sampleRate,
		int channels)
	{
		Action act = () =>
			_ = new AudioOutputFormat(
				sampleRate,
				channels);

		act.Should().Throw<ArgumentOutOfRangeException>();
	}

	[Test]
	public void PlaybackSessionAudioSourceReportsSessionFormat()
	{
		PlaybackSession session =
			CreateSession(
				sampleRate: 48000,
				channels: 2);
		PlaybackSessionAudioSource source =
			new(session);

		source.Format.Should().Be(
			new AudioOutputFormat(
				48000,
				2));
	}

	[Test]
	public void PlaybackSessionAudioSourceRendersSequentialBlocks()
	{
		PlaybackSession session =
			CreateSession(
				sampleRate: 4,
				channels: 2);
		PlaybackSessionAudioSource source =
			new(session);
		float[] first = new float[4];
		float[] second = new float[6];

		source.Render(2, first);
		source.Render(3, second);

		session.NextFrame.Should().Be(5);
		first.Should().OnlyContain(value => value == 0.0f);
		second.Should().OnlyContain(value => value == 0.0f);
	}

	[Test]
	public void PlaybackSessionAudioSourceRequiresExactDestinationSize()
	{
		PlaybackSessionAudioSource source =
			new(
				CreateSession(
					sampleRate: 48000,
					channels: 2));
		float[] wrongSize = new float[3];

		Action act = () =>
			source.Render(
				2,
				wrongSize);

		act.Should().Throw<ArgumentException>();
	}

	private static PlaybackSession CreateSession(
		int sampleRate,
		int channels)
	{
		OutputChannelConfiguration[] outputs =
			new OutputChannelConfiguration[channels];
		for (int index = 0; index < outputs.Length; index++)
		{
			outputs[index] =
				new OutputChannelConfiguration(
					System.Numerics.Vector3.Zero,
					positionalImportance: 0.0);
		}

		return new PlaybackSession(
			new RenderContext(
				new RenderConfiguration(
					sampleRate,
					outputs)),
			new NoteScheduleBuilder().Freeze(),
			new EmptyResolver());
	}

	private sealed class EmptyResolver : ISoundResolver
	{
		public bool TryResolve(
			Heresy.Core.Objects.ObjectId sourceId,
			bool mixdown,
			out ISound? sound)
		{
			sound = null;
			return false;
		}
	}
}
