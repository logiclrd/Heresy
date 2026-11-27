using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

using Heresy.Core.Assets;
using Heresy.Core.Filters;
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

	[TestCase(1)]
	[TestCase(6)]
	public void ScriptVirtualLifecycleMatchesRealtimeAndOfflinePcm(int channels)
		=> AssertParity(CreateScriptVirtualSong(), channels, 1000);

	[TestCase(1)]
	[TestCase(6)]
	public void ScriptInstrumentDirectAndSelectedPrivateMixdownMatchOffline(int channels)
		=> AssertParity(CreateScriptInstrumentSong(), channels, 1000);

	[TestCase(1)]
	[TestCase(6)]
	public void NativeParametricOperatorsMatchOfflineAndRealtimePcm(int channels)
		=> AssertParity(CreateParametricSong(), channels, 1000);

	[TestCase(1, false, false)]
	[TestCase(6, false, true)]
	[TestCase(6, true, false)]
	[TestCase(1, true, true)]
	public void RepeatedPrivateVirtualGenerationsAndNativeRewindMatchOffline(
		int channels, bool relaxedParent, bool relaxedChild)
	{
		SongDocument song = CreateRecycledPrivateVirtualSong(
			relaxedParent, relaxedChild, out ObjectId parent);
		AssertParity(song, channels, 1000, tracedPattern: parent,
			tracedFrames: 700, expectedLogicalFrames: 1440);
	}

	[TestCase(1, false, true)]
	[TestCase(2, true, false)]
	[TestCase(6, false, false)]
	[TestCase(8, true, true)]
	public void FullChannelParityWithFlattenedAndPrivateSourcesMatchesF6AndExport(
		int channels, bool relaxedParent, bool relaxedChild)
	{
		SongDocument song = CreateRecycledPrivateVirtualSong(
			relaxedParent, relaxedChild, out ObjectId parent, parityControls: true);
		AssertParity(song, channels, 1000, tracedPattern: parent,
			tracedFrames: 700, expectedLogicalFrames: 1440);
	}

	private static void AssertParity(SongDocument document,
		int channels, int sampleRate, ObjectId? tracedPattern = null,
		int tracedFrames = 0, int? expectedLogicalFrames = null)
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

		float[]? tracedPcm = null;
		if (tracedPattern is ObjectId pattern)
		{
			// Real editor-F6 request, not merely a script compiled with
			// metadata. Its first visit must sound like ordinary playback.
			var editorFactory = new PlaybackRequestAudioSourceFactory(configuration, samples);
			var editorRequest = PatternPlaybackRequest.Create(document, pattern) with
			{
				EditorCallsiteSession = EditorCallsiteSessionIds.Next(),
			};
			using IDisposable editor = (IDisposable)editorFactory.Create(editorRequest);
			Assert.That(editorFactory.TryTakePlaybackPositionTimeline(editorRequest, out _),
				Is.True);
			tracedPcm = new float[checked(tracedFrames * channels)];
			((IAudioOutputSource)editor).Render(tracedFrames, tracedPcm);
		}

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
		if (expectedLogicalFrames is int frames)
			Assert.That(result.LogicalFrameCount, Is.EqualTo(frames));
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
		if (tracedPcm is not null)
		{
			Assert.That(tracedPcm, Is.EqualTo(live.Take(tracedPcm.Length).ToArray())
				.Within(1e-6f), "Editor tracing must preserve the first visit's PCM.");
			Assert.That(live.Skip(live.Length / 2).Any(v => Math.Abs(v) > 0.00001f),
				Is.True, "The second invocation must actually render audio.");
		}
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

	private static SongDocument CreateParametricSong()
	{
		SongDocument document = new();
		ObjectId sample = document.AllocateObjectId();
		document.Add(new SampleDefinition(sample, "Memory PCM",
			new ExternalAssetReference(Path.Combine(Path.GetTempPath(),
				$"heresy-native-expressions-parity-{sample.Value}.wav"))));
		ObjectId patternId = document.AllocateObjectId();
		DataPatternDefinition pattern = new(patternId, "Parametric")
			{ RowCount = 5, ChannelCount = 2 };
		PatternCell first = pattern.Grid.GetOrCreateCell(0, 0);
		first.Note = new StartPatternNote(sample);
		first.Effects.Add(new ParametricPatternEffect(
			ParametricEffectParameter.GlobalVolume,
			TimeSpan.FromMilliseconds(95),
			"-0.1 * Math.Min(elapsedSeconds / 0.095, 1.0)", true));
		first.Effects.Add(new ParametricPatternEffect(
			ParametricEffectParameter.NoteVolume,
			TimeSpan.FromMilliseconds(75),
			"-0.2", false));
		first.Effects.Add(new ParametricPatternEffect(
			ParametricEffectParameter.OverallChannelVolume,
			TimeSpan.FromMilliseconds(110),
			"-0.05", true));
		first.Effects.Add(new ParametricPatternEffect(
			ParametricEffectParameter.SpatialX,
			TimeSpan.FromMilliseconds(120),
			"0.3 * Math.Sin(elapsedSeconds * 10.0)", false));
		pattern.Grid.GetOrCreateCell(0, 1).Note = new StartPatternNote(sample);
		document.Add(pattern);
		ObjectId rootId = document.AllocateObjectId();
		DataSequenceDefinition root = new(rootId, "Root");
		root.Entries.Add(new SequenceEntry(patternId));
		document.Add(root);
		document.RootSequenceId = rootId;
		return document;
	}

	private static SongDocument CreateScriptInstrumentSong()
	{
		SongDocument document = new();
		ObjectId sample = document.AllocateObjectId();
		document.Add(new SampleDefinition(sample, "Memory PCM",
			new ExternalAssetReference(Path.Combine(Path.GetTempPath(),
				$"heresy-script-instrument-parity-{sample.Value}.wav"))));
		ObjectId childId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(childId, "Private tone")
		{
			RowCount = 3, ChannelCount = 1,
			Source = $"Note(0, 0, _O({sample.Value}));",
		});
		ObjectId instrumentId = document.AllocateObjectId();
		ScriptInstrumentDefinition instrument =
			new(instrumentId, "Program selected")
			{
				Source = "pitchMultiplier < 1.5 ? 0 : 1",
				PitchExpression = "pitchMultiplier < 1.5 ? 1.0 : 0.5",
			};
		instrument.ToneSpecifications.Add(new ToneSpecification
			{ SourceId = sample });
		instrument.ToneSpecifications.Add(new ToneSpecification
			{ SourceId = childId });
		document.Add(instrument);
		ObjectId patternId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(patternId, "Script Instrument calls")
		{
			RowCount = 3, ChannelCount = 2,
			Source = $"""
				Note(0, 0, _O({instrumentId.Value}));
				Note(0, 1, _O({instrumentId.Value}), pitchMultiplier: 2.0);
				Cut(1, 0);
				Off(1.5, 1);
				""",
		});
		ObjectId rootId = document.AllocateObjectId();
		DataSequenceDefinition root = new(rootId, "Root");
		root.Entries.Add(new SequenceEntry(patternId));
		document.Add(root);
		document.RootSequenceId = rootId;
		return document;
	}

	private static SongDocument CreateScriptVirtualSong()
	{
		SongDocument document = new();
		ObjectId sample = document.AllocateObjectId();
		document.Add(new SampleDefinition(sample, "Memory PCM",
			new ExternalAssetReference(Path.Combine(Path.GetTempPath(),
				$"heresy-virtual-parity-{sample.Value}.wav"))));

		ObjectId childId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(childId, "Virtual scripted notes")
		{
			RowCount = 3, ChannelCount = 1,
			Source = $"""
				using var v7 = NoteVirtual(0, _O({sample.Value}));
				using var v8 = NoteVirtual(0, _O({sample.Value}));
				v7.Cut(0.5);
				using var v9 = NoteVirtual(1, _O({sample.Value}));
				OffVirtualInScope(1.5);
				using var v10 = NoteVirtual(1.75, _O({sample.Value}));
				CutAllVirtual(2.25);
				""",
		});

		ObjectId sequenceId = document.AllocateObjectId();
		DataSequenceDefinition root = new(sequenceId, "Virtual root");
		root.Entries.Add(new SequenceEntry(childId));
		document.Add(root);
		document.RootSequenceId = sequenceId;
		return document;
	}

	private static SongDocument CreateRecycledPrivateVirtualSong(
		bool relaxedParent, bool relaxedChild, out ObjectId parent, bool parityControls = false)
	{
		SongDocument document = new();
		ObjectId sample = document.AllocateObjectId();
		document.Add(new SampleDefinition(sample, "Memory PCM",
			new ExternalAssetReference(Path.Combine(Path.GetTempPath(),
				$"heresy-private-virtual-parity-{sample.Value}.wav"))));
		ObjectId filter = document.AllocateObjectId();
		document.Add(new FilterDefinition(filter, "Finite echo", [
			new FilterStageDefinition(1, "echo", FilterKind.CombFeedforward),
		]));
		ObjectId child = document.AllocateObjectId();
		string childSource = $"""
			using var first = NewNoteGroup();
			first += NoteVirtual(0, _O({sample.Value}));
			first.ChannelFilter(0.25, _O({filter.Value}));
			first.Cut(0.75, timeOffsetSeconds: 0.09);
			first.Dispose();
			using var second = NewNoteGroup();
			second += NoteVirtual(1, _O({sample.Value}));
			second.Volume(1.5, 0.5);
			second.Cut(3);
			""";
		if (parityControls)
		{
			childSource = childSource.Replace("first.ChannelFilter(0.25,", """
				first.Pan(0, -0.8, 0.2, 0.3);
				first.Filter(0, 0.4, 0.2);
				first.Seek(0.125, 0.1, timeOffsetSeconds: 0.02);
				first.Surround(0.25, true);
				first.ChannelFilter(0.25,
				""", StringComparison.Ordinal).Replace("second.Volume(1.5,", """
				second.Pan(1, 0.6);
				second.FilterCutoff(1.25, 0.6);
				second.FilterResonance(1.5, 0.15);
				second.Volume(1.5,
				""", StringComparison.Ordinal);
		}
		document.Add(new ScriptPatternDefinition(child, "Private generations")
		{
			RowCount = 6, ChannelCount = 1,
			Source = relaxedChild ? "#order relaxed\n" + childSource : childSource,
		});
		ObjectId privateSequence = document.AllocateObjectId();
		DataSequenceDefinition sequence = new(privateSequence, "Private sequence");
		sequence.Entries.Add(new SequenceEntry(child));
		document.Add(sequence);

		parent = document.AllocateObjectId();
		string parentSource = $"""
			using var old = NewNoteGroup();
			using var lead = NoteVirtual(0, _O({privateSequence.Value}));
			lead.SourceFrame(0.5, 100);
			lead.SourceFrame(0.75, 0);
			lead.Displacement(1, NoteDisplacementAction.Continue);
			lead.Note(1.25, _O({privateSequence.Value}));
			old += lead;
			old.Volume(1.5, 0.5);
			old.Past(1.75, TrackerPastNoteAction.Cut, timeOffsetSeconds: 0.09);
			old.Cut(1.75, timeOffsetSeconds: 0.09);
			old.Dispose();
			using var replacement = NewNoteGroup();
			replacement += NoteVirtual(2, _O({privateSequence.Value}));
			replacement += NoteVirtual(2, _O({privateSequence.Value}), volume: 0.25);
			replacement.Cut(4);
			""";
		if (parityControls)
		{
			ObjectId flattened = document.AllocateObjectId();
			document.Add(new ScriptPatternDefinition(flattened, "Flattened sibling")
			{
				RowCount = 2, ChannelCount = 1,
				Source = (relaxedChild ? "#order relaxed\n" : "") + $"""
					Note(0, 0, _O({sample.Value}), volume: 0.25);
					Note(1.75, 0, _O({sample.Value}), volume: 0.25);
					""",
			});
			// The physical subtree shares the parent clock; the virtual
			// private Sequence keeps its independent clock and PCM state.
			parentSource = parentSource.Replace("lead.SourceFrame(0.5,", $"""
				Note(0, 0, _O({flattened.Value}));
				Volume(0.25, 0, 0.6);
				Displacement(0.25, 0, NoteDisplacementAction.Continue);
				lead.Filter(0.25, 0.4, 0.2);
				lead.SourceFrame(0.5,
				""", StringComparison.Ordinal).Replace("lead.Displacement(1,", $"""
				Note(1, 0, _O({flattened.Value}));
				lead.Displacement(1,
				""", StringComparison.Ordinal).Replace("old += lead;", """
				Past(1.25, 0, TrackerPastNoteAction.Cut, timeOffsetSeconds: 0.02);
				old += lead;
				old.FilterResonance(1.5, 0.1);
				""", StringComparison.Ordinal).Replace("replacement.Cut(4);", $"""
				Note(3, 0, _O({sample.Value}), volume: 0.25);
				SourceFrame(3.25, 0, 100);
				Pan(3.25, 0, -0.5);
				replacement.Seek(3.25, 0.06);
				replacement.Surround(3.5, true);
				Cut(4, 0);
				replacement.Cut(4);
				""", StringComparison.Ordinal);
		}
		document.Add(new ScriptPatternDefinition(parent, "Seeking virtual mixdowns")
		{
			RowCount = 6, ChannelCount = 1,
			Source = relaxedParent ? "#order relaxed\n" + parentSource : parentSource,
		});
		ObjectId rootId = document.AllocateObjectId();
		DataSequenceDefinition root = new(rootId, "Repeated private virtual generations");
		root.Entries.Add(new SequenceEntry(parent));
		root.Entries.Add(new SequenceEntry(parent));
		document.Add(root);
		document.RootSequenceId = rootId;
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
