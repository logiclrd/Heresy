using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;

using Heresy.Core.Sequencing;
using Heresy.Render.Playback;
using System.Text;

using Heresy.Core.Objects;
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
			BindingFlags.NonPublic | BindingFlags.Instance)!;
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
			BindingFlags.Instance | BindingFlags.NonPublic)!;
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
			BindingFlags.NonPublic | BindingFlags.Instance)!;
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
			BindingFlags.NonPublic | BindingFlags.Instance)!;
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
			BindingFlags.Instance | BindingFlags.NonPublic)!;
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
			BindingFlags.NonPublic | BindingFlags.Instance)!;
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
