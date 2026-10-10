using System;
using System.IO;
using System.Numerics;
using System.Text;

using Heresy.Core.Envelopes;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Samples;
using Heresy.Playback;
using Heresy.Render.Configuration;

using NUnit.Framework;

namespace Heresy.Tests.Playback;

[TestFixture]
public sealed class ScriptEnvelopePlaybackTests
{
	[Test]
	public void ScriptVolumeEnvelopeTailsOutAfterNoteOffAndIsChunkInvariant()
	{
		(SongDocument song, ObjectId root, ObjectId envelope) =
			MakeSong("releaseSeconds < 0 ? 0.5 : 0.5 * Math.Max(0, 1 - releaseSeconds / 0.1)",
				TimeSpan.FromMilliseconds(100));
		PreparedIncrementalPlaybackFactory factory = new(Mono());
		using PreparedIncrementalPlaybackPlan full = factory.Create(song, root);
		using PreparedIncrementalPlaybackPlan chunked = factory.Create(song, root);
		float[] a = new float[205];
		full.Source.Render(a.Length, a);
		float[] b = new float[a.Length];
		int[] sizes = [1, 7, 13, 3, 27, 5];
		for (int at = 0, block = 0; at < b.Length; block++)
		{
			int count = Math.Min(b.Length - at, sizes[block % sizes.Length]);
			chunked.Source.Render(count, b.AsSpan(at, count));
			at += count;
		}
		Assert.Multiple(() =>
		{
			Assert.That(b, Is.EqualTo(a).Within(1e-6f));
			Assert.That(a[10], Is.EqualTo(0.25f).Within(1e-4f));
			Assert.That(a[90], Is.GreaterThan(0.15f),
				"Note Off cannot cut the sample before the scripted release ends.");
			Assert.That(a[145], Is.GreaterThan(0.0f),
				"Voice retains a release tail after the Note Off event.");
			Assert.That(a[170], Is.Zero.Within(1e-6f),
				"Declared release duration forces silent retirement.");
		});
		Assert.That(((ScriptEnvelopeDefinition)full.Snapshot.Document.Objects[envelope]).Source,
			Does.Contain("Math.Max"));
	}

	[Test]
	public void EditingAuthoredEnvelopeDoesNotChangeAnExistingPlaybackSnapshot()
	{
		(SongDocument song, ObjectId root, ObjectId envelope) =
			MakeSong("0.25", null);
		PreparedIncrementalPlaybackFactory factory = new(Mono());
		using PreparedIncrementalPlaybackPlan before = factory.Create(song, root);
		((ScriptEnvelopeDefinition)song.Objects[envelope]).Source = "0.75";
		song.MarkChanged(affectsAudio: true);
		using PreparedIncrementalPlaybackPlan after = factory.Create(song, root);
		float[] a = new float[10];
		float[] b = new float[10];
		before.Source.Render(10, a);
		after.Source.Render(10, b);
		Assert.Multiple(() =>
		{
			Assert.That(a[5], Is.EqualTo(0.125f).Within(1e-4f));
			Assert.That(b[5], Is.EqualTo(0.375f).Within(1e-4f));
		});
	}

	[Test]
	public void InvalidScriptEnvelopeCannotProduceHalfPreparedPlayback()
	{
		(SongDocument song, ObjectId root, ObjectId envelope) =
			MakeSong("0.5", TimeSpan.Zero);
		((ScriptEnvelopeDefinition)song.Objects[envelope]).Source =
			"System.Environment.TickCount";
		PreparedIncrementalPlaybackFactory factory = new(Mono());
		Assert.Throws<InvalidOperationException>(() => factory.Create(song, root));
	}

	private static (SongDocument Song, ObjectId Root, ObjectId Envelope) MakeSong(
		string source, TimeSpan? releaseDuration)
	{
		SongDocument song = new();
		ObjectId sample = song.AllocateObjectId();
		song.Add(SampleDefinition.CreateImported(sample,
			"Sustain", "sample.wav", Wave()));
		ObjectId envId = song.AllocateObjectId();
		song.Add(new ScriptEnvelopeDefinition(envId, "Script volume")
		{
			Source = source, ReleaseDuration = releaseDuration,
		});
		ObjectId instId = song.AllocateObjectId();
		InstrumentDefinition inst = new(instId, "Voiced");
		inst.ToneSpecifications.Add(new ToneSpecification
		{
			SourceId = sample, VolumeEnvelopeId = envId,
		});
		inst.ToneTable.Add(0);
		song.Add(inst);
		ObjectId root = song.AllocateObjectId();
		song.Add(new ScriptPatternDefinition(root, "Root")
		{
			RowCount = 4, ChannelCount = 1,
			Source = $"Note(0, 0, _O({instId.Value})); Off(0.5, 0);",
		});
		return (song, root, envId);
	}

	private static RenderConfiguration Mono()
		=> new(1000, [new OutputChannelConfiguration(
			Vector3.Zero, positionalImportance: 0)]);

	private static byte[] Wave()
	{
		const int frames = 800;
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
			writer.Write((short)16384);
		writer.Flush();
		return stream.ToArray();
	}
}
