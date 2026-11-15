using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Headless.NUnit;

using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
using Heresy.Core.Sequencing;
using Heresy.Render.Configuration;
using Heresy.Render.Envelopes;
using Heresy.Render.FmSynthesis;
using Heresy.Render.Playback;
using Heresy.Render.Filters;
using Heresy.Render.Sounds;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

/// <summary>
/// C2 property-matrix acceptance: exercise the real inspector's semantic
/// commit and publication callback, then require the already-sounding PCM
/// voice to adopt the authored revision rather than merely changing the
/// document or a freshly created note.
/// </summary>
[TestFixture]
public sealed class FmSynthInspectorLivePcmTests
{
    private const int Rate = 1000;

    [AvaloniaTest]
    public void OperatorMinimumMaximumInspectorCommitPublishesAndUpdatesHeldPcm()
    {
        DocumentWorkspace workspace = new();
        FmSynthDefinition synth = FmSynthDocumentEditor.CreateFmSynth(
            workspace, "C2 Operator inspector");
        int low = FmSynthDocumentEditor.AddConstantNode(
            workspace, synth, 0.2, 80, 80);
        int high = FmSynthDocumentEditor.AddConstantNode(
            workspace, synth, 0.8, 80, 240);
        int opId = FmSynthDocumentEditor.AddOperatorNode(
            workspace, synth, FmOperatorKind.Minimum,
            [low, high], 300, 130);
        FmSynthDocumentEditor.SetOutputNode(workspace, synth, opId);

        SynthResolver resolver = new(synth.Id);
        FmSynthSound Compile() => new(synth.Graph, resolver,
            soundResolver: resolver, enableLiveMultiExpansion: true);
        FmSynthSound current = Compile();
        resolver.Sound = current;
        PlaybackSession playback = new(
            new RenderContext(new RenderConfiguration(Rate, [
                new OutputChannelConfiguration(Vector3.Zero,
                    positionalImportance: 0),
            ])),
            new NoteScheduleBuilder().Freeze(), resolver);
        playback.ApplyLiveEvent(ChannelTarget.Physical(0),
            [new StartNoteCommand(synth.Id)]);
        float[] initial = new float[10];
        playback.Render(0, initial.Length, initial);
        Assert.That(initial, Is.All.EqualTo(0.2f).Within(1e-5f));
        Assert.That(playback.GetChannelState(0).CurrentVoice!.Sound,
            Is.SameAs(current));

        int publications = 0;
        int changes = 0;
        FmSynthEditorControl editor = new(workspace, synth,
            () => { }, _ => changes++,
            publishSynthEditAsync: () =>
            {
                FmSynthSound revised = Compile();
                playback.AdoptLiveSynthParameters(synth.Id, current, revised);
                resolver.Sound = revised;
                current = revised;
                publications++;
                return Task.CompletedTask;
            });
        StackPanel inspector = (StackPanel)typeof(FmSynthEditorControl)
            .GetField("_inspector", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(editor)!;
        int operationLabel = inspector.Children
            .Select((control, index) => (control, index))
            .Single(entry => entry.control is TextBlock label
                && label.Text == "Operation").index;
        ComboBox operation = (ComboBox)inspector.Children[operationLabel + 1];
        Assert.That(operation.SelectedItem, Is.EqualTo(FmOperatorKind.Minimum));

        uint originalAudioRevision = workspace.Document.AudioRevision;
        uint originalDocumentRevision = workspace.Document.DocumentRevision;

        void Choose(FmOperatorKind kind, int acceptedCount)
        {
            uint previousAudioRevision = workspace.Document.AudioRevision;
            operation.SelectedItem = kind;
            Assert.Multiple(() =>
            {
                Assert.That(synth.Graph.Nodes.OfType<FmOperatorNode>()
                    .Single(n => n.Id == opId).Operation, Is.EqualTo(kind));
                Assert.That(workspace.Document.AudioRevision,
                    Is.EqualTo(previousAudioRevision + 1),
                    "real inspector edits must advance the audio revision");
                Assert.That(publications, Is.EqualTo(acceptedCount),
                    "exactly one live publication per changed selection");
                Assert.That(changes, Is.EqualTo(acceptedCount));
                Assert.That(playback.GetChannelState(0).CurrentVoice!.Sound,
                    Is.SameAs(current),
                    "the existing physical voice must adopt the new compiled Synth");
                Assert.That(playback.CountOlderAudioVoices(), Is.Zero,
                    "eligible Operator mode editing is not old-generation playback");
            });
            operation.SelectedItem = kind;
            Assert.Multiple(() =>
            {
                Assert.That(workspace.Document.AudioRevision,
                    Is.EqualTo(previousAudioRevision + 1),
                    "selecting an already-selected operation must be a no-op");
                Assert.That(publications, Is.EqualTo(acceptedCount));
            });
        }

        static void CheckFade(
            float[] pcm, float from, float to, int frameStart)
        {
            for (int i = 0; i < pcm.Length; i++)
            {
                double t = Math.Clamp(i / 100.0, 0.0, 1.0);
                double w = t * t * (3.0 - 2.0 * t);
                float expected = (float)(from * (1.0 - w) + to * w);
                Assert.That(pcm[i], Is.EqualTo(expected).Within(2e-6f),
                    $"the held voice must blend the two real inputs at frame {frameStart + i}");
            }
        }

        Choose(FmOperatorKind.Maximum, 1);
        float[] maxRamp = new float[101];
        playback.Render(10, maxRamp.Length, maxRamp);
        CheckFade(maxRamp, 0.2f, 0.8f, 10);
        Assert.That(playback.GetChannelState(0).CurrentVoice!.Sound,
            Is.SameAs(current));

        Choose(FmOperatorKind.Minimum, 2);
        float[] minRamp = new float[101];
        playback.Render(111, minRamp.Length, minRamp);
        CheckFade(minRamp, 0.8f, 0.2f, 111);
        Assert.Multiple(() =>
        {
            Assert.That(workspace.Document.AudioRevision,
                Is.EqualTo(originalAudioRevision + 2));
            Assert.That(workspace.Document.DocumentRevision,
                Is.EqualTo(originalDocumentRevision + 2));
            Assert.That(playback.CountOlderAudioVoices(), Is.Zero);
            Assert.That(playback.GetChannelState(0).CurrentVoice!.Sound,
                Is.SameAs(current));
        });
    }

    [AvaloniaTest]
    public void MultiPresetAndCycleInspectorEditsPrepareHeldOnsetsAndMatchAuthoredPcm()
    {
        // C2(b): use the real Multi inspector, not a direct graph setter.
        // A preset that enables randomized onsets, then an enabled/numeric/
        // disabled cycle override, must each reach the same held voice via
        // off-PCM worker-prepared onset histories. The independent oracle
        // gets its revisions from explicit FmMultiNode data, never from
        // the inspector's result or its publication callback.
        DocumentWorkspace workspace = new();
        FmSynthDefinition synth = FmSynthDocumentEditor.CreateFmSynth(
            workspace, "C2 Multi inspector");
        int oscillator = FmSynthDocumentEditor.AddOscillatorNode(
            workspace, synth, FmOscillatorWaveform.Sine, 83, 80, 80);
        int multiId = FmSynthDocumentEditor.AddMultiNode(
            workspace, synth, 320, 80);
        FmSynthDocumentEditor.ConnectNodes(
            workspace, synth, oscillator, multiId, 0);
        FmSynthDocumentEditor.SetOutputNode(workspace, synth, multiId);
        FmMultiNode initial = new(multiId, oscillator,
            detune: 15, voiceCount: 2, randomizePhase: false,
            gainCompensation: true);
        FmSynthDocumentEditor.UpdateNode(workspace, synth, initial);

        SynthResolver actualResolver = new(synth.Id);
        SynthResolver oracleResolver = new(synth.Id);
        SynthResolver unchangedResolver = new(synth.Id);
        FmSynthSound Build(FmMultiNode layout, SynthResolver resolver)
            => new(new FmSynthGraph(synth.Graph.Nodes
                .Select(node => node.Id == multiId
                    ? (FmSynthNode)layout : node), multiId),
                resolver, soundResolver: resolver,
                reserveMultiHeadroom: true, enableLiveMultiExpansion: true);

        FmSynthSound actualSound = Build(initial, actualResolver);
        FmSynthSound oracleSound = Build(initial, oracleResolver);
        FmSynthSound unchangedSound = Build(initial, unchangedResolver);
        actualResolver.Sound = actualSound;
        oracleResolver.Sound = oracleSound;
        unchangedResolver.Sound = unchangedSound;
        RenderContext context = new(new RenderConfiguration(Rate, [
            new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0),
        ]));
        PlaybackSession Session(SynthResolver resolver)
            => new(context, new NoteScheduleBuilder().Freeze(), resolver);
        PlaybackSession actual = Session(actualResolver);
        PlaybackSession oracle = Session(oracleResolver);
        PlaybackSession unchanged = Session(unchangedResolver);
        foreach (PlaybackSession session in new[] { actual, oracle, unchanged })
            session.ApplyLiveEvent(ChannelTarget.Physical(0),
                [new StartNoteCommand(synth.Id)]);
        float[] first = new float[12], referenceFirst = new float[12];
        actual.Render(0, 12, first);
        oracle.Render(0, 12, referenceFirst);
        unchanged.Render(0, 12, new float[12]);
        Assert.That(first, Is.EqualTo(referenceFirst),
            "both independently created Multi invocations begin at the same clock");
        PlaybackVoice held = actual.GetChannelState(0).CurrentVoice!;

        int publications = 0, changes = 0;
        FmSynthEditorControl editor = new(workspace, synth,
            () => { }, _ => changes++,
            publishSynthEditAsync: () =>
            {
                FmMultiNode committed = synth.Graph.Nodes
                    .OfType<FmMultiNode>().Single();
                FmSynthSound revised = Build(committed, actualResolver);
                actual.AdoptLiveSynthParameters(synth.Id, actualSound, revised);
                actualSound = revised;
                actualResolver.Sound = revised;
                publications++;
                return Task.CompletedTask;
            });
        StackPanel inspector = (StackPanel)typeof(FmSynthEditorControl)
            .GetField("_inspector", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(editor)!;
        Control NextField(string label)
        {
            int index = inspector.Children
                .Select((control, i) => (control, i))
                .Single(x => x.control is TextBlock text
                    && text.Text == label).i;
            return inspector.Children[index + 1];
        }
        ComboBox presets = (ComboBox)NextField("Presets");
        CheckBox overrideCycle = inspector.Children.OfType<CheckBox>()
            .Single(x => Equals(x.Content, "Override cycle"));
        TextBox cycleLength = (TextBox)NextField(
            "Cycle Length (samples at C4 / 48 kHz)");
        FmMultiNode expectedMulti = initial;
        int frame = 12;

        using FirPreparationScheduler worker = new();
        void Changed(Action edit, FmMultiNode expected, int number)
        {
            uint audioRevision = workspace.Document.AudioRevision;
            uint documentRevision = workspace.Document.DocumentRevision;
            edit();
            FmMultiNode committed = synth.Graph.Nodes
                .OfType<FmMultiNode>().Single();
            Assert.Multiple(() =>
            {
                Assert.That(committed.Distribution, Is.EqualTo(expected.Distribution));
                Assert.That(committed.Detune, Is.EqualTo(expected.Detune));
                Assert.That(committed.VoiceCount, Is.EqualTo(expected.VoiceCount));
                Assert.That(committed.RandomizePhase, Is.EqualTo(expected.RandomizePhase));
                Assert.That(committed.GainCompensation, Is.EqualTo(expected.GainCompensation));
                Assert.That(committed.CycleLength, Is.EqualTo(expected.CycleLength));
                Assert.That(workspace.Document.AudioRevision,
                    Is.EqualTo(audioRevision + 1), "semantic UI edit advances audio exactly once");
                Assert.That(workspace.Document.DocumentRevision,
                    Is.EqualTo(documentRevision + 1));
                Assert.That(publications, Is.EqualTo(number),
                    "each changed Multi control requests one publication");
                Assert.That(changes, Is.EqualTo(number));
                Assert.That(actual.CountOlderAudioVoices(), Is.Zero,
                    "pending worker onset is not a definite missed revision");
            });
            FmSynthSound revisedOracle = Build(expected, oracleResolver);
            oracle.AdoptLiveSynthParameters(synth.Id, oracleSound, revisedOracle);
            oracleSound = revisedOracle;
            oracleResolver.Sound = revisedOracle;
            expectedMulti = expected;

            long completedBefore = worker.CompletedMultiExpansionCount;
            actual.QueuePendingFirControls(worker);
            oracle.QueuePendingFirControls(worker);
            Assert.That(SpinWait.SpinUntil(
                () => worker.CompletedMultiExpansionCount >= completedBefore + 2,
                TimeSpan.FromSeconds(5)), Is.True,
                "each active invocation needs an independently worker-prepared Multi onset");
            actual.AdoptPreparedFirControls();
            oracle.AdoptPreparedFirControls();
            Assert.Multiple(() =>
            {
                Assert.That(actual.GetChannelState(0).CurrentVoice,
                    Is.SameAs(held), "the edit must not replace the physical voice");
                Assert.That(held.Sound, Is.SameAs(actualSound),
                    "prepared edited Multi must actually become audible");
                Assert.That(oracle.GetChannelState(0).CurrentVoice!.Sound,
                    Is.SameAs(oracleSound));
                Assert.That(actual.CountOlderAudioVoices(), Is.Zero);
                Assert.That(oracle.CountOlderAudioVoices(), Is.Zero);
                Assert.That(worker.Fault, Is.Null);
            });

            float[] actualPcm = new float[140];
            float[] oraclePcm = new float[140];
            float[] originalPcm = new float[140];
            actual.Render(frame, actualPcm.Length, actualPcm);
            oracle.Render(frame, oraclePcm.Length, oraclePcm);
            unchanged.Render(frame, originalPcm.Length, originalPcm);
            for (int i = 0; i < actualPcm.Length; i++)
            {
                Assert.That(actualPcm[i],
                    Is.EqualTo(oraclePcm[i]).Within(2e-5f),
                    $"Multi inspector PCM must match independent authored revision "
                    + $"{number} at frame {frame + i}");
                Assert.That(float.IsFinite(actualPcm[i]), Is.True);
            }
            if (number == 1)
                Assert.That(actualPcm.Zip(originalPcm,
                    (revised, old) => Math.Abs(revised - old)).Max(),
                    Is.GreaterThan(0.05),
                    "Unison must audibly rephase the already-playing Multi, "
                    + "not merely publish equal but unchanged PCM histories");
            Assert.That(actual.CountOlderAudioVoices(), Is.Zero);
            frame += actualPcm.Length;
        }

        // The default Spread(2) layout already has Unison's detune and
        // gain. Applying the real preset flips Randomize Phase from false
        // to true and must prepare two fresh onset histories off PCM.
        Changed(() => presets.SelectedItem = "Unison",
            expectedMulti.WithPreset("Unison"), 1);
        Assert.That(presets.SelectedIndex, Is.Zero,
            "preset is an action and resets to its placeholder");

        // Switching on the override uses the currently displayed draft,
        // not a zero or an unrelated oscillator cycle.
        double defaultCycle = FmMultiNode.ReferenceSampleRate
            / FmMultiNode.NeutralFrequencyHz;
        Changed(() => overrideCycle.IsChecked = true,
            expectedMulti.With(expectedMulti.InputNodeId,
                expectedMulti.Distribution, expectedMulti.Detune,
                expectedMulti.VoiceCount, expectedMulti.HarmonicComponents,
                expectedMulti.ChordComponents, expectedMulti.RandomizePhase,
                expectedMulti.GainCompensation, defaultCycle), 2);
        Assert.That(cycleLength.IsVisible, Is.True);
        Assert.That(overrideCycle.IsChecked, Is.True);

        Changed(() =>
        {
            cycleLength.Text = "1120";
            cycleLength.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.Enter,
            });
        }, expectedMulti.With(expectedMulti.InputNodeId,
            expectedMulti.Distribution, expectedMulti.Detune,
            expectedMulti.VoiceCount, expectedMulti.HarmonicComponents,
            expectedMulti.ChordComponents, expectedMulti.RandomizePhase,
            expectedMulti.GainCompensation, 1120), 3);
        Assert.That(cycleLength.Text, Is.EqualTo("1120"));
        Assert.That(overrideCycle.IsChecked, Is.True);

        Changed(() => overrideCycle.IsChecked = false,
            expectedMulti.With(expectedMulti.InputNodeId,
                expectedMulti.Distribution, expectedMulti.Detune,
                expectedMulti.VoiceCount, expectedMulti.HarmonicComponents,
                expectedMulti.ChordComponents, expectedMulti.RandomizePhase,
                expectedMulti.GainCompensation, null), 4);
        Assert.Multiple(() =>
        {
            Assert.That(cycleLength.IsVisible, Is.False);
            Assert.That(actual.GetChannelState(0).CurrentVoice, Is.SameAs(held));
            Assert.That(actual.CountOlderAudioVoices(), Is.Zero);
            Assert.That(publications, Is.EqualTo(4));
            Assert.That(worker.CompletedMultiExpansionCount, Is.EqualTo(8),
                "four separate real worker onsets in actual+oracle sessions");
        });
    }

    private sealed class SynthResolver(ObjectId synthId)
        : ISoundResolver, IEnvelopeCurveResolver
    {
        public ISound? Sound { get; set; }

        public bool TryResolve(ObjectId id, bool mixdown, out ISound? sound)
        {
            sound = id == synthId && !mixdown ? Sound : null;
            return sound is not null;
        }

        public bool TryResolve(ObjectId id, out IEnvelopeCurve? curve)
        {
            curve = null;
            return false;
        }
    }
}
