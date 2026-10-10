using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;

using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Samples;
using Heresy.Core.Sequences;
using Heresy.Core.Sequencing;
using Heresy.Playback;
using Heresy.Render.Configuration;
using Heresy.Render.Playback;
using Heresy.Render.Realtime;
using Heresy.Render.Sounds;

using NUnit.Framework;

namespace Heresy.Tests.Playback;

/// <summary>
/// Real tracker Oxx/Qxy commands flowing through prepared private Pattern,
/// Sequence and Instrument graphs. The output uses 1 kHz mono, with a known
/// native PCM frame ramp and no speaker filtering. Testing the native frame
/// address here is deliberately separate from Tempo and wall-time conversion.
/// </summary>
[TestFixture]
public sealed class RecursiveNativeSeekBoundaryTests
{
	[TestCase(false)]
	[TestCase(true)]
	public void TrackerOxxSeeksAcrossSequenceOrdersWithOrWithoutInstrument(
		bool viaInstrument)
	{
		SongDocument document = CreateDocument(out ObjectId sample);
		ObjectId first = AddPattern(document, sample, rows: 1);
		ObjectId second = AddPattern(document, sample, rows: 4);
		ObjectId childSequence = AddSequence(document, first, second);
		ObjectId source = viaInstrument
			? AddInstrument(document, childSequence) : childSequence;
		ObjectId parent = AddPattern(document, source, rows: 5,
			mixdown: !viaInstrument);
		DataPatternDefinition parentPattern =
			(DataPatternDefinition)document.Objects[parent];
		parentPattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new SampleOffsetPatternEffect(1)); // 256 native frames
		PreparedIncrementalPlaybackFactory factory = new(Mono());

		using PreparedIncrementalPlaybackPlan reference = factory.Create(
			document, childSequence);
		using PreparedIncrementalPlaybackPlan sought = factory.Create(
			document, parent);
		float[] expected = new float[320];
		reference.Source.Render(expected.Length, expected);
		float[] actual = new float[64];
		sought.Source.Render(1, actual.AsSpan(0, 1));
		PlaybackVoice active = sought.Session.GetChannelState(0).CurrentVoice!;
		Assert.That(active.Sound, Is.AssignableTo<ISourceFrameSeekableSound>(),
			"An Instrument selecting a private Sequence must expose its native " +
			"source-frame seek capability through the bound tone wrapper.");
		for (int i = 1; i < actual.Length; i += 7)
		{
			int count = Math.Min(7, actual.Length - i);
			sought.Source.Render(count, actual.AsSpan(i, count));
		}
		Assert.That(actual, Is.EqualTo(expected.AsSpan(256, 64).ToArray())
			.Within(1e-6f), "O01 must address child source frame 256, not " +
			"a wall-clock offset or a sample offset inside the last child voice.");
		Assert.That(actual[0], Is.GreaterThan(0.01f));
	}

	[TestCase(false)]
	[TestCase(true)]
	public void TrackerQxyRewindsPrivateSequenceAndClearsEarlierOxxOffset(
		bool viaInstrument)
	{
		SongDocument document = CreateDocument(out ObjectId sample);
		ObjectId child = AddPattern(document, sample, rows: 5);
		ObjectId sequence = AddSequence(document, child);
		ObjectId source = viaInstrument
			? AddInstrument(document, sequence) : sequence;
		ObjectId parent = AddPattern(document, source, rows: 4,
			mixdown: !viaInstrument);
		DataPatternDefinition pattern =
			(DataPatternDefinition)document.Objects[parent];
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new SampleOffsetPatternEffect(1));
		pattern.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new RetriggerPatternEffect(0x01));

		using PreparedIncrementalPlaybackPlan plan =
			new PreparedIncrementalPlaybackFactory(Mono()).Create(
				document, parent);
		float[] before = new float[140];
		plan.Source.Render(before.Length, before);
		PlaybackVoice voice = plan.Session.GetChannelState(0).CurrentVoice!;
		PlaybackSession prior = ChildSession(voice);
		Assert.Multiple(() =>
		{
			Assert.That(prior.NextFrame, Is.EqualTo(20L),
				"Q01 already retriggers on row 1's tick zero at frame 120; " +
				"the restarted source has advanced 20 frames by frame 140.");
			Assert.That(voice.SoundState.PlaybackOriginFrame, Is.EqualTo(120L));
		});
		float[] after = new float[1];
		plan.Source.Render(1, after);
		PlaybackSession restarted = ChildSession(voice);
		Assert.Multiple(() =>
		{
			Assert.That(restarted, Is.Not.SameAs(prior),
				"Q01 at the first active tick of row 1 must replay the generator.");
			Assert.That(restarted.NextFrame, Is.EqualTo(1L),
				"The restarted child begins at frame zero, with Oxx forgotten.");
			Assert.That(voice.SoundState.PlaybackOriginFrame, Is.EqualTo(140L));
			Assert.That(voice.SoundState.GetType()
				.GetProperty("SourceFrameOffset")!.GetValue(voice.SoundState),
					Is.EqualTo(0L));
		});
	}

	[Test]
	public void SourceFrameBeyondFiniteChildDoesNotLeakNotesIntoFollowingInvocation()
	{
		SongDocument document = CreateDocument(out ObjectId sample);
		ObjectId shortChild = AddPattern(document, sample, rows: 1);
		ObjectId first = AddPattern(document, shortChild, rows: 1,
			mixdown: true);
		ObjectId second = AddPattern(document, shortChild, rows: 1,
			mixdown: true);
		((DataPatternDefinition)document.Objects[first])
			.Grid.GetOrCreateCell(0, 0).Effects.Add(
				new SampleOffsetPatternEffect(4)); // 1024 > child length
		ObjectId root = AddSequence(document, first, second);
		using PreparedIncrementalPlaybackPlan plan =
			new PreparedIncrementalPlaybackFactory(Mono()).Create(document, root);
		float[] output = new float[121];
		plan.Source.Render(output.Length, output);
		Assert.Multiple(() =>
		{
			Assert.That(output.AsSpan(0, 120).ToArray(),
				Is.All.EqualTo(0f),
				"An offset after the child logical end must not start a late voice.");
			Assert.That(output[120], Is.EqualTo(0f).Within(1e-6f),
				"The next invocation should independently restart at native frame zero.");
		});
		float[] next = new float[2];
		plan.Source.Render(2, next);
		Assert.That(next[0], Is.GreaterThan(0),
			"The un-offset second order must still be able to emit its own notes.");
	}

	[Test]
	public void RepeatedSequenceOrderRestartsPrivateOxxWithoutLeakingPreviousRenderer()
	{
		SongDocument document = CreateDocument(out ObjectId sample);
		ObjectId privateId = AddPattern(document, sample, rows: 5);
		ObjectId first = AddPattern(document, privateId, rows: 1, mixdown: true);
		ObjectId next = AddPattern(document, privateId, rows: 2, mixdown: true);
		((DataPatternDefinition)document.Objects[first])
			.Grid.GetOrCreateCell(0, 0).Effects.Add(new SampleOffsetPatternEffect(1));
		((DataPatternDefinition)document.Objects[next])
			.Grid.GetOrCreateCell(0, 0).Effects.Add(new SampleOffsetPatternEffect(0));
		ObjectId root = AddSequence(document, first, next);

		using PreparedIncrementalPlaybackPlan plan =
			new PreparedIncrementalPlaybackFactory(Mono()).Create(document, root);
		float[] start = new float[1];
		plan.Source.Render(1, start);
		PlaybackVoice original = plan.Session.GetChannelState(0).CurrentVoice!;
		PlaybackSession firstChild = ChildSession(original);
		Assert.Multiple(() =>
		{
			Assert.That(firstChild.NextFrame, Is.EqualTo(257L));
			Assert.That(original.SoundState.GetType()
				.GetProperty("SourceFrameOffset")!.GetValue(original.SoundState),
					Is.EqualTo(256L));
		});
		float[] middle = new float[119];
		plan.Source.Render(middle.Length, middle);
		float[] secondStart = new float[1];
		plan.Source.Render(1, secondStart);
		PlaybackVoice replacement = plan.Session.GetChannelState(0).CurrentVoice!;
		PlaybackSession secondChild = ChildSession(replacement);
		Assert.Multiple(() =>
		{
			Assert.That(replacement, Is.Not.SameAs(original));
			Assert.That(secondChild, Is.Not.SameAs(firstChild));
			Assert.That(secondChild.NextFrame, Is.EqualTo(257L),
					"O00 recalls O01 from the preceding order in the parent channel, " +
					"but creates a fresh private child at its own source frame 256.");
			Assert.That(replacement.SoundState.GetType()
				.GetProperty("SourceFrameOffset")!.GetValue(replacement.SoundState),
					Is.EqualTo(256L));
			Assert.That(firstChild.NextFrame, Is.EqualTo(376L),
					"The earlier child should not rewind when the next order begins.");
		});
	}

	[Test]
	public void NestedMixdownTrackerOxxAtMiddleLevelMatchesIndependentSequence()
	{
		SongDocument document = CreateDocument(out ObjectId sample);
		ObjectId leaf = AddPattern(document, sample, rows: 5);
		ObjectId sequence = AddSequence(document, leaf);
		ObjectId middle = AddPattern(document, sequence, rows: 5, mixdown: true);
		((DataPatternDefinition)document.Objects[middle])
			.Grid.GetOrCreateCell(0, 0).Effects.Add(
				new SampleOffsetPatternEffect(1));
		ObjectId outer = AddPattern(document, middle, rows: 5, mixdown: true);
		PreparedIncrementalPlaybackFactory factory = new(Mono());
		using PreparedIncrementalPlaybackPlan direct =
			factory.Create(document, sequence);
		using PreparedIncrementalPlaybackPlan nested =
			factory.Create(document, outer);
		float[] reference = new float[300];
		direct.Source.Render(reference.Length, reference);
		float[] actual = new float[30];
		nested.Source.Render(actual.Length, actual);
		Assert.That(actual, Is.EqualTo(reference.AsSpan(256, 30).ToArray())
			.Within(1e-6f),
			"Nested source frame offsets must be applied in their own private " +
			"source frame domain, not a parent note's wall-time domain.");
	}

	[TestCase(false)]
	[TestCase(true)]
	public void LiveAndOfflineSourceRenderTheSameOxxQxyPcmWithDifferentChunks(
		bool viaInstrument)
	{
		SongDocument document = CreateDocument(out ObjectId sample);
		ObjectId child = AddPattern(document, sample, rows: 5);
		ObjectId sequence = AddSequence(document, child);
		ObjectId source = viaInstrument
			? AddInstrument(document, sequence) : sequence;
		ObjectId parent = AddPattern(document, source, rows: 4,
			mixdown: !viaInstrument);
		DataPatternDefinition rootPattern =
			(DataPatternDefinition)document.Objects[parent];
		rootPattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new SampleOffsetPatternEffect(1));
		rootPattern.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new RetriggerPatternEffect(0x01));
		ObjectId rootSequence = AddSequence(document, parent);
		document.RootSequenceId = rootSequence;

		RenderConfiguration config = RenderConfiguration.Stereo(1000);
		PlaybackRequestAudioSourceFactory realtimeFactory = new(config);
		PlaybackRequest request = SequencePlaybackRequest.Create(
			document, rootSequence);
		IAudioOutputSource live = realtimeFactory.Create(request);
		using IDisposable realtime = (IDisposable)live;
		using OfflineSongRenderPlan export =
			new OfflineSongRenderPlanFactory(config).Create(document);
		const int frames = 220; // Includes O01 and multiple row-1 Q01 retriggers.
		const int channels = 2;
		float[] livePcm = new float[frames * channels];
		int[] blocks = [1, 7, 43, 3, 17, 5];
		for (int pos = 0, n = 0; pos < frames; n++)
		{
			int count = Math.Min(blocks[n % blocks.Length], frames - pos);
			live.Render(count, livePcm.AsSpan(pos * channels, count * channels));
			pos += count;
		}
		float[] exportPcm = new float[frames * channels];
		for (int pos = 0; pos < frames;)
		{
			int count = Math.Min(64, frames - pos);
			int produced = export.Source.RenderLogical(count,
				exportPcm.AsSpan(pos * channels, count * channels));
			Assert.That(produced, Is.EqualTo(count),
				"Both paths must still be within the finite logical arrangement.");
			pos += count;
		}
		Assert.That(livePcm.Any(v => Math.Abs(v) > 1e-6f), Is.True,
			"Silence would not prove native-offset/retrigger parity.");
		for (int i = 0; i < livePcm.Length; i++)
			Assert.That(livePcm[i], Is.EqualTo(exportPcm[i]).Within(1e-6f),
				$"Source PCM mismatch at frame {i / channels} " +
				$"on speaker {i % channels}.");
	}


	[TestCase(false)]
	[TestCase(true)]
	public void ModulatedChildSampleOxxMatchesUninterruptedPrivateSequencePcm(
		bool viaInstrument)
	{
		SongDocument document = CreateDocument(out ObjectId sample);
		ObjectId child = AddPattern(document, sample, rows: 5);
		DataPatternDefinition childPattern =
			(DataPatternDefinition)document.Objects[child];
		childPattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new VibratoPatternEffect(0x48));
		ObjectId sequence = AddSequence(document, child);
		ObjectId source = viaInstrument
			? AddInstrument(document, sequence) : sequence;
		ObjectId parent = AddPattern(document, source, rows: 5,
			mixdown: !viaInstrument);
		((DataPatternDefinition)document.Objects[parent])
			.Grid.GetOrCreateCell(0, 0).Effects.Add(
				new SampleOffsetPatternEffect(1));
		PreparedIncrementalPlaybackFactory factory = new(Mono());
		using PreparedIncrementalPlaybackPlan uninterrupted =
			factory.Create(document, sequence);
		using PreparedIncrementalPlaybackPlan withOffset =
			factory.Create(document, parent);
		float[] baseline = new float[350];
		uninterrupted.Source.Render(baseline.Length, baseline);
		float[] offsetPcm = new float[60];
		withOffset.Source.Render(1, offsetPcm.AsSpan(0, 1));
		for (int i = 1; i < offsetPcm.Length; i++)
			withOffset.Source.Render(1, offsetPcm.AsSpan(i, 1));
		Assert.That(offsetPcm, Is.EqualTo(
			baseline.AsSpan(256, offsetPcm.Length).ToArray()).Within(1e-6f),
			"Oxx must integrate the child voice's own vibrato pitch trajectory " +
			"while discarding earlier source PCM, even through an Instrument.");
		Assert.That(offsetPcm.Any(v => v != 0f), Is.True);
	}

	[TestCase(false)]
	[TestCase(true)]
	public void QxyAfterOxxReconstructsChildPitchModulationWithoutRememberingOffset(
		bool viaInstrument)
	{
		SongDocument document = CreateDocument(out ObjectId sample);
		ObjectId child = AddPattern(document, sample, rows: 5);
		((DataPatternDefinition)document.Objects[child])
			.Grid.GetOrCreateCell(0, 0).Effects.Add(
				new VibratoPatternEffect(0x48));
		ObjectId sequence = AddSequence(document, child);
		ObjectId source = viaInstrument
			? AddInstrument(document, sequence) : sequence;
		ObjectId parent = AddPattern(document, source, rows: 5,
			mixdown: !viaInstrument);
		DataPatternDefinition root = (DataPatternDefinition)document.Objects[parent];
		root.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new SampleOffsetPatternEffect(1));
		root.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new RetriggerPatternEffect(0x03));
		PreparedIncrementalPlaybackFactory factory = new(Mono());
		using PreparedIncrementalPlaybackPlan withOffset =
			factory.Create(document, parent);
		using PreparedIncrementalPlaybackPlan direct =
			factory.Create(document, sequence);
		float[] earlier = new float[180];
		withOffset.Source.Render(earlier.Length, earlier);
		PlaybackVoice outer = withOffset.Session.GetChannelState(0).CurrentVoice!;
		PlaybackSession old = ChildSession(outer);
		float[] afterRetrigger = new float[31];
		withOffset.Source.Render(afterRetrigger.Length, afterRetrigger);
		PlaybackSession fresh = ChildSession(outer);
		direct.Source.Render(31, new float[31]);
		PlaybackVoice replayedSample = fresh.GetChannelState(0).CurrentVoice!;
		PlaybackVoice referenceSample = direct.Session.GetChannelState(0).CurrentVoice!;
		Assert.Multiple(() =>
		{
			Assert.That(fresh, Is.Not.SameAs(old));
			Assert.That(fresh.NextFrame, Is.EqualTo(31L));
			Assert.That(outer.SoundState.PlaybackOriginFrame, Is.EqualTo(180L));
			Assert.That(outer.SoundState.GetType().GetProperty(
				"SourceFrameOffset")!.GetValue(outer.SoundState), Is.EqualTo(0L));
			Assert.That(replayedSample.SoundState.PitchTrajectory.GetPosition(30),
				Is.EqualTo(referenceSample.SoundState.PitchTrajectory.GetPosition(30))
					.Within(1e-8),
				"Q03 must reconstruct child vibrato's integrated phase after Oxx.");
			Assert.That(fresh.GetChannelState(0).CurrentVoice?.StartFrame,
					Is.EqualTo(0L));
		});
	}

	[TestCase(false)]
	[TestCase(true)]
	[Explicit("Known missing dynamic pitch propagation from a non-flattened " +
		"private note to its active child voices; red PCM repro validated " +
		"on CI run 38038804155. Do not silently repurpose Tempo or " +
		"resample the completed mixdown PCM.")]
	public void PrivateNotePitchSlideShouldModulateActiveChildVoicesWithoutChangingClock(
		bool viaInstrument)
	{
		SongDocument document = CreateDocument(out ObjectId sample);
		ObjectId child = AddPattern(document, sample, rows: 3);
		ObjectId source = viaInstrument ? AddInstrument(document, child) : child;
		ObjectId parent = AddPattern(document, source, rows: 3,
			mixdown: !viaInstrument);
		PreparedIncrementalPlaybackFactory factory = new(Mono());
		using PreparedIncrementalPlaybackPlan noSlide =
			factory.Create(document, parent);

		((DataPatternDefinition)document.Objects[parent])
			.Grid.GetOrCreateCell(0, 0).Effects.Add(
				new PitchSlidePatternEffect(96));
		using PreparedIncrementalPlaybackPlan slide =
			factory.Create(document, parent);
		float[] normalPcm = new float[110];
		float[] modulatedPcm = new float[110];
		noSlide.Source.Render(normalPcm.Length, normalPcm);
		slide.Source.Render(modulatedPcm.Length, modulatedPcm);
		PlaybackVoice parentVoice =
			slide.Session.GetChannelState(0).CurrentVoice!;
		Assert.That(parentVoice.SoundState.PitchTrajectory.GetMultiplier(80),
			Is.GreaterThan(1.001),
			"The parent private note's pitch curve is definitely active.");
		Assert.That(modulatedPcm, Is.Not.EqualTo(normalPcm),
			"Private source note pitch slides should change child note pitch, " +
			"without altering the child Sequence Tempo/row deadlines. " +
			"At present PreparedRecursiveMixdownSound.Render ignores its " +
			"SoundState.PitchTrajectory, so the audible outputs are identical.");
	}

	private static PlaybackSession ChildSession(PlaybackVoice parent)
		=> (PlaybackSession)parent.Sound.GetType()
			.GetProperty("Session", BindingFlags.Instance | BindingFlags.Public)!
			.GetValue(parent.Sound)!;

	private static SongDocument CreateDocument(out ObjectId sampleId)
	{
		SongDocument document = new();
		sampleId = document.AllocateObjectId();
		document.Add(SampleDefinition.CreateImported(sampleId,
			"Long ramp", "ramp.wav", RampWave()));
		return document;
	}

	private static ObjectId AddPattern(SongDocument document,
		ObjectId source, int rows, bool mixdown = false)
	{
		ObjectId id = document.AllocateObjectId();
		DataPatternDefinition pattern = new(id, "Source")
		{
			RowCount = rows, ChannelCount = 1,
		};
		pattern.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(source, mixdown: mixdown);
		document.Add(pattern);
		return id;
	}

	private static ObjectId AddSequence(SongDocument document,
		params ObjectId[] patterns)
	{
		ObjectId id = document.AllocateObjectId();
		DataSequenceDefinition sequence = new(id, "Sequence");
		foreach (ObjectId pattern in patterns)
			sequence.Entries.Add(new SequenceEntry(pattern));
		document.Add(sequence);
		return id;
	}

	private static ObjectId AddInstrument(SongDocument document, ObjectId source)
	{
		ObjectId id = document.AllocateObjectId();
		InstrumentDefinition instrument = new(id, "Recursive instrument");
		instrument.ToneSpecifications.Add(new ToneSpecification { SourceId = source });
		instrument.ToneTable.Add(0);
		document.Add(instrument);
		return id;
	}

	private static RenderConfiguration Mono()
		=> new(1000, [new OutputChannelConfiguration(Vector3.Zero,
			positionalImportance: 0)]);

	private static byte[] RampWave()
	{
		const int frameCount = 2000;
		using MemoryStream stream = new();
		using BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: true);
		writer.Write(Encoding.ASCII.GetBytes("RIFF"));
		writer.Write(36 + frameCount * 2);
		writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
		writer.Write(16);
		writer.Write((ushort)1);
		writer.Write((ushort)1);
		writer.Write(1000);
		writer.Write(2000);
		writer.Write((ushort)2);
		writer.Write((ushort)16);
		writer.Write(Encoding.ASCII.GetBytes("data"));
		writer.Write(frameCount * 2);
		for (int i = 0; i < frameCount; i++)
			writer.Write((short)((i % 1000) * 20));
		writer.Flush();
		return stream.ToArray();
	}
}
