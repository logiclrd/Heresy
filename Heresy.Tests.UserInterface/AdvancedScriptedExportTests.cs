using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Samples;
using Heresy.Core.Sequences;
using Heresy.Playback;
using Heresy.Render.Configuration;
using Heresy.Render.File;
using Heresy.Render.Realtime;
using Heresy.UserInterface;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

/// <summary>
/// Production coroutine export against scripted Pattern generators, source
/// flow and finite limits. In particular, no test constructs an eager note
/// journal or mocks a purportedly unbounded script with a fake generator.
/// </summary>
[TestFixture]
public sealed class AdvancedScriptedExportTests
{
	[TestCase(1)]
	[TestCase(6)]
	public void ScriptTempoAndCombinedOrderJumpPatternBreakRenderIdenticallyAcrossChunks(
		int channels)
	{
		SongDocument document = CreateFlowSong();
		RenderConfiguration config = AudioOutputSettings.Preset(1000, channels);
		(float[] small, OfflineRenderResult result, List<OfflineRenderProgress> updates) =
			Render(document, config, blockSize: 7);
		(float[] large, OfflineRenderResult second, _) =
			Render(document, config, blockSize: 127);

		Assert.Multiple(() =>
		{
			Assert.That(result.LogicalFrameCount,
				Is.EqualTo(second.LogicalFrameCount));
			Assert.That(result.LogicalFrameCount, Is.EqualTo(300),
				"Tempo 250 gives 60-ms rows: two scripted rows, " +
				"one Bxx/Cxx row and two target rows starting at row 1.");
			Assert.That(result.TailFrameCount, Is.EqualTo(second.TailFrameCount));
			Assert.That(result.LogicalFrameCount, Is.GreaterThan(0));
			Assert.That(result.LogicalFrameCount, Is.LessThan(2000));
			Assert.That(small, Is.EqualTo(large).Within(1e-6f),
				"Script-time Tempo, a fractional row note and combined Bxx/Cxx " +
				"must be independent of offline PCM chunk size.");
			Assert.That(small.Any(sample => Math.Abs(sample) > 0.0001f), Is.True,
				"An all-silent export would not test the generated notes.");
			Assert.That(updates.Last().Phase, Is.EqualTo(OfflineRenderPhase.Completed));
		});
		Assert.That(updates.Where(update => update.Phase ==
			OfflineRenderPhase.LogicalBody).Select(update =>
				update.LogicalFramesRendered), Is.Ordered);
		Assert.That(updates.All(update =>
			update.KnownLogicalFrameCount is null), Is.True,
			"The root coroutine must not invent a finite length before finishing.");
	}

	[Test]
	public void FiniteScriptPatternStopsAtExactBodyFrameCapWithoutCompletedProgress()
	{
		SongDocument document = CreateScriptSong("Note(0, 0, _O(1));",
			rowCount: 8, sampleFrames: 500);
		using OfflineSongRenderPlan plan = new OfflineSongRenderPlanFactory(
			AudioOutputSettings.Preset(1000, 1)).Create(document);
		using RecordingSink sink = new(plan.Source.Format);
		List<OfflineRenderProgress> updates = [];
		Assert.That(() => OfflinePlaybackRenderer.Render(
			plan.Source, sink, blockFrameCount: 7,
			maximumLogicalFrames: 17, maximumTailFrames: 100,
			progress: new ImmediateProgress(updates.Add)),
			Throws.InvalidOperationException
			.With.Message.Contains("finite export frame limit"));
		Assert.Multiple(() =>
		{
			Assert.That(sink.FramesWritten, Is.EqualTo(17));
			Assert.That(updates.Select(u => u.LogicalFramesRendered),
				Is.EqualTo(new long[] { 7, 14, 17 }));
			Assert.That(updates, Has.All.Matches<OfflineRenderProgress>(
				u => u.Phase == OfflineRenderPhase.LogicalBody));
			Assert.That(plan.Session.InputEnded, Is.False,
				"Failure in the logical body must not initiate release-tail rendering.");
		});
	}

	[Test]
	public void FiniteScriptPatternReleaseTailRespectsExactExportFrameCap()
	{
		SongDocument document = CreateScriptSong("Note(0, 0, _O(1));",
			rowCount: 1, sampleFrames: 1000);
		using OfflineSongRenderPlan plan = new OfflineSongRenderPlanFactory(
			TailConfiguration()).Create(document);
		using RecordingSink sink = new(plan.Source.Format);
		List<OfflineRenderProgress> updates = [];
		Assert.That(() => OfflinePlaybackRenderer.Render(
			plan.Source, sink, blockFrameCount: 7,
			maximumLogicalFrames: 1000, maximumTailFrames: 11,
			progress: new ImmediateProgress(updates.Add)),
			Throws.InvalidOperationException
			.With.Message.Contains("release tail exceeded"));
		Assert.Multiple(() =>
		{
			Assert.That(plan.Session.InputEnded, Is.True);
			Assert.That(sink.FramesWritten, Is.EqualTo(131),
				"120 natural body frames plus exactly 11 allowed tail frames.");
			Assert.That(updates.Last().Phase, Is.EqualTo(OfflineRenderPhase.ReleaseTail));
			Assert.That(updates.Last().TailFramesRendered, Is.EqualTo(11));
			Assert.That(updates.Any(u => u.Phase == OfflineRenderPhase.Completed),
				Is.False);
		});
	}

	[Test]
	public void CompletedScriptPatternDrainsFiniteReleaseTailWithinBudget()
	{
		SongDocument document = CreateScriptSong("Note(0, 0, _O(1));",
			rowCount: 1, sampleFrames: 200);
		RenderConfiguration config = TailConfiguration();
		(_, OfflineRenderResult result, List<OfflineRenderProgress> updates) =
			Render(document, config, blockSize: 11,
			maximumLogicalFrames: 500, maximumTailFrames: 500);
		Assert.Multiple(() =>
		{
			Assert.That(result.LogicalFrameCount, Is.EqualTo(120));
			Assert.That(result.TailFrameCount, Is.GreaterThan(0));
			Assert.That(result.TailFrameCount, Is.LessThanOrEqualTo(500));
			Assert.That(updates.Last().Phase, Is.EqualTo(OfflineRenderPhase.Completed));
			Assert.That(updates.Last().LogicalFramesRendered, Is.EqualTo(120));
			Assert.That(updates.Last().TailFramesRendered,
				Is.EqualTo(result.TailFrameCount));
		});
	}

	[Test]
	public void SilentNonterminatingRoslynPatternIsStoppedByCooperationBudget()
	{
		SongDocument document = CreateScriptSong("while (true) { }",
			rowCount: 1, sampleFrames: 1);
		using OfflineSongRenderPlan plan = new OfflineSongRenderPlanFactory(
			AudioOutputSettings.Preset(1000, 1)).Create(document);
		using RecordingSink sink = new(plan.Source.Format);
		List<OfflineRenderProgress> updates = [];
		Assert.That(() => OfflinePlaybackRenderer.Render(
			plan.Source, sink, blockFrameCount: 1,
			maximumLogicalFrames: 4, maximumTailFrames: 4,
			progress: new ImmediateProgress(updates.Add)),
			Throws.InvalidOperationException
			.With.Message.Contains("same-tick cooperation budget"));
		Assert.Multiple(() =>
		{
			Assert.That(sink.FramesWritten, Is.Zero,
				"A zero-time runaway script must not silently advance musical time.");
			Assert.That(updates, Is.Empty);
		});
	}

	[Test]
	public void RevisitedScriptPatternTerminatesOnThirdBxxEncounter()
	{
		SongDocument song = CreateScriptSong("Note(0, 0, _O(1));",
			rowCount: 1, sampleFrames: 8);
		ObjectId jumper = song.AllocateObjectId();
		DataPatternDefinition jump = new(jumper, "Repeat root")
		{
			RowCount = 1, ChannelCount = 1,
		};
		jump.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerOrderJumpPatternEffect(0));
		song.Add(jump);
		DataSequenceDefinition root =
			(DataSequenceDefinition)song.Objects[song.RootSequenceId];
		root.Entries.Add(new SequenceEntry(jumper));

		(float[] pcm, OfflineRenderResult result, List<OfflineRenderProgress> progress) =
			Render(song, AudioOutputSettings.Preset(1000, 1), blockSize: 13,
				maximumLogicalFrames: 1000);

		Assert.Multiple(() =>
		{
			Assert.That(result.LogicalFrameCount, Is.EqualTo(720),
				"Three visits to two 120-ms orders must stop after " +
				"the third Bxx, without eagerly expanding repeated scripts.");
			Assert.That(result.TailFrameCount, Is.Zero);
			Assert.That(progress.Last().Phase, Is.EqualTo(OfflineRenderPhase.Completed));
		});
		foreach (int offset in new[] { 0, 240, 480 })
			Assert.That(Math.Abs(pcm[offset]), Is.GreaterThan(0.01f),
				$"The scripted note must regenerate on the visit at {offset} frames.");
	}

	[Test]
	public void ScriptThatEmitsOnceThenSpinsFailsWithoutCommittingPartialOutput()
	{
		SongDocument document = CreateScriptSong(
			"Note(0, 0, _O(1)); while (true) { }",
			rowCount: 1, sampleFrames: 1000);
		using OfflineSongRenderPlan plan = new OfflineSongRenderPlanFactory(
			AudioOutputSettings.Preset(1000, 1)).Create(document);
		using RecordingSink sink = new(plan.Source.Format);
		List<OfflineRenderProgress> progress = [];
		Assert.That(() => OfflinePlaybackRenderer.Render(
			plan.Source, sink, blockFrameCount: 5,
			maximumLogicalFrames: 20, maximumTailFrames: 20,
			progress: new ImmediateProgress(progress.Add)),
			Throws.InvalidOperationException
			.With.Message.Contains("same-tick cooperation budget"));
		Assert.Multiple(() =>
		{
			Assert.That(sink.FramesWritten, Is.Zero);
			Assert.That(progress, Is.Empty,
				"No partial output block can be advertised or committed.");
		});
	}

	private static RenderConfiguration TailConfiguration()
		=> new(1000,
		[
			new OutputChannelConfiguration(System.Numerics.Vector3.Zero,
				positionalImportance: 0,
				filterType: OutputFilterType.LowPass, cutoffHz: 15),
		]);

	private static SongDocument CreateFlowSong()
	{
		SongDocument song = new();
		ObjectId sample = song.AllocateObjectId();
		song.Add(SampleDefinition.CreateImported(
			sample, "Impulse", "impulse.wav", Wave(1000, 8)));

		ObjectId firstId = song.AllocateObjectId();
		song.Add(new ScriptPatternDefinition(firstId, "Script tempo")
		{
			RowCount = 2, ChannelCount = 1,
			Source = $"Tempo(0, 250); Note(0, 0, _O({sample.Value})); " +
				$"Note(1.5, 0, _O({sample.Value}));",
		});

		ObjectId flowId = song.AllocateObjectId();
		DataPatternDefinition flow = new(flowId, "Bxx/Cxx")
		{
			RowCount = 3, ChannelCount = 2,
		};
		flow.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerOrderJumpPatternEffect(2));
		flow.Grid.GetOrCreateCell(0, 1).Effects.Add(
			new TrackerPatternBreakPatternEffect(1));
		song.Add(flow);

		ObjectId targetId = song.AllocateObjectId();
		DataPatternDefinition target = new(targetId, "Row-one target")
		{
			RowCount = 3, ChannelCount = 1,
		};
		target.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(sample); // Must be skipped by Cxx.
		target.Grid.GetOrCreateCell(1, 0).Note =
			new StartPatternNote(sample);
		target.Grid.GetOrCreateCell(2, 0).Note =
			new StartPatternNote(sample);
		song.Add(target);

		ObjectId sequenceId = song.AllocateObjectId();
		DataSequenceDefinition root = new(sequenceId, "Root");
		root.Entries.Add(new SequenceEntry(firstId));
		root.Entries.Add(new SequenceEntry(flowId));
		root.Entries.Add(new SequenceEntry(targetId));
		song.Add(root);
		song.RootSequenceId = sequenceId;
		return song;
	}

	private static SongDocument CreateScriptSong(
		string source, int rowCount, int sampleFrames)
	{
		SongDocument song = new();
		ObjectId sample = song.AllocateObjectId();
		song.Add(SampleDefinition.CreateImported(
			sample, "Sustain", "sustain.wav", Wave(1000, sampleFrames)));
		Assert.That(sample.Value, Is.EqualTo(1U),
			"The test script uses _O(1) as its sample reference.");
		ObjectId scripted = song.AllocateObjectId();
		song.Add(new ScriptPatternDefinition(scripted, "Script")
		{
			RowCount = rowCount, ChannelCount = 1, Source = source,
		});
		ObjectId rootId = song.AllocateObjectId();
		DataSequenceDefinition sequence = new(rootId, "Root");
		sequence.Entries.Add(new SequenceEntry(scripted));
		song.Add(sequence);
		song.RootSequenceId = rootId;
		return song;
	}

	private static byte[] Wave(int sampleRate, int frames)
	{
		using MemoryStream stream = new();
		using BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: true);
		writer.Write(Encoding.ASCII.GetBytes("RIFF"));
		writer.Write(36 + frames * 2);
		writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
		writer.Write(16);
		writer.Write((ushort)1);
		writer.Write((ushort)1);
		writer.Write(sampleRate);
		writer.Write(sampleRate * 2);
		writer.Write((ushort)2);
		writer.Write((ushort)16);
		writer.Write(Encoding.ASCII.GetBytes("data"));
		writer.Write(frames * 2);
		for (int i = 0; i < frames; i++)
			writer.Write((short)16384);
		writer.Flush();
		return stream.ToArray();
	}

	private static (float[] Pcm, OfflineRenderResult Result,
		List<OfflineRenderProgress> Updates) Render(
		SongDocument document, RenderConfiguration configuration,
		int blockSize, long maximumLogicalFrames = 4000,
		long maximumTailFrames = 4000)
	{
		using OfflineSongRenderPlan plan =
			new OfflineSongRenderPlanFactory(configuration).Create(document);
		using RecordingSink sink = new(plan.Source.Format);
		List<OfflineRenderProgress> updates = [];
		OfflineRenderResult result = OfflinePlaybackRenderer.Render(
			plan.Source, sink, blockFrameCount: blockSize,
			maximumLogicalFrames: maximumLogicalFrames,
			maximumTailFrames: maximumTailFrames,
			progress: new ImmediateProgress(updates.Add));
		return (sink.Samples.ToArray(), result, updates);
	}

	private sealed class ImmediateProgress(Action<OfflineRenderProgress> report)
		: IProgress<OfflineRenderProgress>
	{
		public void Report(OfflineRenderProgress value) => report(value);
	}

	private sealed class RecordingSink(AudioOutputFormat format)
		: IAudioFileSink
	{
		public AudioOutputFormat Format => format;
		public List<float> Samples { get; } = [];
		public long FramesWritten => Samples.Count / Format.ChannelCount;
		public void Write(ReadOnlySpan<float> interleaved)
		{
			if (interleaved.Length % Format.ChannelCount != 0)
				throw new ArgumentException("Partial PCM frame.");
			Samples.AddRange(interleaved.ToArray());
		}
		public void Complete() { }
		public void Dispose() { }
	}
}
