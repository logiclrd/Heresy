using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;

using Heresy.Core.Assets;
using Heresy.Core.Envelopes;
using Heresy.Core.Filters;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Samples;
using Heresy.Core.Sequencing;
using Heresy.Core.Timing;
using Heresy.Render.Configuration;
using Heresy.Render.Envelopes;
using Heresy.Render.FmSynthesis;
using Heresy.Render.Filters;
using Heresy.Render.Instruments;
using Heresy.Render.Playback;
using Heresy.Render.Samples;
using Heresy.Render.Sounds;

using NUnit.Framework;

namespace Heresy.Tests.Render;

/// <summary>
/// A prepared ISound-backed Synth is a physical/virtual PlaybackVoice:
/// NNA must move its entire child invocation and live graph Filter history,
/// then reclaim the displaced voice at its observed finite boundary.
/// </summary>
[TestFixture]
public sealed class FmSynthNnaVirtualLifecycleTests
{
    private const int Rate = 1000;
    private static readonly double C4 = 440 * Math.Pow(2.0, -9.0 / 12.0);
    private static readonly ObjectId SampleId = (ObjectId)72001U;
    private static readonly ObjectId InstrumentId = (ObjectId)72002U;
    private static readonly ObjectId InnerId = (ObjectId)72003U;
    private static readonly ObjectId SourceId = (ObjectId)72004U;
    private static readonly ObjectId EnvelopeId = (ObjectId)72005U;

    [Test]
    public void IncompatibleFiniteSourceStopsCountingAtNaturalEofAndFilterTail()
    {
        Registry resolver = new();
        FmSynthSound original = BuildNested(resolver, loop: false,
            action: NewNoteAction.Continue, inheritedEnvelope: false);
        resolver.Add(SourceId, original);
        PlaybackSession session = Session(resolver, Schedule());
        session.ApplyLiveEvent(ChannelTarget.Virtual(7),
            [new StartNoteCommand(SourceId)]);
        session.Render(0, 2, new float[2]);

        FmSynthSound structurallyDifferent = new(new FmSynthGraph([
            new FmConstantNode(1, 0.25),
        ], 1), resolver);
        session.AdoptLiveSynthParameters(SourceId, original,
            structurallyDifferent);
        Assert.That(session.CountOlderAudioVoices(), Is.EqualTo(1));

        // The finite Sample and its downstream Filter have a real
        // lifetime. No new note or transport restart is needed to clear
        // the older-audio warning when they become silent.
        session.Render(2, 30, new float[30]);
        Assert.That(session.CountOlderAudioVoices(), Is.Zero);
    }

    [Test]
    public void NnaDisplacedOlderVoiceOutlivesFreshCurrentNoteButEventuallyRetires()
    {
        Registry resolver = new();
        FmSynthSound original = BuildNested(resolver, loop: false,
            action: NewNoteAction.Continue, inheritedEnvelope: false);
        resolver.Add(SourceId, original);
        PlaybackSession session = Session(resolver, Schedule());
        session.ApplyLiveEvent(ChannelTarget.Virtual(7),
            [new StartNoteCommand(SourceId)]);
        session.Render(0, 1, new float[1]);

        FmSynthSound revised = new(new FmSynthGraph([
            new FmConstantNode(1, 0.5),
        ], 1), resolver);
        session.AdoptLiveSynthParameters(SourceId, original, revised);
        resolver.Replace(SourceId, revised);
        session.ApplyLiveEvent(ChannelTarget.Virtual(7),
            [new StartNoteCommand(SourceId)]);
        Assert.Multiple(() =>
        {
            Assert.That(session.VirtualVoices, Has.Count.EqualTo(1));
            Assert.That(session.CountOlderAudioVoices(), Is.EqualTo(1),
                "the displaced older note retains its own incompatible revision");
        });

        float[] output = new float[30];
        session.Render(1, output.Length, output);
        Assert.Multiple(() =>
        {
            Assert.That(session.VirtualVoices, Is.Empty);
            Assert.That(session.CountOlderAudioVoices(), Is.Zero,
                "the last old note expires independently of the new note");
            Assert.That(output[^1], Is.GreaterThan(0),
                "the newly started revision continues to sound");
        });
    }

    [Test]
    public void InstrumentToneHoldingOldSynthIsOlderWhenItsRevisionCannotMigrate()
    {
        Registry resolver = new();
        FmSynthSound original = new(new FmSynthGraph([
            new FmConstantNode(1, 0.5),
        ], 1), resolver);
        FmSynthSound edited = new(new FmSynthGraph([
            new FmConstantNode(1, 0.5),
            new FmConstantNode(2, 0.25),
            new FmOperatorNode(3, FmOperatorKind.Add, [1, 2]),
        ], 3), resolver);
        resolver.Add(InnerId, original);
        resolver.Add(InstrumentId, new InstrumentSound(
            _ => new ToneSpecification { SourceId = InnerId },
            resolver, resolver));
        PlaybackSession session = Session(resolver, Schedule());
        session.ApplyLiveEvent(ChannelTarget.Virtual(7),
            [new StartNoteCommand(InstrumentId)]);
        session.Render(0, 2, new float[2]);

        session.AdoptLiveSynthParameters(InnerId, original, edited);
        Assert.That(session.CountOlderAudioVoices(), Is.EqualTo(1),
            "the Instrument tone cannot migrate its terminal Synth via the direct route");
        session.ApplyLiveEvent(ChannelTarget.Virtual(7), [new NoteCutCommand()]);
        Assert.That(session.CountOlderAudioVoices(), Is.Zero);
    }

    [Test]
    public void PhysicalScopedAndNnaVoicesRetainIndependentLiveRevisionCurrency()
    {
        Registry registry = new();
        FmSynthSound Constant(double value) => new(new FmSynthGraph([
            new FmConstantNode(1, value),
        ], 1), registry,
            new NewNotePolicy(NewNoteAction.Continue, TimeSpan.Zero),
            soundResolver: registry);
        FmSynthSound Structural(double extra) => new(new FmSynthGraph([
            new FmConstantNode(1, 0.5),
            new FmConstantNode(2, extra),
            new FmOperatorNode(3, FmOperatorKind.Add, [1, 2]),
        ], 3), registry,
            new NewNotePolicy(NewNoteAction.Continue, TimeSpan.Zero),
            soundResolver: registry);

        FmSynthSound first = Constant(0.25);
        FmSynthSound compatible = Constant(0.5);
        FmSynthSound incompatible = Structural(0.1);
        FmSynthSound latest = Structural(0.3);
        registry.Add(SourceId, first);
        PlaybackSession session = Session(registry, Schedule());

        // Same virtual channel number is independent for each cursor;
        // physical channel 0 has a third, unrelated ownership path.
        session.ApplyScopedEvent(101, ChannelTarget.Virtual(7),
            [new StartNoteCommand(SourceId)]);
        session.ApplyScopedEvent(202, ChannelTarget.Virtual(7),
            [new StartNoteCommand(SourceId)]);
        session.ApplyLiveEvent(ChannelTarget.Physical(0),
            [new StartNoteCommand(SourceId)]);
        session.Render(0, 10, new float[10]);

        // This displaces only cursor 101's earlier voice by NNA Continue.
        // The displaced voice is still eligible for future live edits.
        session.ApplyScopedEvent(101, ChannelTarget.Virtual(7),
            [new StartNoteCommand(SourceId)]);
        PlaybackVoice displaced = session.VirtualVoices.Single();
        Assert.Multiple(() =>
        {
            Assert.That(displaced.OriginScopedVirtualOwner, Is.EqualTo(101));
            Assert.That(displaced.OriginVirtualChannelId, Is.EqualTo(7u));
            Assert.That(displaced.Sound, Is.SameAs(first));
            Assert.That(session.CountOlderAudioVoices(), Is.Zero);
        });
        session.Render(10, 5, new float[5]);

        session.AdoptLiveSynthParameters(SourceId, first, compatible);
        Assert.Multiple(() =>
        {
            Assert.That(session.CountOlderAudioVoices(), Is.Zero,
                "all four independently playing generations adopt the compatible edit");
            Assert.That(displaced.Sound, Is.SameAs(compatible),
                "the migrated NNA voice must not be omitted from publication");
            Assert.That(session.GetChannelState(0).CurrentVoice!.Sound,
                Is.SameAs(compatible), "the physical voice also adopts");
        });
        float[] afterAdoption = new float[121];
        session.Render(15, afterAdoption.Length, afterAdoption);
        Assert.That(afterAdoption[^1], Is.EqualTo(2.0f).Within(1e-4f),
            "all four voices must actually sound at the new 0.5 gain, " +
            "including both owner-scoped currents and the displaced NNA");

        // A structurally incompatible edit is a DEFINITIVE refusal for
        // the four old held notes, but the new note gets the new sound.
        session.AdoptLiveSynthParameters(SourceId, compatible, incompatible);
        Assert.That(session.CountOlderAudioVoices(), Is.EqualTo(4));
        registry.Replace(SourceId, incompatible);
        session.ApplyLiveEvent(ChannelTarget.Virtual(99),
            [new StartNoteCommand(SourceId)]);
        session.Render(136, 3, new float[3]);
        Assert.That(session.CountOlderAudioVoices(), Is.EqualTo(4),
            "fresh notes do not inherit a prior note's older-audio warning");

        // A compatible later edit updates the NEW note, but cannot
        // retroactively catch up the missed older four Synth graphs.
        session.AdoptLiveSynthParameters(SourceId, incompatible, latest);
        Assert.That(session.CountOlderAudioVoices(), Is.EqualTo(4),
            "later compatible adoption must not clear a missed generation");
        Assert.That(displaced.Sound, Is.SameAs(compatible),
            "the old displaced graph remains frozen, not silently rebuilt");

        // Cursor 101 owns two older voices: its current scoped note AND
        // its displaced NNA predecessor. Neither scope 202 nor physical
        // channel 0 may be cut when that cursor is cancelled.
        session.CancelScopedVoices(101);
        Assert.Multiple(() =>
        {
            Assert.That(session.VirtualVoices, Is.Empty);
            Assert.That(session.CountOlderAudioVoices(), Is.EqualTo(2),
                "only the two surviving old notes still count as older");
            Assert.That(session.GetChannelState(0).CurrentVoice, Is.Not.Null);
        });

        session.ApplyScopedEvent(202, ChannelTarget.Virtual(7),
            [new NoteOffCommand()]);
        session.Render(139, 4, new float[4]);
        Assert.That(session.CountOlderAudioVoices(), Is.EqualTo(1),
            "scope 202's definite Note Off retires exactly its own old note");
        session.ApplyLiveEvent(ChannelTarget.Physical(0),
            [new NoteCutCommand()]);
        Assert.That(session.CountOlderAudioVoices(), Is.Zero,
            "the remaining newer virtual note must not count as older audio");
        float[] currentOutput = new float[120];
        session.Render(143, currentOutput.Length, currentOutput);
        Assert.That(currentOutput[^1], Is.EqualTo(0.8f).Within(1e-4f),
            "the independent fresh note adopts the latest compatible edit");
        Assert.That(session.CountOlderAudioVoices(), Is.Zero);
    }

    [Test]
    public void PendingSampleRevisionsAcrossScopedAndDisplacedVoicesHonorLatestOnly()
    {
        ObjectId oldId = (ObjectId)72111U;
        ObjectId obsoleteId = (ObjectId)72112U;
        ObjectId latestId = (ObjectId)72113U;
        Registry registry = new();

        SampleSound Sample(ObjectId id, float value)
            => new(new SampleDefinition(id, "Frozen source for C4",
                new ExternalAssetReference("c4-multi-notes.wav")),
                new MemorySampleData(Rate, 1,
                    Enumerable.Repeat(value, 2000).ToArray()));
        registry.Add(oldId, Sample(oldId, 0.2f));
        registry.Add(obsoleteId, Sample(obsoleteId, 0.6f));
        registry.Add(latestId, Sample(latestId, 0.9f));

        FmSynthSound Sound(ObjectId sampleId)
            => new(new FmSynthGraph([
                new FmSoundSourceNode(1, sampleId,
                    FmMultiNode.NeutralFrequencyHz),
            ], 1), registry,
                new NewNotePolicy(NewNoteAction.Continue, TimeSpan.Zero),
                soundResolver: registry, enableLiveMultiExpansion: true);

        FmSynthSound old = Sound(oldId);
        FmSynthSound obsolete = Sound(obsoleteId);
        FmSynthSound latest = Sound(latestId);
        registry.Add(SourceId, old);
        PlaybackSession session = Session(registry, Schedule());

        session.ApplyScopedEvent(101, ChannelTarget.Virtual(7),
            [new StartNoteCommand(SourceId)]);
        session.ApplyScopedEvent(202, ChannelTarget.Virtual(7),
            [new StartNoteCommand(SourceId)]);
        session.ApplyLiveEvent(ChannelTarget.Physical(0),
            [new StartNoteCommand(SourceId)]);
        session.Render(0, 10, new float[10]);
        session.ApplyScopedEvent(101, ChannelTarget.Virtual(7),
            [new StartNoteCommand(SourceId)]);
        Assert.That(session.VirtualVoices, Has.Count.EqualTo(1));
        PlaybackVoice nna = session.VirtualVoices.Single();
        session.Render(10, 2, new float[2]);

        // No worker has run yet. Every live voice gets its own pending
        // Source replacement, including the NNA-displaced predecessor.
        session.AdoptLiveSynthParameters(SourceId, old, obsolete);
        Assert.That(session.CountOlderAudioVoices(), Is.Zero,
            "a prepared-but-not-yet-ready Source edit is not a refusal");
        session.AdoptLiveSynthParameters(SourceId, obsolete, latest);
        Assert.That(session.CountOlderAudioVoices(), Is.Zero,
            "superseding the pending candidate must be latest-wins on every voice");
        Assert.That(nna.Sound, Is.SameAs(old),
            "a pending worker revision has not yet replaced sounding audio");

        // Cursor 101 owns the current scoped note plus its NNA
        // predecessor. Both may be cancelled while their latest workers
        // are pending without affecting scope 202 or the physical note.
        session.CancelScopedVoices(101);
        Assert.Multiple(() =>
        {
            Assert.That(session.VirtualVoices, Is.Empty);
            Assert.That(session.CountOlderAudioVoices(), Is.Zero);
            Assert.That(session.GetChannelState(0).CurrentVoice!.Sound,
                Is.SameAs(old));
        });
        using FirPreparationScheduler worker = new();
        session.QueuePendingFirControls(worker);
        Assert.That(SpinWait.SpinUntil(
            () => worker.CompletedMultiExpansionCount >= 2,
            TimeSpan.FromSeconds(5)), Is.True,
            "the remaining physical and scope-202 voices need two detached Source preparations");
        session.AdoptPreparedFirControls();
        Assert.That(worker.Fault, Is.Null);
        Assert.Multiple(() =>
        {
            Assert.That(session.GetChannelState(0).CurrentVoice!.Sound,
                Is.SameAs(latest),
                "the physical voice must publish the genuinely latest Sample");
            Assert.That(session.CountOlderAudioVoices(), Is.Zero,
                "no pending, adopted or cancelled voice counts as stale");
        });

        float[] actual = new float[112];
        session.Render(12, actual.Length, actual);
        Assert.Multiple(() =>
        {
            Assert.That(actual[50], Is.EqualTo(1.1f).Within(0.02f),
                "two separate 0.2→0.9 Source-local 100-ms smoothsteps must be audible");
            Assert.That(actual[^1], Is.EqualTo(1.8f).Within(1e-4f),
                "scope 202 and the physical note both adopted the newest Sample");
            Assert.That(session.CountOlderAudioVoices(), Is.Zero);
            Assert.That(session.VirtualVoices, Is.Empty);
        });

        // New notes select the latest asset while the previously
        // cancelled scope is still absent. A later source event cannot
        // retroactively resurrect its old/current NNA voices.
        registry.Replace(SourceId, latest);
        session.ApplyLiveEvent(ChannelTarget.Virtual(99),
            [new StartNoteCommand(SourceId)]);
        float[] fresh = new float[4];
        session.Render(124, fresh.Length, fresh);
        Assert.That(fresh[^1], Is.EqualTo(2.7f).Within(1e-4f));
        Assert.That(session.CountOlderAudioVoices(), Is.Zero);
    }

    [TestCase(NewNoteAction.Off)]
    [TestCase(NewNoteAction.Fade)]
    public void ReleasingScopedNnaAndPhysicalVoicesRetainIndependentCurrencyAndHostTails(
        NewNoteAction action)
    {
        Registry registry = new();
        FmSynthSound original = BuildNested(registry, loop: true,
            action: action, inheritedEnvelope: true);

        // Only the parent's compatible OnePole cutoff changes. The selected
        // nested Instrument/Sample, its sustain-release clock and each
        // invocation's own Filter histories remain the same.
        FmSynthSound Retuned(double cutoff)
        {
            FilterDefinition definition = new((ObjectId)72006U, "Graph pole");
            FilterInstance filter = new(definition);
            filter.SetStageParameter(1, "CutoffHz",
                FilterParameterValue.FromNumber(cutoff));
            return new FmSynthSound(new FmSynthGraph([
                new FmSoundSourceNode(0, InnerId, C4),
                new FmFilterNode(1, 0, filter, tailSeconds: 0.002),
            ], 1), registry,
                new NewNotePolicy(action, action == NewNoteAction.Fade
                    ? TimeSpan.FromMilliseconds(4) : TimeSpan.Zero),
                soundResolver: registry);
        }

        FmSynthSound compatible = Retuned(240);
        FmSynthSound incompatible = new(new FmSynthGraph([
            new FmConstantNode(9, 0.25),
        ], 9), registry, soundResolver: registry);
        registry.Add(SourceId, original);
        PlaybackSession session = Session(registry, Schedule());

        // All three hosts own real IIR output histories. NNA displacement
        // must fork scope 101's existing host instead of following its newly
        // started voice through that same Filter a second time.
        FilterDefinition hostDefinition = new((ObjectId)72016U, "Host pole");
        FilterInstance host = new(hostDefinition);
        host.SetStageParameter(1, "CutoffHz",
            FilterParameterValue.FromNumber(100));
        session.ConfigurePhysicalChannelOutputFilter(0, host);
        session.ConfigureVirtualChannelOutputFilter(7, host, scopedOwner: 101);
        session.ConfigureVirtualChannelOutputFilter(7, host, scopedOwner: 202);

        session.ApplyLiveEvent(ChannelTarget.Physical(0),
            [new StartNoteCommand(SourceId)]);
        session.ApplyScopedEvent(101, ChannelTarget.Virtual(7),
            [new StartNoteCommand(SourceId)]);
        session.ApplyScopedEvent(202, ChannelTarget.Virtual(7),
            [new StartNoteCommand(SourceId)]);
        float[] initial = new float[10];
        session.Render(0, initial.Length, initial);
        Assert.That(initial.Any(value => Math.Abs(value) > 1e-5f), Is.True);

        session.AdoptLiveSynthParameters(SourceId, original, compatible);
        Assert.Multiple(() =>
        {
            Assert.That(session.GetChannelState(0).CurrentVoice!.Sound,
                Is.SameAs(compatible));
            Assert.That(session.CountOlderAudioVoices(), Is.Zero,
                "all three held voices should adopt the parent Filter retune");
        });
        registry.Replace(SourceId, compatible);
        session.Render(10, 110, new float[110]);

        // Scope 101 gets a second note, displacing its first note into the
        // NNA Off or Fade pool. Scope 202 releases independently, and the
        // physical voice stays held; all are sounding when publication occurs.
        session.ApplyScopedEvent(101, ChannelTarget.Virtual(7),
            [new StartNoteCommand(SourceId)]);
        Assert.That(session.VirtualVoices, Has.Count.EqualTo(1));
        PlaybackVoice displaced = session.VirtualVoices.Single();
        Assert.Multiple(() =>
        {
            Assert.That(displaced.OriginScopedVirtualOwner, Is.EqualTo(101));
            Assert.That(displaced.Sound, Is.SameAs(compatible));
            Assert.That(displaced.DisplacedOutputFilterState, Is.Not.Null);
            Assert.That(displaced.DisplacedOutputFilterState!.PreparedTailFrames,
                Is.GreaterThan(0));
        });
        if (action == NewNoteAction.Off)
            Assert.That(displaced.SoundState.NoteOffTime,
                Is.EqualTo(TimeSpan.FromMilliseconds(120)));
        else
            Assert.That(displaced.FadeEndFrameExclusive, Is.EqualTo(124));

        session.Render(120, 1, new float[1]);
        session.ApplyScopedEvent(202, ChannelTarget.Virtual(7),
            [new NoteOffCommand()]);
        session.Render(121, 1, new float[1]);
        session.AdoptLiveSynthParameters(SourceId, compatible, incompatible);
        Assert.Multiple(() =>
        {
            Assert.That(session.CountOlderAudioVoices(), Is.EqualTo(4),
                "the releasing NNA, releasing other owner, fresh scoped note " +
                "and held physical note each miss the new graph");
            Assert.That(displaced.Sound, Is.SameAs(compatible),
                "incompatible publication cannot replace the releasing state");
        });

        // Neither an incompatible graph nor another owner's Note Off may
        // reset the NNA release/fade origin or hold it beyond its real end.
        float[] earlyRelease = new float[12];
        session.Render(122, earlyRelease.Length, earlyRelease);
        Assert.Multiple(() =>
        {
            Assert.That(session.VirtualVoices, Is.Empty);
            Assert.That(session.CountOlderAudioVoices(), Is.EqualTo(2),
                "only scope 101's new current and physical current survive");
            Assert.That(displaced.SoundState.NoteOffTime,
                Is.EqualTo(action == NewNoteAction.Off
                    ? TimeSpan.FromMilliseconds(120) : null));
            Assert.That(earlyRelease.Any(value => Math.Abs(value) > 1e-6f),
                Is.True, "the release and its forked host remain audible");
        });

        session.ApplyScopedEvent(101, ChannelTarget.Virtual(7),
            [new NoteOffCommand()]);
        session.Render(134, 10, new float[10]);
        Assert.That(session.CountOlderAudioVoices(), Is.EqualTo(1),
            "scope 101 retires without affecting the held physical note");

        session.ApplyLiveEvent(ChannelTarget.Physical(0),
            [new NoteOffCommand()]);
        float[] finalRelease = new float[5];
        session.Render(144, finalRelease.Length, finalRelease);
        Assert.Multiple(() =>
        {
            Assert.That(session.GetChannelState(0).CurrentVoice, Is.Null);
            Assert.That(session.CountOlderAudioVoices(), Is.Zero,
                "a completed old voice no longer counts as stale");
            Assert.That(finalRelease.Any(value => Math.Abs(value) > 1e-6f),
                Is.True);
        });

        // The physical host's genuine OnePole history can still emit a tail
        // after the last old note retires. It must NOT resurrect its warning.
        float[] hostTail = new float[3];
        session.Render(149, hostTail.Length, hostTail);
        Assert.Multiple(() =>
        {
            Assert.That(hostTail.Any(value => Math.Abs(value) > 1e-7f),
                Is.True, "host-tail PCM is independent of voice currency");
            Assert.That(session.CountOlderAudioVoices(), Is.Zero);
        });
    }

    [Test]
    public void ScopedNestedSynthReleasesKeepChildRevisionsAndOriginalOwnerClocks()
    {
        Registry registry = new();
        registry.AddEnvelope(EnvelopeId, new FortyFrameRelease());

        // The parent has no own release-capable Envelope: its continuing
        // child Synth alone supplies the sustain/release lifecycle.
        FmSynthSound Child(double level, bool structural = false)
            => structural
                ? new FmSynthSound(new FmSynthGraph([
                    new FmConstantNode(1, level),
                    new FmConstantNode(4, 0),
                    new FmOperatorNode(5, FmOperatorKind.Add, [1, 4]),
                    new FmEnvelopeNode(2, EnvelopeId),
                    new FmOperatorNode(3, FmOperatorKind.Multiply, [5, 2]),
                ], 3), registry, enableLiveMultiExpansion: true)
                : new FmSynthSound(new FmSynthGraph([
                    new FmConstantNode(1, level),
                    new FmEnvelopeNode(2, EnvelopeId),
                    new FmOperatorNode(3, FmOperatorKind.Multiply, [1, 2]),
                ], 3), registry, enableLiveMultiExpansion: true);

        FmSynthSound Parent()
        {
            FilterDefinition definition = new((ObjectId)72036U,
                "Ancestor OnePole");
            FilterInstance filter = new(definition);
            filter.SetStageParameter(1, "CutoffHz",
                FilterParameterValue.FromNumber(240));
            return new FmSynthSound(new FmSynthGraph([
                new FmSoundSourceNode(1, InnerId,
                    FmMultiNode.NeutralFrequencyHz),
                new FmFilterNode(2, 1, filter, tailSeconds: 0.003),
            ], 2), registry,
                new NewNotePolicy(NewNoteAction.Off, TimeSpan.Zero),
                soundResolver: registry, enableLiveMultiExpansion: true);
        }

        FmSynthSound originalChild = Child(0.2);
        registry.Add(InnerId, originalChild);
        FmSynthSound original = Parent();
        registry.Add(SourceId, original);
        PlaybackSession actual = Session(registry, Schedule());
        PlaybackSession compatibleReference = Session(registry, Schedule());
        PlaybackSession uneditedReference = Session(registry, Schedule());

        void All(Action<PlaybackSession> action)
        {
            action(actual);
            action(compatibleReference);
            action(uneditedReference);
        }
        void Compare(int start, int count, bool mustHearRetune = false)
        {
            float[] got = new float[count];
            float[] expected = new float[count];
            float[] unedited = new float[count];
            actual.Render(start, count, got);
            compatibleReference.Render(start, count, expected);
            uneditedReference.Render(start, count, unedited);
            for (int i = 0; i < count; i++)
                Assert.That(got[i], Is.EqualTo(expected[i]).Within(1e-5f),
                    $"retained nested release/Filter PCM at frame {start + i}");
            if (mustHearRetune)
                Assert.That(got.Zip(unedited, (a, b) => Math.Abs(a - b)).Max(),
                    Is.GreaterThan(1e-4f),
                    "accepted child Constant edit must audibly reach the releasing ancestors");
        }

        // Same virtual channel ID in different ancestors must never mean
        // the same note. The physical host is a third independent owner.
        All(s => s.ApplyScopedEvent(101, ChannelTarget.Virtual(7),
            [new StartNoteCommand(SourceId)]));
        All(s => s.ApplyScopedEvent(202, ChannelTarget.Virtual(7),
            [new StartNoteCommand(SourceId)]));
        All(s => s.ApplyLiveEvent(ChannelTarget.Physical(0),
            [new StartNoteCommand(SourceId)]));
        Compare(0, 10);

        // The old owner-101 note becomes NNA Off. Owner 202 starts a
        // separate release a frame later. The parent carries the child
        // lifecycle, despite having no top-level sustain Envelope.
        All(s => s.ApplyScopedEvent(101, ChannelTarget.Virtual(7),
            [new StartNoteCommand(SourceId)]));
        Assert.That(actual.VirtualVoices, Has.Count.EqualTo(1));
        PlaybackVoice releasedNna = actual.VirtualVoices.Single();
        Assert.That(releasedNna.SoundState.NoteOffTime,
            Is.EqualTo(TimeSpan.FromMilliseconds(10)));
        Compare(10, 1);
        All(s => s.ApplyScopedEvent(202, ChannelTarget.Virtual(7),
            [new NoteOffCommand()]));
        Compare(11, 1);

        FmSynthSound compatibleChild = Child(0.5);
        registry.Replace(InnerId, compatibleChild);
        FmSynthSound compatibleParent = Parent();

        // Dependency-order publication: the direct child event cannot
        // mutate or mislabel a parent-owned note. The recompiled parent
        // then migrates each eligible nested child invocation in place,
        // including the two already-releasing scopes and the NNA voice.
        actual.AdoptLiveSynthParameters(InnerId,
            originalChild, compatibleChild);
        Assert.That(actual.CountOlderAudioVoices(), Is.Zero);
        actual.AdoptLiveSynthParameters(SourceId, original, compatibleParent);
        compatibleReference.AdoptLiveSynthParameters(SourceId,
            original, compatibleParent);
        Assert.Multiple(() =>
        {
            Assert.That(actual.CountOlderAudioVoices(), Is.Zero);
            Assert.That(releasedNna.Sound, Is.SameAs(compatibleParent));
            Assert.That(releasedNna.SoundState.NoteOffTime,
                Is.EqualTo(TimeSpan.FromMilliseconds(10)));
        });
        Compare(12, 8, mustHearRetune: true);

        // One child becomes structurally incompatible while two
        // independently scoped releases are still sounding. The
        // containing parent retains each old child history (including
        // any compatible transition still in flight) and marks four
        // concrete old voices, not a shared ancestor or virtual ID.
        FmSynthSound structuralChild = Child(0.9, structural: true);
        registry.Replace(InnerId, structuralChild);
        FmSynthSound structuralParent = Parent();
        actual.AdoptLiveSynthParameters(InnerId,
            compatibleChild, structuralChild);
        actual.AdoptLiveSynthParameters(SourceId,
            compatibleParent, structuralParent);
        Assert.Multiple(() =>
        {
            Assert.That(actual.CountOlderAudioVoices(), Is.EqualTo(4));
            Assert.That(releasedNna.SoundState.NoteOffTime,
                Is.EqualTo(TimeSpan.FromMilliseconds(10)),
                "nested revision must not restart NNA's original release");
        });
        Compare(20, 8);

        // Fresh notes select the new child, but can never inherit the
        // older marks of the old parent/ancestor invocations.
        registry.Replace(SourceId, structuralParent);
        actual.ApplyScopedEvent(303, ChannelTarget.Virtual(7),
            [new StartNoteCommand(SourceId)]);
        actual.Render(28, 32, new float[32]);
        Assert.Multiple(() =>
        {
            Assert.That(actual.VirtualVoices, Is.Empty);
            Assert.That(actual.CountOlderAudioVoices(), Is.EqualTo(2),
                "owner-101 NNA and owner-202 release/graph tails have ended");
        });

        actual.ApplyScopedEvent(101, ChannelTarget.Virtual(7),
            [new NoteOffCommand()]);
        actual.Render(60, 50, new float[50]);
        Assert.That(actual.CountOlderAudioVoices(), Is.EqualTo(1),
            "retiring owner 101 must leave only the old physical note");

        actual.ApplyLiveEvent(ChannelTarget.Physical(0),
            [new NoteOffCommand()]);
        float[] finalMix = new float[50];
        actual.Render(110, finalMix.Length, finalMix);
        Assert.Multiple(() =>
        {
            Assert.That(actual.CountOlderAudioVoices(), Is.Zero);
            Assert.That(actual.GetChannelState(0).CurrentVoice, Is.Null);
            Assert.That(finalMix[^1], Is.GreaterThan(0.1f),
                "fresh scope 303 must still sound independently");
            Assert.That(releasedNna.SoundState.NoteOffTime,
                Is.EqualTo(TimeSpan.FromMilliseconds(10)));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void InFlightNestedMultiWorkersCannotResurrectCancelledScopedNnaVoices(
        bool obsoleteWorkerAlreadyReady)
    {
        Registry registry = new();

        FmSynthSound Child(int count, double level) => new(new FmSynthGraph([
            new FmConstantNode(1, level),
            new FmMultiNode(2, 1, voiceCount: count, detune: 0,
                randomizePhase: false, gainCompensation: true),
        ], 2), registry, enableLiveMultiExpansion: true);

        FmSynthSound Parent()
        {
            FilterInstance filter = new(new FilterDefinition(
                (ObjectId)72047U, "C4(g) parent pole"));
            filter.SetStageParameter(1, "CutoffHz",
                FilterParameterValue.FromNumber(180));
            return new FmSynthSound(new FmSynthGraph([
                new FmSoundSourceNode(1, InnerId,
                    FmMultiNode.NeutralFrequencyHz),
                new FmFilterNode(2, 1, filter, tailSeconds: 0.003),
            ], 2), registry,
                new NewNotePolicy(NewNoteAction.Continue, TimeSpan.Zero),
                soundResolver: registry, enableLiveMultiExpansion: true);
        }

        FmSynthSound originalChild = Child(1, 0.2);
        registry.Add(InnerId, originalChild);
        FmSynthSound original = Parent();
        registry.Add(SourceId, original);

        // This candidate needs three fresh nested Multi constituents for
        // every old note. The final revision is instead structurally
        // incompatible with the entire parent graph.
        registry.Replace(InnerId, Child(4, 0.6));
        FmSynthSound obsolete = Parent();
        FmSynthSound incompatible = new(new FmSynthGraph([
            new FmConstantNode(9, 0.8),
        ], 9), registry, soundResolver: registry);

        PlaybackSession actual = Session(registry, Schedule());
        PlaybackSession reference = Session(registry, Schedule());
        void Both(Action<PlaybackSession> action)
        {
            action(actual);
            action(reference);
        }
        void Compare(int start, int count)
        {
            float[] got = new float[count], expected = new float[count];
            actual.Render(start, count, got);
            reference.Render(start, count, expected);
            for (int i = 0; i < count; i++)
                Assert.That(got[i], Is.EqualTo(expected[i]).Within(1e-5f),
                    $"an obsolete nested worker must not alter PCM at frame {start + i}");
        }

        Both(s => s.ApplyScopedEvent(101, ChannelTarget.Virtual(7),
            [new StartNoteCommand(SourceId)]));
        Both(s => s.ApplyScopedEvent(202, ChannelTarget.Virtual(7),
            [new StartNoteCommand(SourceId)]));
        Both(s => s.ApplyLiveEvent(ChannelTarget.Physical(0),
            [new StartNoteCommand(SourceId)]));
        Compare(0, 10);
        Both(s => s.ApplyScopedEvent(101, ChannelTarget.Virtual(7),
            [new StartNoteCommand(SourceId)]));
        Assert.That(actual.VirtualVoices, Has.Count.EqualTo(1));
        PlaybackVoice displaced = actual.VirtualVoices.Single();
        Compare(10, 2);

        using ManualResetEventSlim workerEntered = new(false);
        using ManualResetEventSlim allowWorkerFinish = new(false);
        using FirPreparationScheduler scheduler = new();
        int firstCallback = 0;
        if (!obsoleteWorkerAlreadyReady)
            scheduler.AfterMultiConstituentPrepared = () =>
            {
                if (Interlocked.Exchange(ref firstCallback, 1) != 0)
                    return;
                workerEntered.Set();
                // The finally block releases this gate even if an assertion
                // fails on the PCM owner. No worker is left stranded.
                allowWorkerFinish.Wait(TimeSpan.FromSeconds(10));
            };

        try
        {
            actual.AdoptLiveSynthParameters(SourceId, original, obsolete);
            Assert.That(actual.CountOlderAudioVoices(), Is.Zero,
                "worker-pending nested revisions are not definite refusals");
            actual.QueuePendingFirControls(scheduler);

            if (obsoleteWorkerAlreadyReady)
                Assert.That(SpinWait.SpinUntil(
                    () => scheduler.CompletedMultiExpansionCount >= 4,
                    TimeSpan.FromSeconds(5)), Is.True,
                    "all four obsolete nested worker results should be ready but not adopted");
            else
            {
                Assert.That(workerEntered.Wait(TimeSpan.FromSeconds(5)),
                    Is.True, "one nested expansion must really be in flight");
                Assert.That(scheduler.PeakQueuedWorkItems,
                    Is.GreaterThanOrEqualTo(3),
                    "the other three voices must have queued independent work");
            }

            // A pending proposal cannot change the current audible sound.
            Compare(12, 4);
            Assert.That(displaced.Sound, Is.SameAs(original));

            // All four old notes DEFINITIVELY miss this incompatible
            // publication, even though their previous candidate was only
            // pending or ready. Sticky currency belongs to their voices.
            actual.AdoptLiveSynthParameters(SourceId, obsolete, incompatible);
            Assert.That(actual.CountOlderAudioVoices(), Is.EqualTo(4));
            actual.CancelScopedVoices(101);
            reference.CancelScopedVoices(101);
            Assert.Multiple(() =>
            {
                Assert.That(actual.VirtualVoices, Is.Empty);
                Assert.That(actual.CountOlderAudioVoices(), Is.EqualTo(2),
                    "scope 101 cancellation must retire current and displaced NNA");
                Assert.That(actual.GetChannelState(0).CurrentVoice!.Sound,
                    Is.SameAs(original));
            });

            // Exercise publication both while the worker is still held and
            // after it finishes. Neither ready arrays nor discarded partial
            // construction may revive a cancelled scoped voice or retune the
            // retained incompatible generation.
            actual.AdoptPreparedFirControls();
            actual.QueuePendingFirControls(scheduler);
            allowWorkerFinish.Set();
            if (!obsoleteWorkerAlreadyReady)
                Assert.That(SpinWait.SpinUntil(
                    () => scheduler.CancelledMultiExpansionCount >= 4,
                    TimeSpan.FromSeconds(5)), Is.True,
                    "all four queued/working obsolete mailboxes must be discarded");
            Assert.That(scheduler.Fault, Is.Null);
            actual.AdoptPreparedFirControls();
            Assert.That(actual.CountOlderAudioVoices(), Is.EqualTo(2));
            Compare(16, 160);
            Assert.That(scheduler.CompletedMultiExpansionCount,
                Is.EqualTo(obsoleteWorkerAlreadyReady ? 4 : 0),
                "none of the obsolete work may be accepted after an incompatible edit");

            // A newly started note gets the newest structural definition,
            // while the two surviving old notes keep their own parent
            // Filter histories and old 0.2 constant Synth child.
            registry.Replace(SourceId, incompatible);
            Both(s => s.ApplyLiveEvent(ChannelTarget.Virtual(99),
                [new StartNoteCommand(SourceId)]));
            Compare(176, 128);
            float[] settled = new float[4];
            actual.Render(304, settled.Length, settled);
            reference.Render(304, settled.Length, new float[4]);
            Assert.That(settled[^1], Is.EqualTo(1.2f).Within(0.002f),
                "the two retained 0.2 parents and one new 0.8 note must coexist");
            Assert.That(actual.CountOlderAudioVoices(), Is.EqualTo(2));

            actual.ApplyScopedEvent(202, ChannelTarget.Virtual(7),
                [new NoteCutCommand()]);
            Assert.That(actual.CountOlderAudioVoices(), Is.EqualTo(1));
            actual.ApplyLiveEvent(ChannelTarget.Physical(0),
                [new NoteCutCommand()]);
            Assert.That(actual.CountOlderAudioVoices(), Is.Zero);
            float[] current = new float[120];
            actual.Render(308, current.Length, current);
            Assert.Multiple(() =>
            {
                Assert.That(actual.VirtualVoices, Is.Empty);
                Assert.That(actual.CountOlderAudioVoices(), Is.Zero);
                Assert.That(current[^1], Is.EqualTo(0.8f).Within(1e-4f),
                    "a cancelled old scope must not return during later PCM");
            });
        }
        finally
        {
            allowWorkerFinish.Set();
        }
    }

    [TestCase(true)]
    [TestCase(false)]
    public void SilenceTransitionSupersessionDefersOnlyLatestWithoutStaleCurrency(
        bool startsUnassigned)
    {
        // Both None→Sample and Sample→None must complete their first
        // 100-frame fade before creating a subsequent Sample onset.
        // A separately sounding Sample sibling keeps the parent note
        // alive even when the selected first branch is None.
        ObjectId aId = (ObjectId)72501U;
        ObjectId bId = (ObjectId)72502U;
        ObjectId cId = (ObjectId)72503U;
        ObjectId dId = (ObjectId)72504U;
        ObjectId siblingId = (ObjectId)72505U;
        Registry registry = new();
        SampleSound Sample(ObjectId id, float level)
            => new(new SampleDefinition(id, "C4(l) silence transition",
                new ExternalAssetReference("c4-l-source.wav")),
                new MemorySampleData(Rate, 1,
                    Enumerable.Repeat(level, 800).ToArray()));
        registry.Add(aId, Sample(aId, 0.2f));
        registry.Add(bId, Sample(bId, 0.6f));
        registry.Add(cId, Sample(cId, 0.8f));
        registry.Add(dId, Sample(dId, 0.4f));
        registry.Add(siblingId, Sample(siblingId, 0.1f));

        FmSynthSound Sound(ObjectId id)
            => new(new FmSynthGraph([
                new FmSoundSourceNode(1, id, FmMultiNode.NeutralFrequencyHz),
                new FmSoundSourceNode(2, siblingId, FmMultiNode.NeutralFrequencyHz),
                new FmOperatorNode(3, FmOperatorKind.Add, [1, 2]),
            ], 3), registry,
                new NewNotePolicy(NewNoteAction.Continue, TimeSpan.Zero),
                soundResolver: registry, enableLiveMultiExpansion: true);

        FmSynthSound initial = Sound(startsUnassigned ? ObjectId.None : aId);
        FmSynthSound first = Sound(startsUnassigned ? bId : ObjectId.None);
        FmSynthSound superseded = Sound(cId);
        FmSynthSound latest = Sound(dId);
        registry.Add(SourceId, initial);
        PlaybackSession session = Session(registry, Schedule());
        session.ApplyLiveEvent(ChannelTarget.Physical(0),
            [new StartNoteCommand(SourceId)]);
        session.Render(0, 10, new float[10]);

        using FirPreparationScheduler worker = new();
        session.AdoptLiveSynthParameters(SourceId, initial, first);
        if (startsUnassigned)
        {
            session.QueuePendingFirControls(worker);
            Assert.That(SpinWait.SpinUntil(
                () => worker.CompletedMultiExpansionCount == 1,
                TimeSpan.FromSeconds(5)), Is.True);
            session.AdoptPreparedFirControls();
        }
        Assert.That(session.GetChannelState(0).CurrentVoice!.Sound,
            Is.SameAs(first), "the first None transition should be accepted");
        session.Render(10, 10, new float[10]);

        // Two rapid edits are serialized at event boundaries, not
        // performed by a human inside the 100-ms transition.
        session.AdoptLiveSynthParameters(SourceId, first, superseded);
        Assert.That(session.CountOlderAudioVoices(), Is.Zero,
            "a valid later Source must wait, not mark older during a silence fade");
        session.Render(20, 10, new float[10]);
        session.AdoptLiveSynthParameters(SourceId, superseded, latest);
        Assert.That(session.CountOlderAudioVoices(), Is.Zero);

        registry.Replace(SourceId, latest);
        session.ApplyLiveEvent(ChannelTarget.Virtual(99),
            [new StartNoteCommand(SourceId)]);
        float[] firstRamp = new float[81];
        session.Render(30, firstRamp.Length, firstRamp);
        for (int i = 0; i < firstRamp.Length; i++)
        {
            int frame = 30 + i;
            double t = (frame - 10) / 100.0;
            double w = t * t * (3.0 - 2.0 * t);
            double held = startsUnassigned ? 0.6 * w : 0.2 * (1.0 - w);
            Assert.That(firstRamp[i],
                Is.EqualTo((float)(0.2 + held + 0.4)).Within(1e-5f),
                $"old silence fade must finish unchanged at global frame {frame}");
        }
        Assert.Multiple(() =>
        {
            Assert.That(session.GetChannelState(0).CurrentVoice!.Sound,
                Is.SameAs(first), "the latest edit remains deferred");
            Assert.That(session.CountOlderAudioVoices(), Is.Zero);
            Assert.That(worker.CompletedMultiExpansionCount,
                Is.EqualTo(startsUnassigned ? 1 : 0),
                "do not prepare either superseded candidate during first fade");
        });

        session.AdoptPreparedFirControls();
        session.QueuePendingFirControls(worker);
        Assert.That(SpinWait.SpinUntil(
            () => worker.CompletedMultiExpansionCount
                == (startsUnassigned ? 2 : 1),
            TimeSpan.FromSeconds(5)), Is.True);
        session.AdoptPreparedFirControls();
        Assert.Multiple(() =>
        {
            Assert.That(session.GetChannelState(0).CurrentVoice!.Sound,
                Is.SameAs(latest), "the second transition begins only after first fade");
            Assert.That(session.CountOlderAudioVoices(), Is.Zero);
            Assert.That(worker.Fault, Is.Null);
        });

        float[] secondRamp = new float[101];
        session.Render(111, secondRamp.Length, secondRamp);
        for (int i = 0; i < secondRamp.Length; i++)
        {
            double t = i / 100.0, w = t * t * (3.0 - 2.0 * t);
            double branch = startsUnassigned ? 0.6 * (1.0 - w) + 0.4 * w
                : 0.4 * w;
            Assert.That(secondRamp[i],
                Is.EqualTo((float)(0.2 + branch + 0.4)).Within(1e-5f),
                $"only latest Sample can begin after silence fade at frame {111 + i}");
        }
        Assert.That(session.CountOlderAudioVoices(), Is.Zero);
        session.ApplyLiveEvent(ChannelTarget.Physical(0), [new NoteCutCommand()]);
        // Note Cut intentionally carries a short anti-click tail. Wait
        // through it before asserting exact newest-only PCM: four frames
        // after Cut can still include a tiny outgoing contribution.
        float[] newestOnly = new float[96];
        session.Render(212, newestOnly.Length, newestOnly);
        Assert.That(newestOnly[^1], Is.EqualTo(0.5f).Within(1e-5f),
            "the fresh newest-generation note and its sibling stay independent");
    }

    [TestCase(5)]
    [TestCase(85)]
    public void ReleasingAudibleNodeEnvelopeEditRefusesWholeGraphAndRetainsStickyCurrency(
        int editedReleaseMilliseconds)
    {
        // C4(n): audible Node Envelope is already releasing. A revised
        // curve cannot alter release PCM, completion or shared Filter
        // history. Simultaneous Constant/Filter changes must be atomic.
        const int noteOff = 12, refusedAt = 16, scalarAt = 22, freshAt = 24;
        ObjectId envelopeId = (ObjectId)72701U;
        ObjectId freshEnvelopeId = (ObjectId)72702U;
        ObjectId filterId = (ObjectId)72703U;
        Registry registry = new();

        static NodeEnvelopeCurve Curve(ObjectId id, int duration)
        {
            NodeEnvelopeDefinition definition = new(id, "C4(n) audible release");
            definition.ReplaceCurve([
                new(TimeSpan.Zero, 1.0),
                new(TimeSpan.FromMilliseconds(1), 1.0),
                new(TimeSpan.FromMilliseconds(duration), 0.0),
            ], 1, 1, EnvelopeSustainLoopMode.Forward);
            return new NodeEnvelopeCurve(definition);
        }
        NodeEnvelopeCurve oldCurve = Curve(envelopeId, 40);
        NodeEnvelopeCurve editedCurve = Curve(envelopeId, editedReleaseMilliseconds);
        registry.AddEnvelope(envelopeId, oldCurve);
        registry.AddEnvelope(freshEnvelopeId, Curve(freshEnvelopeId, 18));

        FmSynthSound Sound(ObjectId curveId, double level, int cutoff)
        {
            FilterInstance pole = new(new FilterDefinition(filterId,
                "C4(n) continuously running OnePole"));
            pole.SetStageParameter(1, "CutoffHz",
                FilterParameterValue.FromNumber(cutoff));
            return new FmSynthSound(new FmSynthGraph([
                new FmConstantNode(1, level),
                new FmEnvelopeNode(2, curveId),
                new FmOperatorNode(3, FmOperatorKind.Multiply, [1, 2]),
                new FmFilterNode(4, 3, pole, tailSeconds: 0.004),
            ], 4), registry,
                new NewNotePolicy(NewNoteAction.Continue, TimeSpan.Zero),
                soundResolver: registry, enableLiveMultiExpansion: true);
        }
        FmSynthSound original = Sound(envelopeId, 0.4, 240);
        registry.ReplaceEnvelope(envelopeId, editedCurve);
        FmSynthSound rejected = Sound(envelopeId, 0.9, 60);
        registry.ReplaceEnvelope(envelopeId, oldCurve);
        FmSynthSound compatible = Sound(envelopeId, 0.65, 240);
        FmSynthSound latestForFresh = Sound(freshEnvelopeId, 0.75, 150);
        registry.Add(SourceId, original);

        PlaybackSession actual = Session(registry, Schedule());
        PlaybackSession reference = Session(registry, Schedule());
        void Both(Action<PlaybackSession> action)
        {
            action(actual);
            action(reference);
        }
        void Compare(int start, int count)
        {
            float[] got = new float[count], expected = new float[count];
            actual.Render(start, count, got);
            reference.Render(start, count, expected);
            for (int i = 0; i < count; i++)
                Assert.That(got[i], Is.EqualTo(expected[i]).Within(1e-5f),
                    $"C4(n) original release/OnePole PCM at {start + i}");
        }
        Both(s => s.ApplyLiveEvent(ChannelTarget.Physical(0),
            [new StartNoteCommand(SourceId)]));
        Compare(0, noteOff);
        Both(s => s.ApplyLiveEvent(ChannelTarget.Physical(0),
            [new NoteOffCommand()]));
        Compare(noteOff, refusedAt - noteOff);
        PlaybackVoice sounding = actual.GetChannelState(0).CurrentVoice!;
        TimeSpan offTime = sounding.SoundState.NoteOffTime!.Value;
        RenderContext context = new(new RenderConfiguration(Rate, [
            new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0),
        ]));
        long originalEnd = original.GetEndFrameExclusive(
            context, sounding.SoundState)!.Value;
        Assert.Multiple(() =>
        {
            Assert.That(offTime, Is.EqualTo(TimeSpan.FromMilliseconds(noteOff)));
            Assert.That(originalEnd, Is.EqualTo(original.GetEndFrameExclusive(
                context, reference.GetChannelState(0).CurrentVoice!.SoundState)));
            Assert.That(originalEnd, Is.GreaterThan(freshAt + 10));
        });

        Assert.That(original.CanPrepareMultiExpansion(rejected, sounding.SoundState),
            Is.False, "a changed audible release must refuse whole-graph adoption");
        actual.AdoptLiveSynthParameters(SourceId, original, rejected);
        Assert.Multiple(() =>
        {
            Assert.That(sounding.Sound, Is.SameAs(original),
                "not even simultaneous Constant or Filter edits may partially publish");
            Assert.That(actual.CountOlderAudioVoices(), Is.EqualTo(1));
            Assert.That(sounding.SoundState.NoteOffTime, Is.EqualTo(offTime));
            Assert.That(original.GetEndFrameExclusive(context, sounding.SoundState),
                Is.EqualTo(originalEnd));
        });
        Compare(refusedAt, 4);

        // An authored successor to the REFUSED definition has no
        // migratable predecessor on this still-sounding old voice.
        FmSynthSound successor = Sound(envelopeId, 0.55, 240);
        actual.AdoptLiveSynthParameters(SourceId, rejected, successor);
        Assert.That(sounding.Sound, Is.SameAs(original));
        Assert.That(actual.CountOlderAudioVoices(), Is.EqualTo(1));
        Compare(20, scalarAt - 20);

        // An independent compatible revision explicitly addressed to the
        // actual sounding generation can still update the Constant.
        // Currency must remain sticky for the earlier missed edit.
        Assert.That(original.CanAdoptRunningState(compatible, sounding.SoundState),
            Is.True, "same frozen Node Envelope is compatible during release");
        actual.AdoptLiveSynthParameters(SourceId, original, compatible);
        reference.AdoptLiveSynthParameters(SourceId, original, compatible);
        Assert.Multiple(() =>
        {
            Assert.That(sounding.Sound, Is.SameAs(compatible));
            Assert.That(actual.CountOlderAudioVoices(), Is.EqualTo(1),
                "a later compatible edit cannot erase an actual missed revision");
            Assert.That(sounding.SoundState.NoteOffTime, Is.EqualTo(offTime));
            Assert.That(compatible.GetEndFrameExclusive(context, sounding.SoundState),
                Is.EqualTo(originalEnd));
        });
        Compare(scalarAt, freshAt - scalarAt);

        // Both sessions now start a new current-generation virtual voice;
        // it must remain independent of the old physical release.
        registry.Replace(SourceId, latestForFresh);
        Both(s => s.ApplyLiveEvent(ChannelTarget.Virtual(99),
            [new StartNoteCommand(SourceId)]));
        int untilLastTailFrame = checked((int)originalEnd - freshAt - 1);
        Assert.That(untilLastTailFrame, Is.GreaterThan(0));
        Compare(freshAt, untilLastTailFrame);
        Assert.Multiple(() =>
        {
            Assert.That(actual.GetChannelState(0).CurrentVoice, Is.SameAs(sounding));
            Assert.That(actual.CountOlderAudioVoices(), Is.EqualTo(1),
                "the original Envelope and Filter tail still count as old audio");
            Assert.That(sounding.SoundState.NoteOffTime, Is.EqualTo(offTime));
        });
        Compare((int)originalEnd - 1, 2);
        Assert.Multiple(() =>
        {
            Assert.That(actual.GetChannelState(0).CurrentVoice, Is.Null,
                "old physical voice retires at its authored release/tail end");
            Assert.That(actual.CountOlderAudioVoices(), Is.Zero,
                "older status clears on real retirement rather than new publication");
        });
        Compare((int)originalEnd + 1, 12);
        Assert.That(actual.CountOlderAudioVoices(), Is.Zero);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void NoteOffDuringDeferredSilenceTransitionCancelsOnsetAndPreservesRelease(
        bool startsUnassigned)
    {
        ObjectId firstId = (ObjectId)72511U;
        ObjectId secondId = (ObjectId)72512U;
        ObjectId siblingId = (ObjectId)72513U;
        ObjectId envelopeId = (ObjectId)72514U;
        Registry registry = new();
        registry.AddEnvelope(envelopeId, new OneHundredSixtyFrameRelease());
        SampleSound Sample(ObjectId id, float value)
            => new(new SampleDefinition(id, "C4(l) interrupted release",
                new ExternalAssetReference("c4-l-release.wav")),
                new MemorySampleData(Rate, 1,
                    Enumerable.Repeat(value, 800).ToArray()));
        registry.Add(firstId, Sample(firstId, 0.4f));
        registry.Add(secondId, Sample(secondId, 0.8f));
        registry.Add(siblingId, Sample(siblingId, 0.1f));
        FilterInstance pole = new(new FilterDefinition(
            (ObjectId)72515U, "C4(l) release pole"));
        pole.SetStageParameter(1, "CutoffHz",
            FilterParameterValue.FromNumber(240));

        FmSynthSound Sound(ObjectId id)
            => new(new FmSynthGraph([
                new FmSoundSourceNode(1, id, FmMultiNode.NeutralFrequencyHz),
                new FmSoundSourceNode(2, siblingId, FmMultiNode.NeutralFrequencyHz),
                new FmOperatorNode(3, FmOperatorKind.Add, [1, 2]),
                new FmEnvelopeNode(4, envelopeId),
                new FmOperatorNode(5, FmOperatorKind.Multiply, [3, 4]),
                new FmFilterNode(6, 5, pole, tailSeconds: 0.003),
            ], 6), registry, soundResolver: registry,
                enableLiveMultiExpansion: true);
        FmSynthSound initial = Sound(startsUnassigned ? ObjectId.None : firstId);
        FmSynthSound first = Sound(startsUnassigned ? firstId : ObjectId.None);
        FmSynthSound latest = Sound(secondId);
        registry.Add(SourceId, initial);

        PlaybackSession actual = Session(registry, Schedule());
        PlaybackSession oracle = Session(registry, Schedule());
        void Both(Action<PlaybackSession> action)
        {
            action(actual);
            action(oracle);
        }
        void Compare(int start, int count)
        {
            float[] got = new float[count], expected = new float[count];
            actual.Render(start, count, got);
            oracle.Render(start, count, expected);
            for (int i = 0; i < count; i++)
                Assert.That(got[i], Is.EqualTo(expected[i]).Within(1e-5f),
                    $"a missed onset must not change release, fading Source or OnePole at {start + i}");
        }

        Both(s => s.ApplyLiveEvent(ChannelTarget.Physical(0),
            [new StartNoteCommand(SourceId)]));
        Compare(0, 10);
        using FirPreparationScheduler worker = new();
        Both(s => s.AdoptLiveSynthParameters(SourceId, initial, first));
        if (startsUnassigned)
        {
            Both(s => s.QueuePendingFirControls(worker));
            Assert.That(SpinWait.SpinUntil(
                () => worker.CompletedMultiExpansionCount == 2,
                TimeSpan.FromSeconds(5)), Is.True);
            Both(s => s.AdoptPreparedFirControls());
        }
        Assert.That(actual.GetChannelState(0).CurrentVoice!.Sound,
            Is.SameAs(first));
        Compare(10, 10);

        actual.AdoptLiveSynthParameters(SourceId, first, latest);
        Assert.That(actual.CountOlderAudioVoices(), Is.Zero,
            "before Off this compatible Source edit is still pending");
        Compare(20, 10);
        Both(s => s.ApplyLiveEvent(ChannelTarget.Physical(0),
            [new NoteOffCommand()]));
        Assert.Multiple(() =>
        {
            Assert.That(actual.CountOlderAudioVoices(), Is.EqualTo(1),
                "the pending onset becomes definitively ineligible at original Off");
            Assert.That(actual.GetChannelState(0).CurrentVoice!.SoundState.NoteOffTime,
                Is.EqualTo(TimeSpan.FromMilliseconds(30)));
            Assert.That(actual.GetChannelState(0).CurrentVoice!.Sound,
                Is.SameAs(first), "the original fading Source survives Off");
        });
        registry.Replace(SourceId, latest);
        Both(s => s.ApplyLiveEvent(ChannelTarget.Virtual(99),
            [new StartNoteCommand(SourceId)]));
        actual.AdoptPreparedFirControls();
        actual.QueuePendingFirControls(worker);
        Assert.That(worker.CompletedMultiExpansionCount,
            Is.EqualTo(startsUnassigned ? 2 : 0),
            "Note Off cannot schedule a deferred fresh Sample onset");

        Compare(30, 70);
        Assert.That(actual.CountOlderAudioVoices(), Is.EqualTo(1));
        Both(s => s.ApplyLiveEvent(ChannelTarget.Physical(0),
            [new NoteOffCommand()]));
        Assert.That(actual.GetChannelState(0).CurrentVoice!.SoundState.NoteOffTime,
            Is.EqualTo(TimeSpan.FromMilliseconds(30)),
            "repeated Off never resets either release or Source fade age");
        Compare(100, 120);
        actual.AdoptPreparedFirControls();
        actual.QueuePendingFirControls(worker);
        Assert.Multiple(() =>
        {
            Assert.That(actual.GetChannelState(0).CurrentVoice, Is.Null);
            Assert.That(actual.CountOlderAudioVoices(), Is.Zero,
                "older status clears when the releasing note and Filter tail retire");
            Assert.That(worker.CompletedMultiExpansionCount,
                Is.EqualTo(startsUnassigned ? 2 : 0));
            Assert.That(worker.Fault, Is.Null);
        });
        float[] newest = new float[5], expectedNewest = new float[5];
        actual.Render(220, newest.Length, newest);
        oracle.Render(220, expectedNewest.Length, expectedNewest);
        Assert.That(newest, Is.EqualTo(expectedNewest).Within(1e-5f),
            "the independent newest-generation note continues playing");
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public void DeferredSampleAndFrequencyModeRevisionKeepsBothPitchHistoriesBeforeOnePole(
        bool latestConstant, bool latestExponential)
    {
        // C4(m): the same held note first crossfades A→B, then receives
        // C followed by D *during* that 100-ms transition. D changes
        // SourceId, FrequencyHz and the Constant/exponential rate modes
        // simultaneously. Three independent Source-only render histories
        // and one uninterrupted OnePole are the PCM oracle.
        const int firstOnset = 10, firstFadeEnd = 110;
        const int secondOnset = 111;
        const int total = 240;
        ObjectId aId = (ObjectId)72601U;
        ObjectId bId = (ObjectId)72602U;
        ObjectId cId = (ObjectId)72603U;
        ObjectId dId = (ObjectId)72604U;
        ObjectId siblingId = (ObjectId)72605U;
        ObjectId referenceId = (ObjectId)72606U;
        double neutral = FmMultiNode.NeutralFrequencyHz;
        Registry registry = new();

        SampleSound Sample(ObjectId id, Func<int, float> f)
            => new(new SampleDefinition(id, "C4(m) independent pitch",
                new ExternalAssetReference("c4-m-source.wav")),
                new MemorySampleData(Rate, 1,
                    Enumerable.Range(0, 2400).Select(f).ToArray()));
        registry.Add(aId, Sample(aId, i => (float)(0.04 + (i % 67) * 0.002)));
        registry.Add(bId, Sample(bId, i => (float)(0.08 + (i % 83) * 0.003)));
        registry.Add(cId, Sample(cId, i => (float)(0.29 + (i % 11) * 0.002)));
        registry.Add(dId, Sample(dId, i => (float)(0.11 + (i % 97) * 0.002)));
        registry.Add(siblingId, Sample(siblingId, _ => 0.10f));

        FilterInstance pole = new(new FilterDefinition(
            (ObjectId)72607U, "C4(m) continuously running pole"));
        pole.SetStageParameter(1, "CutoffHz",
            FilterParameterValue.FromNumber(240));

        FmSynthSound Full(ObjectId id, double hz, bool constant,
            bool exponential)
            => new(new FmSynthGraph([
                new FmConstantNode(0, 1.25),
                new FmSoundSourceNode(1, id, hz, multiplierNodeId: 0,
                    constant: constant, exponentialMultiplier: exponential),
                new FmSoundSourceNode(2, siblingId, neutral),
                new FmOperatorNode(3, FmOperatorKind.Add, [1, 2]),
                new FmFilterNode(4, 3, pole, tailSeconds: 0.003),
            ], 4), registry,
                new NewNotePolicy(NewNoteAction.Continue, TimeSpan.Zero),
                soundResolver: registry, enableLiveMultiExpansion: true);

        FmSynthSound Bare(ObjectId id, double hz, bool constant,
            bool exponential)
            => new(new FmSynthGraph([
                new FmConstantNode(0, 1.25),
                new FmSoundSourceNode(1, id, hz, multiplierNodeId: 0,
                    constant: constant, exponentialMultiplier: exponential),
            ], 1), registry, soundResolver: registry,
                enableLiveMultiExpansion: true);

        FmSynthSound a = Full(aId, neutral * 1.4, false, true);
        FmSynthSound b = Full(bId, neutral * 0.75, false, false);
        FmSynthSound superseded = Full(cId, neutral * 3.5, true, false);
        FmSynthSound latest = Full(dId, neutral * 2,
            latestConstant, latestExponential);
        registry.Add(SourceId, a);

        RenderContext context = new(new RenderConfiguration(Rate, [
            new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0),
        ]));
        float[] A = new float[total];
        float[] B = new float[total - firstOnset];
        float[] D = new float[total - secondOnset];
        FmSynthSound bareA = Bare(aId, neutral * 1.4, false, true);
        FmSynthSound bareB = Bare(bId, neutral * 0.75, false, false);
        FmSynthSound bareD = Bare(dId, neutral * 2,
            latestConstant, latestExponential);
        bareA.Render(context, bareA.CreateState(), 0, A.Length, A);
        bareB.Render(context, bareB.CreateState(), 0, B.Length, B);
        bareD.Render(context, bareD.CreateState(), 0, D.Length, D);

        static double Weight(int start, int frame)
        {
            double t = Math.Clamp((frame - start) / 100.0, 0, 1);
            return t * t * (3 - 2 * t);
        }
        float[] bakedInput = new float[total];
        for (int frame = 0; frame < total; frame++)
        {
            double source;
            if (frame < firstOnset)
                source = A[frame];
            else if (frame <= firstFadeEnd)
            {
                double w = Weight(firstOnset, frame);
                source = A[frame] * (1 - w) + B[frame - firstOnset] * w;
            }
            else if (frame < secondOnset)
                source = B[frame - firstOnset];
            else
            {
                double w = Weight(secondOnset, frame);
                source = B[frame - firstOnset] * (1 - w)
                    + D[frame - secondOnset] * w;
            }
            bakedInput[frame] = (float)(source + 0.10);
        }
        // This baked Sample is already on the output clock; applying a
        // Source pitch modifier again would invalidate the oracle.
        Registry referenceRegistry = new();
        referenceRegistry.Add(referenceId,
            new SampleSound(new SampleDefinition(referenceId,
                "C4(m) baked reference",
                new ExternalAssetReference("c4-m-reference.wav")),
                new MemorySampleData(Rate, 1, bakedInput)));
        FmSynthSound reference = new(new FmSynthGraph([
            new FmSoundSourceNode(1, referenceId, neutral),
            new FmFilterNode(2, 1, pole, tailSeconds: 0.003),
        ], 2), referenceRegistry, soundResolver: referenceRegistry,
            enableLiveMultiExpansion: true);
        float[] expected = new float[total];
        reference.Render(context, reference.CreateState(), 0,
            expected.Length, expected);

        PlaybackSession session = Session(registry, Schedule());
        float[] actual = new float[total];
        session.ApplyLiveEvent(ChannelTarget.Physical(0),
            [new StartNoteCommand(SourceId)]);
        session.Render(0, firstOnset,
            actual.AsSpan(0, firstOnset));
        using FirPreparationScheduler worker = new();
        session.AdoptLiveSynthParameters(SourceId, a, b);
        session.QueuePendingFirControls(worker);
        Assert.That(SpinWait.SpinUntil(
            () => worker.CompletedMultiExpansionCount >= 1,
            TimeSpan.FromSeconds(5)), Is.True);
        session.AdoptPreparedFirControls();
        Assert.That(session.GetChannelState(0).CurrentVoice!.Sound,
            Is.SameAs(b));
        session.Render(10, 10, actual.AsSpan(10, 10));

        session.AdoptLiveSynthParameters(SourceId, b, superseded);
        Assert.That(session.CountOlderAudioVoices(), Is.Zero,
            "a C SourceId+frequency/mode edit during A→B must defer, not refuse");
        session.Render(20, 10, actual.AsSpan(20, 10));
        session.AdoptLiveSynthParameters(SourceId, superseded, latest);
        Assert.That(session.CountOlderAudioVoices(), Is.Zero,
            "the latest D supersedes C without mutating A→B's pitch history");
        session.Render(30, firstFadeEnd - 30 + 1,
            actual.AsSpan(30, firstFadeEnd - 30 + 1));
        Assert.Multiple(() =>
        {
            Assert.That(session.GetChannelState(0).CurrentVoice!.Sound,
                Is.SameAs(b), "B still owns the note until A→B's original fade is over");
            Assert.That(session.CountOlderAudioVoices(), Is.Zero);
            Assert.That(worker.CompletedMultiExpansionCount, Is.EqualTo(1),
                "obsolete C and latest D must not be built during A→B");
        });

        session.AdoptPreparedFirControls();
        session.QueuePendingFirControls(worker);
        Assert.That(SpinWait.SpinUntil(
            () => worker.CompletedMultiExpansionCount >= 2,
            TimeSpan.FromSeconds(5)), Is.True,
            "the off-PCM worker creates the latest D only after A→B");
        session.AdoptPreparedFirControls();
        Assert.Multiple(() =>
        {
            Assert.That(session.GetChannelState(0).CurrentVoice!.Sound,
                Is.SameAs(latest), "only the complete D revision may publish");
            Assert.That(session.CountOlderAudioVoices(), Is.Zero);
            Assert.That(worker.CompletedMultiExpansionCount, Is.EqualTo(2),
                "C must never be prepared or adopted");
            Assert.That(worker.Fault, Is.Null);
        });
        session.Render(secondOnset, total - secondOnset,
            actual.AsSpan(secondOnset));

        for (int frame = 0; frame < total; frame++)
            Assert.That(actual[frame],
                Is.EqualTo(expected[frame]).Within(3e-5f),
                $"one continuous pole, independently integrated A/B/D pitch and latest-only Source at frame {frame} ({latestConstant}/{latestExponential})");
        Assert.That(session.CountOlderAudioVoices(), Is.Zero);
        Assert.That(session.GetChannelState(0).CurrentVoice!.Sound,
            Is.SameAs(latest));
    }

    [Test]
    public void RepeatedSampleSourceEditsDuringLiveFadeDeferAndAdoptOnlyLatestAfterCompletion()
    {
        ObjectId aId = (ObjectId)72221U;
        ObjectId bId = (ObjectId)72222U;
        ObjectId cId = (ObjectId)72223U;
        ObjectId dId = (ObjectId)72224U;
        Registry registry = new();

        SampleSound Sample(ObjectId id, float level)
            => new(new SampleDefinition(id, "C4(h) source",
                new ExternalAssetReference("c4-h-source.wav")),
                new MemorySampleData(Rate, 1,
                    Enumerable.Repeat(level, 800).ToArray()));
        registry.Add(aId, Sample(aId, 0.2f));
        registry.Add(bId, Sample(bId, 0.6f));
        registry.Add(cId, Sample(cId, 0.8f));
        registry.Add(dId, Sample(dId, 0.4f));

        FmSynthSound Sound(ObjectId id)
            => new(new FmSynthGraph([
                new FmSoundSourceNode(1, id,
                    FmMultiNode.NeutralFrequencyHz),
            ], 1), registry,
                new NewNotePolicy(NewNoteAction.Continue, TimeSpan.Zero),
                soundResolver: registry, enableLiveMultiExpansion: true);

        FmSynthSound a = Sound(aId);
        FmSynthSound b = Sound(bId);
        FmSynthSound c = Sound(cId);
        FmSynthSound d = Sound(dId);
        registry.Add(SourceId, a);
        PlaybackSession session = Session(registry, Schedule());
        session.ApplyLiveEvent(ChannelTarget.Physical(0),
            [new StartNoteCommand(SourceId)]);
        session.Render(0, 10, new float[10]);

        using FirPreparationScheduler worker = new();
        session.AdoptLiveSynthParameters(SourceId, a, b);
        session.QueuePendingFirControls(worker);
        Assert.That(SpinWait.SpinUntil(
            () => worker.CompletedMultiExpansionCount >= 1,
            TimeSpan.FromSeconds(5)), Is.True);
        session.AdoptPreparedFirControls();
        Assert.That(session.GetChannelState(0).CurrentVoice!.Sound,
            Is.SameAs(b));
        session.Render(10, 10, new float[10]);

        // C arrives 10 ms into A→B's 100-ms Source-local crossfade,
        // then D supersedes C after a further 10 ms. Never discard A's
        // remaining audible history, start C just to abandon it, or
        // reinterpret the current B readhead as C's prior history.
        session.AdoptLiveSynthParameters(SourceId, b, c);
        Assert.That(session.CountOlderAudioVoices(), Is.Zero,
            "a safely deferred live Sample edit must not be classified as a missed revision");
        float[] firstRamp = new float[10];
        session.Render(20, firstRamp.Length, firstRamp);
        session.AdoptLiveSynthParameters(SourceId, c, d);
        Assert.That(session.CountOlderAudioVoices(), Is.Zero,
            "the pending C must be replaceable with D without losing the A→B fade");

        registry.Replace(SourceId, d);
        session.ApplyLiveEvent(ChannelTarget.Virtual(99),
            [new StartNoteCommand(SourceId)]);

        // The newly started voice immediately uses D, while the original
        // continues its precise A→B fade to frame 110 with no interruption.
        float[] remainder = new float[81];
        session.Render(30, remainder.Length, remainder);
        Assert.Multiple(() =>
        {
            Assert.That(remainder[0], Is.EqualTo(
                0.2f * (1 - 0.104f) + 0.6f * 0.104f + 0.4f)
                .Within(0.002f));
            Assert.That(remainder[30], Is.EqualTo(0.8f).Within(1e-4f),
                "frame 60 contains A→B at half weight plus fresh D");
            Assert.That(remainder[^1], Is.EqualTo(1.0f).Within(1e-4f),
                "frame 110 completes the original fade, not an early C or D replacement");
            Assert.That(session.CountOlderAudioVoices(), Is.Zero);
            Assert.That(session.GetChannelState(0).CurrentVoice!.Sound,
                Is.SameAs(b), "second edit is deferred until outgoing A fully retires");
        });

        // The exact first Source fade is over. Prepare a new D invocation
        // off PCM and begin B→D at the current frame; there must never
        // be a worker-created C onset.
        session.AdoptPreparedFirControls();
        session.QueuePendingFirControls(worker);
        Assert.That(SpinWait.SpinUntil(
            () => worker.CompletedMultiExpansionCount >= 2,
            TimeSpan.FromSeconds(5)), Is.True,
            "the deferred latest D must be prepared after the initial fade");
        session.AdoptPreparedFirControls();
        Assert.Multiple(() =>
        {
            Assert.That(session.GetChannelState(0).CurrentVoice!.Sound,
                Is.SameAs(d));
            Assert.That(session.CountOlderAudioVoices(), Is.Zero);
            Assert.That(worker.Fault, Is.Null);
            Assert.That(worker.CompletedMultiExpansionCount, Is.EqualTo(2),
                "only B and D, never superseded C, should create replacement onsets");
        });

        float[] secondRamp = new float[101];
        session.Render(111, secondRamp.Length, secondRamp);
        Assert.Multiple(() =>
        {
            Assert.That(secondRamp[0], Is.EqualTo(1.0f).Within(1e-5f),
                "new B→D fade starts from exactly the audible end of A→B");
            Assert.That(secondRamp[50], Is.EqualTo(0.9f).Within(1e-5f),
                "the second 100-ms smoothstep mixes B=0.6 and D=0.4, plus fresh D");
            Assert.That(secondRamp[^1], Is.EqualTo(0.8f).Within(1e-5f));
            Assert.That(session.CountOlderAudioVoices(), Is.Zero);
        });

        session.ApplyLiveEvent(ChannelTarget.Physical(0),
            [new NoteCutCommand()]);
        Assert.That(session.CountOlderAudioVoices(), Is.Zero);
        float[] freshOnly = new float[4];
        session.Render(212, freshOnly.Length, freshOnly);
        Assert.That(freshOnly[^1], Is.EqualTo(0.4f).Within(1e-4f),
            "the independent newest-generation voice is unaffected by older voice retirement");
    }


    [Test]
    public void NoteOffDuringDeferredSampleEditRetainsOriginalReleaseAndMarksOldImmediately()
    {
        ObjectId firstId = (ObjectId)72401U;
        ObjectId secondId = (ObjectId)72402U;
        ObjectId newestId = (ObjectId)72403U;
        ObjectId sustainId = (ObjectId)72404U;
        Registry registry = new();
        registry.AddEnvelope(sustainId, new OneHundredSixtyFrameRelease());

        SampleSound Sample(ObjectId id, float value)
            => new(new SampleDefinition(id, "C4(i) release",
                new ExternalAssetReference("c4-i-sample.wav")),
                new MemorySampleData(Rate, 1,
                    Enumerable.Repeat(value, 800).ToArray()));
        registry.Add(firstId, Sample(firstId, 0.2f));
        registry.Add(secondId, Sample(secondId, 0.6f));
        registry.Add(newestId, Sample(newestId, 0.9f));

        FmSynthSound Sound(ObjectId id)
        {
            FilterInstance pole = new(new FilterDefinition(
                (ObjectId)72405U, "C4(i) release pole"));
            pole.SetStageParameter(1, "CutoffHz",
                FilterParameterValue.FromNumber(240));
            return new(new FmSynthGraph([
                new FmSoundSourceNode(1, id,
                    FmMultiNode.NeutralFrequencyHz),
                new FmEnvelopeNode(2, sustainId),
                new FmOperatorNode(3, FmOperatorKind.Multiply, [1, 2]),
                new FmFilterNode(4, 3, pole, tailSeconds: 0.003),
            ], 4), registry, soundResolver: registry,
                enableLiveMultiExpansion: true);
        }
        FmSynthSound first = Sound(firstId);
        FmSynthSound second = Sound(secondId);
        FmSynthSound newest = Sound(newestId);
        registry.Add(SourceId, first);

        PlaybackSession actual = Session(registry, Schedule());
        PlaybackSession oracle = Session(registry, Schedule());
        void Both(Action<PlaybackSession> action)
        {
            action(actual);
            action(oracle);
        }
        void Compare(int start, int count)
        {
            float[] got = new float[count], expected = new float[count];
            actual.Render(start, count, got);
            oracle.Render(start, count, expected);
            for (int i = 0; i < count; i++)
                Assert.That(got[i], Is.EqualTo(expected[i]).Within(1e-5f),
                    $"deferred edit must preserve original A/B release and pole at {start + i}");
        }

        Both(s => s.ApplyLiveEvent(ChannelTarget.Physical(0),
            [new StartNoteCommand(SourceId)]));
        Compare(0, 10);
        using FirPreparationScheduler worker = new();
        Both(s => s.AdoptLiveSynthParameters(SourceId, first, second));
        actual.QueuePendingFirControls(worker);
        oracle.QueuePendingFirControls(worker);
        Assert.That(SpinWait.SpinUntil(
            () => worker.CompletedMultiExpansionCount >= 2,
            TimeSpan.FromSeconds(5)), Is.True);
        Both(s => s.AdoptPreparedFirControls());
        Compare(10, 10);

        actual.AdoptLiveSynthParameters(SourceId, second, newest);
        Assert.That(actual.CountOlderAudioVoices(), Is.Zero,
            "the future Sample is a compatible deferred edit before Note Off");
        Compare(20, 10);

        Both(s => s.ApplyLiveEvent(ChannelTarget.Physical(0),
            [new NoteOffCommand()]));
        Assert.Multiple(() =>
        {
            Assert.That(actual.GetChannelState(0).CurrentVoice!.SoundState.NoteOffTime,
                Is.EqualTo(TimeSpan.FromMilliseconds(30)));
            Assert.That(actual.CountOlderAudioVoices(), Is.EqualTo(1),
                "the deferred fresh onset becomes definitively ineligible at Note Off, not at a later worker poll");
            Assert.That(actual.GetChannelState(0).CurrentVoice!.Sound,
                Is.SameAs(second));
        });

        // This is the next authored definition for new notes, but it
        // must not restart the old note's release or outgoing Sample.
        registry.Replace(SourceId, newest);
        Both(s => s.ApplyLiveEvent(ChannelTarget.Virtual(99),
            [new StartNoteCommand(SourceId)]));
        actual.AdoptPreparedFirControls();
        actual.QueuePendingFirControls(worker);
        Assert.That(worker.CompletedMultiExpansionCount, Is.EqualTo(2),
            "an ineligible deferred replacement must never enqueue the third Sample");

        Compare(30, 40);
        Assert.That(actual.CountOlderAudioVoices(), Is.EqualTo(1));
        Both(s => s.ApplyLiveEvent(ChannelTarget.Physical(0),
            [new NoteOffCommand()]));
        Assert.That(actual.GetChannelState(0).CurrentVoice!.SoundState.NoteOffTime,
            Is.EqualTo(TimeSpan.FromMilliseconds(30)),
            "repeated Off cannot restart the release clock");
        Compare(70, 60);
        actual.AdoptPreparedFirControls();
        actual.QueuePendingFirControls(worker);
        Assert.That(actual.GetChannelState(0).CurrentVoice!.Sound,
            Is.SameAs(second), "finishing A/B must not activate newest Sample after Off");
        Compare(130, 85);

        Assert.Multiple(() =>
        {
            Assert.That(actual.CountOlderAudioVoices(), Is.Zero,
                "older currency clears when the original release and Filter tail retire");
            Assert.That(actual.GetChannelState(0).CurrentVoice, Is.Null);
            Assert.That(worker.CompletedMultiExpansionCount, Is.EqualTo(2));
            Assert.That(worker.Fault, Is.Null);
        });
        float[] fresh = new float[5];
        actual.Render(215, fresh.Length, fresh);
        oracle.Render(215, fresh.Length, new float[5]);
        Assert.That(fresh[^1], Is.GreaterThan(0.8f),
            "the new-generation virtual note remains independent of retired old release");
    }

    [Test]
    public void DeferredSampleEditCannotRestartExpiredIncomingSampleFromFilterTail()
    {
        ObjectId firstId = (ObjectId)72411U;
        ObjectId shortId = (ObjectId)72412U;
        ObjectId newestId = (ObjectId)72413U;
        Registry registry = new();

        SampleSound Sample(ObjectId id, float value, int frames)
            => new(new SampleDefinition(id, "C4(i) finite",
                new ExternalAssetReference("c4-i-finite.wav")),
                new MemorySampleData(Rate, 1,
                    Enumerable.Repeat(value, frames).ToArray()));
        registry.Add(firstId, Sample(firstId, 0.2f, 800));
        registry.Add(shortId, Sample(shortId, 0.6f, 15));
        registry.Add(newestId, Sample(newestId, 0.9f, 800));

        FmSynthSound Sound(ObjectId id)
        {
            FilterInstance pole = new(new FilterDefinition(
                (ObjectId)72414U, "C4(i) finite pole"));
            pole.SetStageParameter(1, "CutoffHz",
                FilterParameterValue.FromNumber(240));
            return new(new FmSynthGraph([
                new FmSoundSourceNode(1, id,
                    FmMultiNode.NeutralFrequencyHz),
                new FmFilterNode(2, 1, pole, tailSeconds: 0.040),
            ], 2), registry, soundResolver: registry,
                enableLiveMultiExpansion: true);
        }
        FmSynthSound first = Sound(firstId);
        FmSynthSound second = Sound(shortId);
        FmSynthSound newest = Sound(newestId);
        registry.Add(SourceId, first);
        PlaybackSession actual = Session(registry, Schedule());
        PlaybackSession oracle = Session(registry, Schedule());

        void Both(Action<PlaybackSession> action)
        {
            action(actual);
            action(oracle);
        }
        void Compare(int start, int count)
        {
            float[] got = new float[count], expected = new float[count];
            actual.Render(start, count, got);
            oracle.Render(start, count, expected);
            for (int i = 0; i < count; i++)
                Assert.That(got[i], Is.EqualTo(expected[i]).Within(1e-5f),
                    $"a finished incoming Sample may not resurrect over a pole tail at {start + i}");
        }
        Both(s => s.ApplyLiveEvent(ChannelTarget.Physical(0),
            [new StartNoteCommand(SourceId)]));
        Compare(0, 10);
        using FirPreparationScheduler worker = new();
        Both(s => s.AdoptLiveSynthParameters(SourceId, first, second));
        actual.QueuePendingFirControls(worker);
        oracle.QueuePendingFirControls(worker);
        Assert.That(SpinWait.SpinUntil(
            () => worker.CompletedMultiExpansionCount >= 2,
            TimeSpan.FromSeconds(5)), Is.True);
        Both(s => s.AdoptPreparedFirControls());
        Compare(10, 20);

        // B's 15-frame readhead ended at frame 25, but its outgoing
        // A remains audible through the original fade ending at 110.
        actual.AdoptLiveSynthParameters(SourceId, second, newest);
        Assert.That(actual.CountOlderAudioVoices(), Is.Zero,
            "a deferred candidate is not itself an older-audio refusal");
        actual.AdoptPreparedFirControls();
        Compare(30, 81);

        // The original fade is finished and no independent Source
        // remains active: the terminal B Sample is already at natural
        // EOF. The old note has retired rather than using a deferred
        // edit to create an onset after its finite lifetime. The
        // existing Source EOF contract must not be artificially
        // extended by a completed fade or a speculative Filter tail.
        Assert.That(actual.GetChannelState(0).CurrentVoice, Is.Null);
        actual.AdoptPreparedFirControls();
        actual.QueuePendingFirControls(worker);
        Assert.Multiple(() =>
        {
            Assert.That(actual.CountOlderAudioVoices(), Is.Zero,
                "a finished old note cannot keep a sticky older-audio mark");
            Assert.That(worker.CompletedMultiExpansionCount, Is.EqualTo(2),
                "the deferred C must not be constructed for a retired voice");
        });

        Compare(111, 59);
        Assert.Multiple(() =>
        {
            Assert.That(actual.GetChannelState(0).CurrentVoice, Is.Null,
                "the old note and its Filter tail should retire at their original deadline");
            Assert.That(actual.CountOlderAudioVoices(), Is.Zero);
            Assert.That(worker.CompletedMultiExpansionCount, Is.EqualTo(2));
        });
        registry.Replace(SourceId, newest);
        Both(s => s.ApplyLiveEvent(ChannelTarget.Virtual(99),
            [new StartNoteCommand(SourceId)]));
        float[] latest = new float[50], expectedLatest = new float[50];
        actual.Render(170, latest.Length, latest);
        oracle.Render(170, expectedLatest.Length, expectedLatest);
        for (int i = 0; i < latest.Length; i++)
            Assert.That(latest[i], Is.EqualTo(expectedLatest[i]).Within(1e-5f));
        Assert.That(latest[^1], Is.GreaterThan(0.8f),
            "only a fresh note creates the new Sample invocation");
        Assert.That(worker.Fault, Is.Null);
    }

    [Test]
    public void DeferredReplacementMayReviveFinishedSampleWhileSiblingKeepsNoteActive()
    {
        ObjectId aId = (ObjectId)72421U;
        ObjectId shortId = (ObjectId)72422U;
        ObjectId newestId = (ObjectId)72423U;
        ObjectId siblingId = (ObjectId)72424U;
        Registry registry = new();
        SampleSound Sample(ObjectId id, float value, int frames)
            => new(new SampleDefinition(id, "C4(i) sibling",
                new ExternalAssetReference("c4-i-sibling.wav")),
                new MemorySampleData(Rate, 1,
                    Enumerable.Repeat(value, frames).ToArray()));
        registry.Add(aId, Sample(aId, 0.2f, 800));
        registry.Add(shortId, Sample(shortId, 0.6f, 15));
        registry.Add(newestId, Sample(newestId, 0.9f, 800));
        registry.Add(siblingId, Sample(siblingId, 0.1f, 800));

        FilterInstance pole = new(new FilterDefinition(
            (ObjectId)72425U, "C4(i) shared pole"));
        pole.SetStageParameter(1, "CutoffHz",
            FilterParameterValue.FromNumber(240));

        FmSynthSound Sound(ObjectId id)
            => new(new FmSynthGraph([
                new FmSoundSourceNode(1, id,
                    FmMultiNode.NeutralFrequencyHz),
                new FmSoundSourceNode(2, siblingId,
                    FmMultiNode.NeutralFrequencyHz),
                new FmOperatorNode(3, FmOperatorKind.Add, [1, 2]),
                new FmFilterNode(4, 3, pole, tailSeconds: 0.003),
            ], 4), registry, soundResolver: registry,
                enableLiveMultiExpansion: true);

        FmSynthSound first = Sound(aId);
        FmSynthSound second = Sound(shortId);
        FmSynthSound newest = Sound(newestId);
        registry.Add(SourceId, first);
        PlaybackSession session = Session(registry, Schedule());
        session.ApplyLiveEvent(ChannelTarget.Physical(0),
            [new StartNoteCommand(SourceId)]);
        session.Render(0, 10, new float[10]);

        using FirPreparationScheduler worker = new();
        session.AdoptLiveSynthParameters(SourceId, first, second);
        session.QueuePendingFirControls(worker);
        Assert.That(SpinWait.SpinUntil(
            () => worker.CompletedMultiExpansionCount >= 1,
            TimeSpan.FromSeconds(5)), Is.True);
        session.AdoptPreparedFirControls();
        session.Render(10, 20, new float[20]);

        // B ended at frame 25; A's 100-ms outgoing crossfade has not
        // ended. The additional sibling Sample is still sounding.
        session.AdoptLiveSynthParameters(SourceId, second, newest);
        Assert.That(session.CountOlderAudioVoices(), Is.Zero);
        float[] firstFade = new float[81];
        session.Render(30, firstFade.Length, firstFade);
        Assert.Multiple(() =>
        {
            Assert.That(firstFade[79], Is.GreaterThan(0.0f),
                "the last A contribution and the continuing sibling remain audible");
            Assert.That(session.GetChannelState(0).CurrentVoice!.Sound,
                Is.SameAs(second));
            Assert.That(session.CountOlderAudioVoices(), Is.Zero);
        });

        // Unlike the one-Source note, this note is still genuinely
        // active through the independent sibling; the expired B
        // branch can be replaced with a fresh C invocation off-thread.
        session.AdoptPreparedFirControls();
        session.QueuePendingFirControls(worker);
        Assert.That(SpinWait.SpinUntil(
            () => worker.CompletedMultiExpansionCount >= 2,
            TimeSpan.FromSeconds(5)), Is.True,
            "the independently sounding sibling permits a new prepared C onset");
        session.AdoptPreparedFirControls();
        Assert.Multiple(() =>
        {
            Assert.That(session.GetChannelState(0).CurrentVoice!.Sound,
                Is.SameAs(newest));
            Assert.That(session.CountOlderAudioVoices(), Is.Zero);
            Assert.That(worker.Fault, Is.Null);
            Assert.That(worker.CompletedMultiExpansionCount, Is.EqualTo(2));
        });

        float[] secondFade = new float[101];
        session.Render(111, secondFade.Length, secondFade);
        Assert.Multiple(() =>
        {
            Assert.That(secondFade[0], Is.EqualTo(0.1f).Within(0.02f),
                "the fresh C starts silent, preserving the sibling and its shared Filter history");
            Assert.That(secondFade[50], Is.EqualTo(0.55f).Within(0.025f),
                "the new 100-ms B→C mix contributes 0.45 plus the sibling 0.1");
            Assert.That(secondFade[^1], Is.EqualTo(1.0f).Within(0.01f));
            Assert.That(session.CountOlderAudioVoices(), Is.Zero);
        });
    }

    [TestCase(NewNoteAction.Cut, false)]
    [TestCase(NewNoteAction.Continue, true)]
    [TestCase(NewNoteAction.Off, true)]
    [TestCase(NewNoteAction.Fade, true)]
    public void PhysicalNnaDisplacesNestedFiniteSourceAndReclaimsLongBlock(
        NewNoteAction action, bool migrated)
    {
        Registry resolver = new();
        FmSynthSound synth = BuildNested(resolver, loop: false,
            action: action, inheritedEnvelope: false);
        resolver.Add(SourceId, synth);
        PlaybackSession session = Session(resolver, Schedule(
            Event(0, ChannelTarget.Physical(0), new StartNoteCommand(SourceId)),
            Event(2, ChannelTarget.Physical(0), new StartNoteCommand(SourceId))));

        float[] whole = new float[32];
        session.Render(0, 3, whole.AsSpan(0, 3));
        PlaybackVoice? current = session.GetChannelState(0).CurrentVoice;
        Assert.That(current, Is.Not.Null);
        Assert.That(current!.StartFrame, Is.EqualTo(2));
        Assert.That(session.VirtualVoices.Count, Is.EqualTo(migrated ? 1 : 0));
        if (migrated)
        {
            PlaybackVoice old = session.VirtualVoices.Single();
            Assert.Multiple(() =>
            {
                Assert.That(old.StartFrame, Is.Zero);
                Assert.That(old.SoundState, Is.Not.SameAs(current.SoundState),
                    "NNA must preserve the old per-voice graph and child histories");
                Assert.That(old.Sound, Is.SameAs(current.Sound),
                    "prepared Synth coefficients may be shared, never voice state");
                Assert.That(old.DisplacedOutputFilterState,
                    Is.Null, "no host Filter has been selected to fork");
            });
            if (action == NewNoteAction.Off)
                Assert.That(old.SoundState.NoteOffTime,
                    Is.EqualTo(TimeSpan.FromMilliseconds(2)));
            if (action == NewNoteAction.Fade)
                Assert.That(old.FadeEndFrameExclusive, Is.EqualTo(6));
        }

        int lookupsAfterBinding = resolver.SoundLookups;
        session.Render(3, 29, whole.AsSpan(3, 29));
        Assert.Multiple(() =>
        {
            Assert.That(session.VirtualVoices, Is.Empty,
                "one long block must retire all finished NNA sources immediately");
            Assert.That(session.GetChannelState(0).CurrentVoice, Is.Null);
            Assert.That(whole.Skip(10), Is.All.EqualTo(0f).Within(1e-7f),
                "anti-click residue below floating-point tolerance is silence");
            Assert.That(resolver.SoundLookups, Is.EqualTo(lookupsAfterBinding),
                "no asset resolution may occur during settled PCM rendering");
        });

        Registry otherResolver = new();
        FmSynthSound otherSynth = BuildNested(otherResolver, loop: false,
            action: action, inheritedEnvelope: false);
        otherResolver.Add(SourceId, otherSynth);
        PlaybackSession split = Session(otherResolver, Schedule(
            Event(0, ChannelTarget.Physical(0), new StartNoteCommand(SourceId)),
            Event(2, ChannelTarget.Physical(0), new StartNoteCommand(SourceId))));
        float[] pieces = new float[whole.Length];
        int[] blocks = [1, 1, 2, 3, 1, 8, 16];
        int frame = 0;
        foreach (int count in blocks)
        {
            split.Render(frame, count, pieces.AsSpan(frame, count));
            frame += count;
        }
        Assert.Multiple(() =>
        {
            Assert.That(frame, Is.EqualTo(32));
            Assert.That(pieces, Is.EqualTo(whole).Within(1e-6f),
                "displaced graph Filter histories must be chunk-invariant");
            Assert.That(split.VirtualVoices, Is.Empty);
            Assert.That(split.GetChannelState(0).CurrentVoice, Is.Null);
            Assert.That(otherResolver.SoundLookups,
                Is.EqualTo(resolver.SoundLookups));
        });
    }

    [TestCase(NewNoteAction.Off)]
    [TestCase(NewNoteAction.Fade)]
    public void VirtualNnaPreservesLoopingInstrumentReleaseAndFilterHistory(
        NewNoteAction action)
    {
        Registry resolver = new();
        FmSynthSound synth = BuildNested(resolver, loop: true,
            action: action, inheritedEnvelope: true);
        resolver.Add(SourceId, synth);
        PlaybackSession session = Session(resolver, Schedule());
        session.ApplyLiveEvent(ChannelTarget.Virtual(7),
            [new StartNoteCommand(SourceId)]);
        float[] output = new float[36];
        session.Render(0, 4, output.AsSpan(0, 4));
        session.ApplyLiveEvent(ChannelTarget.Virtual(7),
            [new StartNoteCommand(SourceId)]);
        Assert.That(session.VirtualVoices, Has.Count.EqualTo(1));
        PlaybackVoice displaced = session.VirtualVoices.Single();
        Assert.That(displaced.SoundState, Is.Not.Null);
        if (action == NewNoteAction.Off)
            Assert.That(displaced.SoundState.NoteOffTime,
                Is.EqualTo(TimeSpan.FromMilliseconds(4)));
        else
            Assert.That(displaced.FadeEndFrameExclusive, Is.EqualTo(8));

        session.Render(4, 2, output.AsSpan(4, 2));
        Assert.That(session.VirtualVoices, Has.Count.EqualTo(1),
            "the old loop survives its displacement while releasing or fading");
        session.ApplyLiveEvent(ChannelTarget.Virtual(7), [new NoteOffCommand()]);
        int boundLookups = resolver.SoundLookups;
        session.Render(6, 30, output.AsSpan(6, 30));
        Assert.Multiple(() =>
        {
            Assert.That(session.VirtualVoices, Is.Empty,
                "a long block must not retain a released looping NNA Synth");
            Assert.That(output.Skip(20), Is.All.Zero,
                "neither the volume Envelope nor graph Filter may leak indefinitely");
            Assert.That(resolver.SoundLookups, Is.EqualTo(boundLookups));
        });
    }

    [Test]
    public void SameVirtualChannelIdInTwoScopesKeepsIndependentSynthVoices()
    {
        Registry resolver = new();
        FmSynthSound synth = BuildNested(resolver, loop: false,
            action: NewNoteAction.Continue, inheritedEnvelope: false);
        resolver.Add(SourceId, synth);
        PlaybackSession session = Session(resolver, Schedule());
        session.ApplyScopedEvent(101, ChannelTarget.Virtual(7),
            [new StartNoteCommand(SourceId)]);
        session.ApplyScopedEvent(202, ChannelTarget.Virtual(7),
            [new StartNoteCommand(SourceId)]);
        float[] output = new float[30];
        session.Render(0, 2, output.AsSpan(0, 2));
        Assert.That(output[0], Is.GreaterThan(0),
            "two independent scoped source voices are audible");

        session.ApplyScopedEvent(101, ChannelTarget.Virtual(7),
            [new StartNoteCommand(SourceId)]);
        Assert.That(session.VirtualVoices, Has.Count.EqualTo(1));
        PlaybackVoice old = session.VirtualVoices.Single();
        Assert.Multiple(() =>
        {
            Assert.That(old.OriginScopedVirtualOwner, Is.EqualTo(101));
            Assert.That(old.OriginVirtualChannelId, Is.EqualTo(7u));
        });
        int lookups = resolver.SoundLookups;
        session.Render(2, 28, output.AsSpan(2, 28));
        Assert.Multiple(() =>
        {
            Assert.That(output[2], Is.GreaterThan(output[0]),
                "the new scope-101 note adds a third, separately filtered voice");
            Assert.That(output.Skip(10), Is.All.Zero);
            Assert.That(session.VirtualVoices, Is.Empty);
            Assert.That(resolver.SoundLookups, Is.EqualTo(lookups));
        });
    }

    private static FmSynthSound BuildNested(Registry registry, bool loop,
        NewNoteAction action, bool inheritedEnvelope)
    {
        SampleDefinition sampleDefinition = new(SampleId, "PCM",
            new ExternalAssetReference("source.wav"));
        if (loop)
            sampleDefinition.Loop = new SampleLoop(SampleLoopMode.Forward, 0, 4);
        registry.Add(SampleId, new SampleSound(sampleDefinition,
            new MemorySampleData(Rate, 1, [0.5f, 0.5f, 0.5f, 0.5f])));
        ObjectId innerSource = SampleId;
        if (inheritedEnvelope)
        {
            registry.AddEnvelope(EnvelopeId, new ThreeFrameRelease());
            registry.Add(InstrumentId, new InstrumentSound(
                _ => new ToneSpecification
                {
                    SourceId = SampleId, VolumeEnvelopeId = EnvelopeId,
                }, registry, registry));
            innerSource = InstrumentId;
        }
        FmSynthSound inner = new(new FmSynthGraph([
            new FmSoundSourceNode(0, innerSource, C4),
        ], 0), registry, soundResolver: registry);
        registry.Add(InnerId, inner);
        FilterDefinition definition = new((ObjectId)72006U, "Graph pole");
        FilterInstance filter = new(definition);
        filter.SetStageParameter(1, "CutoffHz",
            FilterParameterValue.FromNumber(150));
        return new FmSynthSound(new FmSynthGraph([
            new FmSoundSourceNode(0, InnerId, C4),
            new FmFilterNode(1, 0, filter, tailSeconds: 0.002),
        ], 1), registry,
            new NewNotePolicy(action, action == NewNoteAction.Fade
                ? TimeSpan.FromMilliseconds(4) : TimeSpan.Zero),
            soundResolver: registry);
    }

    private static PlaybackSession Session(Registry registry, NoteSchedule schedule)
        => new(new RenderContext(new RenderConfiguration(Rate, [
            new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0),
        ])), schedule, registry);

    private static NoteSchedule Schedule(params NoteEvent[] events)
    {
        NoteScheduleBuilder builder = new();
        foreach (NoteEvent e in events)
            builder.Append(e);
        return builder.Freeze();
    }

    private static NoteEvent Event(int frame, ChannelTarget target,
        params NoteCommand[] commands)
        => new(new MusicalTime(TimeSpan.FromMilliseconds(frame), 0),
            target, commands);

    private sealed class OneHundredSixtyFrameRelease : IEnvelopeCurve
    {
        public bool HasSustainLoop => true;
        public bool IsZeroAfterCompletion => true;
        public double GetValue(long frame, int sampleRate, long? off)
            => !off.HasValue ? 1.0
                : Math.Max(0.0, 1.0 - (frame - off.Value) / 160.0);
        public long? GetEndActiveFrameExclusiveAfterNoteOff(
            long off, int sampleRate) => off + 160;
    }

    private sealed class FortyFrameRelease : IEnvelopeCurve
    {
        public bool HasSustainLoop => true;
        public bool IsZeroAfterCompletion => true;
        public double GetValue(long frame, int sampleRate, long? off)
            => !off.HasValue ? 1
                : Math.Max(0, 1 - (frame - off.Value) / 40.0);
        public long? GetEndActiveFrameExclusiveAfterNoteOff(
            long off, int sampleRate) => off + 40;
    }

    private sealed class ThreeFrameRelease : IEnvelopeCurve
    {
        public bool HasSustainLoop => true;
        public bool IsZeroAfterCompletion => true;
        public double GetValue(long frame, int sampleRate, long? off)
            => !off.HasValue ? 1 : Math.Max(0, 1 - (frame - off.Value) / 3.0);

        public long? GetEndActiveFrameExclusiveAfterNoteOff(
            long off, int sampleRate) => off + 3;
    }

    private sealed class Registry : ISoundResolver, IEnvelopeCurveResolver
    {
        private readonly Dictionary<ObjectId, ISound> _sounds = [];
        private readonly Dictionary<ObjectId, IEnvelopeCurve> _envelopes = [];
        public int SoundLookups { get; private set; }
        public void Add(ObjectId id, ISound sound) => _sounds.Add(id, sound);
        public void Replace(ObjectId id, ISound sound) => _sounds[id] = sound;
        public void AddEnvelope(ObjectId id, IEnvelopeCurve envelope) =>
            _envelopes.Add(id, envelope);
        public void ReplaceEnvelope(ObjectId id, IEnvelopeCurve envelope) =>
            _envelopes[id] = envelope;
        public bool TryResolve(ObjectId id, bool mixdown, out ISound? sound)
        {
            SoundLookups++;
            if (mixdown)
            {
                sound = null;
                return false;
            }
            return _sounds.TryGetValue(id, out sound);
        }
        public bool TryResolve(ObjectId id, out IEnvelopeCurve? envelope)
            => _envelopes.TryGetValue(id, out envelope);
    }
}
