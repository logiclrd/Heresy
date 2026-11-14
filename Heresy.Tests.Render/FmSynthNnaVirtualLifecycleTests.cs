using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;

using Heresy.Core.Assets;
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
