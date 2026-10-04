using System;
using System.Collections.Generic;
using System.Numerics;

using Heresy.Core.Assets;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Samples;
using Heresy.Core.Sequencing;
using Heresy.Render.Configuration;
using Heresy.Render.Playback;
using Heresy.Render.Samples;
using Heresy.Render.Sounds;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class PlaybackNoteCutAndDelayTests
{
	[Test]
	public void SD3CreatesNoVoiceUntilTickThree()
	{
		ObjectId sourceId = (ObjectId)10U;
		SampleSound sound = ConstantSample(1.0f, 200, 1000);
		NoteSchedule schedule = ResolvePattern(
			sourceId,
			new TrackerNoteDelayPatternEffect(3));
		PlaybackSession session = Session(
			schedule,
			new TestResolver((sourceId, false, sound)));

		float[] before = new float[60];
		session.Render(0, before.Length, before);

		Assert.That(session.GetChannelState(0).CurrentVoice, Is.Null);
		Assert.That(before, Is.All.EqualTo(0.0f));

		float[] trigger = new float[1];
		session.Render(60, 1, trigger);

		Assert.That(session.GetChannelState(0).CurrentVoice, Is.Not.Null);
		Assert.That(trigger[0], Is.EqualTo(1.0f));
	}

	[Test]
	public void SC3RemovesCurrentVoiceWhenTickThreeEventIsProcessed()
	{
		ObjectId sourceId = (ObjectId)10U;
		SampleSound sound = ConstantSample(1.0f, 200, 1000);
		NoteSchedule schedule = ResolvePattern(
			sourceId,
			new TrackerNoteCutPatternEffect(3));
		PlaybackSession session = Session(
			schedule,
			new TestResolver((sourceId, false, sound)));

		session.Render(0, 60, new float[60]);

		Assert.That(session.GetChannelState(0).CurrentVoice, Is.Not.Null);

		session.Render(60, 1, new float[1]);

		Assert.That(session.GetChannelState(0).CurrentVoice, Is.Null);
	}

	private static NoteSchedule ResolvePattern(
		ObjectId sourceId,
		PatternEffect effect)
	{
		DataPatternDefinition pattern = new((ObjectId)1U, "Pattern")
		{
			RowCount = 1,
			ChannelCount = 1,
		};
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Note = new StartPatternNote(sourceId);
		cell.Effects.Add(effect);

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out _);
		return output.Freeze();
	}

	private static PlaybackSession Session(
		NoteSchedule schedule,
		ISoundResolver resolver)
		=> new(
			new RenderContext(
				new RenderConfiguration(
					1000,
					new[]
					{
						new OutputChannelConfiguration(
							Vector3.Zero,
							positionalImportance: 0.0),
					})),
			schedule,
			resolver);

	private static SampleSound ConstantSample(
		float value,
		int frameCount,
		int sampleRate)
	{
		float[] data = new float[frameCount];
		Array.Fill(data, value);

		return new SampleSound(
			new SampleDefinition(
				(ObjectId)1U,
				"Sample",
				new ExternalAssetReference("sample.raw")),
			new MemorySampleData(sampleRate, 1, data));
	}

	private sealed class TestResolver : ISoundResolver
	{
		private readonly Dictionary<(ObjectId Id, bool Mixdown), ISound> _sounds = [];

		public TestResolver(params (ObjectId Id, bool Mixdown, ISound Sound)[] sounds)
		{
			foreach ((ObjectId id, bool mixdown, ISound sound) in sounds)
				_sounds.Add((id, mixdown), sound);
		}

		public bool TryResolve(ObjectId sourceId, bool mixdown, out ISound? sound)
			=> _sounds.TryGetValue((sourceId, mixdown), out sound);
	}
}
