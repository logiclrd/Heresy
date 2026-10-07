using System.Numerics;

using AwesomeAssertions;

using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Playback;
using Heresy.Render.Configuration;
using Heresy.Render.Realtime;

using NUnit.Framework;

namespace Heresy.Tests.Playback;

[TestFixture]
public sealed class FmSynthPlaybackIntegrationTests
{
	[Test]
	public void PatternPlaybackResolvesPersistentFmSynthFromSnapshot()
	{
		SongDocument document = new();

		ObjectId synthId = document.AllocateObjectId();
		document.Add(
			new FmSynthDefinition(
				synthId,
				"Constant",
				new FmSynthGraph(
					[
						new FmConstantNode(0, 0.25),
					],
					outputNodeId: 0)));

		ObjectId patternId = document.AllocateObjectId();
		DataPatternDefinition pattern =
			new(patternId, "FM")
			{
				RowCount = 1,
				ChannelCount = 1,
			};
		PatternCell cell =
			pattern.Grid.GetOrCreateCell(0, 0);
		cell.SourceId = synthId;
		cell.Note = new StartPatternNote();
		document.Add(pattern);

		PlaybackRequestAudioSourceFactory factory =
			new(MonoConfiguration(sampleRate: 100));
		IAudioOutputSource source =
			factory.Create(
				PatternPlaybackRequest.Create(
					document,
					patternId,
					repeat: false));
		float[] output = new float[1];

		source.Render(1, output);

		output[0].Should().BeApproximately(0.25f, 1e-6f);
	}

	private static RenderConfiguration MonoConfiguration(
		int sampleRate)
		=> new(
			sampleRate,
			new[]
			{
				new OutputChannelConfiguration(
					Vector3.Zero,
					positionalImportance: 0.0),
			});
}
