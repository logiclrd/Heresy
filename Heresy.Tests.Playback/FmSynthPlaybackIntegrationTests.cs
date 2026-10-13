using System.Numerics;

using AwesomeAssertions;

using Heresy.Core.Filters;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Playback;
using Heresy.Render.Configuration;
using Heresy.Render.Filters;
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


	[Test]
	public void FrozenPatternPlaybackBindsNestedSynthSourceBeforeRendering()
	{
		SongDocument document = new();
		ObjectId childId = document.AllocateObjectId();
		document.Add(new FmSynthDefinition(childId, "Child",
			new FmSynthGraph([new FmConstantNode(0, 0.375)], 0)));
		ObjectId parentId = document.AllocateObjectId();
		document.Add(new FmSynthDefinition(parentId, "Parent",
			new FmSynthGraph([
				new FmSoundSourceNode(0, childId,
					440.0 * System.Math.Pow(2, -9.0 / 12.0)),
			], 0)));

		ObjectId patternId = document.AllocateObjectId();
		DataPatternDefinition pattern = new(patternId, "Nested")
		{
			RowCount = 1,
			ChannelCount = 1,
		};
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.SourceId = parentId;
		cell.Note = new StartPatternNote();
		document.Add(pattern);
		IAudioOutputSource outputSource = new PlaybackRequestAudioSourceFactory(
			MonoConfiguration(sampleRate: 100)).Create(
				PatternPlaybackRequest.Create(document, patternId,
					repeat: false));
		float[] output = new float[4];
		outputSource.Render(4, output);
		output.Should().Equal(0.375f, 0.375f, 0.375f, 0.375f);
	}

	[Test]
	public void FrozenNestedInstrumentToneFirIsPreparedBeforeFirstPcmFrame()
	{
		SongDocument document = new();
		ObjectId filterId = document.AllocateObjectId();
		FilterDefinition definition = new(filterId, "Linear FIR", [
			new FilterStageDefinition(1, "low", FilterKind.FirLowPass),
		]);
		document.Add(definition);
		FilterInstance instance = new(definition);
		instance.SetStageParameter(1, "TapCount",
			FilterParameterValue.FromNumber(65));

		ObjectId leafId = document.AllocateObjectId();
		document.Add(new FmSynthDefinition(leafId, "Excitation",
			new FmSynthGraph([new FmConstantNode(0, 1)], 0)));
		ObjectId instrumentId = document.AllocateObjectId();
		InstrumentDefinition instrument = new(instrumentId, "Filtered instrument");
		instrument.ToneSpecifications.Add(new ToneSpecification
		{
			SourceId = leafId, FilterConfiguration = instance,
		});
		instrument.ToneTable.Add(0);
		document.Add(instrument);
		ObjectId parentId = document.AllocateObjectId();
		document.Add(new FmSynthDefinition(parentId, "Source parent",
			new FmSynthGraph([
				new FmSoundSourceNode(0, instrumentId,
					440.0 * System.Math.Pow(2.0, -9.0 / 12.0)),
			], 0)));
		ObjectId patternId = document.AllocateObjectId();
		DataPatternDefinition pattern = new(patternId, "FIR source")
		{
			RowCount = 1, ChannelCount = 1,
		};
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.SourceId = parentId;
		cell.Note = new StartPatternNote();
		document.Add(pattern);

		const int rate = 48000;
		IAudioOutputSource source = new PlaybackRequestAudioSourceFactory(
			MonoConfiguration(rate)).Create(
				PatternPlaybackRequest.Create(document, patternId, repeat: false));
		float[] actual = new float[96];
		source.Render(actual.Length, actual);

		StreamingFilterChain reference = new(
			new FilterChain([instance]), rate, 1);
		float[] expected = new float[actual.Length];
		System.Array.Fill(expected, 1f);
		reference.ProcessInterleaved(expected);
		actual.Should().Equal(expected);
	}

	[Test]
	public void PatternPlaybackUsesRealFmFilterDspFromFrozenSharedDefinition()
	{
		SongDocument document = new();
		ObjectId filterId = document.AllocateObjectId();
		FilterDefinition definition = new(filterId, "Tone filter");
		document.Add(definition);
		FilterInstance use = new(definition);
		use.SetStageParameter(1, "CutoffHz",
			FilterParameterValue.FromNumber(100));
		ObjectId synthId = document.AllocateObjectId();
		document.Add(new FmSynthDefinition(synthId, "Filtered constant",
			new FmSynthGraph([
				new FmConstantNode(0, 1),
				new FmFilterNode(1, 0, use),
			], 1)));

		ObjectId patternId = document.AllocateObjectId();
		DataPatternDefinition pattern = new(patternId, "FM filter")
		{
			RowCount = 1,
			ChannelCount = 1,
		};
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.SourceId = synthId;
		cell.Note = new StartPatternNote();
		document.Add(pattern);

		IAudioOutputSource source = new PlaybackRequestAudioSourceFactory(
			MonoConfiguration(sampleRate: 1000)).Create(
				PatternPlaybackRequest.Create(document, patternId,
					repeat: false));
		float[] output = new float[1];
		source.Render(1, output);

		double k = System.Math.Tan(System.Math.PI * 100 / 1000);
		float expectedFirstSample = (float)(k / (1 + k));
		output[0].Should().BeApproximately(expectedFirstSample, 1e-6f);
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
