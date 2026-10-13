using System;
using System.IO;
using System.Numerics;
using System.Text;

using AwesomeAssertions;

using Heresy.Core.Filters;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Samples;
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

	[TestCase(1.0, 0)]
	[TestCase(2.0, 1)]
	public void ScriptInstrumentDeclaredFirBranchesArePreparedBeforeSynthVoiceStarts(
		double notePitch, int selected)
	{
		// The selector is evaluated at note start, but *both* FIR designs
		// must be ready before the producer begins PCM. No script can
		// generate a new Filter configuration outside this finite table.
		SongDocument song = new();
		ObjectId filterId = song.AllocateObjectId();
		FilterDefinition definition = new(filterId, "Script tone FIR", [
			new FilterStageDefinition(1, "lp", FilterKind.FirLowPass),
		]);
		song.Add(definition);
		FilterInstance minimum = new(definition);
		minimum.SetStageParameter(1, "TapCount",
			FilterParameterValue.FromNumber(31));
		minimum.SetStageParameter(1, "PhaseResponse",
			FilterParameterValue.FromChoice("Minimum"));
		FilterInstance maximum = new(definition);
		maximum.SetStageParameter(1, "TapCount",
			FilterParameterValue.FromNumber(65));
		maximum.SetStageParameter(1, "PhaseResponse",
			FilterParameterValue.FromChoice("Maximum"));

		ObjectId leafId = song.AllocateObjectId();
		song.Add(new FmSynthDefinition(leafId, "Wave",
			new FmSynthGraph([new FmConstantNode(0, 1)], 0)));
		ObjectId instrumentId = song.AllocateObjectId();
		ScriptInstrumentDefinition scripted = new(instrumentId, "Key split")
		{
			Source = "pitchMultiplier < 1.5 ? 0 : 1",
			PitchExpression = "1.0",
		};
		scripted.ToneSpecifications.Add(new ToneSpecification
		{
			SourceId = leafId, FilterConfiguration = minimum,
		});
		scripted.ToneSpecifications.Add(new ToneSpecification
		{
			SourceId = leafId, FilterConfiguration = maximum,
		});
		song.Add(scripted);
		ObjectId parentId = song.AllocateObjectId();
		song.Add(new FmSynthDefinition(parentId, "Nested scripted tone",
			new FmSynthGraph([
				new FmSoundSourceNode(0, instrumentId,
					440 * System.Math.Pow(2.0, -9.0 / 12.0)),
			], 0)));
		ObjectId patternId = song.AllocateObjectId();
		string pitch = notePitch.ToString(
			System.Globalization.CultureInfo.InvariantCulture);
		song.Add(new ScriptPatternDefinition(patternId, "Select tone")
		{
			Source = $"Note(0, 0, _O({parentId.Value}), pitchMultiplier: {pitch});",
			RowCount = 1,
			ChannelCount = 1,
		});

		const int sampleRate = 48000;
		using PreparedIncrementalPlaybackPlan plan =
			new PreparedIncrementalPlaybackFactory(
				MonoConfiguration(sampleRate)).Create(song, patternId);
		float[] actual = new float[100];
		plan.Source.Render(actual.Length, actual);

		FilterInstance chosen = selected == 0 ? minimum : maximum;
		StreamingFilterChain reference = new(
			new FilterChain([chosen]), sampleRate, 1);
		float[] expected = new float[100];
		System.Array.Fill(expected, 1f);
		reference.ProcessInterleaved(expected);
		actual.Should().Equal(expected);
	}

	[TestCase(false)]
	[TestCase(true)]
	public void PatternProducerRetiresDiscoveredFiniteSynthButKeepsIndefiniteBranch(
		bool indefiniteConstantBranch)
	{
		SongDocument song = new();
		ObjectId pcmId = song.AllocateObjectId();
		song.Add(SampleDefinition.CreateImported(pcmId,
			"4-frame PCM", "tiny.wav", FourFrameWave()));
		ObjectId nestedId = song.AllocateObjectId();
		song.Add(new FmSynthDefinition(nestedId, "Finite child",
			new FmSynthGraph([
				new FmSoundSourceNode(0, pcmId,
					440.0 * Math.Pow(2.0, -9.0 / 12.0)),
			], 0)));
		ObjectId parentId = song.AllocateObjectId();
		FmSynthNode[] graph = indefiniteConstantBranch
			? [
				new FmSoundSourceNode(0, nestedId,
					440.0 * Math.Pow(2.0, -9.0 / 12.0)),
				new FmConstantNode(1, 0.25),
				new FmOperatorNode(2, FmOperatorKind.Add, [0, 1]),
			  ]
			: [
				new FmSoundSourceNode(0, nestedId,
					440.0 * Math.Pow(2.0, -9.0 / 12.0)),
			  ];
		song.Add(new FmSynthDefinition(parentId, "Parent",
			new FmSynthGraph(graph, indefiniteConstantBranch ? 2 : 0)));
		ObjectId patternId = song.AllocateObjectId();
		DataPatternDefinition pattern = new(patternId, "Single note")
		{
			RowCount = 1, ChannelCount = 1,
		};
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.SourceId = parentId;
		cell.Note = new StartPatternNote();
		song.Add(pattern);

		using PreparedIncrementalPlaybackPlan plan =
			new PreparedIncrementalPlaybackFactory(
				MonoConfiguration(1000)).Create(song, patternId);
		float[] pcm = new float[16];
		plan.Source.Render(pcm.Length, pcm);
		for (int i = 0; i < 4; i++)
			pcm[i].Should().BeApproximately(
				indefiniteConstantBranch ? 0.75f : 0.5f, 1e-5f);
		for (int i = 4; i < pcm.Length; i++)
			pcm[i].Should().BeApproximately(
				indefiniteConstantBranch ? 0.25f : 0f, 1e-5f);
		if (indefiniteConstantBranch)
			plan.Session.GetChannelState(0).CurrentVoice.Should().NotBeNull();
		else
			plan.Session.GetChannelState(0).CurrentVoice.Should().BeNull(
				"the producer must observe a nested Synth's newly discovered EOF during the same block");
	}

	private static byte[] FourFrameWave()
	{
		using MemoryStream stream = new();
		using BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: true);
		writer.Write(Encoding.ASCII.GetBytes("RIFF"));
		writer.Write(44);
		writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
		writer.Write(16); writer.Write((ushort)1); writer.Write((ushort)1);
		writer.Write(1000); writer.Write(2000);
		writer.Write((ushort)2); writer.Write((ushort)16);
		writer.Write(Encoding.ASCII.GetBytes("data"));
		writer.Write(8);
		for (int i = 0; i < 4; i++) writer.Write((short)16384);
		writer.Flush();
		return stream.ToArray();
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
