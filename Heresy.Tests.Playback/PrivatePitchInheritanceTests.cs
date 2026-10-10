using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;

using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Samples;
using Heresy.Playback;
using Heresy.Render.Configuration;

using NUnit.Framework;

namespace Heresy.Tests.Playback;

/// <summary>
/// Inherited private-note pitch bends every active/future child voice, not
/// the mixed speaker PCM. Every child retains its own tracker clock.
/// </summary>
[TestFixture]
public sealed class PrivatePitchInheritanceTests
{
	[TestCase(false)]
	[TestCase(true)]
	public void FutureChildNoteCapturesParentPitchAtItsUnchangedRowBoundary(
		bool viaInstrument)
	{
		SongDocument document = new();
		ObjectId sample = AddSample(document);
		ObjectId child = AddPattern(document, "Child", 3);
		((DataPatternDefinition)document.Objects[child])
			.Grid.GetOrCreateCell(1, 0).Note = new StartPatternNote(sample);
		ObjectId source = viaInstrument ? AddInstrument(document, child) : child;
		ObjectId root = AddPattern(document, "Parent", 3);
		DataPatternDefinition parent = (DataPatternDefinition)document.Objects[root];
		parent.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(source, mixdown: !viaInstrument);
		PreparedIncrementalPlaybackFactory factory = new(Mono());
		using PreparedIncrementalPlaybackPlan plain = factory.Create(document, root);
		parent.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new PitchSlidePatternEffect(96));
		using PreparedIncrementalPlaybackPlan bent = factory.Create(document, root);
		float[] normal = new float[140];
		float[] changed = new float[140];
		plain.Source.Render(normal.Length, normal);
		bent.Source.Render(changed.Length, changed);
		Assert.Multiple(() =>
		{
			Assert.That(normal.AsSpan(0, 121).ToArray(), Is.All.EqualTo(0f));
			Assert.That(changed.AsSpan(0, 121).ToArray(), Is.All.EqualTo(0f),
				"Parent pitch must not alter child tracker row timing.");
			Assert.That(changed[121], Is.GreaterThan(normal[121] * 1.01f),
				"The future note's initial sample pitch must include the parent bend.");
		});
	}

	[Test]
	public void PitchAtFutureChildNoteSelectsTheCorrespondingInstrumentTone()
	{
		SongDocument document = new();
		ObjectId normalSample = AddSample(document, constant: 10000);
		ObjectId raisedSample = AddSample(document, constant: -10000);
		ObjectId instrumentId = document.AllocateObjectId();
		InstrumentDefinition instrument = new(instrumentId, "Pitch-selected");
		instrument.ToneSpecifications.Add(new ToneSpecification
			{ SourceId = normalSample });
		instrument.ToneSpecifications.Add(new ToneSpecification
			{ SourceId = raisedSample });
		instrument.ToneTable.Add(0);
		for (int index = 1; index < 24; index++)
			instrument.ToneTable.Add(1);
		document.Add(instrument);
		ObjectId child = AddPattern(document, "Child", 3);
		((DataPatternDefinition)document.Objects[child])
			.Grid.GetOrCreateCell(1, 0).Note = new StartPatternNote(instrumentId);
		ObjectId root = AddPattern(document, "Parent", 3);
		DataPatternDefinition parent = (DataPatternDefinition)document.Objects[root];
		parent.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(child, mixdown: true);
		PreparedIncrementalPlaybackFactory factory = new(Mono());
		using PreparedIncrementalPlaybackPlan unbent = factory.Create(document, root);
		parent.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new PitchSlidePatternEffect(96));
		using PreparedIncrementalPlaybackPlan bent = factory.Create(document, root);
		float[] normal = new float[130];
		float[] raised = new float[130];
		unbent.Source.Render(normal.Length, normal);
		bent.Source.Render(raised.Length, raised);
		Assert.Multiple(() =>
		{
			Assert.That(normal[119], Is.Zero);
			Assert.That(raised[119], Is.Zero);
			Assert.That(normal[121], Is.GreaterThan(0.1f));
			Assert.That(raised[121], Is.LessThan(-0.1f),
				"The pitch at child row one must select the higher instrument tone.");
		});
	}

	[Test]
	public void IndependentPitchBendsComposeAcrossTwoPrivateLevelsWithoutSpeedChange()
	{
		SongDocument document = new();
		ObjectId sample = AddSample(document, 1);
		ObjectId leaf = AddPattern(document, "Leaf", 3);
		((DataPatternDefinition)document.Objects[leaf])
			.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote(sample);
		ObjectId middleId = AddPattern(document, "Middle", 3);
		DataPatternDefinition middle =
			(DataPatternDefinition)document.Objects[middleId];
		middle.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(leaf, mixdown: true);
		ObjectId root = AddPattern(document, "Outer", 3);
		DataPatternDefinition outer = (DataPatternDefinition)document.Objects[root];
		outer.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(middleId, mixdown: true);
		PreparedIncrementalPlaybackFactory factory = new(Mono());
		using PreparedIncrementalPlaybackPlan unbent = factory.Create(document, root);
		outer.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new PitchSlidePatternEffect(96));
		using PreparedIncrementalPlaybackPlan outerBent = factory.Create(document, root);
		middle.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new PitchSlidePatternEffect(48));
		using PreparedIncrementalPlaybackPlan bothBent = factory.Create(document, root);
		float[] normal = new float[100];
		float[] outerOnly = new float[100];
		float[] both = new float[100];
		unbent.Source.Render(normal.Length, normal);
		outerBent.Source.Render(outerOnly.Length, outerOnly);
		bothBent.Source.Render(both.Length, both);
		string observed = string.Join(" | ", new[] { 0, 1, 2, 10, 20, 50, 90, 99 }
			.Select(i => $"{i}: {normal[i]:G9}, {outerOnly[i]:G9}, {both[i]:G9}"));
		Assert.Multiple(() =>
		{
			Assert.That(outerOnly[90], Is.GreaterThan(normal[90] * 1.01f),
				observed);
			Assert.That(both[90], Is.GreaterThan(outerOnly[90] * 1.01f),
				"Each private ancestor's pitch modulation must compose per voice. " + observed);
		});
	}

	[TestCase(false)]
	[TestCase(true)]
	public void ParentPitchSlidePlusOxxAdvancesChildNativeFramesAtIntegratedVoicePitch(
		bool viaInstrument)
	{
		SongDocument document = new();
		ObjectId sample = AddSample(document);
		ObjectId child = AddPattern(document, "Child", 4);
		((DataPatternDefinition)document.Objects[child])
			.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote(sample);
		ObjectId source = viaInstrument ? AddInstrument(document, child) : child;
		ObjectId root = AddPattern(document, "Parent", 4);
		DataPatternDefinition parent = (DataPatternDefinition)document.Objects[root];
		PatternCell first = parent.Grid.GetOrCreateCell(0, 0);
		first.Note = new StartPatternNote(source, mixdown: !viaInstrument);
		first.Effects.Add(new SampleOffsetPatternEffect(1));
		first.Effects.Add(new PitchSlidePatternEffect(96));
		using PreparedIncrementalPlaybackPlan plan =
			new PreparedIncrementalPlaybackFactory(Mono()).Create(document, root);
		float[] pcm = new float[80];
		plan.Source.Render(pcm.Length, pcm);
		var voice = plan.Session.GetChannelState(0).CurrentVoice!;
		double sourcePosition = 256.0;
		for (int frame = 0; frame < pcm.Length; frame++)
		{
			Assert.That(pcm[frame],
				Is.EqualTo((float)(sourcePosition * 10 / 32768))
					.Within(2e-5f),
				$"Frame {frame}: O01 must skip exactly 256 native source " +
				"frames; subsequent source pitch must be the *integral* of " +
				"the parent bend, not a resampled speaker-mix offset.");
			sourcePosition += voice.SoundState.PitchTrajectory.GetMultiplier(frame);
		}
	}

	[TestCase(false)]
	[TestCase(true)]
	public void ChildVibratoAndParentSlideSurviveOxxQxyWithChunkInvariantPcm(
		bool viaInstrument)
	{
		SongDocument document = new();
		ObjectId sample = AddSample(document);
		ObjectId child = AddPattern(document, "Vibrato leaf", 5);
		PatternCell childStart =
			((DataPatternDefinition)document.Objects[child])
			.Grid.GetOrCreateCell(0, 0);
		childStart.Note = new StartPatternNote(sample);
		childStart.Effects.Add(new VibratoPatternEffect(0x48));
		ObjectId source = viaInstrument ? AddInstrument(document, child) : child;
		ObjectId root = AddPattern(document, "Private slide", 5);
		DataPatternDefinition parent = (DataPatternDefinition)document.Objects[root];
		PatternCell first = parent.Grid.GetOrCreateCell(0, 0);
		first.Note = new StartPatternNote(source, mixdown: !viaInstrument);
		first.Effects.Add(new SampleOffsetPatternEffect(1));
		first.Effects.Add(new PitchSlidePatternEffect(96));
		parent.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new RetriggerPatternEffect(0x03));
		PreparedIncrementalPlaybackFactory factory = new(Mono());
		using PreparedIncrementalPlaybackPlan big = factory.Create(document, root);
		using PreparedIncrementalPlaybackPlan tiny = factory.Create(document, root);
		float[] expected = new float[205];
		float[] actual = new float[205];
		big.Source.Render(expected.Length, expected);
		int[] blockSizes = [1, 7, 3, 29, 11];
		for (int frame = 0, block = 0; frame < actual.Length; block++)
		{
			int count = Math.Min(blockSizes[block % blockSizes.Length],
				actual.Length - frame);
			tiny.Source.Render(count, actual.AsSpan(frame, count));
			frame += count;
		}
		Assert.That(actual, Is.EqualTo(expected).Within(1e-6f),
			"Parent pitch, child vibrato and Oxx/Qxy replay must compose " +
			"independently of worker PCM chunk size.");
		Assert.That(actual[30], Is.GreaterThan(0.01f));
		Assert.That(actual[181], Is.GreaterThan(0),
			"The row-one Q03 must restart the private child's sample.");
	}

	private static RenderConfiguration Mono()
		=> new(1000, [new OutputChannelConfiguration(Vector3.Zero,
			positionalImportance: 0)]);

	private static ObjectId AddPattern(SongDocument document,
		string name, int rows)
	{
		ObjectId id = document.AllocateObjectId();
		document.Add(new DataPatternDefinition(id, name)
			{ RowCount = rows, ChannelCount = 1 });
		return id;
	}

	private static ObjectId AddInstrument(SongDocument document, ObjectId source)
	{
		ObjectId id = document.AllocateObjectId();
		InstrumentDefinition instrument = new(id, "Recursive tone");
		instrument.ToneSpecifications.Add(new ToneSpecification { SourceId = source });
		instrument.ToneTable.Add(0);
		document.Add(instrument);
		return id;
	}

	private static ObjectId AddSample(SongDocument document,
		short? constant = null)
	{
		ObjectId id = document.AllocateObjectId();
		const int frames = 2000;
		using MemoryStream stream = new();
		using BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: true);
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
			writer.Write(constant ?? (short)(i * 10));
		writer.Flush();
		document.Add(SampleDefinition.CreateImported(id, "PCM",
			$"test-{id.Value}.wav", stream.ToArray()));
		return id;
	}
}
