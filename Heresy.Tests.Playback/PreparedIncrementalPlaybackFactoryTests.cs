using System;
using System.IO;
using System.Numerics;
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
