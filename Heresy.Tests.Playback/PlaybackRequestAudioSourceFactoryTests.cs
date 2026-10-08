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
	public void DefaultFactoryRendersPredecodedPcmWithoutReadingAssetPath()
	{
		SongDocument document = new();
		ObjectId sampleId = document.AllocateObjectId();
		ExternalAssetReference missingAsset =
			new(
				Path.Combine(
					Path.GetTempPath(),
					$"missing-{Guid.NewGuid():N}.wav"),
				new string('0', 64));
		SampleDefinition sample =
			new(sampleId, "Memory", missingAsset);
		sample.ReloadPersistedEncoding(
			missingAsset,
			CreateWave(sample: 16384));
		document.Add(sample);
		ObjectId patternId =
			AddPatternWithNote(
				document,
				sampleId);
		ObjectId sequenceId =
			AddSequence(
				document,
				patternId);
		PlaybackRequestAudioSourceFactory factory =
			new(MonoConfiguration(8000));

		IAudioOutputSource source =
			factory.Create(
				SequencePlaybackRequest.Create(
					document,
					sequenceId));
		float[] output = new float[1];
		source.Render(1, output);

		output[0].Should().BeApproximately(0.5f, 1e-6f);
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
	public void FlattenedPatternRendersThroughMappedParentChannel()
	{
		SongDocument document = new();
		ObjectId sample = AddSample(document, "Sample");
		ObjectId childPattern = AddPatternWithNote(document, sample);
		ObjectId parentPattern = AddPatternWithNote(document, childPattern);
		ObjectId root = AddSequence(document, parentPattern);

		PlaybackRequestAudioSourceFactory factory = new(
			MonoConfiguration(100),
			new RecordingSampleProvider(
				new MemorySampleData(100, 1, new float[] { 0.5f })));

		IAudioOutputSource source = factory.Create(
			SequencePlaybackRequest.Create(document, root));
		float[] buffer = new float[1];
		source.Render(1, buffer);
		buffer[0].Should().BeApproximately(0.5f, 1e-6f);
	}

	[Test]
	public void FlattenedSequenceRendersThroughMappedParentChannel()
	{
		SongDocument document = new();
		ObjectId sample = AddSample(document, "Sample");
		ObjectId childPattern = AddPatternWithNote(document, sample);
		ObjectId childSequence = AddSequence(document, childPattern);
		ObjectId parent = AddPatternWithNote(document, childSequence);
		ObjectId root = AddSequence(document, parent);

		PlaybackRequestAudioSourceFactory factory = new(
			MonoConfiguration(100),
			new RecordingSampleProvider(
				new MemorySampleData(100, 1, new float[] { 0.375f })));

		IAudioOutputSource source = factory.Create(
			SequencePlaybackRequest.Create(document, root));
		float[] buffer = new float[1];
		source.Render(1, buffer);
		buffer[0].Should().BeApproximately(0.375f, 1e-6f);
	}

	[Test]
	public void FlattenedPatternMapsChildChannelsIntoParentPhysicalChannels()
	{
		SongDocument document = new();
		ObjectId sample = AddSample(document, "Sample");
		ObjectId childId = document.AllocateObjectId();
		DataPatternDefinition child = new(childId, "Child")
		{
			RowCount = 1,
			ChannelCount = 2,
		};
		for (int channel = 0; channel < 2; channel++)
		{
			PatternCell cell = child.Grid.GetOrCreateCell(0, channel);
			cell.SourceId = sample;
			cell.Note = new StartPatternNote();
		}
		document.Add(child);
		ObjectId parentId = document.AllocateObjectId();
		DataPatternDefinition parent = new(parentId, "Parent")
		{
			RowCount = 1,
			ChannelCount = 3,
		};
		PatternCell start = parent.Grid.GetOrCreateCell(0, 1);
		start.SourceId = childId;
		start.Note = new StartPatternNote();
		document.Add(parent);
		ObjectId root = AddSequence(document, parentId);
		PlaybackRequestAudioSourceFactory factory = new(
			MonoConfiguration(100),
			new RecordingSampleProvider(
				new MemorySampleData(100, 1, new float[] { 0.25f })));

		IAudioOutputSource source = factory.Create(
			SequencePlaybackRequest.Create(document, root));
		float[] buffer = new float[1];
		source.Render(1, buffer);
		buffer[0].Should().BeApproximately(0.5f, 1e-6f);
	}

	[Test]
	public void FlattenedRecursivePatternCycleFailsBeforeRendering()
	{
		SongDocument document = new();
		ObjectId id = document.AllocateObjectId();
		DataPatternDefinition self = new(id, "Recursive")
		{
			RowCount = 1,
			ChannelCount = 1,
		};
		PatternCell cell = self.Grid.GetOrCreateCell(0, 0);
		cell.SourceId = id;
		cell.Note = new StartPatternNote();
		document.Add(self);
		ObjectId root = AddSequence(document, id);
		PlaybackRequestAudioSourceFactory factory = new(
			MonoConfiguration(100),
			new RecordingSampleProvider(
				new MemorySampleData(100, 1, new float[] { 1.0f })));

		Action create = () =>
			factory.Create(SequencePlaybackRequest.Create(document, root));
		create.Should().Throw<InvalidOperationException>()
			.WithMessage("*cycle*");
	}

	[Test]
	public void NestedPatternMixdownRendersThroughPlaybackSnapshotResolver()
	{
		SongDocument document = new();
		ObjectId sampleId = AddSample(document, "Nested sample");
		ObjectId childPatternId = AddPatternWithNote(document, sampleId);
		ObjectId parentId = AddPatternWithNote(
			document, childPatternId, mixdown: true);
		ObjectId rootId = AddSequence(document, parentId);

		PlaybackRequestAudioSourceFactory factory = new(
			MonoConfiguration(100),
			new RecordingSampleProvider(
				new MemorySampleData(100, 1, new float[] { 0.625f })));
		IAudioOutputSource source =
			factory.Create(SequencePlaybackRequest.Create(document, rootId));
		float[] output = new float[1];
		source.Render(1, output);

		output[0].Should().BeApproximately(0.625f, 1e-6f);
	}

	[Test]
	public void NestedSequenceMixdownRendersThroughPlaybackSnapshotResolver()
	{
		SongDocument document = new();
		ObjectId sampleId = AddSample(document, "Nested sample");
		ObjectId childPatternId = AddPatternWithNote(document, sampleId);
		ObjectId childSequenceId = AddSequence(document, childPatternId);
		ObjectId parentId = AddPatternWithNote(
			document, childSequenceId, mixdown: true);
		ObjectId rootId = AddSequence(document, parentId);

		PlaybackRequestAudioSourceFactory factory = new(
			MonoConfiguration(100),
			new RecordingSampleProvider(
				new MemorySampleData(100, 1, new float[] { 0.375f })));
		IAudioOutputSource source =
			factory.Create(SequencePlaybackRequest.Create(document, rootId));
		float[] output = new float[1];
		source.Render(1, output);

		output[0].Should().BeApproximately(0.375f, 1e-6f);
	}

	[Test]
	public void NestedMixdownOffsetUsesNativeSourceFrames()
	{
		SongDocument document = new();
		ObjectId sampleId = AddSample(document, "Stepped sample");
		ObjectId patternId = AddPatternWithNote(document, sampleId);
		NoteScheduleBuilder events = new();
		events.Append(
			new NoteEvent(
				new Heresy.Core.Timing.MusicalTime(TimeSpan.Zero, 0.0),
				ChannelTarget.Physical(0),
				new NoteCommand[]
				{
					new StartNoteCommand(patternId, Mixdown: true),
					new SetSourceFrameOffsetCommand(2),
				}));
		PlaybackRequestAudioSourceFactory factory = new(
			MonoConfiguration(100),
			new RecordingSampleProvider(
				new MemorySampleData(
					100, 1, [0.1f, 0.2f, 0.3f, 0.4f])));

		IAudioOutputSource source =
			factory.Create(AdHocPlaybackRequest.Create(document, events.Freeze()));
		float[] output = new float[2];
		source.Render(2, output);

		output.Should().Equal(0.3f, 0.4f);
	}

	[Test]
	public void RecursiveNestedMixdownCyclesAreRejectedRatherThanReentered()
	{
		SongDocument document = new();
		ObjectId patternId = document.AllocateObjectId();
		DataPatternDefinition pattern = new(patternId, "Self recursive")
		{
			RowCount = 1,
			ChannelCount = 1,
		};
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.SourceId = patternId;
		cell.Note = new StartPatternNote(mixdown: true);
		document.Add(pattern);
		ObjectId sequenceId = AddSequence(document, patternId);
		PlaybackRequestAudioSourceFactory factory = new(
			MonoConfiguration(100),
			new RecordingSampleProvider(
				new MemorySampleData(100, 1, new float[] { 1.0f })));

		IAudioOutputSource source =
			factory.Create(SequencePlaybackRequest.Create(document, sequenceId));
		Action render = () => source.Render(1, new float[1]);

		render.Should().Throw<InvalidOperationException>()
			.WithMessage("*cycle*");
	}

	[Test]
	public void ParallelNestedMixdownVoicesDoNotShareTheirPlaybackState()
	{
		SongDocument document = new();
		ObjectId sampleId = AddSample(document, "Sample");
		ObjectId nestedId = AddPatternWithNote(document, sampleId);
		ObjectId outerId = document.AllocateObjectId();
		DataPatternDefinition outer = new(outerId, "Parallel")
		{
			RowCount = 1,
			ChannelCount = 2,
		};
		for (int channel = 0; channel < 2; channel++)
		{
			PatternCell cell = outer.Grid.GetOrCreateCell(0, channel);
			cell.SourceId = nestedId;
			cell.Note = new StartPatternNote(mixdown: true);
		}
		document.Add(outer);
		ObjectId rootId = AddSequence(document, outerId);
		PlaybackRequestAudioSourceFactory factory = new(
			MonoConfiguration(100),
			new RecordingSampleProvider(
				new MemorySampleData(100, 1, new float[] { 0.25f })));

		IAudioOutputSource source =
			factory.Create(SequencePlaybackRequest.Create(document, rootId));
		float[] output = new float[1];
		source.Render(1, output);
		output[0].Should().BeApproximately(0.5f, 1e-6f);
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
		ObjectId sourceId,
		bool mixdown = false)
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
		cell.Note = new StartPatternNote(mixdown: mixdown);
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
	private static byte[] CreateWave(short sample)
	{
		using MemoryStream stream = new();
		using (BinaryWriter writer = new(stream, System.Text.Encoding.ASCII, leaveOpen: true))
		{
			writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
			writer.Write(38);
			writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
			writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
			writer.Write(16);
			writer.Write((ushort)1);
			writer.Write((ushort)1);
			writer.Write(8000);
			writer.Write(16000);
			writer.Write((ushort)2);
			writer.Write((ushort)16);
			writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
			writer.Write(2);
			writer.Write(sample);
		}
		return stream.ToArray();
	}

}
