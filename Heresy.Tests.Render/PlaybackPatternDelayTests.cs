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
public sealed class PlaybackPatternDelayTests
{
	[Test]
	public void VolumeSlideContinuesAcrossExtraDelayedRowSpan()
	{
		ObjectId sourceId = (ObjectId)10U;
		DataPatternDefinition pattern = new((ObjectId)1U, "Pattern")
		{
			RowCount = 1,
			ChannelCount = 1,
		};
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Note = new StartPatternNote(sourceId);
		cell.Effects.Add(new SetNoteVolumePatternEffect(0.5));
		cell.Effects.Add(new TrackerVolumeSlidePatternEffect(0x10));
		cell.Effects.Add(new TrackerPatternDelayPatternEffect(1));

		NoteScheduleBuilder output = new();
		PatternNoteProcessor.GenerateNotes(
			pattern,
			new SequencingContext(),
			output,
			out TimeSpan duration);

		Assert.That(duration, Is.EqualTo(TimeSpan.FromMilliseconds(240)));

		SampleSound sound = ConstantSample(1.0f, 400, 1000);
		PlaybackSession session = Session(
			output.Freeze(),
			new TestResolver((sourceId, false, sound)));

		session.Render(0, 241, new float[241]);

		Assert.That(
			session.GetChannelState(0).NoteVolume,
			Is.EqualTo(42.0 / 64.0).Within(1e-12));
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
