using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Numerics;

using Heresy.Core.Envelopes;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Samples;
using Heresy.Playback;
using Heresy.Render.Configuration;

using NUnit.Framework;

namespace Heresy.Tests.Playback;

[TestFixture]
public sealed class NodeEnvelopePlaybackTests
{
	[TestCase(EnvelopeSustainLoopMode.Forward, false)]
	[TestCase(EnvelopeSustainLoopMode.PingPong, false)]
	[TestCase(EnvelopeSustainLoopMode.Forward, true)]
	[TestCase(EnvelopeSustainLoopMode.PingPong, true)]
	public void SharedNodeEnvelopePlaysHeldReleaseAndFinishedAcrossBothSources(
		EnvelopeSustainLoopMode mode, bool throughInstrument)
	{
		SongDocument song = new();
		ObjectId envelopeId = song.AllocateObjectId();
		NodeEnvelopeDefinition envelope = new(envelopeId, "Shared curve");
		envelope.ReplaceCurve(
		[
			new(TimeSpan.Zero, 0.4),
			new(TimeSpan.FromMilliseconds(10), 0.8),
			new(TimeSpan.FromMilliseconds(20), 0.2),
			new(TimeSpan.FromMilliseconds(90), 0.0),
		], 1, 2, mode);
		song.Add(envelope);

		ObjectId sourceId;
		if (throughInstrument)
		{
			ObjectId sampleId = song.AllocateObjectId();
			song.Add(SampleDefinition.CreateImported(
				sampleId, "Held", "held.wav", ConstantSample()));
			ObjectId instrumentId = song.AllocateObjectId();
			InstrumentDefinition instrument = new(instrumentId, "Enveloped");
			instrument.ToneSpecifications.Add(new ToneSpecification
			{
				SourceId = sampleId, VolumeEnvelopeId = envelopeId,
			});
			instrument.ToneTable.Add(0);
			song.Add(instrument);
			sourceId = instrumentId;
		}
		else
		{
			ObjectId fmId = song.AllocateObjectId();
			song.Add(new FmSynthDefinition(fmId, "FM",
				new FmSynthGraph(
					[new FmEnvelopeNode(1, envelopeId)], outputNodeId: 1)));
			sourceId = fmId;
		}

		ObjectId rootId = song.AllocateObjectId();
		song.Add(new ScriptPatternDefinition(rootId, "Play")
		{
			RowCount = 4, ChannelCount = 1,
			Source = $"Note(0, 0, _O({sourceId.Value})); Off(0.5, 0);",
		});
		PreparedIncrementalPlaybackFactory factory = new(
			new RenderConfiguration(1000,
				[new OutputChannelConfiguration(
					Vector3.Zero, positionalImportance: 0)]));
		using PreparedIncrementalPlaybackPlan continuous =
			factory.Create(song, rootId);
		using PreparedIncrementalPlaybackPlan chunked =
			factory.Create(song, rootId);
		float[] whole = new float[320];
		continuous.Source.Render(whole.Length, whole);
		float[] partitioned = new float[whole.Length];
		int[] chunks = [1, 7, 19, 2, 23];
		for (int at = 0, batch = 0; at < partitioned.Length; batch++)
		{
			int n = Math.Min(chunks[batch % chunks.Length],
				partitioned.Length - at);
			chunked.Source.Render(n, partitioned.AsSpan(at, n));
			at += n;
		}
		Assert.Multiple(() =>
		{
			Assert.That(partitioned, Is.EqualTo(whole).Within(1e-5f),
				"the envelope must be independent of renderer block boundaries");
			Assert.That(Math.Abs(whole[35]), Is.GreaterThan(0.01f),
				"a held note traverses its sustain region");
			Assert.That(whole.Skip(75).Take(85).Any(v => Math.Abs(v) > 0.01f),
				Is.True, "note-off preserves its release tail");
			Assert.That(whole.Skip(250), Is.All.EqualTo(0f).Within(1e-6f),
				"finished voices must retire after release");
		});
	}

	[TestCase(EnvelopeSustainLoopMode.Forward)]
	[TestCase(EnvelopeSustainLoopMode.PingPong)]
	public void InstrumentToneVolumeEnvelopeReleasesAnUnderlyingSynthAfterPatternNoteOff(
		EnvelopeSustainLoopMode mode)
	{
		SongDocument song = new();
		ObjectId envelopeId = song.AllocateObjectId();
		NodeEnvelopeDefinition envelope = new(envelopeId, "Instrument release");
		envelope.ReplaceCurve(
		[
			new(TimeSpan.Zero, 0.4),
			new(TimeSpan.FromMilliseconds(10), 0.8),
			new(TimeSpan.FromMilliseconds(20), 0.2),
			new(TimeSpan.FromMilliseconds(90), 0.0),
		], 1, 2, mode);
		song.Add(envelope);
		ObjectId synthId = song.AllocateObjectId();
		song.Add(new FmSynthDefinition(synthId, "Sustained source",
			new FmSynthGraph([new FmConstantNode(0, 0.5)], 0)));
		ObjectId instrumentId = song.AllocateObjectId();
		InstrumentDefinition instrument = new(instrumentId, "Synth with tone envelope");
		instrument.ToneSpecifications.Add(new ToneSpecification
		{
			SourceId = synthId, VolumeEnvelopeId = envelopeId,
		});
		instrument.ToneTable.Add(0);
		song.Add(instrument);
		ObjectId patternId = song.AllocateObjectId();
		song.Add(new ScriptPatternDefinition(patternId, "Release synth")
		{
			RowCount = 4, ChannelCount = 1,
			Source = $"Note(0, 0, _O({instrumentId.Value})); Off(0.5, 0);",
		});
		PreparedIncrementalPlaybackFactory factory = new(
			new RenderConfiguration(1000,
				[new OutputChannelConfiguration(
					Vector3.Zero, positionalImportance: 0)]));
		using PreparedIncrementalPlaybackPlan plan = factory.Create(song, patternId);
		float[] pcm = new float[320];
		plan.Source.Render(pcm.Length, pcm);
		Assert.Multiple(() =>
		{
			Assert.That(Math.Abs(pcm[35]), Is.GreaterThan(0.01f),
				"the held Synth must sound before Note Off");
			Assert.That(pcm.Skip(75).Take(85).Any(v => Math.Abs(v) > 0.01f),
				Is.True, "the Instrument's release must keep its Synth source audible");
			Assert.That(pcm.Skip(250), Is.All.EqualTo(0f).Within(1e-6f),
				"release completion must still retire the Synth");
		});
	}

	[TestCase(0.0, true)]
	[TestCase(0.1, false)]
	public void CompletedVolumeReleaseRetiresOnlyAtPermanentZero(
		double finalVolume, bool shouldRetireEarly)
	{
		SongDocument song = new();
		ObjectId pitchId = song.AllocateObjectId();
		NodeEnvelopeDefinition pitch = new(pitchId, "Long Pitch release");
		pitch.ReplaceCurve([
			new(TimeSpan.Zero, 1.0),
			new(TimeSpan.FromMilliseconds(10), 1.0),
			new(TimeSpan.FromMilliseconds(120), 1.0),
		], 1, 1, EnvelopeSustainLoopMode.Forward);
		song.Add(pitch);
		ObjectId volumeId = song.AllocateObjectId();
		NodeEnvelopeDefinition volume = new(volumeId, "Short Volume release");
		volume.ReplaceCurve([
			new(TimeSpan.Zero, 1.0),
			new(TimeSpan.FromMilliseconds(10), 1.0),
			new(TimeSpan.FromMilliseconds(30), finalVolume),
		], 1, 1, EnvelopeSustainLoopMode.Forward);
		song.Add(volume);
		ObjectId synthId = song.AllocateObjectId();
		song.Add(new FmSynthDefinition(synthId, "Two releasing envelopes",
			new FmSynthGraph([
				new FmEnvelopeNode(1, pitchId),
				new FmOscillatorNode(2, FmOscillatorWaveform.Square, 90,
					multiplierNodeId: 1),
				new FmEnvelopeNode(3, volumeId),
				new FmOperatorNode(4, FmOperatorKind.Multiply, [2, 3]),
			], 4)));
		ObjectId patternId = song.AllocateObjectId();
		song.Add(new ScriptPatternDefinition(patternId, "Release")
		{
			RowCount = 4, ChannelCount = 1,
			Source = $"Note(0, 0, _O({synthId.Value})); Off(0.5, 0);",
		});
		PreparedIncrementalPlaybackFactory factory = new(
			new RenderConfiguration(1000,
				[new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0)]));
		using PreparedIncrementalPlaybackPlan plan = factory.Create(song, patternId);
		float[] pcm = new float[300];
		plan.Source.Render(pcm.Length, pcm);
		Assert.That(pcm.Skip(90).Take(30).Any(v => Math.Abs(v) > 0.02f),
			Is.EqualTo(!shouldRetireEarly),
			"Once short Volume release finishes, a nonzero terminal gain "
			+ "must leave the long Pitch release audible");
		Assert.That(pcm.Skip(220), Is.All.EqualTo(0f).Within(1e-6f),
			"All completed release lifecycles must retire");
	}

	[TestCase(EnvelopeSustainLoopMode.Forward, true)]
	[TestCase(EnvelopeSustainLoopMode.PingPong, true)]
	[TestCase(EnvelopeSustainLoopMode.None, false)]
	public void InstrumentPitchReleaseKeepsUnsustainedNestedSynthAlive(
		EnvelopeSustainLoopMode loop, bool hasRelease)
	{
		SongDocument song = new();
		ObjectId envelopeId = song.AllocateObjectId();
		NodeEnvelopeDefinition envelope = new(envelopeId, "Pitch-only release");
		envelope.ReplaceCurve([
			new(TimeSpan.Zero, 0.0),
			new(TimeSpan.FromMilliseconds(10), 0.0),
			new(TimeSpan.FromMilliseconds(20), 0.0),
			new(TimeSpan.FromMilliseconds(90), 0.0),
		], 1, 2, loop);
		song.Add(envelope);
		ObjectId synthId = song.AllocateObjectId();
		song.Add(new FmSynthDefinition(synthId, "Unsustained child",
			new FmSynthGraph([new FmConstantNode(0, 0.5)], 0)));
		ObjectId instrumentId = song.AllocateObjectId();
		InstrumentDefinition instrument = new(instrumentId, "Pitch release parent");
		instrument.ToneSpecifications.Add(new ToneSpecification
		{
			SourceId = synthId, PitchEnvelopeId = envelopeId,
		});
		instrument.ToneTable.Add(0);
		song.Add(instrument);
		ObjectId patternId = song.AllocateObjectId();
		song.Add(new ScriptPatternDefinition(patternId, "Nested release")
		{
			RowCount = 4, ChannelCount = 1,
			Source = $"Note(0, 0, _O({instrumentId.Value})); Off(0.5, 0);",
		});
		PreparedIncrementalPlaybackFactory factory = new(
			new RenderConfiguration(1000,
				[new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0)]));
		using PreparedIncrementalPlaybackPlan plan = factory.Create(song, patternId);
		float[] pcm = new float[320];
		plan.Source.Render(pcm.Length, pcm);
		Assert.That(pcm.Skip(75).Take(30).Any(v => Math.Abs(v) > 0.1f),
			Is.EqualTo(hasRelease),
			"Pitch-only sustain must protect the unlooped child source");
		Assert.That(pcm.Skip(250), Is.All.EqualTo(0f).Within(1e-6f),
			"Pitch release completion or a Definite cut must end the note");
	}

	private static byte[] ConstantSample()
	{
		const int frames = 400;
		using MemoryStream stream = new();
		using BinaryWriter writer = new(stream, Encoding.ASCII,
			leaveOpen: true);
		writer.Write(Encoding.ASCII.GetBytes("RIFF"));
		writer.Write(36 + frames * 2);
		writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
		writer.Write(16);
		writer.Write((ushort)1);
		writer.Write((ushort)1);
		writer.Write(1000);
		writer.Write(2000);
		writer.Write((ushort)2);
		writer.Write((ushort)16);
		writer.Write(Encoding.ASCII.GetBytes("data"));
		writer.Write(frames * 2);
		for (int i = 0; i < frames; i++)
			writer.Write((short)16384);
		writer.Flush();
		return stream.ToArray();
	}

	[TestCase(EnvelopeSustainLoopMode.Forward)]
	[TestCase(EnvelopeSustainLoopMode.PingPong)]
	[TestCase(EnvelopeSustainLoopMode.None)]
	public void PatternFmFrequencyOnlyEnvelopeRetiresAfterNoteOff(
		EnvelopeSustainLoopMode loop)
	{
		SongDocument song = new();
		ObjectId envelopeId = song.AllocateObjectId();
		NodeEnvelopeDefinition envelope = new(envelopeId, "Pitch contour");
		envelope.ReplaceCurve(
		[
			new(TimeSpan.Zero, 1.0),
			new(TimeSpan.FromMilliseconds(10), 1.0),
			new(TimeSpan.FromMilliseconds(100), 0.0),
		], 1, 1, loop);
		song.Add(envelope);
		ObjectId fmId = song.AllocateObjectId();
		song.Add(new FmSynthDefinition(fmId, "Pitch modulated FM",
			new FmSynthGraph(
			[
				new FmEnvelopeNode(1, envelopeId),
				new FmOscillatorNode(2, FmOscillatorWaveform.Square, 90,
					multiplierNodeId: 1),
			], outputNodeId: 2)));
		ObjectId patternId = song.AllocateObjectId();
		song.Add(new ScriptPatternDefinition(patternId, "Release")
		{
			RowCount = 4, ChannelCount = 1,
			Source = $"Note(0, 0, _O({fmId.Value})); Off(0.5, 0);",
		});
		PreparedIncrementalPlaybackFactory factory = new(
			new RenderConfiguration(1000,
				[new OutputChannelConfiguration(Vector3.Zero,
					positionalImportance: 0)]));
		using PreparedIncrementalPlaybackPlan whole = factory.Create(song, patternId);
		using PreparedIncrementalPlaybackPlan parts = factory.Create(song, patternId);
		float[] pcm = new float[320];
		float[] chunked = new float[320];
		whole.Source.Render(pcm.Length, pcm);
		for (int at = 0; at < chunked.Length;)
		{
			int frames = Math.Min(13, chunked.Length - at);
			parts.Source.Render(frames, chunked.AsSpan(at, frames));
			at += frames;
		}
		Assert.Multiple(() =>
		{
			Assert.That(chunked, Is.EqualTo(pcm).Within(1e-6f));
			if (loop == EnvelopeSustainLoopMode.None)
				Assert.That(pcm.Skip(61).Take(34),
					Is.All.EqualTo(0f).Within(1e-6f),
					"No sustain loop means Definite Note Off must cut the sound");
			else
				Assert.That(pcm.Skip(70).Take(25)
					.Any(v => Math.Abs(v) > 0.5f), Is.True,
					"Any sustain-loop modulation keeps the note releasing");
			Assert.That(pcm.Skip(200), Is.All.EqualTo(0f).Within(1e-6f),
				"An oscillator with only a frequency envelope must "
				+ "still retire when that release completes");
		});
	}

	[Test]
	public void FmNodeEnvelopeResolvesAndReleasesInPreparedPlayback()
	{
		SongDocument song = new();
		ObjectId envelopeId = song.AllocateObjectId();
		NodeEnvelopeDefinition envelope = new(envelopeId, "FM node curve");
		envelope.ReplaceCurve(
		[
			new(TimeSpan.Zero, 0.4),
			new(TimeSpan.FromMilliseconds(10), 0.8),
			new(TimeSpan.FromMilliseconds(20), 0.2),
			new(TimeSpan.FromMilliseconds(40), 0.0),
		], sustainStartIndex: 1, sustainEndIndex: 1,
			EnvelopeSustainLoopMode.Forward);
		song.Add(envelope);
		ObjectId fmId = song.AllocateObjectId();
		song.Add(new FmSynthDefinition(fmId, "Node modulator",
			new FmSynthGraph([new FmEnvelopeNode(1, envelopeId)],
				outputNodeId: 1)));
		ObjectId rootId = song.AllocateObjectId();
		song.Add(new ScriptPatternDefinition(rootId, "One voice")
		{
			RowCount = 3,
			ChannelCount = 1,
			Source = $"Note(0, 0, _O({fmId.Value})); Off(0.5, 0);",
		});

		PreparedIncrementalPlaybackFactory factory = new(
			new RenderConfiguration(1000,
				[new OutputChannelConfiguration(
					Vector3.Zero, positionalImportance: 0)]));
		using PreparedIncrementalPlaybackPlan playback =
			factory.Create(song, rootId);
		float[] samples = new float[200];
		playback.Source.Render(samples.Length, samples);

		Assert.Multiple(() =>
		{
			Assert.That(samples[0], Is.EqualTo(0.4f).Within(1e-5));
			Assert.That(samples[20], Is.EqualTo(0.8f).Within(1e-5),
				"Before Note Off a single-node sustain remains held.");
			Assert.That(samples[40], Is.EqualTo(0.8f).Within(1e-5));
			Assert.That(samples[70], Is.GreaterThan(0.0f),
				"FM rendering preserves the post-Note-Off node tail.");
			Assert.That(samples[160], Is.Zero.Within(1e-6f),
				"Completion of the node curve renders silence.");
		});
	}

	[Test]
	public void FrozenPlaybackRetainsOldNodeValuesAfterAuthoredEdit()
	{
		SongDocument song = new();
		ObjectId envelopeId = song.AllocateObjectId();
		NodeEnvelopeDefinition envelope = new(envelopeId, "FM node");
		envelope.ReplaceCurve([new(TimeSpan.Zero, 0.3)],
			0, 0, EnvelopeSustainLoopMode.Forward);
		song.Add(envelope);
		ObjectId fmId = song.AllocateObjectId();
		song.Add(new FmSynthDefinition(fmId, "FM",
			new FmSynthGraph([new FmEnvelopeNode(1, envelopeId)],
				outputNodeId: 1)));
		ObjectId patternId = song.AllocateObjectId();
		song.Add(new ScriptPatternDefinition(patternId, "Root")
		{
			RowCount = 2, ChannelCount = 1,
			Source = $"Note(0, 0, _O({fmId.Value}));",
		});
		PreparedIncrementalPlaybackFactory factory = new(
			new RenderConfiguration(1000,
				[new OutputChannelConfiguration(
					Vector3.Zero, positionalImportance: 0)]));
		using PreparedIncrementalPlaybackPlan before =
			factory.Create(song, patternId);
		envelope.ReplaceCurve([new(TimeSpan.Zero, -0.75)],
			0, 0, EnvelopeSustainLoopMode.PingPong);
		song.MarkChanged(affectsAudio: true);
		using PreparedIncrementalPlaybackPlan after =
			factory.Create(song, patternId);
		float[] oldSamples = new float[5];
		float[] newSamples = new float[5];
		before.Source.Render(5, oldSamples);
		after.Source.Render(5, newSamples);
		Assert.That(oldSamples, Is.All.EqualTo(0.3f).Within(1e-5f));
		Assert.That(newSamples, Is.All.EqualTo(-0.75f).Within(1e-5f));
	}
}
