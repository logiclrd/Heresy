using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;

using Heresy.Core.Sequencing;
using Heresy.Render.Playback;
using System.Text;

using Heresy.Core.Objects;
using Heresy.Core.Instruments;
using Heresy.Core.Envelopes;
using Heresy.Core.Patterns;
using Heresy.Core.Samples;
using Heresy.Core.Sequences;
using Heresy.Playback;
using Heresy.Render.Configuration;
using Heresy.Render.Samples;
using Heresy.Render.Sounds;

using NUnit.Framework;

namespace Heresy.Tests.Playback;

[TestFixture]
public sealed class PreparedIncrementalPlaybackFactoryTests
{
	[TestCase(false)]
	[TestCase(true)]
	public void SameEventFlattenedStartThenCutOrOffDoesNotRetainControllerOrStartChildPcm(
		bool release)
	{
		SongDocument document = new();
		ObjectId sampleId = document.AllocateObjectId();
		document.Add(SampleDefinition.CreateImported(
			sampleId, "Sustain", "sustain.wav", LongWave(16384)));
		ObjectId childId = document.AllocateObjectId();
		DataPatternDefinition child = new(childId, "Deferred child")
		{
			RowCount = 3, ChannelCount = 2,
		};
		child.Grid.GetOrCreateCell(0, 1).Note =
			new StartPatternNote(sampleId);
		child.Grid.GetOrCreateCell(1, 1).Note =
			new StartPatternNote(sampleId);
		document.Add(child);

		// An incremental raw event may carry multiple ordered commands
		// at one timestamp. The same-frame Cut stops generation before
		// the child gets an opportunity to emit its first note.
		NoteScheduleBuilder builder = new();
		builder.Append(new NoteEvent(
			Heresy.Core.Timing.MusicalTime.Zero,
			ChannelTarget.Physical(0),
			[new StartNoteCommand(childId),
				release ? new NoteOffCommand() : new NoteCutCommand()]));
		using PreparedIncrementalPlaybackPlan plan =
			new PreparedIncrementalPlaybackFactory(Mono(1000))
				.CreateAdHoc(SongDocumentSnapshot.Create(document), builder.Freeze());

		float[] pcm = new float[250];
		plan.Source.Render(pcm.Length, pcm);
		Assert.That(pcm, Is.All.Zero.Within(1e-6f),
			"Cut in the start's own event must cancel the producer before child PCM begins.");
		Assert.That(plan.SequencingContext.ScopedMemory.ActiveScopeCount, Is.Zero);
		Assert.That(plan.Session.RetainedFlattenedSourceControllerCount, Is.Zero,
			"Scope retirement can precede playback of the enclosing event; "
			+ "the retired controller must not be registered afterward.");
	}

	[TestCase(false, 0.5f)]
	[TestCase(true, 1.0f)]
	public void SameFrameNnaContinuePreservesChildStartWithoutDuplicatingHostVoice(
		bool continueOld, float expectedAtFrameZero)
	{
		SongDocument document = new();
		ObjectId sampleId = document.AllocateObjectId();
		document.Add(SampleDefinition.CreateImported(sampleId,
			"Sustain", "sustain.wav", LongWave(16384)));
		ObjectId childId = document.AllocateObjectId();
		DataPatternDefinition child = new(childId, "Child")
		{
			RowCount = 2, ChannelCount = 2,
		};
		child.Grid.GetOrCreateCell(0, 1).Note = new StartPatternNote(sampleId);
		document.Add(child);

		// A single raw event starts the source, optionally installs S74
		// Continue, and immediately replaces it with a direct sample.
		// Both child and replacement map to the same shared PCM frame,
		// but only Continue permits the child producer to survive.
		List<NoteCommand> commands = [new StartNoteCommand(childId)];
		if (continueOld)
			commands.Add(new SetCurrentVoiceDisplacementActionCommand(
				NoteDisplacementAction.Continue));
		commands.Add(new StartNoteCommand(sampleId));
		NoteScheduleBuilder builder = new();
		builder.Append(new NoteEvent(
			Heresy.Core.Timing.MusicalTime.Zero,
			ChannelTarget.Physical(0), commands));
		using PreparedIncrementalPlaybackPlan plan =
			new PreparedIncrementalPlaybackFactory(Mono(1000))
				.CreateAdHoc(SongDocumentSnapshot.Create(document), builder.Freeze());

		float[] frame0 = new float[1];
		plan.Source.Render(1, frame0);
		Assert.That(frame0[0], Is.EqualTo(expectedAtFrameZero).Within(1e-6f),
			"At the same frame, Continue preserves the child's first voice "
			+ "while Cut removes its producer before child generation.");
		float[] next = new float[150];
		plan.Source.Render(next.Length, next);
		Assert.That(next[0], Is.EqualTo(expectedAtFrameZero).Within(1e-6f));
		plan.Source.Render(400, new float[400]);
		Assert.That(plan.Session.RetainedFlattenedSourceControllerCount, Is.Zero);
	}

	[Test]
	public void CancelingSequenceDoesNotSuppressOtherRootPendingCursorEvent()
	{
		SongDocument document = new();
		ObjectId sampleId = document.AllocateObjectId();
		document.Add(SampleDefinition.CreateImported(sampleId,
			"Independent", "independent.wav", LongWave(16384)));

		ObjectId silentId = document.AllocateObjectId();
		document.Add(new DataPatternDefinition(silentId, "Long silent order")
		{
			RowCount = 4, ChannelCount = 1,
		});
		ObjectId sequenceId = document.AllocateObjectId();
		DataSequenceDefinition sequence = new(sequenceId, "Cancelled sequence");
		sequence.Entries.Add(new SequenceEntry(silentId));
		document.Add(sequence);

		ObjectId otherId = document.AllocateObjectId();
		DataPatternDefinition other = new(otherId, "Independent root")
		{
			RowCount = 3, ChannelCount = 1,
		};
		other.Grid.GetOrCreateCell(1, 0).Note = new StartPatternNote(sampleId);
		document.Add(other);

		using PreparedIncrementalPlaybackPlan plan =
			new PreparedIncrementalPlaybackFactory(Mono(1000))
				.Create(document, sequenceId);
		plan.Timeline.AddRoot(otherId);

		// Precisely exercise the existing one-event lookahead: the
		// independent root's row-1 event is ready for frame 120, but
		// the PCM render head is still on frame zero. Sequence order
		// frames and Pattern cursors have different identity spaces.
		MethodInfo prime = typeof(PreparedIncrementalAudioSource).GetMethod(
			"FindNextEvent", BindingFlags.Instance | BindingFlags.NonPublic)!;
		prime.Invoke(plan.Source, [1000L]);
		FieldInfo pending = typeof(PreparedIncrementalAudioSource).GetField(
			"_pendingEvent", BindingFlags.Instance | BindingFlags.NonPublic)!;
		Assert.That(pending.GetValue(plan.Source), Is.Not.Null);
		Assert.That(plan.Source.NextFrame, Is.Zero);
		Assert.That(plan.Source.Cancel(plan.RootInvocationId), Is.True);

		float[] pcm = new float[135];
		plan.Source.Render(pcm.Length, pcm);
		Assert.That(pcm[119], Is.Zero.Within(1e-6f));
		Assert.That(pcm[120], Is.EqualTo(0.5f).Within(1e-6f),
			"Cancelling a Sequence must not cancel an independent cursor "
			+ "whose numeric ID matches a removed recursive frame.");
		Assert.That(pcm[134], Is.EqualTo(0.5f).Within(1e-6f));
	}

	[Test]
	public void CanceledPrefetchedEventCannotBlockNewIndependentStartAtSameFrame()
	{
		SongDocument document = new();
		ObjectId canceledSample = document.AllocateObjectId();
		document.Add(SampleDefinition.CreateImported(canceledSample,
			"Canceled", "canceled.wav", LongWave(8192)));
		ObjectId survivingSample = document.AllocateObjectId();
		document.Add(SampleDefinition.CreateImported(survivingSample,
			"Survives", "survives.wav", LongWave(16384)));
		ObjectId orderId = document.AllocateObjectId();
		DataPatternDefinition order = new(orderId, "Order")
		{
			RowCount = 4, ChannelCount = 1,
		};
		order.Grid.GetOrCreateCell(1, 0).Note =
			new StartPatternNote(canceledSample);
		document.Add(order);
		ObjectId sequenceId = document.AllocateObjectId();
		DataSequenceDefinition sequence = new(sequenceId, "Sequence");
		sequence.Entries.Add(new SequenceEntry(orderId));
		document.Add(sequence);
		ObjectId otherId = document.AllocateObjectId();
		DataPatternDefinition other = new(otherId, "New root")
		{
			RowCount = 2, ChannelCount = 1,
		};
		other.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(survivingSample);
		document.Add(other);

		using PreparedIncrementalPlaybackPlan plan =
			new PreparedIncrementalPlaybackFactory(Mono(1000))
				.Create(document, sequenceId);
		MethodInfo prime = typeof(PreparedIncrementalAudioSource).GetMethod(
			"FindNextEvent", BindingFlags.Instance | BindingFlags.NonPublic)!;
		prime.Invoke(plan.Source, [1000L]);
		Assert.That(plan.Source.NextFrame, Is.Zero);
		Assert.That(plan.Timeline.Elapsed,
			Is.EqualTo(TimeSpan.FromMilliseconds(120)));

		// The Sequence's raw event has already been prefetched, but its
		// PCM remains unrendered. Start a separate root on the shared
		// tick boundary, then cancel the Sequence before frame 120.
		// The canceled old event must disappear; the new root starts at
		// that exact output frame, without inheriting the old owner ID.
		plan.Timeline.AddRoot(otherId);
		Assert.That(plan.Source.Cancel(plan.RootInvocationId), Is.True);
		float[] pcm = new float[140];
		plan.Source.Render(pcm.Length, pcm);
		Assert.That(pcm[119], Is.Zero.Within(1e-6f));
		Assert.That(pcm[120], Is.EqualTo(0.5f).Within(1e-6f),
			"The canceled prefetched event must not block or replace the "
			+ "independent producer starting on its same PCM frame.");
		Assert.That(pcm[139], Is.EqualTo(0.5f).Within(1e-6f));
	}

	[Test]
	public void HundredsOfFlatInvocationsReclaimRendererMemoryAfterTheirVoicesEnd()
	{
		SongDocument document = new();
		ObjectId sample = document.AllocateObjectId();
		document.Add(SampleDefinition.CreateImported(sample, "Short", "short.wav",
			Wave(16384)));
		ObjectId leaf = document.AllocateObjectId();
		DataPatternDefinition child = new(leaf, "Short note")
		{
			RowCount = 1, ChannelCount = 2,
		};
		child.Grid.GetOrCreateCell(0, 1).Note = new StartPatternNote(sample);
		document.Add(child);
		ObjectId riff = document.AllocateObjectId();
		DataPatternDefinition parent = new(riff, "Repeat")
		{
			RowCount = 1, ChannelCount = 2,
		};
		parent.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote(leaf);
		parent.Grid.GetOrCreateCell(0, 1).Effects.Add(
			new TrackerOrderJumpPatternEffect(0));
		document.Add(parent);
		ObjectId root = document.AllocateObjectId();
		DataSequenceDefinition song = new(root, "Loop");
		song.Entries.Add(new SequenceEntry(riff));
		document.Add(song);
		int jumps = 0;
		using PreparedIncrementalPlaybackPlan plan =
			new PreparedIncrementalPlaybackFactory(Mono(1000))
				.Create(SongDocumentSnapshot.Create(document), root,
					shouldFollowOrderJump: _ => ++jumps < 256);
		float[] block = new float[120];
		int peakRenderingChannels = 0;
		int peakProducerScopes = 0;
		for (int i = 0; i < 256; i++)
		{
			plan.Source.Render(120, block);
			Assert.That(block[0], Is.EqualTo(0.5f).Within(1e-6f));
			peakRenderingChannels = Math.Max(peakRenderingChannels,
				plan.Session.RetainedScopedPhysicalChannelCount);
			peakProducerScopes = Math.Max(peakProducerScopes,
				plan.SequencingContext.ScopedMemory.ActiveScopeCount);
		}
		plan.Source.Render(240, new float[240]);
		Assert.That(jumps, Is.EqualTo(256));
		Assert.That(peakProducerScopes, Is.LessThanOrEqualTo(2));
		Assert.That(peakRenderingChannels, Is.LessThanOrEqualTo(3));
		Assert.That(plan.SequencingContext.ScopedMemory.ActiveScopeCount, Is.Zero);
		Assert.That(plan.SequencingContext.ScopedMemory.MaterializedScopeCount, Is.Zero);
		Assert.That(plan.Session.RetainedScopedPhysicalChannelCount, Is.Zero,
			"Finished and silent playback hosts must not accumulate indefinitely.");
		Assert.That(plan.Session.RetainedFlattenedSourceControllerCount, Is.Zero,
			"Finished invocations must also release live volume-controller registrations.");
	}

	[Test]
	public void PreparedScriptSequenceAndNestedDataAndScriptPatternsRenderDecodedPcm()
	{
		SongDocument document = new();
		ObjectId sampleId = document.AllocateObjectId();
		SampleDefinition sample = SampleDefinition.CreateImported(
			sampleId, "PCM", "memory.wav", Wave(16384));
		document.Add(sample);

		ObjectId scriptPatternId = document.AllocateObjectId();
		ScriptPatternDefinition scripted = new(scriptPatternId, "Scripted")
		{
			RowCount = 1,
			ChannelCount = 1,
			Source = $"Note(0, 0, _O({sampleId.Value}));",
		};
		document.Add(scripted);

		ObjectId dataPatternId = document.AllocateObjectId();
		DataPatternDefinition parent = new(dataPatternId, "Parent")
		{
			RowCount = 1,
			ChannelCount = 1,
		};
		PatternCell cell = parent.Grid.GetOrCreateCell(0, 0);
		cell.SourceId = scriptPatternId;
		cell.Note = new StartPatternNote();
		document.Add(parent);

		ObjectId sequenceId = document.AllocateObjectId();
		document.Add(new ScriptSequenceDefinition(sequenceId, "Script order")
		{
			Source = $"return sequenceIndex == 0 ? Play(_O({dataPatternId.Value})) : null;",
		});
		document.RootSequenceId = sequenceId;

		PreparedIncrementalPlaybackFactory factory = new(Mono(1000));
		using PreparedIncrementalPlaybackPlan plan = factory.Create(document);
		Assert.That(plan.Snapshot.Document, Is.Not.SameAs(document));
		SampleDefinition snapshotSample =
			(SampleDefinition)plan.Snapshot.Document.Objects[sampleId];
		Assert.That(snapshotSample.PcmData, Is.SameAs(sample.PcmData));

		// Authoring changes must not rewrite an already prepared program.
		scripted.Source = "Cut(0, 0);";
		((ScriptSequenceDefinition)document.Objects[sequenceId]).Source = "return null;";
		((ScriptSequenceDefinition)plan.Snapshot.Document.Objects[sequenceId]).Source = "return null;";
		((ScriptPatternDefinition)plan.Snapshot.Document.Objects[scriptPatternId]).Source = "Cut(0, 0);";
		float[] output = new float[2];
		plan.Source.Render(2, output);
		Assert.That(output, Is.All.EqualTo(0.5f).Within(1e-6f));
	}

	[Test]
	public void ResolvesSampleDataBeforeTheAudioCallback()
	{
		SongDocument document = new();
		ObjectId sampleId = document.AllocateObjectId();
		// This definition has no decoded PCM: the injected provider supplies it.
		document.Add(SampleDefinition.CreateImported(
			sampleId, "PCM", "memory.wav", Wave(8192)));
		ObjectId patternId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(patternId, "Root")
		{
			RowCount = 1,
			ChannelCount = 1,
			Source = $"Note(0, 0, _O({sampleId.Value}));",
		});
		CountingProvider provider = new();
		PreparedIncrementalPlaybackFactory factory = new(Mono(1000), provider);
		using PreparedIncrementalPlaybackPlan plan =
			factory.Create(SongDocumentSnapshot.Create(document), patternId);
		Assert.That(provider.Calls, Is.EqualTo(1),
			"Sample-data providers must run during preparation, never Render.");
		plan.Source.Render(1, new float[1]);
		Assert.That(provider.Calls, Is.EqualTo(1));
	}

	[Test]
	public void NestedPatternMixdownRendersSilenceWithoutIntermediatePcm()
	{
		SongDocument document = new();
		ObjectId childId = document.AllocateObjectId();
		document.Add(new DataPatternDefinition(childId, "Mixdown child")
		{
			RowCount = 1,
			ChannelCount = 1,
		});
		ObjectId parentId = document.AllocateObjectId();
		DataPatternDefinition parent = new(parentId, "Parent")
		{
			RowCount = 1,
			ChannelCount = 1,
		};
		PatternCell cell = parent.Grid.GetOrCreateCell(0, 0);
		cell.SourceId = childId;
		cell.Note = new StartPatternNote(mixdown: true);
		document.Add(parent);
		using PreparedIncrementalPlaybackPlan plan =
			new PreparedIncrementalPlaybackFactory(Mono(1000))
				.Create(document, parentId);
		Assert.DoesNotThrow(() => plan.Source.Render(1, new float[1]));
		float[] silence = new float[12];
		plan.Source.Render(12, silence);
		Assert.That(silence, Is.All.Zero);
	}

	[Test]
	public void NestedScriptPatternMixdownUsesPrivateClockWithoutShiftingParentTempo()
	{
		SongDocument document = new();
		ObjectId sampleId = document.AllocateObjectId();
		document.Add(SampleDefinition.CreateImported(
			sampleId, "PCM", "sample.wav", Wave(16384)));
		ObjectId childId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(childId, "Nested script")
		{
			RowCount = 1, ChannelCount = 1,
			Source = $"Tempo(0, 250); Note(0, 0, _O({sampleId.Value}));",
		});
		ObjectId parentId = document.AllocateObjectId();
		DataPatternDefinition parent = new(parentId, "Parent")
		{
			RowCount = 2, ChannelCount = 2,
		};
		PatternCell nested = parent.Grid.GetOrCreateCell(0, 0);
		nested.SourceId = childId;
		nested.Note = new StartPatternNote(mixdown: true);
		PatternCell direct = parent.Grid.GetOrCreateCell(1, 1);
		direct.SourceId = sampleId;
		direct.Note = new StartPatternNote();
		document.Add(parent);
		using PreparedIncrementalPlaybackPlan plan =
			new PreparedIncrementalPlaybackFactory(Mono(1000))
				.Create(document, parentId);
		float[] result = new float[121];
		plan.Source.Render(result.Length, result);
		Assert.That(result[0], Is.EqualTo(0.5f).Within(1e-5f));
		Assert.That(result[10], Is.Zero);
		Assert.That(result[119], Is.Zero);
		Assert.That(result[120], Is.EqualTo(0.5f).Within(1e-5f),
			"Child private Tempo must not retime the parent row.");
	}

	[Test]
	public void FlattenedInitialSourceVolumeAlsoUpdatesCallerRememberedNoteVolume()
	{
		SongDocument document = new();
		ObjectId sample = document.AllocateObjectId();
		document.Add(SampleDefinition.CreateImported(sample, "PCM",
			"pcm.wav", Wave(16384)));
		ObjectId child = document.AllocateObjectId();
		DataPatternDefinition inner = new(child, "Child")
		{
			RowCount = 1, ChannelCount = 1,
		};
		inner.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote(sample);
		document.Add(inner);
		ObjectId root = document.AllocateObjectId();
		DataPatternDefinition parent = new(root, "Parent")
		{
			RowCount = 3, ChannelCount = 1,
		};
		PatternCell nested = parent.Grid.GetOrCreateCell(0, 0);
		nested.Note = new StartPatternNote(child);
		nested.Volume = 0.25;
		parent.Grid.GetOrCreateCell(1, 0).Note = new StartPatternNote(sample);
		document.Add(parent);

		using PreparedIncrementalPlaybackPlan plan =
			new PreparedIncrementalPlaybackFactory(Mono(1000)).Create(document, root);
		float[] pcm = new float[122];
		plan.Source.Render(2, pcm.AsSpan(0, 2));
		plan.Source.Render(120, pcm.AsSpan(2));
		Assert.That(pcm[0], Is.EqualTo(0.125f).Within(1e-6f));
		Assert.That(pcm[1], Is.EqualTo(0.125f).Within(1e-6f));
		Assert.That(pcm[120], Is.EqualTo(0.125f).Within(1e-6f),
			"The instigating note's volume must be recalled by later parent notes.");
	}

	[Test]
	public void FlattenedSourceVolumeComposesWithChildExplicitVolumeAndPrivateMixdown()
	{
		SongDocument document = new();
		ObjectId sample = document.AllocateObjectId();
		document.Add(SampleDefinition.CreateImported(sample, "PCM",
			"pcm.wav", Wave(16384)));
		ObjectId privateId = document.AllocateObjectId();
		DataPatternDefinition inner = new(privateId, "Private")
		{
			RowCount = 1, ChannelCount = 1,
		};
		inner.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote(sample);
		document.Add(inner);
		ObjectId flattenedId = document.AllocateObjectId();
		DataPatternDefinition middle = new(flattenedId, "Flattened")
		{
			RowCount = 1, ChannelCount = 1,
		};
		PatternCell privateStart = middle.Grid.GetOrCreateCell(0, 0);
		privateStart.Note = new StartPatternNote(privateId, mixdown: true);
		privateStart.Volume = 0.25;
		document.Add(middle);
		ObjectId root = document.AllocateObjectId();
		DataPatternDefinition parent = new(root, "Parent")
		{
			RowCount = 2, ChannelCount = 1,
		};
		PatternCell call = parent.Grid.GetOrCreateCell(0, 0);
		call.Note = new StartPatternNote(flattenedId);
		call.Volume = 0.5;
		document.Add(parent);

		using PreparedIncrementalPlaybackPlan plan =
			new PreparedIncrementalPlaybackFactory(Mono(1000)).Create(document, root);
		float[] pcm = new float[2];
		plan.Source.Render(pcm.Length, pcm);
		Assert.That(pcm, Is.All.EqualTo(0.0625f).Within(1e-6f),
			"Inherited 0.5 gain * mixdown's 0.25 note volume * 0.5 PCM.");
	}

	[Test]
	public void FlattenedSourceWithoutExplicitVolumeRecallsCallerNoteVolume()
	{
		SongDocument document = new();
		ObjectId sample = document.AllocateObjectId();
		document.Add(SampleDefinition.CreateImported(sample, "PCM",
			"pcm.wav", Wave(16384)));
		ObjectId child = document.AllocateObjectId();
		DataPatternDefinition notes = new(child, "Child")
		{
			RowCount = 1, ChannelCount = 2,
		};
		notes.Grid.GetOrCreateCell(0, 1).Note = new StartPatternNote(sample);
		document.Add(notes);
		ObjectId root = document.AllocateObjectId();
		DataPatternDefinition parent = new(root, "Parent")
		{
			RowCount = 3, ChannelCount = 1,
		};
		PatternCell first = parent.Grid.GetOrCreateCell(0, 0);
		first.Note = new StartPatternNote(sample);
		first.Volume = 0.4;
		parent.Grid.GetOrCreateCell(1, 0).Note = new StartPatternNote(child);
		document.Add(parent);
		using PreparedIncrementalPlaybackPlan plan =
			new PreparedIncrementalPlaybackFactory(Mono(1000)).Create(document, root);
		float[] pcm = new float[122];
		plan.Source.Render(pcm.Length, pcm);
		Assert.That(pcm[0], Is.EqualTo(0.2f).Within(1e-6f));
		Assert.That(pcm[120], Is.EqualTo(0.2f).Within(1e-6f),
			"Flattening without volume must recall 0.4, not use unity.");
	}

	[Test]
	public void FlattenedGuestAndUnrelatedParentVoiceMaySharePhysicalHost()
	{
		SongDocument document = new();
		ObjectId sample = document.AllocateObjectId();
		document.Add(SampleDefinition.CreateImported(sample, "PCM",
			"pcm.wav", Wave(16384)));
		ObjectId child = document.AllocateObjectId();
		DataPatternDefinition notes = new(child, "Child")
		{
			RowCount = 1, ChannelCount = 2,
		};
		notes.Grid.GetOrCreateCell(0, 1).Note = new StartPatternNote(sample);
		document.Add(notes);
		ObjectId root = document.AllocateObjectId();
		DataPatternDefinition parent = new(root, "Parent")
		{
			RowCount = 1, ChannelCount = 2,
		};
		parent.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote(child);
		parent.Grid.GetOrCreateCell(0, 1).Note = new StartPatternNote(sample);
		document.Add(parent);

		using PreparedIncrementalPlaybackPlan plan =
			new PreparedIncrementalPlaybackFactory(Mono(1000)).Create(document, root);
		float[] pcm = new float[2];
		plan.Source.Render(pcm.Length, pcm);
		Assert.That(pcm, Is.All.EqualTo(1.0f).Within(1e-6f),
			"The flattened channel and the unrelated host voice must both sound.");
	}

	[Test]
	public void FlattenedSourceUsesLiveInstigatorOverallVolumeNotHostVolume()
	{
		SongDocument document = new();
		ObjectId sample = document.AllocateObjectId();
		document.Add(SampleDefinition.CreateImported(sample, "PCM",
			"pcm.wav", Wave(16384)));
		ObjectId child = document.AllocateObjectId();
		DataPatternDefinition notes = new(child, "Child")
		{
			RowCount = 1, ChannelCount = 2,
		};
		notes.Grid.GetOrCreateCell(0, 1).Note = new StartPatternNote(sample);
		document.Add(notes);
		ObjectId root = document.AllocateObjectId();
		DataPatternDefinition parent = new(root, "Parent")
		{
			RowCount = 1, ChannelCount = 2,
		};
		PatternCell instigator = parent.Grid.GetOrCreateCell(0, 0);
		instigator.Note = new StartPatternNote(child);
		instigator.Volume = 0.5;
		document.Add(parent);

		using PreparedIncrementalPlaybackPlan plan =
			new PreparedIncrementalPlaybackFactory(Mono(1000)).Create(document, root);
		plan.Source.ApplyLiveEvent(ChannelTarget.Physical(0),
			[new SetOverallChannelVolumeCommand(0.5)]);
		plan.Source.ApplyLiveEvent(ChannelTarget.Physical(1),
			[new SetOverallChannelVolumeCommand(0.2)]);
		float[] pcm = new float[2];
		plan.Source.Render(1, pcm.AsSpan(0, 1));
		Assert.That(pcm[0], Is.EqualTo(0.125f).Within(1e-6f),
			"Child uses its instigating channel volume, not the host volume.");
		plan.Source.ApplyLiveEvent(ChannelTarget.Physical(0),
			[new SetOverallChannelVolumeCommand(0.8)]);
		plan.Source.Render(1, pcm.AsSpan(1, 1));
		Assert.That(pcm[1], Is.EqualTo(0.2f).Within(1e-6f),
			"Instigator's overall-volume changes must affect the child's running voice live.");
	}

	[Test]
	public void TrackerChannelVolumeOnFlattenedStartControlsSplayedVoiceButHostVolumeDoesNot()
	{
		SongDocument document = new();
		ObjectId sample = document.AllocateObjectId();
		document.Add(SampleDefinition.CreateImported(sample, "PCM",
			"pcm.wav", Wave(16384)));
		ObjectId child = document.AllocateObjectId();
		DataPatternDefinition notes = new(child, "Child")
		{
			RowCount = 1, ChannelCount = 2,
		};
		notes.Grid.GetOrCreateCell(0, 1).Note =
			new StartPatternNote(sample);
		document.Add(notes);
		ObjectId root = document.AllocateObjectId();
		DataPatternDefinition parent = new(root, "Parent")
		{
			RowCount = 1, ChannelCount = 2,
		};
		PatternCell instigator = parent.Grid.GetOrCreateCell(0, 0);
		instigator.Note = new StartPatternNote(child);
		instigator.Volume = 0.5;
		instigator.Effects.Add(new TrackerChannelVolumePatternEffect(32));
		instigator.Effects.Add(new TonePortamentoVolumeSlidePatternEffect(0x34));
		parent.Grid.GetOrCreateCell(0, 1).Effects.Add(
			new TrackerChannelVolumePatternEffect(16));
		document.Add(parent);

		using PreparedIncrementalPlaybackPlan plan =
			new PreparedIncrementalPlaybackFactory(Mono(1000))
				.Create(document, root);
		float[] pcm = new float[2];
		plan.Source.Render(pcm.Length, pcm);
		Assert.That(pcm, Is.All.EqualTo(0.125f).Within(1e-6f),
			"0.5 PCM * captured source gain 0.5 * instigator M32/64; "
			+ "the host's M16/64 must not affect the flattened child.");
		Assert.That(plan.SequencingContext.Diagnostics.IgnoredFlatteningEffects,
			Is.EqualTo(1), "Lxx is discarded without disrupting the source.");
	}

	[TestCase(false)]
	[TestCase(true)]
	public void LaterRowVolumeSlideModulatesAlreadySoundingFlattenedChild(
		bool combinedVibratoSlide)
	{
		SongDocument document = new();
		ObjectId sample = document.AllocateObjectId();
		document.Add(SampleDefinition.CreateImported(sample,
			"Sustain", "sustain.wav", LongWave(16384)));
		ObjectId child = document.AllocateObjectId();
		DataPatternDefinition nested = new(child, "Child")
			{ RowCount = 3, ChannelCount = 2 };
		nested.Grid.GetOrCreateCell(0, 1).Note = new StartPatternNote(sample);
		document.Add(nested);
		ObjectId root = document.AllocateObjectId();
		DataPatternDefinition parent = new(root, "Parent")
			{ RowCount = 3, ChannelCount = 1 };
		PatternCell call = parent.Grid.GetOrCreateCell(0, 0);
		call.Note = new StartPatternNote(child);
		call.Volume = 0.75;
		parent.Grid.GetOrCreateCell(1, 0).Effects.Add(
			combinedVibratoSlide
				? new VibratoVolumeSlidePatternEffect(0x01)
				: new TrackerVolumeSlidePatternEffect(0x01));
		document.Add(parent);
		using PreparedIncrementalPlaybackPlan plan =
			new PreparedIncrementalPlaybackFactory(Mono(1000))
				.Create(document, root);
		float[] pcm = new float[244];
		plan.Source.Render(pcm.Length, pcm);
		Assert.That(pcm[0], Is.EqualTo(0.375f).Within(1e-6f));
		Assert.That(pcm[120], Is.EqualTo(0.375f).Within(1e-6f));
		Assert.That(pcm[140], Is.EqualTo((float)(0.5 * (0.75 - 5.0 / (6.0 * 64.0))))
			.Within(1e-5f));
		Assert.That(pcm[240], Is.EqualTo((float)(0.5 * (0.75 - 5.0 / 64.0)))
			.Within(1e-5f));
		Assert.That(plan.SequencingContext.Diagnostics.IgnoredFlatteningEffects,
			Is.EqualTo(combinedVibratoSlide ? 1 : 0),
			"The source-volume component is audible while only the vibrato component is warned.");
	}

	[Test]
	public void NestedFlattenedSourcesMultiplyIndependentLiveVolumeSlides()
	{
		SongDocument document = new();
		ObjectId sample = document.AllocateObjectId();
		document.Add(SampleDefinition.CreateImported(sample,
			"Sustain", "sustain.wav", LongWave(16384)));
		ObjectId leaf = document.AllocateObjectId();
		DataPatternDefinition riff = new(leaf, "Riff")
			{ RowCount = 3, ChannelCount = 1 };
		riff.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote(sample);
		document.Add(riff);
		ObjectId middle = document.AllocateObjectId();
		DataPatternDefinition drums = new(middle, "Drums")
			{ RowCount = 3, ChannelCount = 1 };
		PatternCell drumsCall = drums.Grid.GetOrCreateCell(0, 0);
		drumsCall.Note = new StartPatternNote(leaf);
		drumsCall.Volume = 0.8;
		drums.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new TrackerVolumeSlidePatternEffect(0x01));
		document.Add(drums);
		ObjectId root = document.AllocateObjectId();
		DataPatternDefinition verse = new(root, "Verse")
			{ RowCount = 3, ChannelCount = 1 };
		PatternCell verseCall = verse.Grid.GetOrCreateCell(0, 0);
		verseCall.Note = new StartPatternNote(middle);
		verseCall.Volume = 0.75;
		verse.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new TrackerVolumeSlidePatternEffect(0x01));
		document.Add(verse);
		using PreparedIncrementalPlaybackPlan plan =
			new PreparedIncrementalPlaybackFactory(Mono(1000))
				.Create(document, root);
		float[] pcm = new float[141];
		plan.Source.Render(pcm.Length, pcm);
		Assert.That(pcm[0], Is.EqualTo(0.3f).Within(1e-6f));
		Assert.That(pcm[140],
			Is.EqualTo((float)(0.5 * (0.75 - 5.0 / (6.0 * 64.0))
				* (0.8 - 5.0 / (6.0 * 64.0)))).Within(1e-5f));
	}

	[Test]
	public void ZeroVolumeFlattenedSourceCanBecomeAudibleFromLaterVolumeCommand()
	{
		SongDocument document = new();
		ObjectId sample = document.AllocateObjectId();
		document.Add(SampleDefinition.CreateImported(sample,
			"Sustain", "sustain.wav", LongWave(16384)));
		ObjectId child = document.AllocateObjectId();
		DataPatternDefinition nested = new(child, "Child")
			{ RowCount = 3, ChannelCount = 2 };
		nested.Grid.GetOrCreateCell(0, 1).Note = new StartPatternNote(sample);
		document.Add(nested);
		ObjectId root = document.AllocateObjectId();
		DataPatternDefinition parent = new(root, "Parent")
			{ RowCount = 3, ChannelCount = 1 };
		PatternCell call = parent.Grid.GetOrCreateCell(0, 0);
		call.Note = new StartPatternNote(child);
		call.Volume = 0;
		parent.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new SetNoteVolumePatternEffect(0.5));
		document.Add(parent);
		using PreparedIncrementalPlaybackPlan plan =
			new PreparedIncrementalPlaybackFactory(Mono(1000))
				.Create(document, root);
		float[] pcm = new float[122];
		plan.Source.Render(pcm.Length, pcm);
		Assert.That(pcm[0], Is.Zero);
		Assert.That(pcm[120], Is.EqualTo(0.25f).Within(1e-6f));
	}

	[TestCase(2.0, 60)]
	[TestCase(0.5, 240)]
	public void FlattenedRecursiveSpeedChangesChildRowsButNotSiblingTiming(
		double rate, int childNoteFrame)
	{
		SongDocument document = new();
		ObjectId sample = document.AllocateObjectId();
		document.Add(SampleDefinition.CreateImported(sample, "Pulse",
			"pulse.wav", Wave(16384)));
		ObjectId child = document.AllocateObjectId();
		DataPatternDefinition childPattern = new(child, "Child")
		{
			RowCount = 2, ChannelCount = 2,
		};
		childPattern.Grid.GetOrCreateCell(1, 1).Note = new StartPatternNote(sample);
		document.Add(childPattern);
		ObjectId root = document.AllocateObjectId();
		DataPatternDefinition parent = new(root, "Parent")
		{
			RowCount = 3, ChannelCount = 2,
		};
		PatternCell instigator = parent.Grid.GetOrCreateCell(0, 0);
		instigator.Note =
			new StartPatternNote(child, playbackSpeedMultiplier: rate);
		// This test isolates source scheduling from NNA displacement:
		// the child must keep producing even after another parent note starts.
		instigator.Effects.Add(new TrackerNewNoteActionPatternEffect(
			NoteDisplacementAction.Continue));
		parent.Grid.GetOrCreateCell(1, 0).Note = new StartPatternNote(sample);
		document.Add(parent);
		using PreparedIncrementalPlaybackPlan plan =
			new PreparedIncrementalPlaybackFactory(Mono(1000)).Create(document, root);
		float[] pcm = new float[Math.Max(childNoteFrame, 120) + 6];
		plan.Source.Render(5, pcm.AsSpan(0, 5));
		plan.Source.Render(pcm.Length - 5, pcm.AsSpan(5));
		Assert.That(pcm[119], Is.Zero);
		Assert.That(pcm[120], Is.EqualTo(0.5f).Within(1e-6f),
			"Parent source row remains at its original 120 ms.");
		Assert.That(pcm[childNoteFrame], Is.EqualTo(0.5f).Within(1e-6f),
			"The flattened child source row must follow its own speed.");
		Assert.That(pcm[childNoteFrame + 1], Is.EqualTo(0.5f).Within(1e-6f),
			"Playback speed changes scheduling, not source PCM frequency.");
	}

	[TestCase(2.0, 60)]
	[TestCase(0.5, 240)]
	public void PrivateMixdownSpeedScalesMusicalRowsWithoutResamplingNotes(
		double rate, int nextNoteFrame)
	{
		SongDocument document = new();
		ObjectId sample = document.AllocateObjectId();
		document.Add(SampleDefinition.CreateImported(sample, "Ramp",
			"ramp.wav", RampWave()));
		ObjectId child = document.AllocateObjectId();
		DataPatternDefinition notes = new(child, "Two rows")
		{
			RowCount = 2, ChannelCount = 1,
		};
		notes.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote(sample);
		notes.Grid.GetOrCreateCell(1, 0).Note = new StartPatternNote(sample);
		document.Add(notes);
		ObjectId root = document.AllocateObjectId();
		DataPatternDefinition parent = new(root, "Parent")
		{
			RowCount = 3, ChannelCount = 1,
		};
		parent.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(child, playbackSpeedMultiplier: rate, mixdown: true);
		document.Add(parent);

		using PreparedIncrementalPlaybackPlan plan =
			new PreparedIncrementalPlaybackFactory(Mono(1000)).Create(document, root);
		float[] pcm = new float[nextNoteFrame + 3];
		plan.Source.Render(3, pcm.AsSpan(0, 3));
		plan.Source.Render(pcm.Length - 3, pcm.AsSpan(3));
		Assert.That(pcm[1], Is.EqualTo(512f / 32768f).Within(1e-6f),
			"Playback speed may not implicitly change the note's pitch.");
		Assert.That(pcm[34], Is.Zero);
		Assert.That(pcm[nextNoteFrame + 1],
			Is.EqualTo(512f / 32768f).Within(1e-6f),
			"Second child row must execute at its speed-scaled deadline.");
	}

	[Test]
	public void PrivateMixdownSpeedRespectsChildTempoChangesButNotParentClock()
	{
		SongDocument document = new();
		ObjectId sample = document.AllocateObjectId();
		document.Add(SampleDefinition.CreateImported(sample, "PCM",
			"tone.wav", Wave(16384)));
		ObjectId child = document.AllocateObjectId();
		DataPatternDefinition notes = new(child, "Private tempo")
		{
			RowCount = 3, ChannelCount = 1,
		};
		notes.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote(sample);
		notes.Grid.GetOrCreateCell(1, 0).Effects.Add(new SetTempoPatternEffect(250));
		notes.Grid.GetOrCreateCell(2, 0).Note = new StartPatternNote(sample);
		document.Add(notes);
		ObjectId root = document.AllocateObjectId();
		DataPatternDefinition parent = new(root, "Parent clock")
		{
			RowCount = 3, ChannelCount = 2,
		};
		parent.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(child, playbackSpeedMultiplier: 2.0, mixdown: true);
		parent.Grid.GetOrCreateCell(1, 1).Note = new StartPatternNote(sample);
		document.Add(parent);
		using PreparedIncrementalPlaybackPlan plan =
			new PreparedIncrementalPlaybackFactory(Mono(1000)).Create(document, root);
		plan.Source.Render(1, new float[1]);
		PlaybackVoice privateVoice = plan.Session.GetChannelState(0).CurrentVoice!;
		PropertyInfo childSessionProperty = privateVoice.Sound.GetType().GetProperty(
			"Session", BindingFlags.Public | BindingFlags.Instance)!;
		PlaybackSession childSession =
			(PlaybackSession)childSessionProperty.GetValue(privateVoice.Sound)!;
		Assert.That(childSession.BaselineTempo, Is.EqualTo(250.0));
		float[] pcm = new float[140];
		plan.Source.Render(pcm.Length, pcm);
		Assert.That(pcm[88], Is.Zero);
		Assert.That(pcm[89], Is.EqualTo(0.5f).Within(1e-5f),
			"Child's second-row Tempo change accelerates its third row.");
		Assert.That(pcm[118], Is.Zero);
		Assert.That(pcm[119], Is.EqualTo(0.5f).Within(1e-5f),
			"Private tempo must not retime the parent's next row.");
	}

	[Test]
	public void InstrumentSelectedPrivateSpeedTransformsChildClockNotPitch()
	{
		SongDocument document = new();
		ObjectId sample = document.AllocateObjectId();
		document.Add(SampleDefinition.CreateImported(sample, "Ramp",
			"ramp.wav", RampWave()));
		ObjectId child = document.AllocateObjectId();
		DataPatternDefinition notes = new(child, "Child")
		{
			RowCount = 2, ChannelCount = 1,
		};
		notes.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote(sample);
		notes.Grid.GetOrCreateCell(1, 0).Note = new StartPatternNote(sample);
		document.Add(notes);
		ObjectId instrumentId = document.AllocateObjectId();
		InstrumentDefinition instrument = new(instrumentId, "Selected");
		instrument.ToneSpecifications.Add(new ToneSpecification { SourceId = child });
		instrument.ToneTable.Add(0);
		document.Add(instrument);
		ObjectId root = document.AllocateObjectId();
		DataPatternDefinition parent = new(root, "Parent")
		{
			RowCount = 2, ChannelCount = 1,
		};
		parent.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(instrumentId, playbackSpeedMultiplier: 2.0);
		document.Add(parent);
		using PreparedIncrementalPlaybackPlan plan =
			new PreparedIncrementalPlaybackFactory(Mono(1000)).Create(document, root);
		float[] pcm = new float[65];
		plan.Source.Render(pcm.Length, pcm);
		Assert.That(pcm[1], Is.EqualTo(512f / 32768f).Within(1e-6f));
		Assert.That(pcm[61], Is.EqualTo(512f / 32768f).Within(1e-6f));
	}

	[Test]
	public void NestedPrivateMixdownSpeedFactorsComposeWithoutResampling()
	{
		SongDocument document = new();
		ObjectId sample = document.AllocateObjectId();
		document.Add(SampleDefinition.CreateImported(sample, "Ramp",
			"ramp.wav", RampWave()));
		ObjectId leaf = document.AllocateObjectId();
		DataPatternDefinition inner = new(leaf, "Leaf")
		{
			RowCount = 2, ChannelCount = 1,
		};
		inner.Grid.GetOrCreateCell(1, 0).Note = new StartPatternNote(sample);
		document.Add(inner);
		ObjectId middleId = document.AllocateObjectId();
		DataPatternDefinition middle = new(middleId, "Middle")
		{
			RowCount = 3, ChannelCount = 1,
		};
		middle.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(leaf, playbackSpeedMultiplier: 1.5, mixdown: true);
		document.Add(middle);
		ObjectId root = document.AllocateObjectId();
		DataPatternDefinition parent = new(root, "Parent")
		{
			RowCount = 3, ChannelCount = 1,
		};
		parent.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(middleId, playbackSpeedMultiplier: 2.0, mixdown: true);
		document.Add(parent);
		using PreparedIncrementalPlaybackPlan plan =
			new PreparedIncrementalPlaybackFactory(Mono(1000)).Create(document, root);
		float[] pcm = new float[45];
		plan.Source.Render(10, pcm.AsSpan(0, 10));
		plan.Source.Render(35, pcm.AsSpan(10));
		Assert.That(pcm[39], Is.Zero);
		Assert.That(pcm[41], Is.EqualTo(512f / 32768f).Within(1e-6f),
			"Outer 2x and inner 1.5x should compose to a 3x leaf clock.");
	}

	[Test]
	public void PrivateMixdownSpeedSurvivesDeterministicNativeFrameRewind()
	{
		SongDocument document = new();
		ObjectId sample = document.AllocateObjectId();
		document.Add(SampleDefinition.CreateImported(sample, "Ramp",
			"ramp.wav", RampWave()));
		ObjectId child = document.AllocateObjectId();
		DataPatternDefinition notes = new(child, "Later")
		{
			RowCount = 2, ChannelCount = 1,
		};
		notes.Grid.GetOrCreateCell(1, 0).Note = new StartPatternNote(sample);
		document.Add(notes);
		ObjectId root = document.AllocateObjectId();
		DataPatternDefinition parent = new(root, "Parent")
		{
			RowCount = 3, ChannelCount = 1,
		};
		parent.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(child, playbackSpeedMultiplier: 3.0, mixdown: true);
		document.Add(parent);
		using PreparedIncrementalPlaybackPlan plan =
			new PreparedIncrementalPlaybackFactory(Mono(1000)).Create(document, root);
		plan.Source.Render(1, new float[1]);
		PlaybackVoice voice = plan.Session.GetChannelState(0).CurrentVoice!;
		ISourceFrameSeekableSound seekable = (ISourceFrameSeekableSound)voice.Sound;
		PropertyInfo sessionProperty = voice.Sound.GetType().GetProperty(
			"Session", BindingFlags.Public | BindingFlags.Instance)!;
		PlaybackSession firstSession =
			(PlaybackSession)sessionProperty.GetValue(voice.Sound)!;
		seekable.SetSourceFrameOffset(voice.SoundState, 40);
		float[] forward = new float[1];
		plan.Source.Render(1, forward);
		Assert.That(forward[0], Is.EqualTo(512f / 32768f).Within(1e-6f),
			"A native source-frame seek must advance the accelerated child timeline.");
		seekable.SetSourceFrameOffset(voice.SoundState, 0);
		float[] backward = new float[1];
		plan.Source.Render(1, backward);
		PlaybackSession rebuilt =
			(PlaybackSession)sessionProperty.GetValue(voice.Sound)!;
		Assert.That(backward[0], Is.Zero);
		Assert.That(rebuilt, Is.Not.SameAs(firstSession));
		Assert.That(rebuilt.BaselineTempo, Is.EqualTo(375.0),
			"Reconstruction must retain the threefold tracker-clock rate.");
	}

	[Test]
	public void RepeatedNestedMixdownStartsCreateIndependentPrivateSources()
	{
		SongDocument document = new();
		ObjectId sampleId = document.AllocateObjectId();
		document.Add(SampleDefinition.CreateImported(
			sampleId, "PCM", "sample.wav", Wave(8192)));
		ObjectId childId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(childId, "Child")
		{
			RowCount = 1, ChannelCount = 1,
			Source = $"Note(0, 0, _O({sampleId.Value}));",
		});
		ObjectId parentId = document.AllocateObjectId();
		DataPatternDefinition parent = new(parentId, "Parallel")
		{
			RowCount = 1, ChannelCount = 2,
		};
		for (int channel = 0; channel < 2; channel++)
		{
			PatternCell cell = parent.Grid.GetOrCreateCell(0, channel);
			cell.SourceId = childId;
			cell.Note = new StartPatternNote(mixdown: true);
		}
		document.Add(parent);
		using PreparedIncrementalPlaybackPlan plan =
			new PreparedIncrementalPlaybackFactory(Mono(1000))
				.Create(document, parentId);
		float[] output = new float[2];
		plan.Source.Render(2, output);
		Assert.That(output, Is.All.EqualTo(0.5f).Within(1e-5f));
	}

	[Test]
	public void RecursiveTwoLevelMixdownUsesNestedPrivateClocks()
	{
		SongDocument document = new();
		ObjectId sampleId = document.AllocateObjectId();
		document.Add(SampleDefinition.CreateImported(
			sampleId, "PCM", "test.wav", Wave(16384)));
		ObjectId scriptId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(scriptId, "Script")
		{
			RowCount = 1, ChannelCount = 1,
			Source = $"Note(0, 0, _O({sampleId.Value}));",
		});
		ObjectId intermediateId = document.AllocateObjectId();
		DataPatternDefinition intermediate = new(intermediateId, "Middle")
		{
			RowCount = 1, ChannelCount = 1,
		};
		intermediate.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(scriptId, mixdown: true);
		document.Add(intermediate);
		ObjectId rootId = document.AllocateObjectId();
		DataPatternDefinition root = new(rootId, "Root")
		{
			RowCount = 1, ChannelCount = 1,
		};
		root.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(intermediateId, mixdown: true);
		document.Add(root);
		using PreparedIncrementalPlaybackPlan plan =
			new PreparedIncrementalPlaybackFactory(Mono(1000))
				.Create(document, rootId);
		float[] actual = new float[4];
		plan.Source.Render(4, actual);
		Assert.That(actual, Is.All.EqualTo(0.5f).Within(1e-5f));
	}

	[Test]
	public void RecursiveMixdownPreservesNativeStereoSpeakerFeedsAcrossHorizons()
	{
		SongDocument document = new();
		ObjectId sampleId = document.AllocateObjectId();
		document.Add(SampleDefinition.CreateImported(
			sampleId, "Stereo", "stereo.wav", StereoWave()));
		ObjectId scriptId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(scriptId, "Child")
		{
			RowCount = 1, ChannelCount = 1,
			Source = $"Note(0, 0, _O({sampleId.Value}));",
		});
		ObjectId parentId = document.AllocateObjectId();
		DataPatternDefinition parent = new(parentId, "Mixdown")
		{
			RowCount = 1, ChannelCount = 1,
		};
		parent.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(scriptId, mixdown: true);
		document.Add(parent);
		RenderConfiguration stereo = RenderConfiguration.Stereo(sampleRate: 1000);
		PreparedIncrementalPlaybackFactory factory = new(stereo);
		using PreparedIncrementalPlaybackPlan direct =
			factory.Create(document, scriptId);
		using PreparedIncrementalPlaybackPlan nested =
			factory.Create(document, parentId);

		float[] directPcm = new float[16];
		float[] mixedPcm = new float[16];
		direct.Source.Render(3, directPcm.AsSpan(0, 6));
		nested.Source.Render(3, mixedPcm.AsSpan(0, 6));

		direct.Source.Render(5, directPcm.AsSpan(6, 10));
		nested.Source.Render(5, mixedPcm.AsSpan(6, 10));
		for (int i = 0; i < directPcm.Length; i++)
			Assert.That(mixedPcm[i],
				Is.EqualTo(directPcm[i]).Within(1e-6f),
				$"Speaker sample {i} should match native direct rendering.");
		Assert.That(directPcm[0], Is.Not.EqualTo(directPcm[1]));
	}

	[Test]
	public void RecursiveMixdownSourceCycleFailsBeforeAudioRendering()
	{
		SongDocument document = new();
		ObjectId id = document.AllocateObjectId();
		DataPatternDefinition root = new(id, "Cycle")
		{
			RowCount = 1, ChannelCount = 1,
		};
		root.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(id, mixdown: true);
		document.Add(root);
		using PreparedIncrementalPlaybackPlan plan =
			new PreparedIncrementalPlaybackFactory(Mono(1000))
				.Create(document, id);
		Assert.That(() => plan.Source.Render(20, new float[20]),
			Throws.InvalidOperationException.With.Message.Contains("cycle"));
	}

	[Test]
	public void ParentNoteOffTerminatesPrivateInputAtItsOwnMusicalFrame()
	{
		SongDocument document = new();
		ObjectId sampleId = document.AllocateObjectId();
		SampleDefinition sample = SampleDefinition.CreateImported(
			sampleId, "Loop", "memory.wav", Wave(16384));
		sample.Loop = new SampleLoop(SampleLoopMode.Forward, 0, 4);
		document.Add(sample);
		ObjectId childId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(childId, "Long child")
		{
			RowCount = 8, ChannelCount = 1,
			Source = $"Note(0, 0, _O({sampleId.Value}));",
		});
		ObjectId parentId = document.AllocateObjectId();
		DataPatternDefinition parent = new(parentId, "Off parent")
		{
			RowCount = 3, ChannelCount = 1,
		};
		parent.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(childId, mixdown: true);
		parent.Grid.GetOrCreateCell(1, 0).Note = new PatternNoteOff();
		document.Add(parent);

		using PreparedIncrementalPlaybackPlan plan =
			new PreparedIncrementalPlaybackFactory(Mono(1000))
				.Create(document, parentId);
		float[] prefix = new float[1];
		plan.Source.Render(1, prefix);
		Assert.That(prefix[0], Is.GreaterThan(0));
		object mixdown = plan.Session.GetChannelState(0).CurrentSound!;
		PropertyInfo sessionField = mixdown.GetType().GetProperty("Session",
			BindingFlags.Public | BindingFlags.Instance)!;
		PlaybackSession childSession =
			(PlaybackSession)sessionField.GetValue(mixdown)!;
		Assert.That(childSession.InputEnded, Is.False,
			"The live private input must not be released before the parent Off frame.");
		Assert.That(childSession.NextFrame, Is.EqualTo(1L),
			"A one-frame parent render must advance its child by one frame.");
		float[] remaining = new float[120];
		plan.Source.Render(remaining.Length, remaining);
		Assert.That(childSession.InputEnded, Is.True,
			"Parent Off must release private input when rendering reaches its frame.");
		Assert.That(childSession.NextFrame, Is.EqualTo(120L));
	}

	[Test]
	public void ContinueNewNoteActionPreservesPrivateMixdownAfterReplacement()
	{
		SongDocument document = new();
		ObjectId sampleId = document.AllocateObjectId();
		SampleDefinition sample = SampleDefinition.CreateImported(
			sampleId, "Loop", "memory.wav", Wave(16384));
		sample.Loop = new SampleLoop(SampleLoopMode.Forward, 0, 4);
		document.Add(sample);
		ObjectId childId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(childId, "Continued child")
		{
			RowCount = 8, ChannelCount = 1,
			Source = $"Note(0, 0, _O({sampleId.Value}));",
		});
		ObjectId parentId = document.AllocateObjectId();
		DataPatternDefinition parent = new(parentId, "Continue parent")
		{
			RowCount = 3, ChannelCount = 1,
		};
		PatternCell first = parent.Grid.GetOrCreateCell(0, 0);
		first.Note = new StartPatternNote(childId, mixdown: true);
		first.Effects.Add(new TrackerNewNoteActionPatternEffect(
			NoteDisplacementAction.Continue));
		parent.Grid.GetOrCreateCell(1, 0).Note =
			new StartPatternNote(sampleId);
		document.Add(parent);

		using PreparedIncrementalPlaybackPlan plan =
			new PreparedIncrementalPlaybackFactory(Mono(1000))
				.Create(document, parentId);
		float[] pcm = new float[150];
		plan.Source.Render(pcm.Length, pcm);
		Assert.That(pcm[110], Is.EqualTo(0.5f).Within(1e-5f));
		Assert.That(pcm[120], Is.EqualTo(1f).Within(1e-5f));
		Assert.That(pcm[140], Is.EqualTo(1f).Within(1e-5f),
			"Continued private mixdown and replacement sample must both stay audible.");
	}

	[Test]
	public void PastNoteCutTerminatesOnlyTheDisplacedMixdown()
	{
		SongDocument document = new();
		ObjectId sampleId = document.AllocateObjectId();
		SampleDefinition sample = SampleDefinition.CreateImported(
			sampleId, "Loop", "memory.wav", Wave(16384));
		sample.Loop = new SampleLoop(SampleLoopMode.Forward, 0, 4);
		document.Add(sample);
		ObjectId childId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(childId, "Long child")
		{
			RowCount = 8, ChannelCount = 1,
			Source = $"Note(0, 0, _O({sampleId.Value}));",
		});
		ObjectId parentId = document.AllocateObjectId();
		DataPatternDefinition parent = new(parentId, "NNA and past cut")
		{
			RowCount = 4, ChannelCount = 1,
		};
		PatternCell start = parent.Grid.GetOrCreateCell(0, 0);
		start.Note = new StartPatternNote(childId, mixdown: true);
		start.Effects.Add(new TrackerNewNoteActionPatternEffect(
			NoteDisplacementAction.Continue));
		parent.Grid.GetOrCreateCell(1, 0).Note =
			new StartPatternNote(sampleId);
		parent.Grid.GetOrCreateCell(2, 0).Effects.Add(
			new TrackerPastNoteActionPatternEffect(TrackerPastNoteAction.Cut));
		document.Add(parent);
		using PreparedIncrementalPlaybackPlan plan =
			new PreparedIncrementalPlaybackFactory(Mono(1000))
				.Create(document, parentId);
		float[] pcm = new float[300];
		plan.Source.Render(pcm.Length, pcm);
		Assert.That(pcm[200], Is.EqualTo(1f).Within(1e-5f));
		Assert.That(pcm[280], Is.EqualTo(0.5f).Within(1e-5f));
	}

	[Test]
	public void NnaNoteOffReleasesOldPrivateInputButNotReplacement()
	{
		SongDocument document = new();
		ObjectId sampleId = document.AllocateObjectId();
		SampleDefinition sample = SampleDefinition.CreateImported(
			sampleId, "Loop", "memory.wav", Wave(16384));
		sample.Loop = new SampleLoop(SampleLoopMode.Forward, 0, 4);
		document.Add(sample);
		ObjectId childId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(childId, "Long child")
		{
			RowCount = 8, ChannelCount = 1,
			Source = $"Note(0, 0, _O({sampleId.Value}));",
		});
		ObjectId parentId = document.AllocateObjectId();
		DataPatternDefinition parent = new(parentId, "NNA off")
		{
			RowCount = 3, ChannelCount = 1,
		};
		PatternCell start = parent.Grid.GetOrCreateCell(0, 0);
		start.Note = new StartPatternNote(childId, mixdown: true);
		start.Effects.Add(new TrackerNewNoteActionPatternEffect(
			NoteDisplacementAction.Off));
		parent.Grid.GetOrCreateCell(1, 0).Note =
			new StartPatternNote(sampleId);
		document.Add(parent);
		using PreparedIncrementalPlaybackPlan plan =
			new PreparedIncrementalPlaybackFactory(Mono(1000))
				.Create(document, parentId);
		float[] output = new float[130];
		plan.Source.Render(output.Length, output);
		Assert.That(output[110], Is.EqualTo(0.5f).Within(1e-5f));
		Assert.That(output[125], Is.EqualTo(0.5f).Within(1e-5f));
		// NNA Off applies to the previous private input as well as the
		// parent-renderer voice; only the new direct sample survives.
		Assert.That(plan.Session.VirtualVoices
			.Where(voice => voice.Sound.GetType().Name == "PreparedRecursiveMixdownSound")
			.All(voice => voice.SoundState.NoteOffTime.HasValue), Is.True);
	}

	[Test]
	public void NnaFadeRequestsFadeOnChildPrivateVoicesAtDisplacement()
	{
		SongDocument document = new();
		ObjectId sampleId = document.AllocateObjectId();
		SampleDefinition sample = SampleDefinition.CreateImported(
			sampleId, "Loop", "memory.wav", Wave(16384));
		sample.Loop = new SampleLoop(SampleLoopMode.Forward, 0, 4);
		document.Add(sample);
		ObjectId childId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(childId, "Long child")
		{
			RowCount = 8, ChannelCount = 1,
			Source = $"Note(0, 0, _O({sampleId.Value}));",
		});
		ObjectId parentId = document.AllocateObjectId();
		DataPatternDefinition parent = new(parentId, "NNA fade")
		{
			RowCount = 3, ChannelCount = 1,
		};
		PatternCell first = parent.Grid.GetOrCreateCell(0, 0);
		first.Note = new StartPatternNote(childId, mixdown: true);
		first.Effects.Add(new TrackerNewNoteActionPatternEffect(
			NoteDisplacementAction.Fade));
		parent.Grid.GetOrCreateCell(1, 0).Note = new StartPatternNote(sampleId);
		document.Add(parent);

		using PreparedIncrementalPlaybackPlan plan =
			new PreparedIncrementalPlaybackFactory(Mono(1000))
				.Create(document, parentId);
		plan.Source.Render(121, new float[121]);
		object privateSound = plan.Session.VirtualVoices
			.Single(voice => voice.Sound.GetType().Name == "PreparedRecursiveMixdownSound")
			.Sound;
		PropertyInfo privateSession = privateSound.GetType().GetProperty("Session",
			BindingFlags.Public | BindingFlags.Instance)!;
		PlaybackSession childSession =
			(PlaybackSession)privateSession.GetValue(privateSound)!;
		Assert.That(childSession.GetChannelState(0).CurrentVoice?
			.IsNoteFadeRequested, Is.True);
	}

	[Test]
	public void ParentNoteCutEndsPrivateMixdownAtExactOutputFrame()
	{
		SongDocument document = new();
		ObjectId sampleId = document.AllocateObjectId();
		SampleDefinition sample = SampleDefinition.CreateImported(
			sampleId, "Loop", "memory.wav", Wave(16384));
		sample.Loop = new SampleLoop(SampleLoopMode.Forward, 0, 4);
		document.Add(sample);
		ObjectId childId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(childId, "Long child")
		{
			RowCount = 8, ChannelCount = 1,
			Source = $"Note(0, 0, _O({sampleId.Value}));",
		});
		ObjectId parentId = document.AllocateObjectId();
		DataPatternDefinition parent = new(parentId, "Cut parent")
		{
			RowCount = 3, ChannelCount = 1,
		};
		parent.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(childId, mixdown: true);
		parent.Grid.GetOrCreateCell(1, 0).Note = new PatternNoteCut();
		document.Add(parent);

		using PreparedIncrementalPlaybackPlan plan =
			new PreparedIncrementalPlaybackFactory(Mono(1000))
				.Create(document, parentId);
		plan.Source.Render(1, new float[1]);
		var voice = plan.Session.GetChannelState(0).CurrentVoice!;
		plan.Source.Render(120, new float[120]);
		long? privateEnd = voice.Sound.GetEndFrameExclusive(
			new RenderContext(Mono(1000)), voice.SoundState);
		Assert.That(privateEnd, Is.EqualTo(120L),
			"Parent Cut must stop future child PCM generation, not only mute its parent voice.");
	}

	[Test]
	public void ParentRenderAdvancesNestedChildByExactlyRequestedFrames()
	{
		SongDocument document = new();
		ObjectId sampleId = document.AllocateObjectId();
		document.Add(SampleDefinition.CreateImported(
			sampleId, "PCM", "memory.wav", Wave(16384)));
		ObjectId childId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(childId, "Child")
		{
			RowCount = 8, ChannelCount = 1,
			Source = $"Note(0, 0, _O({sampleId.Value}));",
		});
		ObjectId rootId = document.AllocateObjectId();
		DataPatternDefinition root = new(rootId, "Parent")
		{
			RowCount = 3, ChannelCount = 1,
		};
		root.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(childId, mixdown: true);
		document.Add(root);
		using PreparedIncrementalPlaybackPlan plan =
			new PreparedIncrementalPlaybackFactory(Mono(1000))
				.Create(document, rootId);
		float[] first = new float[1];
		plan.Source.Render(1, first);
		object sound = plan.Session.GetChannelState(0).CurrentSound!;
		PropertyInfo sessionField = sound.GetType().GetProperty("Session",
			BindingFlags.Public | BindingFlags.Instance)!;
		PlaybackSession childSession = (PlaybackSession)sessionField.GetValue(sound)!;
		Assert.That(childSession.NextFrame, Is.EqualTo(1L),
			"Child PlaybackSession must render only the frame requested by its parent.");
		Assert.That(sound.GetType().GetField("_blocks",
			BindingFlags.NonPublic | BindingFlags.Instance), Is.Null,
			"Pre-rendered PCM chunk storage must be removed.");
		Assert.That(first[0], Is.EqualTo(0.5f).Within(1e-5f));
		float[] next = new float[3];
		plan.Source.Render(3, next);
		Assert.That(childSession.NextFrame, Is.EqualTo(4L));
	}

	[Test]
	public void PrivateMixdownForwardSeekAdvancesLiveRendererWithoutPcmHistory()
	{
		SongDocument document = new();
		ObjectId sampleId = document.AllocateObjectId();
		document.Add(SampleDefinition.CreateImported(
			sampleId, "Ramp", "memory.wav", RampWave()));
		ObjectId childId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(childId, "Child")
		{
			RowCount = 4, ChannelCount = 1,
			Source = $"Note(0, 0, _O({sampleId.Value}));",
		});
		ObjectId rootId = document.AllocateObjectId();
		DataPatternDefinition root = new(rootId, "Parent")
		{
			RowCount = 4, ChannelCount = 1,
		};
		root.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(childId, mixdown: true);
		document.Add(root);
		PreparedIncrementalPlaybackFactory factory = new(Mono(1000));
		using PreparedIncrementalPlaybackPlan nested = factory.Create(document, rootId);
		using PreparedIncrementalPlaybackPlan direct = factory.Create(document, childId);

		float[] reference = new float[12];
		direct.Source.Render(reference.Length, reference);
		nested.Source.Render(1, new float[1]);
		PlaybackVoice voice = nested.Session.GetChannelState(0).CurrentVoice!;
		((ISourceFrameSeekableSound)voice.Sound).SetSourceFrameOffset(
			voice.SoundState, 10);
		float[] sought = new float[1];
		nested.Source.Render(1, sought);
		Assert.That(sought[0], Is.EqualTo(reference[11]).Within(1e-6f));
		PropertyInfo sessionField = voice.Sound.GetType().GetProperty("Session",
			BindingFlags.Public | BindingFlags.Instance)!;
		PlaybackSession child = (PlaybackSession)sessionField.GetValue(voice.Sound)!;
		Assert.That(child.NextFrame, Is.EqualTo(12L),
			"Forward seek must advance the actual child renderer, discarding samples.");

		((ISourceFrameSeekableSound)voice.Sound).SetSourceFrameOffset(
			voice.SoundState, 0);
		float[] rewound = new float[1];
		nested.Source.Render(1, rewound);
		Assert.That(rewound[0], Is.EqualTo(reference[2]).Within(1e-6f),
			"Backward source-frame offset must replay the child state rather than PCM history.");
		PlaybackSession reconstructed = (PlaybackSession)sessionField.GetValue(voice.Sound)!;
		Assert.That(reconstructed, Is.Not.SameAs(child),
			"A backward seek must replace the live child playback session.");
		Assert.That(reconstructed.NextFrame, Is.EqualTo(3L));
	}

	[Test]
	public void PrivateMixdownRetriggerReconstructsChildPlaybackState()
	{
		SongDocument document = new();
		ObjectId sampleId = document.AllocateObjectId();
		document.Add(SampleDefinition.CreateImported(
			sampleId, "PCM", "memory.wav", Wave(16384)));
		ObjectId childId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(childId, "Child")
		{
			RowCount = 4, ChannelCount = 1,
			Source = $"Note(0, 0, _O({sampleId.Value}));",
		});
		ObjectId rootId = document.AllocateObjectId();
		DataPatternDefinition root = new(rootId, "Parent")
		{
			RowCount = 4, ChannelCount = 1,
		};
		root.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(childId, mixdown: true);
		document.Add(root);
		using PreparedIncrementalPlaybackPlan plan =
			new PreparedIncrementalPlaybackFactory(Mono(1000))
				.Create(document, rootId);
		float[] before = new float[3];
		plan.Source.Render(3, before);
		Assert.That(before[0], Is.EqualTo(0.5f).Within(1e-6f));
		PlaybackVoice voice = plan.Session.GetChannelState(0).CurrentVoice!;
		PropertyInfo sessionField = voice.Sound.GetType().GetProperty("Session",
			BindingFlags.Public | BindingFlags.Instance)!;
		PlaybackSession original = (PlaybackSession)sessionField.GetValue(voice.Sound)!;
		Assert.That(original.NextFrame, Is.EqualTo(3L));
		plan.Session.ApplyLiveEvent(ChannelTarget.Physical(0),
			[new RetriggerCurrentVoiceCommand(0)]);
		float[] after = new float[3];
		plan.Source.Render(3, after);
		PlaybackSession replayed = (PlaybackSession)sessionField.GetValue(voice.Sound)!;
		Assert.That(replayed, Is.Not.SameAs(original));
		Assert.That(replayed.NextFrame, Is.EqualTo(3L),
			"Retrigger must restart the private renderer at frame zero.");
		Assert.That(replayed.GetChannelState(0).CurrentVoice?.StartFrame,
			Is.EqualTo(0L), "The child source's initial event must be replayed.");
		Assert.That(after[0], Is.GreaterThan(before[0]),
			"Normal parent retrigger anti-click PCM should overlap the restarted child.");
	}

	[Test]
	public void RewindingOuterMixdownRecursivelyReconstructsGrandchildPlayback()
	{
		SongDocument document = new();
		ObjectId sampleId = document.AllocateObjectId();
		document.Add(SampleDefinition.CreateImported(
			sampleId, "PCM", "test.wav", Wave(16384)));
		ObjectId leafId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(leafId, "Leaf")
		{
			RowCount = 2, ChannelCount = 1,
			Source = $"Note(0, 0, _O({sampleId.Value}));",
		});
		ObjectId middleId = document.AllocateObjectId();
		DataPatternDefinition middle = new(middleId, "Middle")
		{
			RowCount = 2, ChannelCount = 1,
		};
		middle.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(leafId, mixdown: true);
		document.Add(middle);
		ObjectId rootId = document.AllocateObjectId();
		DataPatternDefinition root = new(rootId, "Root")
		{
			RowCount = 2, ChannelCount = 1,
		};
		root.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(middleId, mixdown: true);
		document.Add(root);
		using PreparedIncrementalPlaybackPlan plan =
			new PreparedIncrementalPlaybackFactory(Mono(1000))
				.Create(document, rootId);
		plan.Source.Render(1, new float[1]);
		PlaybackVoice outer = plan.Session.GetChannelState(0).CurrentVoice!;
		ISourceFrameSeekableSound seekable = (ISourceFrameSeekableSound)outer.Sound;
		PropertyInfo sessionField = outer.Sound.GetType().GetProperty("Session",
			BindingFlags.Public | BindingFlags.Instance)!;
		PlaybackSession oldMiddle = (PlaybackSession)sessionField.GetValue(outer.Sound)!;
		PlaybackVoice inner = oldMiddle.GetChannelState(0).CurrentVoice!;
		PlaybackSession oldLeaf = (PlaybackSession)sessionField.GetValue(inner.Sound)!;
		seekable.SetSourceFrameOffset(outer.SoundState, 15);
		plan.Source.Render(1, new float[1]);
		Assert.That(oldMiddle.NextFrame, Is.EqualTo(17L));
		Assert.That(oldLeaf.NextFrame, Is.EqualTo(17L));
		seekable.SetSourceFrameOffset(outer.SoundState, 0);
		float[] again = new float[1];
		plan.Source.Render(1, again);
		Assert.That(again[0], Is.EqualTo(0.5f).Within(1e-6f));
		PlaybackSession newMiddle = (PlaybackSession)sessionField.GetValue(outer.Sound)!;
		PlaybackVoice newInner = newMiddle.GetChannelState(0).CurrentVoice!;
		PlaybackSession newLeaf = (PlaybackSession)sessionField.GetValue(newInner.Sound)!;
		Assert.That(newMiddle, Is.Not.SameAs(oldMiddle));
		Assert.That(newLeaf, Is.Not.SameAs(oldLeaf));
		Assert.That(newMiddle.NextFrame, Is.EqualTo(3L));
		Assert.That(newLeaf.NextFrame, Is.EqualTo(3L));
	}

	[Test]
	public void InstrumentToneRunsScriptedPatternAndPreservesToneVolumeEnvelope()
	{
		SongDocument document = new();
		ObjectId sample = document.AllocateObjectId();
		document.Add(SampleDefinition.CreateImported(sample, "PCM",
			"sample.wav", Wave(16384)));
		ObjectId child = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(child, "Child")
		{
			RowCount = 3, ChannelCount = 1,
			Source = $"Note(0, 0, _O({sample.Value}));",
		});
		ObjectId envelopeId = document.AllocateObjectId();
		document.Add(new AdsrEnvelopeDefinition(envelopeId, "Quarter")
		{
			SustainLevel = 0.25,
		});
		ObjectId instrumentId = document.AllocateObjectId();
		InstrumentDefinition instrument = new(instrumentId, "Instrument");
		instrument.ToneSpecifications.Add(new ToneSpecification
		{
			SourceId = child,
			VolumeEnvelopeId = envelopeId,
		});
		instrument.ToneTable.Add(0);
		document.Add(instrument);
		ObjectId root = document.AllocateObjectId();
		DataPatternDefinition pattern = new(root, "Root")
		{
			RowCount = 1, ChannelCount = 1,
		};
		pattern.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(instrumentId);
		document.Add(pattern);
		using PreparedIncrementalPlaybackPlan plan =
			new PreparedIncrementalPlaybackFactory(Mono(1000))
				.Create(document, root);
		float[] output = new float[3];
		plan.Source.Render(output.Length, output);
		Assert.That(output, Is.All.EqualTo(0.125f).Within(1e-6f));
		Assert.That(plan.Session.GetChannelState(0).CurrentVoice!.Sound
			.GetType().Name, Is.EqualTo("PreparedRecursiveMixdownSound"));
	}

	[Test]
	public void NestedInstrumentsCanSelectSequenceAndInheritEnvelopeChain()
	{
		SongDocument document = new();
		ObjectId sample = document.AllocateObjectId();
		document.Add(SampleDefinition.CreateImported(sample, "PCM",
			"sample.wav", Wave(16384)));
		ObjectId leaf = document.AllocateObjectId();
		DataPatternDefinition leafPattern = new(leaf, "Leaf")
		{
			RowCount = 2, ChannelCount = 1,
		};
		leafPattern.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(sample);
		document.Add(leafPattern);
		ObjectId child = document.AllocateObjectId();
		DataSequenceDefinition sequence = new(child, "Child Sequence");
		sequence.Entries.Add(new SequenceEntry(leaf));
		document.Add(sequence);
		ObjectId quarter = document.AllocateObjectId();
		document.Add(new AdsrEnvelopeDefinition(quarter, "Quarter")
		{
			SustainLevel = 0.25,
		});
		ObjectId inner = document.AllocateObjectId();
		InstrumentDefinition innerInstrument = new(inner, "Inner");
		innerInstrument.ToneSpecifications.Add(new ToneSpecification
		{
			SourceId = child, VolumeEnvelopeId = quarter,
		});
		innerInstrument.ToneTable.Add(0);
		document.Add(innerInstrument);
		ObjectId outer = document.AllocateObjectId();
		InstrumentDefinition outerInstrument = new(outer, "Outer");
		outerInstrument.ToneSpecifications.Add(new ToneSpecification
		{
			SourceId = inner,
		});
		outerInstrument.ToneTable.Add(0);
		document.Add(outerInstrument);
		ObjectId root = document.AllocateObjectId();
		DataPatternDefinition rootPattern = new(root, "Root")
		{
			RowCount = 1, ChannelCount = 1,
		};
		rootPattern.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(outer);
		document.Add(rootPattern);
		using PreparedIncrementalPlaybackPlan plan =
			new PreparedIncrementalPlaybackFactory(Mono(1000))
				.Create(document, root);
		float[] output = new float[2];
		plan.Source.Render(output.Length, output);
		Assert.That(output, Is.All.EqualTo(0.125f).Within(1e-6f));
	}

	[Test]
	public void DirectToneDoesNotAllocateRecursiveVoiceForUnselectedPatternTone()
	{
		SongDocument document = new();
		ObjectId sample = document.AllocateObjectId();
		document.Add(SampleDefinition.CreateImported(sample, "PCM",
			"sample.wav", Wave(16384)));
		ObjectId unusedPattern = document.AllocateObjectId();
		document.Add(new DataPatternDefinition(unusedPattern, "Unused")
		{
			RowCount = 1, ChannelCount = 1,
		});
		ObjectId instrumentId = document.AllocateObjectId();
		InstrumentDefinition instrument = new(instrumentId, "Mixed tones");
		instrument.ToneSpecifications.Add(new ToneSpecification
		{
			SourceId = sample,
		});
		instrument.ToneSpecifications.Add(new ToneSpecification
		{
			SourceId = unusedPattern,
		});
		instrument.ToneTable.Add(0); // Default pitch selects ordinary sample.
		instrument.ToneTable.Add(1);
		document.Add(instrument);
		ObjectId root = document.AllocateObjectId();
		DataPatternDefinition pattern = new(root, "Parent")
		{
			RowCount = 1, ChannelCount = 1,
		};
		pattern.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(instrumentId);
		document.Add(pattern);
		using PreparedIncrementalPlaybackPlan plan =
			new PreparedIncrementalPlaybackFactory(Mono(1000))
				.Create(document, root);
		float[] output = new float[1];
		plan.Source.Render(1, output);
		Assert.That(output[0], Is.EqualTo(0.5f).Within(1e-6f));
		Assert.That(plan.Session.GetChannelState(0).CurrentVoice!.Sound
			.GetType().Name, Is.EqualTo("SampleSound"),
			"Only selected recursive tone paths need invocation-unique sounds.");
	}

	[TestCase(false)]
	[TestCase(true)]
	public void FlattenedInstigatorOffReleasesIndirectPrivateSequenceInput(
		bool instrumentSelected)
	{
		SongDocument document = new();
		ObjectId sample = document.AllocateObjectId();
		SampleDefinition looping = SampleDefinition.CreateImported(
			sample, "Sustain", "sustain.wav", Wave(16384));
		looping.Loop = new SampleLoop(SampleLoopMode.Forward, 0, 4);
		document.Add(looping);

		ObjectId privatePatternId = document.AllocateObjectId();
		DataPatternDefinition privatePattern = new(privatePatternId, "Private notes")
		{
			RowCount = 4, ChannelCount = 1,
		};
		privatePattern.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(sample);
		privatePattern.Grid.GetOrCreateCell(2, 0).Note =
			new StartPatternNote(sample);
		document.Add(privatePattern);
		ObjectId privateSequenceId = document.AllocateObjectId();
		DataSequenceDefinition privateSequence = new(privateSequenceId, "Private sequence");
		privateSequence.Entries.Add(new SequenceEntry(privatePatternId));
		document.Add(privateSequence);

		ObjectId selectedSource = privateSequenceId;
		if (instrumentSelected)
		{
			ObjectId innerId = document.AllocateObjectId();
			InstrumentDefinition inner = new(innerId, "Inner Instrument");
			inner.ToneSpecifications.Add(new ToneSpecification
			{
				SourceId = privateSequenceId,
			});
			inner.ToneTable.Add(0);
			document.Add(inner);
			ObjectId outerId = document.AllocateObjectId();
			InstrumentDefinition outer = new(outerId, "Outer Instrument");
			outer.ToneSpecifications.Add(new ToneSpecification
			{
				SourceId = innerId,
			});
			outer.ToneTable.Add(0);
			document.Add(outer);
			selectedSource = outerId;
		}

		ObjectId flattenedId = document.AllocateObjectId();
		DataPatternDefinition flattened = new(flattenedId, "Flattened")
		{
			RowCount = 4, ChannelCount = 2,
		};
		flattened.Grid.GetOrCreateCell(0, 1).Note =
			new StartPatternNote(selectedSource, mixdown: !instrumentSelected);
		document.Add(flattened);
		ObjectId root = document.AllocateObjectId();
		DataPatternDefinition parent = new(root, "Root")
		{
			RowCount = 4, ChannelCount = 2,
		};
		parent.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(flattenedId);
		// Unrelated voice shares the physical host index (1) but has
		// independent root ownership, and must not be released.
		parent.Grid.GetOrCreateCell(0, 1).Note =
			new StartPatternNote(sample);
		parent.Grid.GetOrCreateCell(1, 0).Note = new PatternNoteOff();
		document.Add(parent);

		using PreparedIncrementalPlaybackPlan plan =
			new PreparedIncrementalPlaybackFactory(Mono(1000))
				.Create(document, root);
		float[] intro = new float[1];
		plan.Source.Render(1, intro);
		Assert.That(intro[0], Is.EqualTo(1f).Within(1e-6f));
		PlaybackVoice privateVoice =
			plan.Session.GetChannelState(1, 1).CurrentVoice!;
		Assert.That(privateVoice.Sound.GetType().Name,
			Is.EqualTo("PreparedRecursiveMixdownSound"));
		PropertyInfo childSessionProperty = privateVoice.Sound.GetType()
			.GetProperty("Session", BindingFlags.Instance | BindingFlags.Public)!;
		PlaybackSession childSession =
			(PlaybackSession)childSessionProperty.GetValue(privateVoice.Sound)!;
		Assert.That(childSession.InputEnded, Is.False);

		float[] beforeOff = new float[119];
		plan.Source.Render(beforeOff.Length, beforeOff);
		Assert.That(childSession.InputEnded, Is.False);
		float[] afterOff = new float[70];
		plan.Source.Render(afterOff.Length, afterOff);
		Assert.That(childSession.InputEnded, Is.True,
			"Flattened Note Off must stop private Sequence input even when "
			+ "the private sound is selected through nested Instruments.");
		Assert.That(afterOff[40], Is.EqualTo(0.5f).Within(1e-5f),
			"The unrelated parent host voice must remain audible.");
	}

	[Test]
	public void IndirectPrivateVirtualNnaContinueAndPastNoteOffReleaseOnlyOldInput()
	{
		SongDocument document = new();
		ObjectId sample = document.AllocateObjectId();
		SampleDefinition looping = SampleDefinition.CreateImported(
			sample, "Loop", "loop.wav", Wave(16384));
		looping.Loop = new SampleLoop(SampleLoopMode.Forward, 0, 4);
		document.Add(looping);
		ObjectId child = document.AllocateObjectId();
		DataPatternDefinition privatePattern = new(child, "Private")
		{
			RowCount = 5, ChannelCount = 1,
		};
		privatePattern.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(sample);
		document.Add(privatePattern);
		ObjectId instrumentId = document.AllocateObjectId();
		InstrumentDefinition instrument = new(instrumentId, "Indirect");
		instrument.ToneSpecifications.Add(new ToneSpecification
		{
			SourceId = child,
		});
		instrument.ToneTable.Add(0);
		document.Add(instrument);
		NoteScheduleBuilder schedule = new();
		schedule.Append(new NoteEvent(
			Heresy.Core.Timing.MusicalTime.Zero,
			ChannelTarget.Virtual(7),
			[new StartNoteCommand(instrumentId),
				new SetCurrentVoiceDisplacementActionCommand(
					NoteDisplacementAction.Continue)]));
		schedule.Append(new NoteEvent(
			new Heresy.Core.Timing.MusicalTime(TimeSpan.Zero, 1),
			ChannelTarget.Virtual(7),
			[new StartNoteCommand(sample)]));
		schedule.Append(new NoteEvent(
			new Heresy.Core.Timing.MusicalTime(TimeSpan.Zero, 2),
			ChannelTarget.Virtual(7),
			[new ApplyPastNoteActionCommand(TrackerPastNoteAction.Off)]));

		using PreparedIncrementalPlaybackPlan plan =
			new PreparedIncrementalPlaybackFactory(Mono(1000))
				.CreateAdHoc(SongDocumentSnapshot.Create(document),
					schedule.Freeze());
		float[] intro = new float[121];
		plan.Source.Render(intro.Length, intro);
		Assert.That(intro[110], Is.EqualTo(0.5f).Within(1e-5f));
		Assert.That(intro[120], Is.EqualTo(1f).Within(1e-5f));
		PlaybackVoice previous = plan.Session.VirtualVoices.Single();
		Assert.That(previous.Sound.GetType().Name,
			Is.EqualTo("PreparedRecursiveMixdownSound"));
		PropertyInfo sessionProperty = previous.Sound.GetType()
			.GetProperty("Session", BindingFlags.Instance | BindingFlags.Public)!;
		PlaybackSession privateSession =
			(PlaybackSession)sessionProperty.GetValue(previous.Sound)!;
		Assert.That(privateSession.InputEnded, Is.False);

		float[] after = new float[140];
		plan.Source.Render(after.Length, after);
		Assert.That(privateSession.InputEnded, Is.True,
			"Virtual S71 on the displaced recursive voice must release its "
			+ "private input, not only its parent renderer voice.");
		Assert.That(after[130], Is.EqualTo(0.5f).Within(1e-5f),
			"The newer virtual note must remain audible after old input releases.");
	}

	[TestCase(false)]
	[TestCase(true)]
	public void FlattenedInstigatorCutOrFadeControlsNestedInstrumentMixdownAtBoundary(
		bool fade)
	{
		SongDocument document = new();
		ObjectId sample = document.AllocateObjectId();
		SampleDefinition looping = SampleDefinition.CreateImported(sample,
			"Loop", "loop.wav", Wave(16384));
		looping.Loop = new SampleLoop(SampleLoopMode.Forward, 0, 4);
		document.Add(looping);
		ObjectId privateId = document.AllocateObjectId();
		DataPatternDefinition privatePattern = new(privateId, "Nested notes")
		{
			RowCount = 4, ChannelCount = 1,
		};
		privatePattern.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(sample);
		privatePattern.Grid.GetOrCreateCell(2, 0).Note =
			new StartPatternNote(sample);
		document.Add(privatePattern);
		ObjectId instrumentId = document.AllocateObjectId();
		InstrumentDefinition instrument = new(instrumentId, "Recursive tone");
		instrument.ToneSpecifications.Add(new ToneSpecification
		{
			SourceId = privateId,
		});
		instrument.ToneTable.Add(0);
		document.Add(instrument);
		ObjectId flattenedId = document.AllocateObjectId();
		DataPatternDefinition flattened = new(flattenedId, "Flattened")
		{
			RowCount = 4, ChannelCount = 2,
		};
		flattened.Grid.GetOrCreateCell(0, 1).Note =
			new StartPatternNote(instrumentId);
		document.Add(flattened);
		ObjectId root = document.AllocateObjectId();
		DataPatternDefinition parent = new(root, "Parent")
		{
			RowCount = 4, ChannelCount = 1,
		};
		PatternCell instigator = parent.Grid.GetOrCreateCell(0, 0);
		instigator.Note = new StartPatternNote(flattenedId);
		if (fade)
		{
			instigator.Effects.Add(new TrackerNewNoteActionPatternEffect(
				NoteDisplacementAction.Fade));
			parent.Grid.GetOrCreateCell(1, 0).Note =
				new StartPatternNote(sample);
		}
		else
			parent.Grid.GetOrCreateCell(1, 0).Note = new PatternNoteCut();
		document.Add(parent);
		using PreparedIncrementalPlaybackPlan plan =
			new PreparedIncrementalPlaybackFactory(Mono(1000))
				.Create(document, root);
		plan.Source.Render(1, new float[1]);
		PlaybackVoice privateVoice =
			plan.Session.GetChannelState(1, 1).CurrentVoice!;
		PropertyInfo sessionProperty = privateVoice.Sound.GetType()
			.GetProperty("Session", BindingFlags.Instance | BindingFlags.Public)!;
		PlaybackSession childSession =
			(PlaybackSession)sessionProperty.GetValue(privateVoice.Sound)!;
		plan.Source.Render(120, new float[120]);
		if (fade)
		{
			Assert.That(childSession.GetChannelState(0).CurrentVoice?
				.IsNoteFadeRequested, Is.True,
				"S76 on the flattened instigator must reach the private "
				+ "Instrument-selected renderer, not only the outer voice.");
			// Fade stops the private *producer* while retaining existing
			// voices to complete their normal fade. It must not generate
			// the child Pattern's future row-2 note during the tail.
			object privatePlayback = privateVoice.Sound.GetType().GetField(
				"_playback", BindingFlags.NonPublic | BindingFlags.Instance)!
				.GetValue(privateVoice.Sound)!;
			var stream = (PreparedIncrementalAudioSource)privatePlayback
				.GetType().GetProperty("Source")!.GetValue(privatePlayback)!;
			Assert.That(stream.IsComplete, Is.True,
				"A source-level Fade must stop new private note generation.");
			Assert.That(childSession.InputEnded, Is.False,
				"Fade must not impose Note Off or Cut on the already fading voices.");
		}
		else
		{
			long? end = privateVoice.Sound.GetEndFrameExclusive(
				new RenderContext(Mono(1000)), privateVoice.SoundState);
			Assert.That(end, Is.EqualTo(120L),
				"The nested private sound must end at the exact source Cut frame.");
			float[] tail = new float[65];
			plan.Source.Render(tail.Length, tail);
			Assert.That(tail[64], Is.Zero.Within(1e-5f));
		}
	}

	[Test]
	public void ParentCutPropagatesIntoInstrumentSelectedRecursiveChild()
	{
		SongDocument document = new();
		ObjectId sample = document.AllocateObjectId();
		SampleDefinition looping = SampleDefinition.CreateImported(
			sample, "Loop", "loop.wav", Wave(16384));
		looping.Loop = new SampleLoop(SampleLoopMode.Forward, 0, 4);
		document.Add(looping);
		ObjectId child = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(child, "Child")
		{
			RowCount = 8, ChannelCount = 1,
			Source = $"Note(0, 0, _O({sample.Value}));",
		});
		ObjectId instrumentId = document.AllocateObjectId();
		InstrumentDefinition instrument = new(instrumentId, "Recursive");
		instrument.ToneSpecifications.Add(new ToneSpecification
		{
			SourceId = child,
		});
		instrument.ToneTable.Add(0);
		document.Add(instrument);
		ObjectId root = document.AllocateObjectId();
		DataPatternDefinition parent = new(root, "Parent")
		{
			RowCount = 3, ChannelCount = 1,
		};
		parent.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(instrumentId);
		parent.Grid.GetOrCreateCell(1, 0).Note = new PatternNoteCut();
		document.Add(parent);
		using PreparedIncrementalPlaybackPlan plan =
			new PreparedIncrementalPlaybackFactory(Mono(1000))
				.Create(document, root);
		plan.Source.Render(1, new float[1]);
		PlaybackVoice voice = plan.Session.GetChannelState(0).CurrentVoice!;
		Assert.That(voice.Sound.GetType().Name,
			Is.EqualTo("PreparedRecursiveMixdownSound"));
		plan.Source.Render(120, new float[120]);
		long? exclusiveEnd = voice.Sound.GetEndFrameExclusive(
			new RenderContext(Mono(1000)), voice.SoundState);
		Assert.That(exclusiveEnd, Is.EqualTo(120L));
	}

	[Test]
	public void IndirectInstrumentCyclesAreRejectedBeforeRecursiveNoteRendering()
	{
		SongDocument document = new();
		ObjectId first = document.AllocateObjectId();
		ObjectId second = document.AllocateObjectId();
		InstrumentDefinition a = new(first, "A");
		a.ToneSpecifications.Add(new ToneSpecification { SourceId = second });
		a.ToneTable.Add(0);
		document.Add(a);
		InstrumentDefinition b = new(second, "B");
		b.ToneSpecifications.Add(new ToneSpecification { SourceId = first });
		b.ToneTable.Add(0);
		document.Add(b);
		ObjectId root = document.AllocateObjectId();
		DataPatternDefinition pattern = new(root, "Root")
		{
			RowCount = 1, ChannelCount = 1,
		};
		pattern.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(first);
		document.Add(pattern);
		using PreparedIncrementalPlaybackPlan plan =
			new PreparedIncrementalPlaybackFactory(Mono(1000))
				.Create(document, root);
		Assert.That(() => plan.Source.Render(1, new float[1]),
			Throws.InvalidOperationException.With.Message.Contains("cycle"));
	}

	[TestCase(false)]
	[TestCase(true)]
	public void RetiredRecursiveVoicesDoNotAccumulateInInfiniteStylePlayback(
		bool instrumentSelected)
	{
		SongDocument document = new();
		ObjectId sample = document.AllocateObjectId();
		document.Add(SampleDefinition.CreateImported(sample, "PCM",
			"sample.wav", Wave(16384)));
		ObjectId child = document.AllocateObjectId();
		DataPatternDefinition childPattern = new(child, "Short child")
		{
			RowCount = 1, ChannelCount = 1,
		};
		childPattern.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(sample);
		document.Add(childPattern);

		ObjectId selected = child;
		if (instrumentSelected)
		{
			selected = document.AllocateObjectId();
			InstrumentDefinition instrument = new(selected, "Recursive tone");
			instrument.ToneSpecifications.Add(new ToneSpecification
			{
				SourceId = child,
			});
			instrument.ToneTable.Add(0);
			document.Add(instrument);
		}

		ObjectId root = document.AllocateObjectId();
		DataPatternDefinition parent = new(root, "Repeated source")
		{
			RowCount = 49, ChannelCount = 1,
		};
		for (int row = 0; row < 48; row++)
			parent.Grid.GetOrCreateCell(row, 0).Note =
				new StartPatternNote(selected, mixdown: !instrumentSelected);
		// Terminate the last live voice explicitly, rather than assuming an
		// otherwise unbounded child is safe to evict after an elapsed timeout.
		parent.Grid.GetOrCreateCell(48, 0).Note = new PatternNoteCut();
		document.Add(parent);

		using PreparedIncrementalPlaybackPlan plan =
			new PreparedIncrementalPlaybackFactory(Mono(1000))
				.Create(document, root);
		for (int row = 0; row < 48; row++)
		{
			plan.Source.Render(120, new float[120]);
			Assert.That(PreparedRegistrationCount(plan), Is.LessThanOrEqualTo(2),
				$"Retired voices accumulated by row {row}.");
		}
		plan.Source.Render(400, new float[400]);
		Assert.That(PreparedRegistrationCount(plan), Is.Zero,
			"Finite recursive voices must release their transient IDs after finishing.");
	}

	private static int PreparedRegistrationCount(PreparedIncrementalPlaybackPlan plan)
	{
		FieldInfo resolverField = typeof(PlaybackSession).GetField(
			"_soundResolver", BindingFlags.NonPublic | BindingFlags.Instance)!;
		object resolver = resolverField.GetValue(plan.Session)!;
		FieldInfo registrationsField = resolver.GetType().GetField(
			"_preparedMixdowns", BindingFlags.NonPublic | BindingFlags.Instance)!;
		object registrations = registrationsField.GetValue(resolver)!;
		return (int)registrations.GetType().GetProperty("Count")!
			.GetValue(registrations)!;
	}

	[TestCase(false)]
	[TestCase(true)]
	public void RecursivePatternPitchTransposesChildNotesWithoutChangingTheirTiming(bool mixdown)
	{
		SongDocument document = new();
		ObjectId sample = document.AllocateObjectId();
		document.Add(SampleDefinition.CreateImported(sample, "Ramp",
			"ramp.wav", RampWave()));
		ObjectId child = document.AllocateObjectId();
		DataPatternDefinition childPattern = new(child, "Notes")
		{
			RowCount = 2, ChannelCount = 1,
		};
		childPattern.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(sample);
		document.Add(childPattern);
		ObjectId root = document.AllocateObjectId();
		DataPatternDefinition parent = new(root, "Parent")
		{
			RowCount = 3, ChannelCount = 1,
		};
		parent.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(child, pitchMultiplier: 2.0, mixdown: mixdown);
		document.Add(parent);

		using PreparedIncrementalPlaybackPlan plan =
			new PreparedIncrementalPlaybackFactory(Mono(1000))
				.Create(document, root);
		float[] first = new float[3];
		float[] remaining = new float[5];
		plan.Source.Render(first.Length, first);
		plan.Source.Render(remaining.Length, remaining);
		float[] pcm = [.. first, .. remaining];
		for (int frame = 0; frame < pcm.Length; frame++)
			Assert.That(pcm[frame],
				Is.EqualTo(frame * 2 * 512f / 32768f).Within(1e-6f),
				$"Pitch transposition must sample child source at twice its normal frequency (frame {frame}).");
		Assert.That(plan.Session.NextFrame, Is.EqualTo(8L));
	}

	[Test]
	public void InstrumentSelectedRecursivePatternComposesTonePitchWithNotePitch()
	{
		SongDocument document = new();
		ObjectId sample = document.AllocateObjectId();
		document.Add(SampleDefinition.CreateImported(sample, "Ramp",
			"ramp.wav", RampWave()));
		ObjectId child = document.AllocateObjectId();
		DataPatternDefinition childPattern = new(child, "Child")
		{
			RowCount = 2, ChannelCount = 1,
		};
		childPattern.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote(sample);
		document.Add(childPattern);
		ObjectId instrumentId = document.AllocateObjectId();
		InstrumentDefinition instrument = new(instrumentId, "Selected");
		instrument.ToneSpecifications.Add(new ToneSpecification
		{
			SourceId = child,
			PitchMultiplier = 2.0,
		});
		instrument.ToneTable.Add(0);
		document.Add(instrument);
		ObjectId root = document.AllocateObjectId();
		DataPatternDefinition parent = new(root, "Parent")
		{
			RowCount = 2, ChannelCount = 1,
		};
		parent.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote(instrumentId);
		document.Add(parent);

		using PreparedIncrementalPlaybackPlan plan =
			new PreparedIncrementalPlaybackFactory(Mono(1000))
				.Create(document, root);
		float[] pcm = new float[6];
		plan.Source.Render(pcm.Length, pcm);
		for (int frame = 0; frame < pcm.Length; frame++)
			Assert.That(pcm[frame],
				Is.EqualTo(frame * 2 * 512f / 32768f).Within(1e-6f));
	}

	[Test]
	public void FlattenedRecursivePlaybackSpeedSupportsEmptyChild()
	{
		SongDocument document = new();
		ObjectId child = document.AllocateObjectId();
		document.Add(new DataPatternDefinition(child, "Child")
		{
			RowCount = 1, ChannelCount = 1,
		});
		ObjectId root = document.AllocateObjectId();
		DataPatternDefinition parent = new(root, "Parent")
		{
			RowCount = 1, ChannelCount = 1,
		};
		parent.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(child, playbackSpeedMultiplier: 2.0, mixdown: false);
		document.Add(parent);
		using PreparedIncrementalPlaybackPlan plan =
			new PreparedIncrementalPlaybackFactory(Mono(1000))
				.Create(document, root);
		Assert.DoesNotThrow(() => plan.Source.Render(1, new float[1]),
			"A transformed empty child must be schedulable without special cases.");
	}

	[Test]
	public void RequiresRootAndRejectsInvalidRoslynDuringPreparation()
	{
		SongDocument document = new();
		PreparedIncrementalPlaybackFactory factory = new(Mono(1000));
		Assert.Throws<InvalidOperationException>(() => factory.Create(document));
		ObjectId badId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(badId, "Unsafe")
		{
			Source = "System.IO.File.ReadAllText(\"nope\");",
		});
		Assert.Throws<NotSupportedException>(() => factory.Create(
			SongDocumentSnapshot.Create(document), badId));
	}

	[TestCase(false)]
	[TestCase(true)]
	public void FlattenedInstigatorCutOrOffHaltsFutureNotesWithoutCuttingHost(
		bool release)
	{
		SongDocument document = new();
		ObjectId sample = document.AllocateObjectId();
		document.Add(SampleDefinition.CreateImported(sample,
			"Sustain", "sustain.wav", LongWave(16384)));
		ObjectId child = document.AllocateObjectId();
		DataPatternDefinition nested = new(child, "Child")
			{ RowCount = 3, ChannelCount = 3 };
		nested.Grid.GetOrCreateCell(0, 1).Note = new StartPatternNote(sample);
		nested.Grid.GetOrCreateCell(2, 2).Note = new StartPatternNote(sample);
		document.Add(nested);
		ObjectId root = document.AllocateObjectId();
		DataPatternDefinition parent = new(root, "Parent")
			{ RowCount = 3, ChannelCount = 2 };
		parent.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote(child);
		parent.Grid.GetOrCreateCell(1, 0).Note =
			release ? new PatternNoteOff() : new PatternNoteCut();
		document.Add(parent);
		using PreparedIncrementalPlaybackPlan plan =
			new PreparedIncrementalPlaybackFactory(Mono(1000))
				.Create(document, root);
		float[] pcm = new float[300];
		plan.Source.Render(pcm.Length, pcm);
		Assert.That(pcm[0], Is.EqualTo(0.5f).Within(1e-6f));
		Assert.That(pcm[110], Is.EqualTo(0.5f).Within(1e-6f));
		Assert.That(pcm[260], Is.Zero.Within(1e-5f),
			"The second child note must not start after either Cut or Off.");
		// This PCM-only sample ends immediately on Note Off. The Core
		// lifecycle test separately verifies that Off is emitted instead
		// of Cut, preserving release semantics for instruments with tails.
	}

	[TestCase(false)]
	[TestCase(true)]
	public void SourceNnaContinueKeepsOldProducerWhileDefaultCutStopsIt(
		bool continueOld)
	{
		SongDocument document = new();
		ObjectId sample = document.AllocateObjectId();
		document.Add(SampleDefinition.CreateImported(sample,
			"Sustain", "sustain.wav", LongWave(16384)));
		ObjectId child = document.AllocateObjectId();
		DataPatternDefinition nested = new(child, "Child")
			{ RowCount = 3, ChannelCount = 3 };
		nested.Grid.GetOrCreateCell(0, 1).Note = new StartPatternNote(sample);
		nested.Grid.GetOrCreateCell(2, 2).Note = new StartPatternNote(sample);
		document.Add(nested);
		ObjectId root = document.AllocateObjectId();
		DataPatternDefinition parent = new(root, "Parent")
			{ RowCount = 3, ChannelCount = 2 };
		PatternCell start = parent.Grid.GetOrCreateCell(0, 0);
		start.Note = new StartPatternNote(child);
		if (continueOld)
			start.Effects.Add(new TrackerNewNoteActionPatternEffect(
				NoteDisplacementAction.Continue));
		parent.Grid.GetOrCreateCell(1, 0).Note =
			new StartPatternNote(sample);
		document.Add(parent);
		using PreparedIncrementalPlaybackPlan plan =
			new PreparedIncrementalPlaybackFactory(Mono(1000))
				.Create(document, root);
		float[] pcm = new float[280];
		plan.Source.Render(pcm.Length, pcm);
		Assert.That(pcm[0], Is.EqualTo(0.5f).Within(1e-6f));
		Assert.That(pcm[260],
			Is.EqualTo(continueOld ? 1.5f : 0.5f).Within(1e-5f),
			"Continue must preserve the old collection's future events as "
			+ "well as its currently playing voices.");
	}

	[Test]
	public void ExplicitCancellationCutsNestedPhysicalVoicesAtCurrentFrame()
	{
		SongDocument document = new();
		ObjectId sample = document.AllocateObjectId();
		document.Add(SampleDefinition.CreateImported(sample,
			"Sustain", "sustain.wav", LongWave(16384)));
		ObjectId child = document.AllocateObjectId();
		DataPatternDefinition nested = new(child, "Child")
			{ RowCount = 4, ChannelCount = 2 };
		nested.Grid.GetOrCreateCell(0, 1).Note = new StartPatternNote(sample);
		document.Add(nested);
		ObjectId root = document.AllocateObjectId();
		DataPatternDefinition parent = new(root, "Parent")
			{ RowCount = 4, ChannelCount = 1 };
		parent.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote(child);
		document.Add(parent);
		using PreparedIncrementalPlaybackPlan plan =
			new PreparedIncrementalPlaybackFactory(Mono(1000))
				.Create(document, root);
		float[] intro = new float[10];
		plan.Source.Render(10, intro);
		Assert.That(intro[0], Is.EqualTo(0.5f).Within(1e-6f));
		Assert.That(plan.Source.Cancel(plan.RootInvocationId), Is.True);
		float[] after = new float[60];
		plan.Source.Render(60, after);
		Assert.That(after[50], Is.Zero.Within(1e-6f),
			"Explicit subtree cancellation must not leave its physical-host voice sounding.");
		Assert.That(plan.SequencingContext.ScopedMemory.ActiveScopeCount, Is.Zero);
	}

	private static RenderConfiguration Mono(int rate)
		=> new(rate, [new OutputChannelConfiguration(
			Vector3.Zero, positionalImportance: 0.0)]);

	private static byte[] RampWave()
	{
		using MemoryStream stream = new();
		using (BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: true))
		{
			writer.Write(Encoding.ASCII.GetBytes("RIFF"));
			writer.Write(36 + 64);
			writer.Write(Encoding.ASCII.GetBytes("WAVE"));
			writer.Write(Encoding.ASCII.GetBytes("fmt "));
			writer.Write(16);
			writer.Write((ushort)1);
			writer.Write((ushort)1);
			writer.Write(1000);
			writer.Write(2000);
			writer.Write((ushort)2);
			writer.Write((ushort)16);
			writer.Write(Encoding.ASCII.GetBytes("data"));
			writer.Write(64);
			for (short frame = 0; frame < 32; frame++)
				writer.Write((short)(frame * 512));
		}
		return stream.ToArray();
	}

	private static byte[] StereoWave()
	{
		using MemoryStream stream = new();
		using (BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: true))
		{
			writer.Write(Encoding.ASCII.GetBytes("RIFF"));
			writer.Write(36 + 32);
			writer.Write(Encoding.ASCII.GetBytes("WAVE"));
			writer.Write(Encoding.ASCII.GetBytes("fmt "));
			writer.Write(16);
			writer.Write((ushort)1);
			writer.Write((ushort)2);
			writer.Write(1000);
			writer.Write(4000);
			writer.Write((ushort)4);
			writer.Write((ushort)16);
			writer.Write(Encoding.ASCII.GetBytes("data"));
			writer.Write(32);
			for (int frame = 0; frame < 8; frame++)
			{
				writer.Write((short)16384);
				writer.Write((short)8192);
			}
		}
		return stream.ToArray();
	}

	/// <summary>Constant PCM long enough to exercise row-spanning
	/// live source-gain curves; Wave() intentionally has only 4 frames.</summary>
	private static byte[] LongWave(short sample, int frames = 512)
	{
		using MemoryStream stream = new();
		using (BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: true))
		{
			writer.Write(Encoding.ASCII.GetBytes("RIFF"));
			writer.Write(36 + frames * 2);
			writer.Write(Encoding.ASCII.GetBytes("WAVE"));
			writer.Write(Encoding.ASCII.GetBytes("fmt "));
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
				writer.Write(sample);
		}
		return stream.ToArray();
	}

	private static byte[] Wave(short sample)
	{
		using MemoryStream stream = new();
		using (BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: true))
		{
			writer.Write(Encoding.ASCII.GetBytes("RIFF"));
			writer.Write(36 + 8);
			writer.Write(Encoding.ASCII.GetBytes("WAVE"));
			writer.Write(Encoding.ASCII.GetBytes("fmt "));
			writer.Write(16);
			writer.Write((ushort)1);
			writer.Write((ushort)1);
			writer.Write(1000);
			writer.Write(2000);
			writer.Write((ushort)2);
			writer.Write((ushort)16);
			writer.Write(Encoding.ASCII.GetBytes("data"));
			writer.Write(8);
			for (int i = 0; i < 4; i++)
				writer.Write(sample);
		}
		return stream.ToArray();
	}

	private sealed class CountingProvider : ISampleDataProvider
	{
		public int Calls { get; private set; }
		public ISampleData GetSampleData(SampleDefinition sample)
		{
			Calls++;
			return new MemorySampleData(1000, 1, [0.25f, 0.25f]);
		}
	}
}
