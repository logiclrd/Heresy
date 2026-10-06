using System;
using System.IO;
using System.Numerics;

using AwesomeAssertions;

using Heresy.Core.Assets;
using Heresy.Core.Envelopes;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Samples;
using Heresy.Core.Sequences;
using Heresy.Core.Sequencing;
using Heresy.Playback;
using Heresy.Render.Configuration;
using Heresy.Render.Realtime;
using Heresy.Render.Samples;
using Heresy.Render.Timing;
using Heresy.Scripting.Compilation;

using NUnit.Framework;

namespace Heresy.Tests.Playback;

[TestFixture]
public sealed class PlaybackRequestAudioSourceFactoryTests
{
	[Test]
	public void SequenceRequestRendersSampleFromSnapshot()
	{
		SongDocument document = new();
		ObjectId sampleId =
			AddSample(document, "Sample");
		ObjectId patternId =
			AddPatternWithNote(
				document,
				sampleId);
		ObjectId sequenceId =
			AddSequence(
				document,
				patternId);
		SampleDefinition original =
			(SampleDefinition)document.Objects[sampleId];

		RecordingSampleProvider samples =
			new(
				new MemorySampleData(
					100,
					1,
					new float[] { 1.0f }));
		PlaybackRequestAudioSourceFactory factory =
			new(
				MonoConfiguration(100),
				samples);

		IAudioOutputSource source =
			factory.Create(
				SequencePlaybackRequest.Create(
					document,
					sequenceId));
		float[] output = new float[1];
		source.Render(1, output);

		output[0].Should().BeApproximately(1.0f, 1e-6f);
		samples.LastDefinition.Should().NotBeSameAs(original);
		samples.LastDefinition!.Id.Should().Be(sampleId);
	}

	[Test]
	public void InstrumentAndAdsrEnvelopeResolveFromSnapshot()
	{
		SongDocument document = new();
		ObjectId sampleId =
			AddSample(document, "Sample");

		ObjectId envelopeId =
			document.AllocateObjectId();
		document.Add(
			new AdsrEnvelopeDefinition(
				envelopeId,
				"Quarter")
			{
				SustainLevel = 0.25,
			});

		ObjectId instrumentId =
			document.AllocateObjectId();
		InstrumentDefinition instrument =
			new(instrumentId, "Instrument");
		instrument.ToneSpecifications.Add(
			new ToneSpecification
			{
				SourceId = sampleId,
				VolumeEnvelopeId = envelopeId,
			});
		instrument.ToneTable.Add(0);
		document.Add(instrument);

		ObjectId patternId =
			AddPatternWithNote(
				document,
				instrumentId);
		ObjectId sequenceId =
			AddSequence(
				document,
				patternId);

		PlaybackRequestAudioSourceFactory factory =
			new(
				MonoConfiguration(100),
				new RecordingSampleProvider(
					new MemorySampleData(
						100,
						1,
						new float[] { 1.0f })));

		IAudioOutputSource source =
			factory.Create(
				SequencePlaybackRequest.Create(
					document,
					sequenceId));
		float[] output = new float[1];
		source.Render(1, output);

		output[0].Should().BeApproximately(0.25f, 1e-6f);
	}

	[Test]
	public void AdHocScheduleUsesSnapshotSoundResolver()
	{
		SongDocument document = new();
		ObjectId sampleId =
			AddSample(document, "Sample");
		NoteScheduleBuilder builder = new();
		builder.Append(
			new NoteEvent(
				new Heresy.Core.Timing.MusicalTime(
					TimeSpan.Zero,
					0.0),
				ChannelTarget.Physical(0),
				new NoteCommand[]
				{
					new StartNoteCommand(sampleId),
				}));
		NoteSchedule schedule =
			builder.Freeze();

		PlaybackRequestAudioSourceFactory factory =
			new(
				MonoConfiguration(100),
				new RecordingSampleProvider(
					new MemorySampleData(
						100,
						1,
						new float[] { 0.75f })));

		IAudioOutputSource source =
			factory.Create(
				AdHocPlaybackRequest.Create(
					document,
					schedule));
		float[] output = new float[1];
		source.Render(1, output);

		output[0].Should().BeApproximately(0.75f, 1e-6f);
	}

	[Test]
	public void RepeatingPatternRestartsAtBxxBoundary()
	{
		SongDocument document = new();
		ObjectId sampleId =
			AddSample(document, "Sample");
		ObjectId patternId =
			document.AllocateObjectId();
		DataPatternDefinition pattern =
			new(patternId, "Pattern")
			{
				RowCount = 8,
				ChannelCount = 1,
			};
		PatternCell first =
			pattern.Grid.GetOrCreateCell(0, 0);
		first.SourceId = sampleId;
		first.Note = new StartPatternNote();
		pattern.Grid.GetOrCreateCell(1, 0)
			.Effects.Add(
				new TrackerOrderJumpPatternEffect(0));
		document.Add(pattern);

		SongScheduleCompilationResult compilation =
			SongScheduleCompiler.CompilePattern(
				document,
				patternId);
		compilation.Success.Should().BeTrue();
		long cycleFrames =
			FrameTime.Ceiling(
				compilation.Duration,
				100);
		cycleFrames.Should().BeGreaterThan(1);

		PlaybackRequestAudioSourceFactory factory =
			new(
				MonoConfiguration(100),
				new RecordingSampleProvider(
					new MemorySampleData(
						100,
						1,
						new float[] { 1.0f })));
		IAudioOutputSource source =
			factory.Create(
				PatternPlaybackRequest.Create(
					document,
					patternId,
					repeat: true));
		float[] output =
			new float[checked((int)cycleFrames + 1)];

		source.Render(
			output.Length,
			output);

		output[0].Should().BeApproximately(1.0f, 1e-6f);
		output[checked((int)cycleFrames)]
			.Should().BeApproximately(1.0f, 1e-6f);
	}

	[Test]
	public void MissingSequenceProducesCompilationException()
	{
		PlaybackRequestAudioSourceFactory factory =
			new(
				MonoConfiguration(100),
				new RecordingSampleProvider(
					new MemorySampleData(
						100,
						1,
						new float[] { 1.0f })));
		SequencePlaybackRequest request =
			SequencePlaybackRequest.Create(
				new SongDocument(),
				(ObjectId)999U);

		Action act = () =>
			factory.Create(request);

		PlaybackSourceCompilationException exception =
			act.Should()
				.Throw<PlaybackSourceCompilationException>()
				.Which;
		exception.Diagnostics.Should().Contain(
			diagnostic =>
				diagnostic.Code == "HRS3001");
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

	private static ObjectId AddSample(
		SongDocument document,
		string name)
	{
		ObjectId id =
			document.AllocateObjectId();
		document.Add(
			new SampleDefinition(
				id,
				name,
				new ExternalAssetReference(
					Path.Combine(
						Path.GetTempPath(),
						$"{id.Value}.wav"))));
		return id;
	}

	private static ObjectId AddPatternWithNote(
		SongDocument document,
		ObjectId sourceId)
	{
		ObjectId id =
			document.AllocateObjectId();
		DataPatternDefinition pattern =
			new(id, "Pattern")
			{
				RowCount = 1,
				ChannelCount = 1,
			};
		PatternCell cell =
			pattern.Grid.GetOrCreateCell(0, 0);
		cell.SourceId = sourceId;
		cell.Note = new StartPatternNote();
		document.Add(pattern);
		return id;
	}

	private static ObjectId AddSequence(
		SongDocument document,
		ObjectId patternId)
	{
		ObjectId id =
			document.AllocateObjectId();
		DataSequenceDefinition sequence =
			new(id, "Sequence");
		sequence.Entries.Add(
			new SequenceEntry(patternId));
		document.Add(sequence);
		return id;
	}

	private sealed class RecordingSampleProvider
		: ISampleDataProvider
	{
		private readonly ISampleData _data;

		public RecordingSampleProvider(
			ISampleData data)
			=> _data = data;

		public SampleDefinition? LastDefinition { get; private set; }

		public ISampleData GetSampleData(
			SampleDefinition sample)
		{
			LastDefinition = sample;
			return _data;
		}
	}
}
