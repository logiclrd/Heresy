using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

using Heresy.Core.Assets;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Samples;
using Heresy.Core.Sequences;
using Heresy.Core.Sequencing;
using Heresy.Playback;
using Heresy.Render.Configuration;
using Heresy.Render.File;
using Heresy.Render.Realtime;
using Heresy.Render.Samples;
using Heresy.UserInterface;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

/// <summary>
/// Compare the actual realtime request factory's unencoded PCM against the
/// production offline renderer's interleaved float blocks, before codec
/// quantization. Different chunk sizes must not change either path.
/// The logical body is compared separately from the intentional offline
/// end-input release-tail policy (not used during live playback).
/// </summary>
[TestFixture]
public sealed class PlaybackExportPcmParityTests
{
	[TestCase(1)]
	[TestCase(2)]
	[TestCase(6)]
	[TestCase(8)]
	public void RecursiveDataSongMatchesOfflineOnEverySpeakerFeed(int channels)
		=> AssertParity(CreateRecursiveSong(scriptChild: false,
			instrumentChild: false), channels, 1000);

	[TestCase(2)]
	[TestCase(8)]
	public void ScriptGeneratedChildAndTrackerTempoMatchOffline(int channels)
		=> AssertParity(CreateRecursiveSong(scriptChild: true,
			instrumentChild: false), channels, 1000);

	[TestCase(2)]
	[TestCase(6)]
	public void IndirectInstrumentPrivateMixdownMatchesOffline(int channels)
		=> AssertParity(CreateRecursiveSong(scriptChild: false,
			instrumentChild: true), channels, 1000);

	private static void AssertParity(SongDocument document,
		int channels, int sampleRate)
	{
		RenderConfiguration configuration = Configuration(sampleRate, channels);
		float[] pcm = Enumerable.Range(0, 1800)
			.Select(index => (float)(Math.Sin(index * 0.06) * 0.34
				+ Math.Cos(index * 0.031) * 0.16)).ToArray();
		TestSamples samples = new(new MemorySampleData(sampleRate, 1, pcm));
		PlaybackRequestAudioSourceFactory realtimeFactory =
			new(configuration, samples);
		OfflineSongRenderPlanFactory exportFactory =
			new(configuration, samples);
		PlaybackRequest request = SequencePlaybackRequest.Create(
			document, document.RootSequenceId);
		using IDisposable realtime = (IDisposable)realtimeFactory.Create(request);
		IAudioOutputSource liveSource = (IAudioOutputSource)realtime;
		using OfflineSongRenderPlan export = exportFactory.Create(document);

		// A fresh authored edit cannot change either already-frozen render.
		ObjectId rootId = document.RootSequenceId;
		DataSequenceDefinition root = (DataSequenceDefinition)document.Objects[rootId];
		root.Entries.Clear();
		document.MarkChanged(affectsAudio: true);

		using FloatSink sink = new(export.Source.Format);
		OfflineRenderResult result = OfflinePlaybackRenderer.Render(
			export.Source, sink, blockFrameCount: 17,
			maximumLogicalFrames: 8000, maximumTailFrames: 8000);
		Assert.That(result.LogicalFrameCount, Is.GreaterThan(0));
		Assert.That(result.LogicalFrameCount, Is.LessThan(8000));
		Assert.That(result.TotalFrameCount,
			Is.EqualTo(sink.FramesWritten));

		int logical = checked((int)result.LogicalFrameCount);
		float[] live = new float[checked(logical * channels)];
		int nextFrame = 0;
		int[] realtimeBlocks = [1, 19, 7, 131, 3, 64, 11];
		for (int index = 0; nextFrame < logical; index++)
		{
			int count = Math.Min(realtimeBlocks[index % realtimeBlocks.Length],
				logical - nextFrame);
			liveSource.Render(count, live.AsSpan(
				nextFrame * channels, count * channels));
			nextFrame += count;
		}

		float[] offline = sink.Samples.ToArray();
		Assert.That(offline.Length, Is.GreaterThanOrEqualTo(live.Length));
		Assert.That(live.Any(v => Math.Abs(v) > 0.00001f), Is.True,
			"An all-silent arrangement would not prove PCM parity.");
		for (int index = 0; index < live.Length; index++)
		{
			Assert.That(live[index], Is.EqualTo(offline[index]).Within(1e-6),
				$"Mismatch on frame {index / channels}, speaker {index % channels}" +
				$" of {channels}, when rendering the logical body.");
		}
	}

	private static RenderConfiguration Configuration(int rate, int channels)
	{
		RenderConfiguration preset = AudioOutputSettings.Preset(rate, channels);
		OutputChannelConfiguration[] outputs =
			new OutputChannelConfiguration[channels];
		for (int index = 0; index < channels; index++)
		{
			OutputChannelConfiguration existing = preset.OutputChannels[index];
			OutputFilterType filter = (index % 3) switch
			{
				0 => OutputFilterType.LowPass,
				1 => OutputFilterType.HighPass,
				_ => OutputFilterType.None,
			};
			outputs[index] = new OutputChannelConfiguration(
				existing.Position, 1.0 + index * 0.15,
				filter, filter == OutputFilterType.None ? null : 95 + index * 13);
		}
		return new RenderConfiguration(rate, outputs);
	}

	private static SongDocument CreateRecursiveSong(
		bool scriptChild, bool instrumentChild)
	{
		SongDocument document = new();
		ObjectId sample = document.AllocateObjectId();
		document.Add(new SampleDefinition(sample, "Memory PCM",
			new ExternalAssetReference(Path.Combine(Path.GetTempPath(),
				$"heresy-parity-{sample.Value}.wav"))));

		ObjectId childId = document.AllocateObjectId();
		if (scriptChild)
		{
			document.Add(new ScriptPatternDefinition(childId, "Script notes")
			{
				RowCount = 2, ChannelCount = 1,
				Source = $"Tempo(0, 180); Note(0, 0, _O({sample.Value}));" +
					$"Note(1, 0, _O({sample.Value}));",
			});
		}
		else
		{
			DataPatternDefinition child = new(childId, "Two voices")
			{
				RowCount = 2, ChannelCount = 1,
			};
			child.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote(sample);
			child.Grid.GetOrCreateCell(1, 0).Note = new StartPatternNote(sample);
			document.Add(child);
		}

		ObjectId sourceId = childId;
		if (instrumentChild)
		{
			ObjectId instrumentId = document.AllocateObjectId();
			InstrumentDefinition instrument = new(instrumentId, "Private instrument");
			instrument.ToneSpecifications.Add(new ToneSpecification { SourceId = childId });
			instrument.ToneTable.Add(0);
			document.Add(instrument);
			sourceId = instrumentId;
		}

		ObjectId parentId = document.AllocateObjectId();
		DataPatternDefinition parent = new(parentId, "Parent")
		{
			RowCount = 3, ChannelCount = 3,
		};
		parent.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote(sample);
		parent.Grid.GetOrCreateCell(0, 1).Note =
			new StartPatternNote(sourceId);
		parent.Grid.GetOrCreateCell(0, 2).Note =
			new StartPatternNote(childId, mixdown: true);
		parent.Grid.GetOrCreateCell(1, 0).Note = new StartPatternNote(sample);
		parent.Grid.GetOrCreateCell(2, 2).Note = new PatternNoteOff();
		document.Add(parent);

		ObjectId sequenceId = document.AllocateObjectId();
		DataSequenceDefinition sequence = new(sequenceId, "Root");
		sequence.Entries.Add(new SequenceEntry(parentId));
		document.Add(sequence);
		document.RootSequenceId = sequenceId;
		return document;
	}

	private sealed class TestSamples(ISampleData data) : ISampleDataProvider
	{
		public ISampleData GetSampleData(SampleDefinition sample)
			=> data;
	}

	private sealed class FloatSink(AudioOutputFormat format) : IAudioFileSink
	{
		public AudioOutputFormat Format => format;
		public List<float> Samples { get; } = [];
		public long FramesWritten => Samples.Count / Format.ChannelCount;
		public void Write(ReadOnlySpan<float> samples)
		{
			if (samples.Length % Format.ChannelCount != 0)
				throw new ArgumentException("Incomplete interleaved frame.");
			Samples.AddRange(samples.ToArray());
		}
		public void Complete() { }
		public void Dispose() { }
	}
}
