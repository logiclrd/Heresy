using System;
using System.IO;
using System.Linq;
using System.Numerics;

using AwesomeAssertions;

using Heresy.Core.Assets;
using Heresy.Core.Envelopes;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Filters;
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
	public void PublishedSynthRevisionChangesFutureNotesWithoutRestartingExistingVoices()
	{
		SongDocument document = new();
		ObjectId synthId = document.AllocateObjectId();
		FmSynthDefinition synth = new(synthId, "Live",
			new FmSynthGraph([new FmConstantNode(0, 0.25)], 0));
		document.Add(synth);

		PlaybackRequestAudioSourceFactory factory =
			new(MonoConfiguration(100));
		IAudioOutputSource output = factory.Create(AdHocPlaybackRequest.Create(
			document, new NoteScheduleBuilder().Freeze()));
		try
		{
			ILiveAudioOutputSource notes = (ILiveAudioOutputSource)output;
			ILiveSynthSourceRevisionOutput revisions =
				(ILiveSynthSourceRevisionOutput)output;

			notes.EnqueueLiveEvent(ChannelTarget.Virtual(10),
				[new StartNoteCommand(synthId)]);
			float[] before = new float[1];
			output.Render(1, before);
			before[0].Should().BeApproximately(0.25f, 1e-6f);

			synth.Graph = new FmSynthGraph(
				[new FmConstantNode(0, 0.5)], 0);
			document.MarkChanged(affectsAudio: true);
			revisions.EnqueueLiveSynthSource(synthId,
				factory.PrepareLiveSynthSource(
					SongDocumentSnapshot.Create(document), synthId));
			notes.EnqueueLiveEvent(ChannelTarget.Virtual(11),
				[new StartNoteCommand(synthId)]);
			float[] together = new float[1];
			output.Render(1, together);
			together[0].Should().BeApproximately(0.75f, 1e-6f,
				"the first voice is still on 0.25 and the second starts on 0.5");

			// Even a structural graph change can be used by the next NoteOn,
			// while the old two generations continue their original sounds.
			synth.Graph = new FmSynthGraph(
				[new FmConstantNode(0, 0.125),
				 new FmConstantNode(1, 0)], 0);
			document.MarkChanged(affectsAudio: true);
			revisions.EnqueueLiveSynthSource(synthId,
				factory.PrepareLiveSynthSource(
					SongDocumentSnapshot.Create(document), synthId));
			notes.EnqueueLiveEvent(ChannelTarget.Virtual(12),
				[new StartNoteCommand(synthId)]);
			float[] threeGenerations = new float[1];
			output.Render(1, threeGenerations);
			threeGenerations[0].Should().BeApproximately(0.875f, 1e-6f,
				"running voices must not be reconstructed when publishing a newer graph");

			notes.EnqueueLiveEvent(ChannelTarget.Virtual(11),
				[new NoteCutCommand()]);
			// Note Cut intentionally carries a short anti-click residue:
			// first frame must retain the waveform, then only the older
			// generation (0.25) and third generation (0.125) remain.
			float[] afterCut = new float[8];
			output.Render(afterCut.Length, afterCut);
			afterCut[0].Should().BeApproximately(0.875f, 1e-6f,
				"the anti-click tail preserves the cut voice's last sample");
			afterCut[^1].Should().BeApproximately(0.375f, 1e-6f,
				"retired newer voices must not alter older generations");
		}
		finally
		{
			(output as IDisposable)?.Dispose();
		}
	}

	[Test]
	public void CommittedSynthEditsAdoptHeldVoicesWithoutNewNotesAndIsolateStructuralGenerations()
	{
		SongDocument document = new();
		ObjectId synthId = document.AllocateObjectId();
		FmSynthDefinition synth = new(synthId, "Live",
			new FmSynthGraph([new FmConstantNode(0, 0.25)], 0));
		document.Add(synth);
		PlaybackRequestAudioSourceFactory factory = new(MonoConfiguration(100));
		IAudioOutputSource output = factory.Create(AdHocPlaybackRequest.Create(
			document, new NoteScheduleBuilder().Freeze()));
		try
		{
			ILiveAudioOutputSource notes = (ILiveAudioOutputSource)output;
			ILiveSynthSourceRevisionOutput edits =
				(ILiveSynthSourceRevisionOutput)output;
			void Commit(FmSynthGraph graph)
			{
				synth.Graph = graph;
				document.MarkChanged(affectsAudio: true);
				edits.EnqueueLiveSynthParameters(synthId,
					factory.PrepareLiveSynthSource(
						SongDocumentSnapshot.Create(document), synthId));
			}
			float Next()
			{
				float[] pcm = new float[1];
				output.Render(1, pcm);
				return pcm[0];
			}

			notes.EnqueueLiveEvent(ChannelTarget.Virtual(10),
				[new StartNoteCommand(synthId)]);
			notes.EnqueueLiveEvent(ChannelTarget.Virtual(11),
				[new StartNoteCommand(synthId)]);
			Next().Should().BeApproximately(0.5f, 1e-6f);

			Commit(new FmSynthGraph([new FmConstantNode(0, 0.375)], 0));
			Next().Should().BeApproximately(0.75f, 1e-6f,
				"both existing voices adopt the edited scalar without NoteOn");

			Commit(new FmSynthGraph([
				new FmConstantNode(0, 0.125),
				new FmConstantNode(1, 0)], 0));
			Next().Should().BeApproximately(0.75f, 1e-6f,
				"even an unreachable node addition changes graph generation");

			notes.EnqueueLiveEvent(ChannelTarget.Virtual(12),
				[new StartNoteCommand(synthId)]);
			Next().Should().BeApproximately(0.875f, 1e-6f);

			Commit(new FmSynthGraph([
				new FmConstantNode(0, 0.2),
				new FmConstantNode(1, 0)], 0));
			Next().Should().BeApproximately(0.95f, 1e-6f,
				"the newest compatible generation updates, not older voices");
		}
		finally
		{
			(output as IDisposable)?.Dispose();
		}
	}

	[Test]
	public void LiveOscillatorRangeEditPreservesTheExistingVoicePhase()
	{
		SongDocument document = new();
		ObjectId synthId = document.AllocateObjectId();
		FmSynthDefinition synth = new(synthId, "Oscillator",
			new FmSynthGraph([
				new FmOscillatorNode(0, FmOscillatorWaveform.Sine, 100.0)], 0));
		document.Add(synth);
		PlaybackRequestAudioSourceFactory factory =
			new(MonoConfiguration(1000));
		NoteSchedule empty = new NoteScheduleBuilder().Freeze();
		IAudioOutputSource control = factory.Create(
			AdHocPlaybackRequest.Create(document, empty));
		IAudioOutputSource edited = factory.Create(
			AdHocPlaybackRequest.Create(document, empty));
		try
		{
			((ILiveAudioOutputSource)control).EnqueueLiveEvent(
				ChannelTarget.Virtual(10), [new StartNoteCommand(synthId)]);
			((ILiveAudioOutputSource)edited).EnqueueLiveEvent(
				ChannelTarget.Virtual(10), [new StartNoteCommand(synthId)]);
			float[] firstControl = new float[7];
			float[] firstEdited = new float[7];
			control.Render(7, firstControl);
			edited.Render(7, firstEdited);
			firstEdited.Should().Equal(firstControl);

			synth.Graph = new FmSynthGraph([
				new FmOscillatorNode(0, FmOscillatorWaveform.Sine, 100.0,
					minimum: -2.0, maximum: 2.0)], 0);
			document.MarkChanged(affectsAudio: true);
			((ILiveSynthSourceRevisionOutput)edited).EnqueueLiveSynthParameters(
				synthId, factory.PrepareLiveSynthSource(
					SongDocumentSnapshot.Create(document), synthId));
			float[] continuedControl = new float[13];
			float[] continuedEdited = new float[13];
			control.Render(13, continuedControl);
			edited.Render(13, continuedEdited);
			for (int i = 0; i < continuedControl.Length; i++)
				continuedEdited[i].Should().BeApproximately(
					continuedControl[i] * 2.0f, 1e-5f,
					"amplitude changes while the original phase continues");
		}
		finally
		{
			(control as IDisposable)?.Dispose();
			(edited as IDisposable)?.Dispose();
		}
	}


	[Test]
	public void CommittedEnvelopeEditsAndReferenceChangesReachAlreadyPlayingNotes()
	{
		SongDocument document = new();
		ObjectId envelopeId = document.AllocateObjectId();
		NodeEnvelopeDefinition envelope = QuarterEnvelope(envelopeId);
		document.Add(envelope);
		ObjectId synthId = document.AllocateObjectId();
		FmSynthDefinition synth = new(synthId, "Envelope",
			new FmSynthGraph([new FmEnvelopeNode(0, envelopeId)], 0));
		document.Add(synth);
		PlaybackRequestAudioSourceFactory factory =
			new(MonoConfiguration(1000));
		IAudioOutputSource output = factory.Create(AdHocPlaybackRequest.Create(
			document, new NoteScheduleBuilder().Freeze()));
		try
		{
			((ILiveAudioOutputSource)output).EnqueueLiveEvent(
				ChannelTarget.Virtual(80), [new StartNoteCommand(synthId)]);
			float[] pcm = new float[1];
			output.Render(1, pcm);
			pcm[0].Should().BeApproximately(0.25f, 1e-6f);

			// A shared curve edit commits without replacing the FM graph.
			envelope.ReplaceCurve([new EnvelopeNode(TimeSpan.Zero, 0.75)],
				0, 0, EnvelopeSustainLoopMode.Forward);
			document.MarkChanged(affectsAudio: true);
			((ILiveSynthSourceRevisionOutput)output).EnqueueLiveSynthParameters(
				synthId, factory.PrepareLiveSynthSource(
					SongDocumentSnapshot.Create(document), synthId));
			output.Render(1, pcm);
			pcm[0].Should().BeApproximately(0.75f, 1e-6f,
				"the held note must evaluate the revised shared curve");

			ObjectId otherId = document.AllocateObjectId();
			NodeEnvelopeDefinition other = new(otherId, "Other");
			other.ReplaceCurve([new EnvelopeNode(TimeSpan.Zero, 0.5)],
				0, 0, EnvelopeSustainLoopMode.Forward);
			document.Add(other);
			synth.Graph = new FmSynthGraph([new FmEnvelopeNode(0, otherId)], 0);
			document.MarkChanged(affectsAudio: true);
			((ILiveSynthSourceRevisionOutput)output).EnqueueLiveSynthParameters(
				synthId, factory.PrepareLiveSynthSource(
					SongDocumentSnapshot.Create(document), synthId));
			output.Render(1, pcm);
			pcm[0].Should().BeApproximately(0.5f, 1e-6f,
				"switching a curve reference preserves the existing note age");
		}
		finally
		{
			(output as IDisposable)?.Dispose();
		}
	}

	[Test]
	public void LiveMultiGainAndNestedOscillatorEditsPreserveConstituentPhases()
	{
		SongDocument document = new();
		ObjectId synthId = document.AllocateObjectId();
		FmSynthDefinition synth = new(synthId, "Multi", new FmSynthGraph([
			new FmOscillatorNode(0, FmOscillatorWaveform.Sine, 100.0),
			new FmMultiNode(1, inputNodeId: 0, detune: 0,
				voiceCount: 2, randomizePhase: false,
				gainCompensation: true)], 1));
		document.Add(synth);
		PlaybackRequestAudioSourceFactory factory =
			new(MonoConfiguration(1000));
		NoteSchedule empty = new NoteScheduleBuilder().Freeze();
		IAudioOutputSource control = factory.Create(
			AdHocPlaybackRequest.Create(document, empty));
		IAudioOutputSource edited = factory.Create(
			AdHocPlaybackRequest.Create(document, empty));
		try
		{
			foreach (IAudioOutputSource output in new[] { control, edited })
				((ILiveAudioOutputSource)output).EnqueueLiveEvent(
					ChannelTarget.Virtual(40), [new StartNoteCommand(synthId)]);
			float[] firstControl = new float[7];
			float[] firstEdited = new float[7];
			control.Render(7, firstControl);
			edited.Render(7, firstEdited);
			firstEdited.Should().Equal(firstControl);

			synth.Graph = new FmSynthGraph([
				new FmOscillatorNode(0, FmOscillatorWaveform.Sine, 100.0,
					minimum: -2.0, maximum: 2.0),
				new FmMultiNode(1, inputNodeId: 0, detune: 0,
					voiceCount: 2, randomizePhase: false,
					gainCompensation: false)], 1);
			document.MarkChanged(affectsAudio: true);
			((ILiveSynthSourceRevisionOutput)edited).EnqueueLiveSynthParameters(
				synthId, factory.PrepareLiveSynthSource(
					SongDocumentSnapshot.Create(document), synthId));
			float[] continuedControl = new float[13];
			float[] continuedEdited = new float[13];
			control.Render(13, continuedControl);
			edited.Render(13, continuedEdited);
			for (int i = 0; i < continuedControl.Length; i++)
				continuedEdited[i].Should().BeApproximately(
					continuedControl[i] * 4.0f, 1e-5f,
					"each existing constituent retains phase while its prepared sound changes");
		}
		finally
		{
			(control as IDisposable)?.Dispose();
			(edited as IDisposable)?.Dispose();
		}
	}

	[Test]
	public void EditedParentOperatorKeepsBoundSourceChildPlaybackPosition()
	{
		SongDocument document = new();
		ObjectId childId = document.AllocateObjectId();
		document.Add(new FmSynthDefinition(childId, "Child",
			new FmSynthGraph([
				new FmOscillatorNode(0, FmOscillatorWaveform.Sine, 100.0)], 0)));
		ObjectId parentId = document.AllocateObjectId();
		FmSynthDefinition parent = new(parentId, "Parent",
			new FmSynthGraph([
				new FmSoundSourceNode(1, childId, FmMultiNode.NeutralFrequencyHz),
				new FmConstantNode(2, 0.5),
				new FmOperatorNode(3, FmOperatorKind.Multiply, [1, 2])], 3));
		document.Add(parent);
		PlaybackRequestAudioSourceFactory factory =
			new(MonoConfiguration(1000));
		NoteSchedule empty = new NoteScheduleBuilder().Freeze();
		IAudioOutputSource control = factory.Create(
			AdHocPlaybackRequest.Create(document, empty));
		IAudioOutputSource edited = factory.Create(
			AdHocPlaybackRequest.Create(document, empty));
		try
		{
			foreach (IAudioOutputSource output in new[] { control, edited })
				((ILiveAudioOutputSource)output).EnqueueLiveEvent(
					ChannelTarget.Virtual(40), [new StartNoteCommand(parentId)]);
			float[] firstControl = new float[7];
			float[] firstEdited = new float[7];
			control.Render(7, firstControl);
			edited.Render(7, firstEdited);
			firstEdited.Should().Equal(firstControl);

			parent.Graph = new FmSynthGraph([
				new FmSoundSourceNode(1, childId, FmMultiNode.NeutralFrequencyHz),
				new FmConstantNode(2, 1.0),
				new FmOperatorNode(3, FmOperatorKind.Multiply, [1, 2])], 3);
			document.MarkChanged(affectsAudio: true);
			((ILiveSynthSourceRevisionOutput)edited).EnqueueLiveSynthParameters(
				parentId, factory.PrepareLiveSynthSource(
					SongDocumentSnapshot.Create(document), parentId));
			float[] nextControl = new float[13];
			float[] nextEdited = new float[13];
			control.Render(13, nextControl);
			edited.Render(13, nextEdited);
			for (int i = 0; i < nextControl.Length; i++)
				nextEdited[i].Should().BeApproximately(
					nextControl[i] * 2.0f, 1e-5f,
					"the bound Source child continues at its original phase");
		}
		finally
		{
			(control as IDisposable)?.Dispose();
			(edited as IDisposable)?.Dispose();
		}
	}

	[Test]
	public void FilterHistoriesSurviveUpstreamEditsWhenConfigurationIsUnchanged()
	{
		SongDocument document = new();
		ObjectId filterId = document.AllocateObjectId();
		FilterDefinition definition = new(filterId, "Lowpass");
		document.Add(definition);
		ObjectId synthId = document.AllocateObjectId();
		FmSynthDefinition synth = new(synthId, "Filtered",
			new FmSynthGraph([
				new FmOscillatorNode(0, FmOscillatorWaveform.Sine, 440.0),
				new FmFilterNode(1, 0, new FilterInstance(definition))], 1));
		document.Add(synth);
		PlaybackRequestAudioSourceFactory factory =
			new(MonoConfiguration(48000));
		NoteSchedule empty = new NoteScheduleBuilder().Freeze();
		IAudioOutputSource control = factory.Create(
			AdHocPlaybackRequest.Create(document, empty));
		IAudioOutputSource edited = factory.Create(
			AdHocPlaybackRequest.Create(document, empty));
		try
		{
			foreach (IAudioOutputSource output in new[] { control, edited })
				((ILiveAudioOutputSource)output).EnqueueLiveEvent(
					ChannelTarget.Virtual(10), [new StartNoteCommand(synthId)]);
			float[] firstControl = new float[70];
			float[] firstEdited = new float[70];
			control.Render(70, firstControl);
			edited.Render(70, firstEdited);
			firstEdited.Should().Equal(firstControl);

			// The changed TailSeconds must not reconstruct IIR state.
			synth.Graph = new FmSynthGraph([
				new FmOscillatorNode(0, FmOscillatorWaveform.Sine, 440.0),
				new FmFilterNode(1, 0, new FilterInstance(definition),
					tailSeconds: 0.1)], 1);
			document.MarkChanged(affectsAudio: true);
			((ILiveSynthSourceRevisionOutput)edited).EnqueueLiveSynthParameters(
				synthId, factory.PrepareLiveSynthSource(
					SongDocumentSnapshot.Create(document), synthId));
			float[] nextControl = new float[75];
			float[] nextEdited = new float[75];
			control.Render(75, nextControl);
			edited.Render(75, nextEdited);
			for (int i = 0; i < nextControl.Length; i++)
				nextEdited[i].Should().BeApproximately(
					nextControl[i], 1e-6f,
					"Filter state and upstream oscillator phase must remain continuous");
		}
		finally
		{
			(control as IDisposable)?.Dispose();
			(edited as IDisposable)?.Dispose();
		}
	}

	[Test]
	public void LiveSourceFrequencyAndConstantEditsPreserveSamplePosition()
	{
		SongDocument document = new();
		ObjectId sampleId = AddSample(document, "Ramp");
		ObjectId synthId = document.AllocateObjectId();
		FmSynthDefinition synth = new(synthId, "Source",
			new FmSynthGraph([
				new FmSoundSourceNode(0, sampleId,
					FmMultiNode.NeutralFrequencyHz)], 0));
		document.Add(synth);
		float[] ramp = Enumerable.Range(0, 30)
			.Select(i => i / 20.0f).ToArray();
		PlaybackRequestAudioSourceFactory factory = new(
			MonoConfiguration(1000),
			new RecordingSampleProvider(new MemorySampleData(
				1000, 1, ramp)));
		IAudioOutputSource output = factory.Create(AdHocPlaybackRequest.Create(
			document, new NoteScheduleBuilder().Freeze()));
		try
		{
			((ILiveAudioOutputSource)output).EnqueueLiveEvent(
				ChannelTarget.Virtual(51), [new StartNoteCommand(synthId)]);
			float[] before = new float[6];
			output.Render(before.Length, before);
			for (int i = 0; i < before.Length; i++)
				before[i].Should().BeApproximately(i / 20.0f, 1e-6f);

			// Double the rate without changing the source's base pitch.
			// Frame 6 must read sample 6, NOT retroactively jump to 12.
			synth.Graph = new FmSynthGraph([
				new FmSoundSourceNode(0, sampleId,
					FmMultiNode.NeutralFrequencyHz * 2)], 0);
			document.MarkChanged(affectsAudio: true);
			((ILiveSynthSourceRevisionOutput)output).EnqueueLiveSynthParameters(
				synthId, factory.PrepareLiveSynthSource(
					SongDocumentSnapshot.Create(document), synthId));
			float[] doubled = new float[3];
			output.Render(doubled.Length, doubled);
			doubled[0].Should().BeApproximately(6 / 20.0f, 1e-6f);
			doubled[1].Should().BeApproximately(8 / 20.0f, 1e-6f);
			doubled[2].Should().BeApproximately(10 / 20.0f, 1e-6f);

			// Constant mode ignores the parent pitch multiplier. With
			// a neutral note, the current rate remains 2x; the position
			// must still continue from the same phase.
			synth.Graph = new FmSynthGraph([
				new FmSoundSourceNode(0, sampleId,
					FmMultiNode.NeutralFrequencyHz * 2,
					constant: true)], 0);
			document.MarkChanged(affectsAudio: true);
			((ILiveSynthSourceRevisionOutput)output).EnqueueLiveSynthParameters(
				synthId, factory.PrepareLiveSynthSource(
					SongDocumentSnapshot.Create(document), synthId));
			float[] continued = new float[2];
			output.Render(2, continued);
			continued[0].Should().BeApproximately(12 / 20.0f, 1e-6f);
			continued[1].Should().BeApproximately(14 / 20.0f, 1e-6f);
		}
		finally
		{
			(output as IDisposable)?.Dispose();
		}
	}

	[Test]
	public void LiveSourceConstantToggleRetunesBentNoteWithoutRewindingSample()
	{
		SongDocument document = new();
		ObjectId sampleId = AddSample(document, "Ramp");
		ObjectId synthId = document.AllocateObjectId();
		FmSynthDefinition synth = new(synthId, "Source",
			new FmSynthGraph([
				new FmSoundSourceNode(0, sampleId,
					FmMultiNode.NeutralFrequencyHz)], 0));
		document.Add(synth);
		PlaybackRequestAudioSourceFactory factory = new(
			MonoConfiguration(1000),
			new RecordingSampleProvider(new MemorySampleData(1000, 1,
				Enumerable.Range(0, 30).Select(i => i / 20.0f).ToArray())));
		IAudioOutputSource output = factory.Create(AdHocPlaybackRequest.Create(
			document, new NoteScheduleBuilder().Freeze()));
		try
		{
			((ILiveAudioOutputSource)output).EnqueueLiveEvent(
				ChannelTarget.Virtual(52),
				[new StartNoteCommand(synthId, PitchMultiplier: 2.0)]);
			float[] before = new float[3];
			output.Render(3, before);
			before[0].Should().BeApproximately(0, 1e-6f);
			before[1].Should().BeApproximately(2 / 20.0f, 1e-6f);
			before[2].Should().BeApproximately(4 / 20.0f, 1e-6f);

			synth.Graph = new FmSynthGraph([
				new FmSoundSourceNode(0, sampleId,
					FmMultiNode.NeutralFrequencyHz, constant: true)], 0);
			document.MarkChanged(affectsAudio: true);
			((ILiveSynthSourceRevisionOutput)output).EnqueueLiveSynthParameters(
				synthId, factory.PrepareLiveSynthSource(
					SongDocumentSnapshot.Create(document), synthId));
			float[] after = new float[3];
			output.Render(3, after);
			after[0].Should().BeApproximately(6 / 20.0f, 1e-6f);
			after[1].Should().BeApproximately(7 / 20.0f, 1e-6f);
			after[2].Should().BeApproximately(8 / 20.0f, 1e-6f);
		}
		finally
		{
			(output as IDisposable)?.Dispose();
		}
	}


	[Test]
	public void AuthoredOnePoleCutoffEditMatchesRealtimeFmRetuneOfSameHeldHistory()
	{
		SongDocument document = new();
		ObjectId filterId = document.AllocateObjectId();
		FilterDefinition filter = new(filterId, "Lowpass");
		filter.SetStageDefaults(1, [new("CutoffHz",
			FilterParameterValue.FromNumber(250))]);
		document.Add(filter);
		ObjectId authoredId = document.AllocateObjectId();
		FmSynthDefinition authored = new(authoredId, "Authored",
			new FmSynthGraph([
				new FmOscillatorNode(0, FmOscillatorWaveform.Sine, 440),
				new FmFilterNode(1, 0, new FilterInstance(filter))], 1));
		document.Add(authored);

		ObjectId referenceId = document.AllocateObjectId();
		FmFilterNode connected = new FmFilterNode(1, 0,
			new FilterInstance(filter));
		connected = connected.WithParameterConnection(
			connected.GetPorts().Single(p => p.ParameterName == "CutoffHz"), 2);
		FmSynthDefinition reference = new(referenceId, "Reference",
			new FmSynthGraph([
				new FmOscillatorNode(0, FmOscillatorWaveform.Sine, 440),
				connected,
				new FmConstantNode(2, 250)], 1));
		document.Add(reference);
		PlaybackRequestAudioSourceFactory factory =
			new(MonoConfiguration(48000));
		NoteSchedule empty = new NoteScheduleBuilder().Freeze();
		IAudioOutputSource authoredOutput = factory.Create(
			AdHocPlaybackRequest.Create(document, empty));
		IAudioOutputSource referenceOutput = factory.Create(
			AdHocPlaybackRequest.Create(document, empty));
		try
		{
			((ILiveAudioOutputSource)authoredOutput).EnqueueLiveEvent(
				ChannelTarget.Virtual(1), [new StartNoteCommand(authoredId)]);
			((ILiveAudioOutputSource)referenceOutput).EnqueueLiveEvent(
				ChannelTarget.Virtual(1), [new StartNoteCommand(referenceId)]);
			float[] beforeA = new float[140];
			float[] beforeB = new float[140];
			authoredOutput.Render(beforeA.Length, beforeA);
			referenceOutput.Render(beforeB.Length, beforeB);
			for (int i = 0; i < beforeA.Length; i++)
				beforeA[i].Should().BeApproximately(beforeB[i], 1e-5f);

			// Both voices have identical history; the reference changes its
			// connected FM numeric cutoff and uses the existing per-frame
			// atomic realtime retune. The authored edit must match it.
			FilterInstance revision = new(filter);
			revision.SetStageParameter(1, "CutoffHz",
				FilterParameterValue.FromNumber(4000));
			authored.Graph = new FmSynthGraph([
				new FmOscillatorNode(0, FmOscillatorWaveform.Sine, 440),
				new FmFilterNode(1, 0, revision)], 1);
			reference.Graph = new FmSynthGraph([
				new FmOscillatorNode(0, FmOscillatorWaveform.Sine, 440),
				connected, new FmConstantNode(2, 4000)], 1);
			document.MarkChanged(affectsAudio: true);
			foreach ((IAudioOutputSource output, ObjectId id) in
				new[] { (authoredOutput, authoredId),
					(referenceOutput, referenceId) })
				((ILiveSynthSourceRevisionOutput)output).EnqueueLiveSynthParameters(
					id, factory.PrepareLiveSynthSource(
						SongDocumentSnapshot.Create(document), id));
			float[] afterA = new float[170];
			float[] afterB = new float[170];
			authoredOutput.Render(afterA.Length, afterA);
			referenceOutput.Render(afterB.Length, afterB);
			for (int i = 0; i < afterA.Length; i++)
				afterA[i].Should().BeApproximately(afterB[i], 1e-5f,
					"authored coefficients must use the same live IIR history");
		}
		finally
		{
			(authoredOutput as IDisposable)?.Dispose();
			(referenceOutput as IDisposable)?.Dispose();
		}
	}

	[Test]
	public void CommittedFirCutoffUsesOffThreadPreparedPlanOnTheHeldVoice()
	{
		SongDocument document = new();
		ObjectId filterId = document.AllocateObjectId();
		FilterDefinition definition = new(filterId, "FIR", [
			new FilterStageDefinition(1, "lowpass", FilterKind.FirLowPass,
				[new("CutoffHz", FilterParameterValue.FromNumber(250))])]);
		document.Add(definition);
		ObjectId synthId = document.AllocateObjectId();
		FmSynthDefinition synth = new(synthId, "FIR Synth",
			new FmSynthGraph([
				new FmOscillatorNode(0, FmOscillatorWaveform.Sine, 2000,
					constant: true),
				new FmFilterNode(1, 0, new FilterInstance(definition))], 1));
		document.Add(synth);
		PlaybackRequestAudioSourceFactory factory =
			new(MonoConfiguration(48000));
		NoteSchedule empty = new NoteScheduleBuilder().Freeze();
		IAudioOutputSource edited = factory.Create(
			AdHocPlaybackRequest.Create(document, empty));
		IAudioOutputSource unchanged = factory.Create(
			AdHocPlaybackRequest.Create(document, empty));
		try
		{
			foreach (IAudioOutputSource output in new[] { edited, unchanged })
				((ILiveAudioOutputSource)output).EnqueueLiveEvent(
					ChannelTarget.Virtual(30), [new StartNoteCommand(synthId)]);
			float[] firstA = new float[350];
			float[] firstB = new float[350];
			edited.Render(firstA.Length, firstA);
			unchanged.Render(firstB.Length, firstB);
			firstA.Should().Equal(firstB);

			FilterInstance revised = new(definition);
			revised.SetStageParameter(1, "CutoffHz",
				FilterParameterValue.FromNumber(4000));
			synth.Graph = new FmSynthGraph([
				new FmOscillatorNode(0, FmOscillatorWaveform.Sine, 2000,
					constant: true),
				new FmFilterNode(1, 0, revised)], 1);
			document.MarkChanged(affectsAudio: true);
			((ILiveSynthSourceRevisionOutput)edited).EnqueueLiveSynthParameters(
				synthId, factory.PrepareLiveSynthSource(
					SongDocumentSnapshot.Create(document), synthId));
			float[] afterA = new float[550];
			float[] afterB = new float[550];
			edited.Render(afterA.Length, afterA);
			unchanged.Render(afterB.Length, afterB);
			afterA.Should().OnlyContain(sample => float.IsFinite(sample));
			float difference = Enumerable.Range(200, afterA.Length - 200)
				.Max(i => Math.Abs(afterA[i] - afterB[i]));
			difference.Should().BeGreaterThan(0.15f,
				"the prepared FIR cutoff must change the sound of the existing voice");
		}
		finally
		{
			(edited as IDisposable)?.Dispose();
			(unchanged as IDisposable)?.Dispose();
		}
	}


	[Test]
	public void TwoAuthoredFilterStagesAdoptTogetherWithPreservedIirHistories()
	{
		SongDocument song = new();
		ObjectId filterId = song.AllocateObjectId();
		FilterDefinition definition = new(filterId, "Serial", [
			new FilterStageDefinition(1, "first", FilterKind.OnePole,
				[new("CutoffHz", FilterParameterValue.FromNumber(200))]),
			new FilterStageDefinition(2, "second", FilterKind.OnePole,
				[new("CutoffHz", FilterParameterValue.FromNumber(300))])]);
		song.Add(definition);
		ObjectId authoredId = song.AllocateObjectId();
		FmSynthDefinition authored = new(authoredId, "Authored", new FmSynthGraph([
			new FmOscillatorNode(0, FmOscillatorWaveform.Sine, 1500),
			new FmFilterNode(1, 0, new FilterInstance(definition))], 1));
		song.Add(authored);

		FmFilterNode controlled = new(1, 0, new FilterInstance(definition));
		controlled = controlled.WithParameterConnection(
			controlled.GetPorts().Single(p => p.StageId == 1
				&& p.ParameterName == "CutoffHz"), 2);
		controlled = controlled.WithParameterConnection(
			controlled.GetPorts().Single(p => p.StageId == 2
				&& p.ParameterName == "CutoffHz"), 3);
		ObjectId controlId = song.AllocateObjectId();
		FmSynthDefinition reference = new(controlId, "FM Reference",
			new FmSynthGraph([
				new FmOscillatorNode(0, FmOscillatorWaveform.Sine, 1500),
				controlled,
				new FmConstantNode(2, 200),
				new FmConstantNode(3, 300)], 1));
		song.Add(reference);

		PlaybackRequestAudioSourceFactory factory = new(MonoConfiguration(48000));
		NoteSchedule empty = new NoteScheduleBuilder().Freeze();
		IAudioOutputSource edited = factory.Create(AdHocPlaybackRequest.Create(song, empty));
		IAudioOutputSource control = factory.Create(AdHocPlaybackRequest.Create(song, empty));
		try
		{
			((ILiveAudioOutputSource)edited).EnqueueLiveEvent(ChannelTarget.Virtual(61),
				[new StartNoteCommand(authoredId)]);
			((ILiveAudioOutputSource)control).EnqueueLiveEvent(ChannelTarget.Virtual(61),
				[new StartNoteCommand(controlId)]);
			float[] beforeA = new float[190], beforeB = new float[190];
			edited.Render(beforeA.Length, beforeA);
			control.Render(beforeB.Length, beforeB);
			for (int i = 0; i < beforeA.Length; i++)
				beforeA[i].Should().BeApproximately(beforeB[i], 1e-5f);

			FilterInstance changes = new(definition);
			changes.SetStageParameter(1, "CutoffHz",
				FilterParameterValue.FromNumber(2500));
			changes.SetStageParameter(2, "CutoffHz",
				FilterParameterValue.FromNumber(4500));
			authored.Graph = new FmSynthGraph([
				new FmOscillatorNode(0, FmOscillatorWaveform.Sine, 1500),
				new FmFilterNode(1, 0, changes)], 1);
			reference.Graph = new FmSynthGraph([
				new FmOscillatorNode(0, FmOscillatorWaveform.Sine, 1500),
				controlled, new FmConstantNode(2, 2500),
				new FmConstantNode(3, 4500)], 1);
			song.MarkChanged(affectsAudio: true);
			foreach ((IAudioOutputSource output, ObjectId synth) in
				new[] { (edited, authoredId), (control, controlId) })
				((ILiveSynthSourceRevisionOutput)output).EnqueueLiveSynthParameters(
					synth, factory.PrepareLiveSynthSource(
						SongDocumentSnapshot.Create(song), synth));
			float[] afterA = new float[220], afterB = new float[220];
			edited.Render(afterA.Length, afterA);
			control.Render(afterB.Length, afterB);
			for (int i = 0; i < afterA.Length; i++)
				afterA[i].Should().BeApproximately(afterB[i], 1e-5f,
					"both serial coefficients must adopt at the same PCM boundary");
		}
		finally
		{
			(edited as IDisposable)?.Dispose();
			(control as IDisposable)?.Dispose();
		}
	}

	[Test]
	public void NestedMultiConstituentsAtomicallyAdoptTwoFilterProcessors()
	{
		SongDocument song = new();
		ObjectId firstId = song.AllocateObjectId();
		FilterDefinition first = new(firstId, "First", [
			new FilterStageDefinition(1, "first", FilterKind.OnePole,
				[new("CutoffHz", FilterParameterValue.FromNumber(200))])]);
		song.Add(first);
		ObjectId secondId = song.AllocateObjectId();
		FilterDefinition second = new(secondId, "Second", [
			new FilterStageDefinition(1, "second", FilterKind.OnePole,
				[new("CutoffHz", FilterParameterValue.FromNumber(350))])]);
		song.Add(second);
		ObjectId authoredId = song.AllocateObjectId();
		FmMultiNode multi = new(4, inputNodeId: 3, detune: 0,
			voiceCount: 2, randomizePhase: false, gainCompensation: true);
		FmSynthDefinition authored = new(authoredId, "Multi", new FmSynthGraph([
			new FmOscillatorNode(0, FmOscillatorWaveform.Sine, 1100),
			new FmFilterNode(2, 0, new FilterInstance(first)),
			new FmFilterNode(3, 2, new FilterInstance(second)),
			multi], 4));
		song.Add(authored);
		FmFilterNode controlledFirst = new(2, 0, new FilterInstance(first));
		controlledFirst = controlledFirst.WithParameterConnection(
			controlledFirst.GetPorts().Single(p => p.ParameterName == "CutoffHz"), 5);
		FmFilterNode controlledSecond = new(3, 2, new FilterInstance(second));
		controlledSecond = controlledSecond.WithParameterConnection(
			controlledSecond.GetPorts().Single(p => p.ParameterName == "CutoffHz"), 6);
		ObjectId controlId = song.AllocateObjectId();
		FmSynthDefinition reference = new(controlId, "Multi Reference",
			new FmSynthGraph([
				new FmOscillatorNode(0, FmOscillatorWaveform.Sine, 1100),
				controlledFirst, controlledSecond, multi,
				new FmConstantNode(5, 200), new FmConstantNode(6, 350)], 4));
		song.Add(reference);

		PlaybackRequestAudioSourceFactory factory = new(MonoConfiguration(48000));
		NoteSchedule empty = new NoteScheduleBuilder().Freeze();
		IAudioOutputSource edited = factory.Create(AdHocPlaybackRequest.Create(song, empty));
		IAudioOutputSource control = factory.Create(AdHocPlaybackRequest.Create(song, empty));
		try
		{
			((ILiveAudioOutputSource)edited).EnqueueLiveEvent(ChannelTarget.Virtual(61),
				[new StartNoteCommand(authoredId)]);
			((ILiveAudioOutputSource)control).EnqueueLiveEvent(ChannelTarget.Virtual(61),
				[new StartNoteCommand(controlId)]);
			float[] beforeA = new float[170], beforeB = new float[170];
			edited.Render(beforeA.Length, beforeA);
			control.Render(beforeB.Length, beforeB);
			for (int i = 0; i < beforeA.Length; i++)
				beforeA[i].Should().BeApproximately(beforeB[i], 1e-5f);
			FilterInstance revisedFirst = new(first);
			revisedFirst.SetStageParameter(1, "CutoffHz",
				FilterParameterValue.FromNumber(3200));
			FilterInstance revisedSecond = new(second);
			revisedSecond.SetStageParameter(1, "CutoffHz",
				FilterParameterValue.FromNumber(4500));
			authored.Graph = new FmSynthGraph([
				new FmOscillatorNode(0, FmOscillatorWaveform.Sine, 1100),
				new FmFilterNode(2, 0, revisedFirst),
				new FmFilterNode(3, 2, revisedSecond), multi], 4);
			reference.Graph = new FmSynthGraph([
				new FmOscillatorNode(0, FmOscillatorWaveform.Sine, 1100),
				controlledFirst, controlledSecond, multi,
				new FmConstantNode(5, 3200), new FmConstantNode(6, 4500)], 4);
			song.MarkChanged(affectsAudio: true);
			foreach ((IAudioOutputSource output, ObjectId synth) in
				new[] { (edited, authoredId), (control, controlId) })
				((ILiveSynthSourceRevisionOutput)output).EnqueueLiveSynthParameters(
					synth, factory.PrepareLiveSynthSource(
						SongDocumentSnapshot.Create(song), synth));
			float[] afterA = new float[180], afterB = new float[180];
			edited.Render(afterA.Length, afterA);
			control.Render(afterB.Length, afterB);
			for (int i = 0; i < afterA.Length; i++)
				afterA[i].Should().BeApproximately(afterB[i], 1e-5f,
					"every nested constituent must retain both Filter histories");
		}
		finally
		{
			(edited as IDisposable)?.Dispose();
			(control as IDisposable)?.Dispose();
		}
	}

	[Test]
	public void RejectedSecondFilterUpdateLeavesFirstFilterAndOldVoiceUntouched()
	{
		SongDocument song = new();
		ObjectId firstId = song.AllocateObjectId();
		FilterDefinition first = new(firstId, "First", [
			new FilterStageDefinition(1, "one", FilterKind.OnePole,
				[new("CutoffHz", FilterParameterValue.FromNumber(100))])]);
		song.Add(first);
		ObjectId secondId = song.AllocateObjectId();
		FilterDefinition second = new(secondId, "Second", [
			new FilterStageDefinition(1, "two", FilterKind.OnePole,
				[new("CutoffHz", FilterParameterValue.FromNumber(120))])]);
		song.Add(second);
		ObjectId synthId = song.AllocateObjectId();
		FmSynthDefinition synth = new(synthId, "Cascade", new FmSynthGraph([
			new FmOscillatorNode(0, FmOscillatorWaveform.Sine, 80),
			new FmFilterNode(1, 0, new FilterInstance(first)),
			new FmFilterNode(2, 1, new FilterInstance(second))], 2));
		song.Add(synth);
		PlaybackRequestAudioSourceFactory factory = new(MonoConfiguration(1000));
		NoteSchedule empty = new NoteScheduleBuilder().Freeze();
		IAudioOutputSource edited = factory.Create(AdHocPlaybackRequest.Create(song, empty));
		IAudioOutputSource unchanged = factory.Create(AdHocPlaybackRequest.Create(song, empty));
		try
		{
			foreach (IAudioOutputSource output in new[] { edited, unchanged })
				((ILiveAudioOutputSource)output).EnqueueLiveEvent(
					ChannelTarget.Virtual(70), [new StartNoteCommand(synthId)]);
			float[] beforeA = new float[80], beforeB = new float[80];
			edited.Render(beforeA.Length, beforeA);
			unchanged.Render(beforeB.Length, beforeB);
			beforeA.Should().Equal(beforeB);

			FilterInstance firstChange = new(first);
			firstChange.SetStageParameter(1, "CutoffHz",
				FilterParameterValue.FromNumber(250));
			FilterInstance invalidSecond = new(second);
			// Valid as an authored parameter, but above the 1000-Hz
			// output's Nyquist frequency and therefore not admissible.
			invalidSecond.SetStageParameter(1, "CutoffHz",
				FilterParameterValue.FromNumber(4000));
			synth.Graph = new FmSynthGraph([
				new FmOscillatorNode(0, FmOscillatorWaveform.Sine, 80),
				new FmFilterNode(1, 0, firstChange),
				new FmFilterNode(2, 1, invalidSecond)], 2);
			song.MarkChanged(affectsAudio: true);
			((ILiveSynthSourceRevisionOutput)edited).EnqueueLiveSynthParameters(
				synthId, factory.PrepareLiveSynthSource(
					SongDocumentSnapshot.Create(song), synthId));
			float[] afterA = new float[140], afterB = new float[140];
			edited.Render(afterA.Length, afterA);
			unchanged.Render(afterB.Length, afterB);
			afterA.Should().Equal(afterB,
				"a later invalid coefficient must not partially commit an earlier Filter");
		}
		finally
		{
			(edited as IDisposable)?.Dispose();
			(unchanged as IDisposable)?.Dispose();
		}
	}

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

	private static NodeEnvelopeDefinition QuarterEnvelope(ObjectId id)
	{
		NodeEnvelopeDefinition node = new(id, "Quarter");
		node.ReplaceCurve([new EnvelopeNode(TimeSpan.Zero, 0.25)],
			0, 0, EnvelopeSustainLoopMode.Forward);
		return node;
	}

	[Test]
	public void InstrumentAndNodeEnvelopeResolveFromSnapshot()
	{
		SongDocument document = new();
		ObjectId sampleId =
			AddSample(document, "Sample");

		ObjectId envelopeId =
			document.AllocateObjectId();
		document.Add(
			QuarterEnvelope(envelopeId));

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
