using System;
using System.IO;
using System.Linq;
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

using NUnit.Framework;

namespace Heresy.Tests.Playback;

[TestFixture]
public sealed class PlaybackRequestAudioSourceFactoryTests
{
	[Test]
	public void RealtimeFactoryCapturesFreshOutputConfigurationForEachNewRequest()
	{
		SongDocument document = new();
		ObjectId id = document.AllocateObjectId();
		document.Add(new DataPatternDefinition(id, "Empty")
		{
			RowCount = 1, ChannelCount = 1,
		});
		RenderConfiguration selection = MonoConfiguration(1000);
		PlaybackRequestAudioSourceFactory factory = new(() => selection);
		IAudioOutputSource first =
			factory.Create(PatternPlaybackRequest.Create(document, id));
		Assert.That(first.Format.SampleRate, Is.EqualTo(1000));
		Assert.That(first.Format.ChannelCount, Is.EqualTo(1));

		selection = RenderConfiguration.Stereo(48000);
		IAudioOutputSource second =
			factory.Create(PatternPlaybackRequest.Create(document, id));
		Assert.That(second.Format.SampleRate, Is.EqualTo(48000));
		Assert.That(second.Format.ChannelCount, Is.EqualTo(2));
		Assert.That(first.Format.SampleRate, Is.EqualTo(1000),
			"The factory cannot mutate already-prepared PCM source formats.");
		(first as IDisposable)?.Dispose();
		(second as IDisposable)?.Dispose();
	}

	[Test]
	public void ScriptedPatternWarningsReachRequestScopedPlaybackDiagnosticReport()
	{
		SongDocument document = new();
		ObjectId id = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(id, "Malformed")
		{
			RowCount = 5,
			ChannelCount = 1,
			Source = "Cut(4, 0); Off(2, 0); Cut(4, 0);",
		});

		PlaybackRequest request = PatternPlaybackRequest.Create(
			document, id, startRow: 0, repeat: false);
		PlaybackRequestAudioSourceFactory factory =
			new(MonoConfiguration(100));
		IAudioOutputSource source = factory.Create(request);
		// Diagnostics now arise as the coroutine runs on its rendering worker,
		// rather than from an eager Create()-time full-song compilation.
		source.Render(512, new float[512]);
		IPlaybackRuntimeDiagnosticReportProvider diagnostics = factory;

		diagnostics.TryTakeRuntimeDiagnostics(request, out var warnings)
			.Should().BeTrue();
		warnings.Should().ContainSingle();
		warnings[0].Code.Should().Be("HRSEQ001");
		warnings[0].Row.Should().Be(2);
		warnings[0].LastAcceptedRow.Should().Be(4);
		diagnostics.TryTakeRuntimeDiagnostics(request, out var again)
			.Should().BeFalse();
		again.Should().BeEmpty();
	}

	[Test]
	public void MainPlaybackPositionFollowsLivePatternRowsWithoutGreedySchedule()
	{
		SongDocument document = new();
		ObjectId id = document.AllocateObjectId();
		document.Add(new DataPatternDefinition(id, "Rows")
		{
			RowCount = 3, ChannelCount = 1,
		});
		PlaybackRequest request =
			PatternPlaybackRequest.Create(document, id);
		PlaybackRequestAudioSourceFactory factory = new(MonoConfiguration(100));
		IAudioOutputSource source = factory.Create(request);
		factory.TryTakePlaybackPositionTimeline(request,
			out PlaybackPositionTimeline? positions).Should().BeTrue();
		positions.Should().NotBeNull();
		positions!.GetPositionAt(TimeSpan.Zero).Should().BeNull();
		float[] first = new float[1];
		source.Render(1, first);
		positions.GetPositionAt(TimeSpan.Zero)?.PatternId.Should().Be(id);
		positions.GetPositionAt(TimeSpan.Zero)?.PatternRow.Should().Be(0);
		source.Render(13, new float[13]);
		positions.GetPositionAt(TimeSpan.FromMilliseconds(120))
			?.PatternRow.Should().Be(1);
		positions.IsComplete(TimeSpan.FromMilliseconds(120)).Should().BeFalse();
	}

	[TestCase(false, false)]
	[TestCase(false, true)]
	[TestCase(true, false)]
	[TestCase(true, true)]
	public void FlattenedSoundSourceRowsDoNotDisplaceRequestedPlaybackHighlight(
		bool playSequence, bool nestedSequence)
	{
		// Pattern B plays Pattern A as a note (possibly via a nested
		// Sequence). The editor follows B, not A's active internal rows.
		SongDocument document = new();
		ObjectId sample = AddSample(document, "Leaf");
		ObjectId child = AddPatternWithNote(document, sample);
		ObjectId sourceId = nestedSequence
			? AddSequence(document, child) : child;
		ObjectId parentId = document.AllocateObjectId();
		DataPatternDefinition parent = new(parentId, "Containing Pattern")
		{
			RowCount = 3,
			ChannelCount = 1,
		};
		PatternCell call = parent.Grid.GetOrCreateCell(0, 0);
		call.SourceId = sourceId;
		call.Note = new StartPatternNote();
		document.Add(parent);
		ObjectId rootSequence = AddSequence(document, parentId);
		PlaybackRequest request = playSequence
			? SequencePlaybackRequest.Create(document, rootSequence)
			: PatternPlaybackRequest.Create(document, parentId);
		PlaybackRequestAudioSourceFactory factory = new(
			MonoConfiguration(100),
			new RecordingSampleProvider(
				new MemorySampleData(100, 1, [0.75f])));
		IAudioOutputSource output = factory.Create(request);
		factory.TryTakePlaybackPositionTimeline(request,
			out PlaybackPositionTimeline? timeline).Should().BeTrue();
		timeline.Should().NotBeNull();

		float[] first = new float[1];
		output.Render(1, first);
		first[0].Should().BeApproximately(0.75f, 1e-6f,
			"The nested sound must still play.");
		PlaybackPatternPosition initial = timeline!.GetPositionAt(TimeSpan.Zero)!;
		initial.PatternId.Should().Be(parentId);
		initial.PatternRow.Should().Be(0);
		initial.SequenceId.Should().Be(playSequence ? rootSequence : null);
		initial.SequenceEntryIndex.Should().Be(playSequence ? 0 : null);

		// A second parent row must be followed normally even though the
		// child still belongs to the recursive playback tree.
		output.Render(14, new float[14]);
		timeline.GetPositionAt(TimeSpan.FromMilliseconds(120))!
			.Should().Be(new PlaybackPatternPosition(parentId, 1,
				playSequence ? rootSequence : null, playSequence ? 0 : null));
		timeline.Entries.Should().OnlyContain(entry =>
			entry.Position.PatternId == parentId,
			"Flattened source rows cannot steal the editor's highlight.");
		(output as IDisposable)?.Dispose();
	}

	[TestCase(false, false)]
	[TestCase(false, true)]
	[TestCase(true, false)]
	[TestCase(true, true)]
	public void LiveNoteEntryAndHeldPreviewRenderPatternAndSequenceSources(
		bool nestedSequence, bool heldPreview)
	{
		// A non-Mixdown nested source is valid Pattern data. Live entry
		// bypasses the ordinary recursive row generator, but must still
		// instantiate the child without editing the Mixdown flag.
		SongDocument document = new();
		ObjectId sample = AddSample(document, "Live child sample");
		ObjectId childId = document.AllocateObjectId();
		DataPatternDefinition child = new(childId, "Child")
		{
			RowCount = 2,
			ChannelCount = 1,
		};
		for (int row = 0; row < 2; row++)
		{
			PatternCell cell = child.Grid.GetOrCreateCell(row, 0);
			cell.SourceId = sample;
			cell.Note = new StartPatternNote();
		}
		document.Add(child);
		ObjectId sourceId = nestedSequence
			? AddSequence(document, childId) : childId;
		ObjectId parentId = AddPatternWithNote(document, sourceId);
		DataPatternDefinition parent =
			(DataPatternDefinition)document.Objects[parentId];

		LivePlaybackEvent audition = heldPreview
			? PatternLiveEventCompiler.CompileHeldPreviewStart(
				parent, row: 0, channel: 0,
				virtualChannelId: 7, pitchMultiplier: 1)!
			: PatternLiveEventCompiler.CompileEnteredNote(
				parent, row: 0, channel: 0)!;
		audition.Commands.OfType<StartNoteCommand>()
			.Should().ContainSingle()
			.Which.Mixdown.Should().BeFalse();
		((StartPatternNote)parent.Grid[0, 0]!.Note!).Mixdown
			.Should().BeFalse("preview cannot rewrite the saved note");

		PlaybackRequest request = AdHocPlaybackRequest.Create(
			document, new NoteScheduleBuilder().Freeze());
		PlaybackRequestAudioSourceFactory factory = new(
			MonoConfiguration(100),
			new RecordingSampleProvider(
				new MemorySampleData(100, 1, [0.625f])));
		IAudioOutputSource output = factory.Create(request);
		ILiveAudioOutputSource live = (ILiveAudioOutputSource)output;
		live.EnqueueLiveEvent(audition.Target, audition.Commands);

		float[] first = new float[1];
		output.Render(1, first);
		first[0].Should().BeApproximately(0.625f, 1e-6f,
			"the child Pattern's first note must sound on live entry");

		// Row 1 starts after 120 ms at the default tracker tempo.
		float[] between = new float[11];
		output.Render(between.Length, between);
		between.Should().OnlyContain(value => value == 0,
			"the second child note must wait for its own musical row");
		float[] second = new float[1];
		output.Render(1, second);
		second[0].Should().BeApproximately(0.625f, 1e-6f,
			"the live preview must run the entire child timeline");
		(output as IDisposable)?.Dispose();
	}

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
	public void RealtimeInstrumentToneCanStartRecursivePatternThroughProductionFactory()
	{
		SongDocument document = new();
		ObjectId sample = AddSample(document, "Sample");
		ObjectId nested = AddPatternWithNote(document, sample);
		ObjectId instrumentId = document.AllocateObjectId();
		InstrumentDefinition instrument = new(instrumentId, "Recursive tone");
		instrument.ToneSpecifications.Add(new ToneSpecification
		{
			SourceId = nested,
		});
		instrument.ToneTable.Add(0);
		document.Add(instrument);
		ObjectId parent = AddPatternWithNote(document, instrumentId);
		ObjectId sequence = AddSequence(document, parent);
		PlaybackRequestAudioSourceFactory factory = new(
			MonoConfiguration(100),
			new RecordingSampleProvider(
				new MemorySampleData(100, 1, [0.625f])));
		IAudioOutputSource source = factory.Create(
			SequencePlaybackRequest.Create(document, sequence));
		float[] block = new float[1];
		source.Render(1, block);
		block[0].Should().BeApproximately(0.625f, 1e-6f);
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

	[TestCase(false)]
	[TestCase(true)]
	public void RepeatingPatternBeginsNextCycleWhileFlattenedChildIsStillActive(
		bool nestedSequence)
	{
		SongDocument document = new();
		ObjectId sampleId = AddSample(document, "Child sound");
		ObjectId childId = document.AllocateObjectId();
		DataPatternDefinition child = new(childId, "Longer child")
		{
			RowCount = 4, ChannelCount = 1,
		};
		// This note begins 240 ms after the first invocation, well
		// beyond the parent's 120 ms repeat boundary.
		PatternCell delayed = child.Grid.GetOrCreateCell(2, 0);
		delayed.SourceId = sampleId;
		delayed.Note = new StartPatternNote();
		document.Add(child);
		ObjectId nested = nestedSequence
			? AddSequence(document, childId) : childId;
		ObjectId parentId = document.AllocateObjectId();
		DataPatternDefinition parent = new(parentId, "Repeated host")
		{
			RowCount = 1, ChannelCount = 1,
		};
		PatternCell start = parent.Grid.GetOrCreateCell(0, 0);
		start.SourceId = nested;
		start.Note = new StartPatternNote();
		start.Effects.Add(new TrackerNewNoteActionPatternEffect(
			NoteDisplacementAction.Continue));
		document.Add(parent);

		PatternPlaybackRequest request =
			PatternPlaybackRequest.Create(document, parentId, repeat: true);
		PlaybackRequestAudioSourceFactory factory = new(
			MonoConfiguration(100),
			new RecordingSampleProvider(
				new MemorySampleData(100, 1, [0.75f])));
		IAudioOutputSource output = factory.Create(request);
		factory.TryTakePlaybackPositionTimeline(request,
			out PlaybackPositionTimeline? position).Should().BeTrue();
		position.Should().NotBeNull();

		float[] rendered = new float[25];
		output.Render(rendered.Length, rendered);
		position!.Entries.Where(e => e.Position.PatternId == parentId)
			.Select(e => e.Offset)
			.Take(3)
			.Should().Equal(TimeSpan.Zero,
				TimeSpan.FromMilliseconds(120),
				TimeSpan.FromMilliseconds(240));
		rendered[24].Should().BeApproximately(0.75f, 1e-6f,
			"the original child must still be able to produce its late note " +
			"after the parent has repeated, with NNA Continue");
		(output as IDisposable)?.Dispose();
	}

	[Test]
	public void RepeatingPatternKeepsSampleTailAcrossAnEmptyLoopBoundary()
	{
		SongDocument document = new();
		ObjectId sample = AddSample(document, "Long sample");
		ObjectId id = document.AllocateObjectId();
		DataPatternDefinition pattern = new(id, "Last-row note")
		{
			RowCount = 2, ChannelCount = 1,
		};
		PatternCell last = pattern.Grid.GetOrCreateCell(1, 0);
		last.SourceId = sample;
		last.Note = new StartPatternNote();
		document.Add(pattern);
		PlaybackRequestAudioSourceFactory factory = new(
			MonoConfiguration(100),
			new RecordingSampleProvider(new MemorySampleData(
				100, 1, Enumerable.Repeat(0.5f, 60).ToArray())));
		PatternPlaybackRequest request =
			PatternPlaybackRequest.Create(document, id, repeat: true);
		IAudioOutputSource output = factory.Create(request);
		factory.TryTakePlaybackPositionTimeline(request,
			out PlaybackPositionTimeline? positions).Should().BeTrue();

		float[] rendered = new float[26];
		output.Render(rendered.Length, rendered);
		rendered[12].Should().BeApproximately(0.5f, 1e-6f);
		rendered[24].Should().BeApproximately(0.5f, 1e-6f,
			"looping an empty row must not cut the final row's sample tail");
		positions!.Entries.Where(e => e.Position.PatternId == id)
			.Select(e => (e.Offset, e.Position.PatternRow))
			.Take(3).Should().Equal(
				(TimeSpan.Zero, 0),
				(TimeSpan.FromMilliseconds(120), 1),
				(TimeSpan.FromMilliseconds(240), 0));
		(output as IDisposable)?.Dispose();
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

		// Bxx at row 1 terminates the two 120-ms tracker rows.
		long cycleFrames = FrameTime.Ceiling(
			TimeSpan.FromMilliseconds(240), 100);
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
	public void FlattenedChildTimingIsRelativeToItsParentStart()
	{
		SongDocument document = new();
		ObjectId sampleId = AddSample(document, "Offset sample");
		ObjectId childId = document.AllocateObjectId();
		DataPatternDefinition child = new(childId, "Delayed child")
		{
			RowCount = 2,
			ChannelCount = 1,
		};
		PatternCell note = child.Grid.GetOrCreateCell(1, 0);
		note.SourceId = sampleId;
		note.Note = new StartPatternNote();
		document.Add(child);
		ObjectId parent = AddPatternWithNote(document, childId);
		ObjectId root = AddSequence(document, parent);
		int noteFrame = checked((int)FrameTime.Ceiling(
			TimeSpan.FromMilliseconds(120), 100));
		noteFrame.Should().BeGreaterThan(0);
		PlaybackRequestAudioSourceFactory factory = new(
			MonoConfiguration(100),
			new RecordingSampleProvider(
				new MemorySampleData(100, 1, new float[] { 0.75f })));

		IAudioOutputSource source = factory.Create(
			SequencePlaybackRequest.Create(document, root));
		float[] prefix = new float[noteFrame];
		source.Render(prefix.Length, prefix);
		prefix.Should().OnlyContain(value => value == 0.0f);
		float[] result = new float[1];
		source.Render(1, result);
		result[0].Should().BeApproximately(0.75f, 1e-6f);
	}

	[Test]
	public void FlattenedNestedSourceInitialPitchTransposesRealtimeSampleFrames()
	{
		SongDocument document = new();
		ObjectId sample = AddSample(document, "Sample");
		ObjectId childId = AddPatternWithNote(document, sample);
		ObjectId parentId = document.AllocateObjectId();
		DataPatternDefinition parent = new(parentId, "Transposed")
		{
			RowCount = 1,
			ChannelCount = 1,
		};
		PatternCell note = parent.Grid.GetOrCreateCell(0, 0);
		note.SourceId = childId;
		note.Note = new StartPatternNote(pitchMultiplier: 2.0);
		document.Add(parent);
		ObjectId root = AddSequence(document, parentId);
		PlaybackRequestAudioSourceFactory factory = new(
			MonoConfiguration(100),
			new RecordingSampleProvider(
				new MemorySampleData(100, 1,
					new float[] { 0.125f, 0.25f, 0.5f, 0.75f, 1.0f })));

		IAudioOutputSource source = factory.Create(
			SequencePlaybackRequest.Create(document, root));
		float[] output = new float[2];
		source.Render(output.Length, output);
		output[0].Should().BeApproximately(0.125f, 1e-6f);
		output[1].Should().BeApproximately(0.5f, 1e-6f,
			"The flattened child must read source frame two at output frame one.");
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

		IAudioOutputSource source = factory.Create(
			SequencePlaybackRequest.Create(document, root));
		Action render = () => source.Render(1, new float[1]);
		render.Should().Throw<InvalidOperationException>()
			.WithMessage("*cycle*");
	}

	[Test]
	public void LiveAuditionMixdownUsesRecursiveGeneratorRatherThanCompiledResolver()
	{
		SongDocument document = new();
		ObjectId sample = AddSample(document, "Preview");
		ObjectId pattern = AddPatternWithNote(document, sample);
		PlaybackRequestAudioSourceFactory factory = new(
			MonoConfiguration(100),
			new RecordingSampleProvider(new MemorySampleData(
				100, 1, [0.625f])));
		NoteSchedule schedule = new NoteScheduleBuilder().Freeze();
		IAudioOutputSource source = factory.Create(
			AdHocPlaybackRequest.Create(document, schedule));
		ILiveAudioOutputSource live = (ILiveAudioOutputSource)source;
		live.EnqueueLiveEvent(ChannelTarget.Physical(0),
			[new StartNoteCommand(pattern, Mixdown: true)]);
		float[] output = new float[1];
		source.Render(1, output);
		output[0].Should().BeApproximately(0.625f, 1e-6f);
	}

	[Test]
	public void RealtimeSequenceCanStartAtSelectedOrderRowWithoutCompilingEarlierRows()
	{
		SongDocument document = new();
		ObjectId sample = AddSample(document, "Started late");
		ObjectId patternId = document.AllocateObjectId();
		DataPatternDefinition pattern = new(patternId, "Rows")
		{
			RowCount = 4, ChannelCount = 1,
		};
		PatternCell cell = pattern.Grid.GetOrCreateCell(2, 0);
		cell.SourceId = sample;
		cell.Note = new StartPatternNote();
		document.Add(pattern);
		ObjectId sequence = AddSequence(document, patternId);
		PlaybackRequestAudioSourceFactory factory = new(
			MonoConfiguration(100),
			new RecordingSampleProvider(new MemorySampleData(100, 1, [0.75f])));
		IAudioOutputSource source = factory.Create(
			SequencePlaybackRequest.Create(document, sequence,
				new SequencePlaybackPosition(0, 2)));
		float[] output = new float[1];
		source.Render(1, output);
		output[0].Should().BeApproximately(0.75f, 1e-6f);
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
