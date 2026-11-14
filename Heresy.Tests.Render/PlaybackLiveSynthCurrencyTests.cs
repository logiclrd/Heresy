using System;
using System.Collections.Generic;
using System.Numerics;

using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
using Heresy.Core.Sequencing;
using Heresy.Render.Configuration;
using Heresy.Render.Envelopes;
using Heresy.Render.FmSynthesis;
using Heresy.Render.Playback;
using Heresy.Render.Sounds;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class PlaybackLiveSynthCurrencyTests
{
    private static readonly ObjectId SynthId = (ObjectId)98101U;
    private static readonly ObjectId UnrelatedSynthId = (ObjectId)98102U;

    [Test]
    public void CompatibleAndUnrelatedEditsNeverMarkTheHeldNoteOlder()
    {
        Registry sounds = new();
        FmSynthSound original = Constant(0.5);
        FmSynthSound compatible = Constant(0.8);
        FmSynthSound structural = Structural(0.8);
        sounds.Set(SynthId, original);
        PlaybackSession session = Session(sounds);
        session.ApplyLiveEvent(ChannelTarget.Virtual(1),
            [new StartNoteCommand(SynthId)]);
        session.Render(0, 16, new float[16]);

        Assert.That(session.CountOlderAudioVoices(), Is.Zero);
        session.AdoptLiveSynthParameters(UnrelatedSynthId, original, structural);
        Assert.That(session.CountOlderAudioVoices(), Is.Zero,
            "edits to another source do not concern this voice");

        session.AdoptLiveSynthParameters(SynthId, original, compatible);
        Assert.Multiple(() =>
        {
            Assert.That(session.CountOlderAudioVoices(), Is.Zero,
                "a successfully adopted edit does not obsolete the note");
            Assert.That(session.GetVirtualChannelState(1)?.CurrentVoice?.Sound,
                Is.SameAs(compatible));
        });
    }

    [Test]
    public void IncompatibleEditSticksToOnlyOldNotesUntilTheirActualRetirement()
    {
        Registry sounds = new();
        FmSynthSound original = Constant(0.5);
        FmSynthSound structural = Structural(0.4);
        FmSynthSound nextStructural = Structural(0.7);
        sounds.Set(SynthId, original);
        PlaybackSession session = Session(sounds);
        session.ApplyLiveEvent(ChannelTarget.Virtual(1),
            [new StartNoteCommand(SynthId)]);
        session.Render(0, 8, new float[8]);

        session.AdoptLiveSynthParameters(SynthId, original, structural);
        Assert.That(session.CountOlderAudioVoices(), Is.EqualTo(1),
            "a changed graph cannot be migrated to the held invocation");

        // Fresh notes use the latest published definition, not the
        // previous voice's frozen graph.
        sounds.Set(SynthId, structural);
        session.ApplyLiveEvent(ChannelTarget.Virtual(2),
            [new StartNoteCommand(SynthId)]);
        session.Render(8, 8, new float[8]);
        Assert.That(session.CountOlderAudioVoices(), Is.EqualTo(1),
            "a new up-to-date note must not inherit the older mark");

        session.AdoptLiveSynthParameters(SynthId, structural, nextStructural);
        Assert.That(session.CountOlderAudioVoices(), Is.EqualTo(1),
            "a later compatible revision must not erase an earlier miss");

        session.ApplyLiveEvent(ChannelTarget.Virtual(1),
            [new NoteCutCommand()]);
        Assert.That(session.CountOlderAudioVoices(), Is.Zero,
            "the final older note has been detached, though a current note remains");
        Assert.That(session.GetVirtualChannelState(2)?.CurrentVoice,
            Is.Not.Null);
    }

    private static PlaybackSession Session(ISoundResolver sounds)
        => new(new RenderContext(new RenderConfiguration(48000, [
            new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0),
        ])), new NoteScheduleBuilder().Freeze(), sounds);

    private static FmSynthSound Constant(double value)
        => new(new FmSynthGraph([
            new FmConstantNode(1, value),
        ], 1), new NoEnvelopes());

    private static FmSynthSound Structural(double value)
        => new(new FmSynthGraph([
            new FmConstantNode(1, value),
            new FmConstantNode(2, 0.3),
            new FmOperatorNode(3, FmOperatorKind.Add, [1, 2]),
        ], 3), new NoEnvelopes());

    private sealed class Registry : ISoundResolver
    {
        private readonly Dictionary<ObjectId, ISound> _sounds = [];
        public void Set(ObjectId id, ISound sound) => _sounds[id] = sound;
        public bool TryResolve(ObjectId id, bool mixdown, out ISound? sound)
        {
            if (!mixdown)
                return _sounds.TryGetValue(id, out sound);
            sound = null;
            return false;
        }
    }

    private sealed class NoEnvelopes : IEnvelopeCurveResolver
    {
        public bool TryResolve(ObjectId id, out IEnvelopeCurve? curve)
        {
            curve = null;
            return false;
        }
    }
}
