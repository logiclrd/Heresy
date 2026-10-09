using System;
using System.IO;
using System.Numerics;
using System.Reflection;

using Heresy.Core.Instruments;
using Heresy.Core.Envelopes;
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
		Assert.Throws<InvalidOperationException>(() =>
			plan.Source.Render(1, new float[1]));
		plan.Source.PrepareThrough(TimeSpan.FromMilliseconds(160));
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
		plan.Source.PrepareThrough(TimeSpan.FromMilliseconds(130));
		plan.Source.Render(1, new float[1]);
		Assert.That(provider.Calls, Is.EqualTo(1));
	}

	[Test]
	public void NestedPatternMixdownPreparesOnProducerAndRendersSilence()
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
		Assert.DoesNotThrow(() =>
			plan.Source.PrepareThrough(TimeSpan.FromMilliseconds(120)));
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
		plan.Source.PrepareThrough(TimeSpan.FromMilliseconds(150));
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
		plan.Source.PrepareThrough(TimeSpan.FromMilliseconds(40));
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
		plan.Source.PrepareThrough(TimeSpan.FromMilliseconds(30));
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

		direct.Source.PrepareThrough(TimeSpan.FromMilliseconds(3));
		nested.Source.PrepareThrough(TimeSpan.FromMilliseconds(3));
		float[] directPcm = new float[16];
		float[] mixedPcm = new float[16];
		direct.Source.Render(3, directPcm.AsSpan(0, 6));
		nested.Source.Render(3, mixedPcm.AsSpan(0, 6));

		direct.Source.PrepareThrough(TimeSpan.FromMilliseconds(9));
		nested.Source.PrepareThrough(TimeSpan.FromMilliseconds(9));
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
		Assert.That(() => plan.Source.PrepareThrough(
			TimeSpan.FromMilliseconds(20)),
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
		plan.Source.PrepareThrough(TimeSpan.FromMilliseconds(200));
		float[] prefix = new float[1];
		plan.Source.Render(1, prefix);
		Assert.That(prefix[0], Is.GreaterThan(0));
		object mixdown = plan.Session.GetChannelState(0).CurrentSound!;
		FieldInfo sessionField = mixdown.GetType().GetField("_session",
			BindingFlags.NonPublic | BindingFlags.Instance)!;
		PlaybackSession childSession =
			(PlaybackSession)sessionField.GetValue(mixdown)!;
		Assert.That(childSession.InputEnded, Is.True,
			"Parent Off must release the child's private input on the producer.");
		Assert.That(childSession.NextFrame, Is.GreaterThanOrEqualTo(120));
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
		plan.Source.PrepareThrough(TimeSpan.FromMilliseconds(200));
		float[] pcm = new float[150];
		plan.Source.Render(pcm.Length, pcm);
		Assert.That(pcm[110], Is.EqualTo(0.5f).Within(1e-5f));
		Assert.That(pcm[120], Is.EqualTo(1f).Within(1e-5f));
		Assert.That(pcm[140], Is.EqualTo(0.5f).Within(1e-5f),
			"Old private mixdown must keep playing after its replacement ends.");
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
		plan.Source.PrepareThrough(TimeSpan.FromMilliseconds(310));
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
		plan.Source.PrepareThrough(TimeSpan.FromMilliseconds(200));
		float[] output = new float[130];
		plan.Source.Render(output.Length, output);
		Assert.That(output[110], Is.EqualTo(0.5f).Within(1e-5f));
		Assert.That(output[125], Is.EqualTo(0.5f).Within(1e-5f));
		// NNA Off applies to the previous private input as well as the
		// parent-renderer voice; only the new direct sample survives.
		Assert.That(plan.Session.VirtualVoices,
			Is.All.Matches<Heresy.Render.Playback.PlaybackVoice>(
				voice => voice.Sound.GetType().Name != "PreparedRecursiveMixdownSound"
					|| voice.SoundState.NoteOffTime.HasValue));
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
