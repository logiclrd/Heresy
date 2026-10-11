using System;
using System.Numerics;

using Heresy.Core.Envelopes;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Playback;
using Heresy.Render.Configuration;

using NUnit.Framework;

namespace Heresy.Tests.Playback;

[TestFixture]
public sealed class NodeEnvelopePlaybackTests
{
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
