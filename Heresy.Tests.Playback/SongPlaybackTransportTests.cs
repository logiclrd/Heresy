using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using AwesomeAssertions;

using Heresy.Core.Envelopes;
using Heresy.Core.Filters;
using Heresy.Core.Diagnostics;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;
using Heresy.Playback;
using Heresy.Render.Realtime;
using Heresy.Render.Sounds;

using NUnit.Framework;

namespace Heresy.Tests.Playback;

[TestFixture]
public sealed class SongPlaybackTransportTests
{
	[Test]
	public async Task FaultyDiagnosticsSubscriberCannotInterruptPlaybackOrOtherSubscribers()
	{
		SongDocument document = new();
		ObjectId id = document.AllocateObjectId();
		document.Add(new DataPatternDefinition(id, "Test"));
		using SongPlaybackTransport transport =
			new(new TestBackend(), new DiagnosticFactory());
		List<PlaybackRuntimeDiagnosticsEventArgs> received = [];
		((IPlaybackRuntimeDiagnosticsTransport)transport).RuntimeDiagnostics +=
			(_, _) => throw new InvalidOperationException("Observer failure");
		((IPlaybackRuntimeDiagnosticsTransport)transport).RuntimeDiagnostics +=
			(_, e) => received.Add(e);

		await transport.PlayPatternAsync(document, id);
		received.Should().ContainSingle();
	}

	[Test]
	public async Task RuntimeDiagnosticsArePublishedFromPreparedSourceAfterPlay()
	{
		SongDocument document = new();
		ObjectId id = document.AllocateObjectId();
		document.Add(new DataPatternDefinition(id, "Test"));

		DiagnosticFactory factory = new();
		using SongPlaybackTransport transport = new(new TestBackend(), factory);
		List<PlaybackRuntimeDiagnosticsEventArgs> events = [];
		((IPlaybackRuntimeDiagnosticsTransport)transport).RuntimeDiagnostics +=
			(_, e) => events.Add(e);

		await transport.PlayPatternAsync(document, id);
		events.Should().ContainSingle();
		events[0].Diagnostics.Should().ContainSingle();
		events[0].Diagnostics[0].Code.Should().Be("HRSEQ001");
		events[0].Diagnostics[0].Row.Should().Be(2);

		await transport.PlayPatternAsync(document, id);
		events.Should().HaveCount(2);
		events[1].Diagnostics.Should().ContainSingle();
		events[1].Diagnostics[0].Row.Should().Be(2);
	}

	private sealed class DiagnosticFactory :
		IBackgroundPlaybackSourceFactory,
		IPlaybackRuntimeDiagnosticReportProvider
	{
		private readonly Dictionary<PlaybackRequest, SequencingDiagnostic[]>
			_pending = new(ReferenceEqualityComparer.Instance);

		public IAudioOutputSource Create(PlaybackRequest request)
		{
			_pending[request] = [
				new SequencingDiagnostic("HRSEQ001",
					"Dropped out-of-order Pattern note at row 2", 2, 4),
			];
			return new SilentSource();
		}

		public bool TryTakeRuntimeDiagnostics(
			PlaybackRequest request,
			out SequencingDiagnostic[] diagnostics)
		{
			if (_pending.Remove(request, out SequencingDiagnostic[]? found))
			{
				diagnostics = found;
				return true;
			}
			diagnostics = [];
			return false;
		}
	}

	[Test]
	public async Task PlaySongTargetsCurrentRootSequence()
	{
		SongDocument document = new();
		ObjectId rootId = document.AllocateObjectId();
		document.Add(
			new Heresy.Core.Sequences.DataSequenceDefinition(
				rootId,
				"Root"));
		document.RootSequenceId = rootId;
		RecordingFactory factory = new();
		TestBackend backend = new();
		using SongPlaybackTransport transport =
			new(backend, factory);

		await transport.PlaySongAsync(document);

		factory.Requests.Should().ContainSingle()
			.Which.Should()
			.BeOfType<SequencePlaybackRequest>()
			.Which.SequenceId.Should().Be(rootId);
	}

	[Test]
	public async Task PatternRequestCarriesStartAndRepeat()
	{
		SongDocument document = new();
		ObjectId patternId = document.AllocateObjectId();
		document.Add(
			new DataPatternDefinition(
				patternId,
				"Pattern"));
		RecordingFactory factory = new();
		TestBackend backend = new();
		using SongPlaybackTransport transport =
			new(backend, factory);

		await transport.PlayPatternAsync(
			document,
			patternId,
			startRow: 12,
			repeat: true);

		PatternPlaybackRequest request =
			factory.Requests.Should().ContainSingle()
				.Which.Should()
				.BeOfType<PatternPlaybackRequest>()
				.Which;
		request.PatternId.Should().Be(patternId);
		request.StartRow.Should().Be(12);
		request.Repeat.Should().BeTrue();
	}

	[Test]
	public async Task OrdinaryAdHocPlaybackDoesNotReserveSynthMultiHeadroom()
	{
		SongDocument document = new();
		RecordingFactory factory = new();
		TestBackend backend = new();
		using SongPlaybackTransport transport = new(backend, factory);

		await transport.PlayAdHocAsync(document, new NoteScheduleBuilder().Freeze());

		factory.Requests.Should().ContainSingle()
			.Which.Should().BeOfType<AdHocPlaybackRequest>()
			.Which.ReserveSynthMultiHeadroom.Should().BeFalse(
				"ordinary ad-hoc playback must not reserve latent Multi voices");
		backend.OpenCount.Should().Be(1);
	}

	[Test]
	public async Task LiveAuditionStartsOneSessionAndQueuesStartThenNoteOff()
	{
		SongDocument document = new();
		LiveFactory factory = new();
		TestBackend backend = new();
		using SongPlaybackTransport transport =
			new(backend, factory);

		await transport.SendLiveEventAsync(
			document,
			ChannelTarget.Physical(3),
			[new StartNoteCommand((ObjectId)17U)]);
		await transport.SendLiveEventAsync(
			document,
			ChannelTarget.Physical(3),
			[new NoteOffCommand()]);

		factory.Source.Events.Should().HaveCount(2);
		factory.Source.Events[0].Target.Should().Be(
			ChannelTarget.Physical(3));
		factory.Source.Events[0].Commands.Should().ContainSingle()
			.Which.Should().Be(
				new StartNoteCommand((ObjectId)17U));
		factory.Source.Events[1].Target.Should().Be(
			ChannelTarget.Physical(3));
		factory.Source.Events[1].Commands.Should().ContainSingle()
			.Which.Should().BeOfType<NoteOffCommand>();
		backend.OpenCount.Should().Be(1);
	}


	[Test]
	public async Task LiveEventsEnsureOneSessionAndPreserveRequestedTargets()
	{
		SongDocument document = new();
		LiveFactory factory = new();
		TestBackend backend = new();
		using SongPlaybackTransport transport =
			new(backend, factory);

		await transport.SendLiveEventAsync(
			document,
			ChannelTarget.Physical(3),
			[new StartNoteCommand((ObjectId)17U)]);
		await transport.SendLiveEventAsync(
			document,
			ChannelTarget.Virtual(42),
			[new StartNoteCommand((ObjectId)18U)]);

		backend.OpenCount.Should().Be(1);
		factory.Source.Events.Should().HaveCount(2);
		factory.Source.Events[0].Target.Should().Be(
			ChannelTarget.Physical(3));
		factory.Source.Events[1].Target.Should().Be(
			ChannelTarget.Virtual(42));
	}

	[Test]
	public async Task LiveEventAfterStopStartsFreshSession()
	{
		SongDocument document = new();
		LiveFactory factory = new();
		TestBackend backend = new();
		using SongPlaybackTransport transport =
			new(backend, factory);

		await transport.SendLiveEventAsync(
			document,
			ChannelTarget.Physical(0),
			[new StartNoteCommand((ObjectId)17U)]);
		await transport.StopAsync();
		await transport.SendLiveEventAsync(
			document,
			ChannelTarget.Physical(0),
			[new StartNoteCommand((ObjectId)17U)]);

		backend.OpenCount.Should().Be(2);
	}


	[Test]
	public async Task NewLiveNoteAfterAudioEditRefreshesPlaybackSnapshot()
	{
		SongDocument document = new();
		LiveFactory factory = new();
		TestBackend backend = new();
		using SongPlaybackTransport transport = new(backend, factory);

		await transport.SendLiveEventAsync(
			document,
			ChannelTarget.Virtual(42),
			[new StartNoteCommand((ObjectId)17U)]);
		document.MarkChanged(affectsAudio: true);

		// Releasing the running note must not restart its old session.
		await transport.SendLiveEventAsync(
			document,
			ChannelTarget.Virtual(42),
			[new NoteOffCommand()]);
		backend.OpenCount.Should().Be(1);

		await transport.SendLiveEventAsync(
			document,
			ChannelTarget.Virtual(42),
			[new StartNoteCommand((ObjectId)17U)]);
		backend.OpenCount.Should().Be(2);
	}

	[Test]
	public async Task LiveAuditionReusesSnapshotAcrossLayoutChangesOnly()
	{
		SongDocument document = new();
		LiveFactory factory = new();
		TestBackend backend = new();
		using SongPlaybackTransport transport = new(backend, factory);

		await transport.SendLiveEventAsync(
			document,
			ChannelTarget.Virtual(42),
			[new StartNoteCommand((ObjectId)17U)]);
		document.MarkChanged(affectsAudio: false);
		await transport.SendLiveEventAsync(
			document,
			ChannelTarget.Virtual(43),
			[new StartNoteCommand((ObjectId)17U)]);
		backend.OpenCount.Should().Be(1);
	}

	[Test]
	public async Task LiveAuditionOnDifferentDocumentUsesFreshSnapshot()
	{
		SongDocument first = new();
		SongDocument second = new();
		LiveFactory factory = new();
		TestBackend backend = new();
		using SongPlaybackTransport transport = new(backend, factory);

		await transport.SendLiveEventAsync(
			first,
			ChannelTarget.Virtual(42),
			[new StartNoteCommand((ObjectId)17U)]);
		await transport.SendLiveEventAsync(
			second,
			ChannelTarget.Virtual(42),
			[new StartNoteCommand((ObjectId)17U)]);
		backend.OpenCount.Should().Be(2);
	}


	[Test]
	public async Task NewSynthNoteAfterAudioEditPublishesWithoutReopeningSession()
	{
		SongDocument document = new();
		ObjectId synthId = document.AllocateObjectId();
		FmSynthDefinition synth = new(synthId, "Live",
			new FmSynthGraph([new FmConstantNode(0, 0.25)], 0));
		document.Add(synth);
		PublishingFactory factory = new();
		TestBackend backend = new();
		using SongPlaybackTransport transport = new(backend, factory);

		await transport.SendLiveEventAsync(document, ChannelTarget.Virtual(20),
			[new StartNoteCommand(synthId)]);
		backend.OpenCount.Should().Be(1);
		factory.Source.Order.Should().Equal("Start:20");

		synth.Graph = new FmSynthGraph([new FmConstantNode(0, 0.5)], 0);
		document.MarkChanged(affectsAudio: true);

		// Releasing an already-playing voice after the edit must stay on
		// the same live renderer; the next note uses the prepared revision.
		await transport.SendLiveEventAsync(document, ChannelTarget.Virtual(20),
			[new NoteOffCommand()]);
		await transport.SendLiveEventAsync(document, ChannelTarget.Virtual(21),
			[new StartNoteCommand(synthId)]);

		backend.OpenCount.Should().Be(1,
			"audio revisions of a direct Synth must not discard other voices");
		factory.PreparedValues.Should().Equal(0.5);
		factory.Source.Order.Should().Equal(
			"Start:20", "Off:20", "Publish", "Start:21");

		// Multiple note starts at the same revision use the cached source.
		await transport.SendLiveEventAsync(document, ChannelTarget.Virtual(22),
			[new StartNoteCommand(synthId)]);
		factory.PreparedValues.Should().HaveCount(1);
		backend.OpenCount.Should().Be(1);

		// Structural changes are likewise deferred to *future* note starts;
		// the older voices are neither retargeted nor stopped here.
		synth.Graph = new FmSynthGraph(
			[new FmConstantNode(0, 0.75), new FmConstantNode(1, 0)], 0);
		document.MarkChanged(affectsAudio: true);
		await transport.SendLiveEventAsync(document, ChannelTarget.Virtual(23),
			[new StartNoteCommand(synthId)]);
		factory.PreparedValues.Should().Equal(0.5, 0.75);
		backend.OpenCount.Should().Be(1);
	}

	[Test]
	public async Task CommittedSynthEditPublishesIntoExistingAuditionBeforeNextNote()
	{
		SongDocument document = new();
		ObjectId synthId = document.AllocateObjectId();
		FmSynthDefinition synth = new(synthId, "Live",
			new FmSynthGraph([new FmConstantNode(0, 0.25)], 0));
		document.Add(synth);
		PublishingFactory factory = new();
		TestBackend backend = new();
		using SongPlaybackTransport transport = new(backend, factory);

		// An edit before the first audition must not open audio.
		await transport.PublishSynthEditAsync(document, synthId);
		backend.OpenCount.Should().Be(0);

		await transport.SendLiveEventAsync(document, ChannelTarget.Virtual(20),
			[new StartNoteCommand(synthId)]);
		synth.Graph = new FmSynthGraph([new FmConstantNode(0, 0.5)], 0);
		document.MarkChanged(affectsAudio: true);
		await transport.PublishSynthEditAsync(document, synthId);
		factory.Source.Order.Should().Equal("Start:20", "Update");
		factory.PreparedValues.Should().Equal(0.5);
		backend.OpenCount.Should().Be(1);

		await transport.SendLiveEventAsync(document, ChannelTarget.Virtual(21),
			[new StartNoteCommand(synthId)]);
		await transport.PublishSynthEditAsync(document, synthId);
		factory.Source.Order.Should().Equal("Start:20", "Update", "Start:21");
		factory.PreparedValues.Should().HaveCount(1);
		backend.OpenCount.Should().Be(1);
	}

	[Test]
	public async Task EditingNestedSynthPublishesDirectAndTransitiveParentsInDependencyOrder()
	{
		SongDocument document = new();
		ObjectId childId = document.AllocateObjectId();
		FmSynthDefinition child = new(childId, "Child",
			new FmSynthGraph([new FmConstantNode(0, 0.25)], 0));
		document.Add(child);
		ObjectId middleId = document.AllocateObjectId();
		document.Add(new FmSynthDefinition(middleId, "Middle",
			new FmSynthGraph([
				new FmSoundSourceNode(0, childId, FmMultiNode.NeutralFrequencyHz)], 0)));
		ObjectId outerId = document.AllocateObjectId();
		document.Add(new FmSynthDefinition(outerId, "Outer",
			new FmSynthGraph([
				new FmSoundSourceNode(0, middleId, FmMultiNode.NeutralFrequencyHz)], 0)));
		ObjectId siblingId = document.AllocateObjectId();
		document.Add(new FmSynthDefinition(siblingId, "Sibling",
			new FmSynthGraph([
				new FmSoundSourceNode(0, childId, FmMultiNode.NeutralFrequencyHz)], 0)));
		ObjectId unrelatedId = document.AllocateObjectId();
		document.Add(new FmSynthDefinition(unrelatedId, "Unrelated",
			new FmSynthGraph([new FmConstantNode(0, 0.75)], 0)));

		PublishingFactory factory = new();
		TestBackend backend = new();
		using SongPlaybackTransport transport = new(backend, factory);
		await transport.SendLiveEventAsync(document, ChannelTarget.Virtual(11),
			[new StartNoteCommand(outerId)]);

		child.Graph = new FmSynthGraph([new FmConstantNode(0, 0.5)], 0);
		document.MarkChanged(affectsAudio: true);
		await transport.PublishSynthEditAsync(document, childId);
		factory.PreparedIds.Should().Equal(childId, middleId, siblingId, outerId);
		factory.Source.Order.Should().Equal("Start:11",
			"Update", "Update", "Update", "Update");
		backend.OpenCount.Should().Be(1);

		// All affected revisions were already published for this exact
		// audio snapshot, so repeated child notifications do no work.
		await transport.PublishSynthEditAsync(document, childId);
		factory.PreparedIds.Should().HaveCount(4);
		backend.OpenCount.Should().Be(1);

		child.Graph = new FmSynthGraph([new FmConstantNode(0, 0.8)], 0);
		document.MarkChanged(affectsAudio: true);
		await transport.PublishSynthEditAsync(document, childId);
		factory.PreparedIds.Should().Equal(childId, middleId, siblingId, outerId,
			childId, middleId, siblingId, outerId);
		backend.OpenCount.Should().Be(1);
	}

	[Test]
	public async Task SharedEnvelopeAndFilterEditsPublishOnlyDependentSynthsAndParents()
	{
		SongDocument document = new();
		ObjectId envelopeId = document.AllocateObjectId();
		NodeEnvelopeDefinition envelope = new(envelopeId, "Shared Env");
		document.Add(envelope);
		ObjectId filterId = document.AllocateObjectId();
		FilterDefinition filter = new(filterId, "Shared Filter");
		document.Add(filter);
		ObjectId rootId = document.AllocateObjectId();
		document.Add(new FmSynthDefinition(rootId, "Root", new FmSynthGraph([
			new FmEnvelopeNode(0, envelopeId),
			new FmFilterNode(1, 0, new FilterInstance(filter))], 1)));
		ObjectId parentId = document.AllocateObjectId();
		document.Add(new FmSynthDefinition(parentId, "Source Parent",
			new FmSynthGraph([
				new FmSoundSourceNode(0, rootId, FmMultiNode.NeutralFrequencyHz)], 0)));
		ObjectId filterOnlyId = document.AllocateObjectId();
		document.Add(new FmSynthDefinition(filterOnlyId, "Filter Only",
			new FmSynthGraph([
				new FmConstantNode(0, 0.25),
				new FmFilterNode(1, 0, new FilterInstance(filter))], 1)));
		ObjectId envelopeOnlyId = document.AllocateObjectId();
		document.Add(new FmSynthDefinition(envelopeOnlyId, "Envelope Only",
			new FmSynthGraph([new FmEnvelopeNode(0, envelopeId)], 0)));
		ObjectId unrelatedId = document.AllocateObjectId();
		document.Add(new FmSynthDefinition(unrelatedId, "Unrelated",
			new FmSynthGraph([new FmConstantNode(0, 0.5)], 0)));
		ObjectId unreferencedId = document.AllocateObjectId();
		document.Add(new FilterDefinition(unreferencedId, "Unreferenced"));

		PublishingFactory factory = new();
		TestBackend backend = new();
		using SongPlaybackTransport transport = new(backend, factory);
		await transport.PublishSharedObjectEditAsync(document, envelopeId);
		backend.OpenCount.Should().Be(0,
			"editing a shared object must never start an audition");

		await transport.SendLiveEventAsync(document, ChannelTarget.Virtual(11),
			[new StartNoteCommand(parentId)]);

		envelope.ReplaceCurve([new EnvelopeNode(TimeSpan.Zero, 0.75)],
			0, 0, EnvelopeSustainLoopMode.Forward);
		document.MarkChanged(affectsAudio: true);
		await transport.PublishSharedObjectEditAsync(document, envelopeId);
		factory.PreparedIds.Should().Equal(rootId, envelopeOnlyId, parentId);
		factory.Source.Order.Should().Equal(
			"Start:11", "Update", "Update", "Update");
		backend.OpenCount.Should().Be(1);

		await transport.PublishSharedObjectEditAsync(document, envelopeId);
		factory.PreparedIds.Should().HaveCount(3,
			"each dependent Synth is published only once per audio revision");

		filter.SetStageDefaults(1, [
			new("CutoffHz", FilterParameterValue.FromNumber(2300))]);
		document.MarkChanged(affectsAudio: true);
		await transport.PublishSharedObjectEditAsync(document, filterId);
		factory.PreparedIds.Should().Equal(
			rootId, envelopeOnlyId, parentId,
			rootId, filterOnlyId, parentId);
		backend.OpenCount.Should().Be(1);

		await transport.PublishSharedObjectEditAsync(document, unreferencedId);
		factory.PreparedIds.Should().HaveCount(6,
			"unreferenced changes must not republish unrelated Synths");
		factory.PreparedIds.Should().NotContain(unrelatedId);
	}

	private sealed class PublishingFactory :
		IBackgroundPlaybackSourceFactory, ILiveSynthSourcePreparationFactory
	{
		public PublishingLiveSource Source { get; } = new();
		public List<double> PreparedValues { get; } = [];
		public List<ObjectId> PreparedIds { get; } = [];

		public IAudioOutputSource Create(PlaybackRequest request)
			=> Source;

		public ISound PrepareLiveSynthSource(
			SongDocumentSnapshot snapshot, ObjectId sourceId)
		{
			snapshot.Document.TryGet(sourceId, out SongObject? obj)
				.Should().BeTrue();
			FmSynthDefinition synth = obj.Should()
				.BeOfType<FmSynthDefinition>().Which;
			PreparedIds.Add(sourceId);
			double value = synth.Graph.Nodes.OfType<FmConstantNode>()
				.FirstOrDefault()?.Value ?? 0;
			PreparedValues.Add(value);
			return new PublishingDummySound();
		}
	}

	private sealed class PublishingLiveSource :
		ILiveAudioOutputSource, ILiveSynthSourceRevisionOutput
	{
		public List<string> Order { get; } = [];
		public AudioOutputFormat Format => new(48000, 2);

		public void EnqueueLiveSynthSource(ObjectId sourceId, ISound sound)
		{
			sound.Should().BeOfType<PublishingDummySound>();
			Order.Add("Publish");
		}

		public void EnqueueLiveSynthParameters(ObjectId sourceId, ISound sound)
		{
			sound.Should().BeOfType<PublishingDummySound>();
			Order.Add("Update");
		}

		public void EnqueueLiveEvent(ChannelTarget target,
			IReadOnlyList<NoteCommand> commands)
		{
			string operation = commands.Any(c => c is StartNoteCommand)
				? "Start" : "Off";
			Order.Add($"{operation}:{target.VirtualChannelId}");
		}
		public void Render(int frameCount, Span<float> destination)
			=> destination.Clear();
	}

	private sealed class PublishingDummyState : SoundState { }
	private sealed class PublishingDummySound : ISound
	{
		public NoteConfigurationSnapshot SnapshotNoteConfiguration()
			=> NoteConfigurationSnapshot.Default;
		public SoundState CreateState() => new PublishingDummyState();
		public long? GetEndFrameExclusive(RenderContext context, SoundState state)
			=> null;
		public void Render(RenderContext context, SoundState state,
			long startFrame, int frameCount, Span<float> destination)
			=> destination.Clear();
	}

	private sealed class LiveFactory
		: IBackgroundPlaybackSourceFactory
	{
		public LiveSource Source { get; } = new();

		public IAudioOutputSource Create(
			PlaybackRequest request)
		{
			request.Should().BeOfType<AdHocPlaybackRequest>()
				.Which.ReserveSynthMultiHeadroom.Should().BeTrue(
					"live audition sessions alone need spare Multi constituents");
			return Source;
		}
	}

	private sealed class LiveSource : ILiveAudioOutputSource
	{
		public List<LivePlaybackEvent> Events { get; } = [];

		public AudioOutputFormat Format => new(48000, 2);

		public void EnqueueLiveEvent(
			ChannelTarget target,
			IReadOnlyList<NoteCommand> commands)
			=> Events.Add(
				new LivePlaybackEvent(
					target,
					commands));

		public void Render(
			int frameCount,
			Span<float> destination)
			=> destination.Clear();
	}

	private sealed class RecordingFactory
		: IBackgroundPlaybackSourceFactory
	{
		public List<PlaybackRequest> Requests { get; } = [];

		public IAudioOutputSource Create(
			PlaybackRequest request)
		{
			Requests.Add(request);
			return new SilentSource();
		}
	}

	private sealed class SilentSource : IAudioOutputSource
	{
		public AudioOutputFormat Format => new(48000, 2);

		public void Render(
			int frameCount,
			Span<float> destination)
			=> destination.Clear();
	}

	private sealed class TestBackend : IAudioOutputBackend
	{
		public int OpenCount { get; private set; }

		public IAudioOutputSession Open(
			AudioOutputFormat format,
			IAudioOutputSource source)
		{
			OpenCount++;
			return new TestSession(format);
		}

		public void Dispose()
		{
		}
	}

	private sealed class TestSession : IAudioOutputSession
	{
		public TestSession(
			AudioOutputFormat format)
			=> Format = format;

		public AudioOutputFormat Format { get; }
		public bool IsRunning { get; private set; }
		public Exception? Fault => null;

		public void Start()
			=> IsRunning = true;

		public void Stop()
			=> IsRunning = false;

		public void Dispose()
		{
		}
	}
}
