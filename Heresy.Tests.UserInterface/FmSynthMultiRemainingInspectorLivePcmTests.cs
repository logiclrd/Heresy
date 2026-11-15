using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;

using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
using Heresy.Core.Sequencing;
using Heresy.Render.Configuration;
using Heresy.Render.Envelopes;
using Heresy.Render.Filters;
using Heresy.Render.FmSynthesis;
using Heresy.Render.Playback;
using Heresy.Render.Sounds;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

/// <summary>C2(e): omitted Multi control families through the real inspector,
/// independently frozen reference histories and eligible detached onsets.</summary>
[TestFixture]
public sealed class FmSynthMultiRemainingInspectorLivePcmTests
{
    [AvaloniaTest]
    public void InspectorMultiEditAfterStructuralOutputChangeReachesNewNoteBesideTwoOlderNotes()
    {
        // C6-C: use the *same mounted inspector* to add/connect/select
        // Multi, then retune it after an unrelated old generation remains.
        DocumentWorkspace workspace = new();
        FmSynthDefinition synth = FmSynthDocumentEditor.CreateFmSynth(
            workspace, "C6-C new Multi live migration");
        int oscillator = FmSynthDocumentEditor.AddOscillatorNode(
            workspace, synth, FmOscillatorWaveform.Sine, 83, 80, 80);
        FmSynthDocumentEditor.SetOutputNode(workspace, synth, oscillator);

        Resolver resolver = new(synth.Id);
        FmSynthSound Compile() => new(synth.Graph, resolver,
            soundResolver: resolver, reserveMultiHeadroom: true,
            enableLiveMultiExpansion: true);
        FmSynthSound original = Compile();
        resolver.Sound = original;
        PlaybackSession playback = new(new RenderContext(
            new RenderConfiguration(1000, [
                new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0),
            ])), new NoteScheduleBuilder().Freeze(), resolver);

        int published = 0;
        FmSynthSound current = original;
        FmSynthEditorControl editor = new(workspace, synth, () => { },
            _ => { }, publishSynthEditAsync: () =>
            {
                FmSynthSound revision = Compile();
                playback.AdoptLiveSynthParameters(synth.Id, current, revision);
                resolver.Sound = revision;
                current = revision;
                published++;
                return Task.CompletedTask;
            });

        playback.ApplyLiveEvent(ChannelTarget.Physical(0),
            [new StartNoteCommand(synth.Id)]);
        playback.ApplyLiveEvent(ChannelTarget.Physical(1),
            [new StartNoteCommand(synth.Id)]);
        playback.Render(0, 80, new float[80]);

        // Real UI handlers—not just direct graph setters—create the Multi
        // inspector, connect its input and choose it as graph output.
        typeof(FmSynthEditorControl).GetMethod(
            "AddMulti", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(editor, null);
        int multi = synth.Graph.Nodes.OfType<FmMultiNode>().Single().Id;
        typeof(FmSynthEditorControl).GetMethod(
            "ConnectNodes", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(editor, [oscillator, multi, 0]);
        StackPanel inspector = (StackPanel)typeof(FmSynthEditorControl)
            .GetField("_inspector", BindingFlags.NonPublic
                | BindingFlags.Instance)!.GetValue(editor)!;
        Button setOutput = inspector.Children.OfType<Button>()
            .Single(x => Equals(x.Content, "Make output"));
        setOutput.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.That(synth.Graph.OutputNodeId, Is.EqualTo(multi));
        Assert.That(published, Is.EqualTo(3),
            "Add Multi, connect oscillator and make output must publish");
        FmSynthSound withMulti = current;
        Assert.That(playback.CountOlderAudioVoices(), Is.EqualTo(2));
        playback.ApplyLiveEvent(ChannelTarget.Physical(2),
            [new StartNoteCommand(synth.Id)]);
        playback.Render(80, 128, new float[128]);

        PlaybackVoice oldFirst = playback.GetChannelState(0).CurrentVoice!;
        PlaybackVoice oldSecond = playback.GetChannelState(1).CurrentVoice!;
        PlaybackVoice newest = playback.GetChannelState(2).CurrentVoice!;
        Assert.Multiple(() =>
        {
            Assert.That(oldFirst.Sound, Is.SameAs(original));
            Assert.That(oldSecond.Sound, Is.SameAs(original));
            Assert.That(newest.Sound, Is.SameAs(withMulti));
            Assert.That(playback.CountOlderAudioVoices(), Is.EqualTo(2));
        });

        // Reproduce the observed order: the third note responds to an
        // upstream Oscillator inspector edit before a Multi edit is tried.
        var select = typeof(FmSynthEditorControl).GetMethod(
            "SelectNode", BindingFlags.NonPublic | BindingFlags.Instance)!;
        select.Invoke(editor, [oscillator]);
        TextBox frequency = (TextBox)inspector.Children[
            inspector.Children.Select((x, index) => (x, index))
                .Single(pair => pair.x is TextBlock t
                    && t.Text == "Frequency Hz").index + 1];
        int beforeOscillator = published;
        frequency.Text = "117";
        frequency.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Enter,
        });
        Assert.Multiple(() =>
        {
            Assert.That(published, Is.EqualTo(beforeOscillator + 1));
            Assert.That(newest.Sound, Is.SameAs(current),
                "a newly started Multi note must accept Oscillator revisions");
            Assert.That(playback.CountOlderAudioVoices(), Is.EqualTo(2));
        });
        select.Invoke(editor, [multi]);
        TextBox voices = (TextBox)inspector.Children[
            inspector.Children.Select((x, index) => (x, index))
                .Single(pair => pair.x is TextBlock t
                    && t.Text == "Voice Count").index + 1];
        uint before = workspace.Document.AudioRevision;
        int count = published;
        voices.Text = "3";
        voices.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Enter,
        });
        Assert.Multiple(() =>
        {
            Assert.That(workspace.Document.AudioRevision, Is.EqualTo(before + 1),
                "the new Multi field must commit one semantic audio revision");
            Assert.That(synth.Graph.Nodes.OfType<FmMultiNode>()
                .Single().VoiceCount, Is.EqualTo(3));
            Assert.That(published, Is.EqualTo(count + 1),
                "the actual Multi inspector edit must request live publication");
            Assert.That(oldFirst.Sound, Is.SameAs(original));
            Assert.That(oldSecond.Sound, Is.SameAs(original));
            Assert.That(newest.Sound, Is.SameAs(current),
                "post-structural current voice must adopt new Multi configuration");
            Assert.That(playback.CountOlderAudioVoices(), Is.EqualTo(2),
                "an earlier structural refusal must not poison the newer voice");
        });
    }

    [AvaloniaTest]
    public void DistributionComponentsVoiceCountPhaseGainAndCompoundPresetAdoptHeldPcm()
    {
        const int sampleRate = 1000, framesPerEdit = 140;
        DocumentWorkspace workspace = new();
        FmSynthDefinition synth = FmSynthDocumentEditor.CreateFmSynth(
            workspace, "C2(e) Multi controls");
        int osc = FmSynthDocumentEditor.AddOscillatorNode(workspace,
            synth, FmOscillatorWaveform.Sine, 83, 80, 80);
        int multiId = FmSynthDocumentEditor.AddMultiNode(workspace, synth, 320, 80);
        FmSynthDocumentEditor.ConnectNodes(workspace, synth, osc, multiId, 0);
        FmSynthDocumentEditor.SetOutputNode(workspace, synth, multiId);
        FmMultiNode expected = new(multiId, osc, detune: 15, voiceCount: 2,
            randomizePhase: false, gainCompensation: true);
        FmSynthDocumentEditor.UpdateNode(workspace, synth, expected);

        Resolver actualResolver = new(synth.Id), referenceResolver = new(synth.Id),
            untouchedResolver = new(synth.Id);
        FmSynthSound Build(FmMultiNode layout, Resolver resolver)
            => new(new FmSynthGraph(synth.Graph.Nodes.Select(node =>
                node.Id == multiId ? (FmSynthNode)layout : node), multiId),
                resolver, soundResolver: resolver, reserveMultiHeadroom: true,
                enableLiveMultiExpansion: true);
        FmSynthSound current = Build(expected, actualResolver);
        FmSynthSound reference = Build(expected, referenceResolver);
        FmSynthSound untouched = Build(expected, untouchedResolver);
        actualResolver.Sound = current;
        referenceResolver.Sound = reference;
        untouchedResolver.Sound = untouched;
        RenderContext context = new(new RenderConfiguration(sampleRate, [
            new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0),
        ]));
        PlaybackSession Session(Resolver resolver) => new(context,
            new NoteScheduleBuilder().Freeze(), resolver);
        PlaybackSession actual = Session(actualResolver);
        PlaybackSession oracle = Session(referenceResolver);
        PlaybackSession unchanged = Session(untouchedResolver);
        foreach (PlaybackSession session in new[] { actual, oracle, unchanged })
            session.ApplyLiveEvent(ChannelTarget.Physical(0),
                [new StartNoteCommand(synth.Id)]);
        float[] first = new float[12], otherFirst = new float[12];
        actual.Render(0, 12, first);
        oracle.Render(0, 12, otherFirst);
        unchanged.Render(0, 12, new float[12]);
        Assert.That(first, Is.EqualTo(otherFirst).Within(1e-6f));
        PlaybackVoice held = actual.GetChannelState(0).CurrentVoice!;

        int publications = 0, changed = 0;
        FmSynthEditorControl editor = new(workspace, synth, () => { },
            _ => changed++, publishSynthEditAsync: () =>
            {
                FmSynthSound revised = new(synth.Graph, actualResolver,
                    soundResolver: actualResolver,
                    reserveMultiHeadroom: true, enableLiveMultiExpansion: true);
                actual.AdoptLiveSynthParameters(synth.Id, current, revised);
                current = revised;
                actualResolver.Sound = revised;
                publications++;
                return Task.CompletedTask;
            });
        StackPanel panel = (StackPanel)typeof(FmSynthEditorControl)
            .GetField("_inspector", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(editor)!;
        Control Field(string label)
        {
            int index = panel.Children.Select((control, i) => (control, i))
                .Single(x => x.control is TextBlock text && text.Text == label).i;
            return panel.Children[index + 1];
        }
        static void Enter(TextBox box, string value)
        {
            box.Text = value;
            box.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.Enter,
            });
        }
        ComboBox distribution = (ComboBox)Field("Distribution");
        ComboBox presets = (ComboBox)Field("Presets");
        TextBox detune = (TextBox)Field("Detune (total cents)");
        TextBox voices = (TextBox)Field("Voice Count");
        TextBox components = (TextBox)Field("Components");
        CheckBox randomize = panel.Children.OfType<CheckBox>()
            .Single(x => Equals(x.Content, "Randomize Phase"));
        CheckBox gain = panel.Children.OfType<CheckBox>()
            .Single(x => Equals(x.Content, "Gain Compensation"));
        uint audioStart = workspace.Document.AudioRevision;
        uint documentStart = workspace.Document.DocumentRevision;
        int frame = 12, number = 0;
        using FirPreparationScheduler worker = new();

        void Change(Action edit, FmMultiNode independent,
            bool differenceRequired = false)
        {
            uint audio = workspace.Document.AudioRevision;
            uint document = workspace.Document.DocumentRevision;
            edit();
            number++;
            FmMultiNode committed = synth.Graph.Nodes.OfType<FmMultiNode>()
                .Single(x => x.Id == multiId);
            Assert.Multiple(() =>
            {
                Assert.That(committed.InputNodeId, Is.EqualTo(independent.InputNodeId));
                Assert.That(committed.Distribution, Is.EqualTo(independent.Distribution));
                Assert.That(committed.Detune, Is.EqualTo(independent.Detune));
                Assert.That(committed.VoiceCount, Is.EqualTo(independent.VoiceCount));
                Assert.That(committed.HarmonicComponents, Is.EqualTo(independent.HarmonicComponents));
                Assert.That(committed.ChordComponents, Is.EqualTo(independent.ChordComponents));
                Assert.That(committed.RandomizePhase, Is.EqualTo(independent.RandomizePhase));
                Assert.That(committed.GainCompensation, Is.EqualTo(independent.GainCompensation));
                Assert.That(committed.CycleLength, Is.EqualTo(independent.CycleLength));
                Assert.That(workspace.Document.AudioRevision, Is.EqualTo(audio + 1));
                Assert.That(workspace.Document.DocumentRevision, Is.EqualTo(document + 1));
                Assert.That(publications, Is.EqualTo(number));
                Assert.That(changed, Is.EqualTo(number));
                Assert.That(actual.CountOlderAudioVoices(), Is.Zero);
            });

            FmSynthSound nextOracle = Build(independent, referenceResolver);
            oracle.AdoptLiveSynthParameters(synth.Id, reference, nextOracle);
            reference = nextOracle;
            referenceResolver.Sound = reference;
            expected = independent;
            actual.QueuePendingFirControls(worker);
            oracle.QueuePendingFirControls(worker);

            Assert.That(SpinWait.SpinUntil(() =>
            {
                actual.AdoptPreparedFirControls();
                oracle.AdoptPreparedFirControls();
                return worker.Fault is not null
                    || actual.GetChannelState(0).CurrentVoice!.Sound == current
                        && oracle.GetChannelState(0).CurrentVoice!.Sound == reference;
            }, TimeSpan.FromSeconds(5)), Is.True,
                "eligible Multi field editing should eventually adopt after worker prep");
            Assert.Multiple(() =>
            {
                Assert.That(worker.Fault, Is.Null);
                Assert.That(actual.GetChannelState(0).CurrentVoice, Is.SameAs(held));
                Assert.That(held.Sound, Is.SameAs(current));
                Assert.That(oracle.GetChannelState(0).CurrentVoice!.Sound, Is.SameAs(reference));
                Assert.That(actual.CountOlderAudioVoices(), Is.Zero);
                Assert.That(oracle.CountOlderAudioVoices(), Is.Zero);
            });

            float[] heard = new float[framesPerEdit], expectedPcm = new float[framesPerEdit],
                unchangedPcm = new float[framesPerEdit];
            actual.Render(frame, framesPerEdit, heard);
            oracle.Render(frame, framesPerEdit, expectedPcm);
            unchanged.Render(frame, framesPerEdit, unchangedPcm);
            for (int i = 0; i < heard.Length; i++)
                Assert.That(heard[i], Is.EqualTo(expectedPcm[i]).Within(3e-5f),
                    $"Multi field edit #{number}: frame {frame + i}");
            if (differenceRequired)
                Assert.That(heard.Zip(unchangedPcm, (a, b) => Math.Abs(a-b)).Max(),
                    Is.GreaterThan(0.01),
                    $"Multi edit #{number} must produce audible, non-vacuous changed PCM");
            frame += framesPerEdit;
        }

        Change(() => Enter(detune, "30"),
            expected.With(expected.InputNodeId, expected.Distribution,
                30, expected.VoiceCount, expected.HarmonicComponents,
                expected.ChordComponents, expected.RandomizePhase,
                expected.GainCompensation, expected.CycleLength));
        Change(() => Enter(voices, "3"),
            expected.With(expected.InputNodeId, expected.Distribution,
                expected.Detune, 3, expected.HarmonicComponents,
                expected.ChordComponents, expected.RandomizePhase,
                expected.GainCompensation, expected.CycleLength), true);
        Change(() => randomize.IsChecked = true,
            expected.With(expected.InputNodeId, expected.Distribution,
                expected.Detune, expected.VoiceCount, expected.HarmonicComponents,
                expected.ChordComponents, true, expected.GainCompensation,
                expected.CycleLength));
        Change(() => gain.IsChecked = false,
            expected.With(expected.InputNodeId, expected.Distribution,
                expected.Detune, expected.VoiceCount, expected.HarmonicComponents,
                expected.ChordComponents, expected.RandomizePhase, false,
                expected.CycleLength), true);
        Change(() => distribution.SelectedItem = FmMultiDistribution.Harmonic,
            expected.WithDistribution(FmMultiDistribution.Harmonic), true);
        Change(() => Enter(components, "1 3/2 2"),
            expected.With(expected.InputNodeId, expected.Distribution,
                expected.Detune, expected.VoiceCount, "1 3/2 2",
                expected.ChordComponents, expected.RandomizePhase,
                expected.GainCompensation, expected.CycleLength), true);
        Change(() => distribution.SelectedItem = FmMultiDistribution.Chord,
            expected.WithDistribution(FmMultiDistribution.Chord), true);
        Change(() => Enter(components, "0 3 7"),
            expected.With(expected.InputNodeId, expected.Distribution,
                expected.Detune, expected.VoiceCount, expected.HarmonicComponents,
                "0 3 7", expected.RandomizePhase,
                expected.GainCompensation, expected.CycleLength), true);
        Change(() => presets.SelectedItem = "Chorus",
            expected.WithPreset("Chorus"), true);
        Assert.That(presets.SelectedIndex, Is.Zero,
            "preset action must return to its placeholder even after multi-field change");

        uint beforeNoop = workspace.Document.AudioRevision;
        Enter(detune, "25");
        Enter(voices, "4");
        Assert.Multiple(() =>
        {
            Assert.That(workspace.Document.AudioRevision, Is.EqualTo(beforeNoop),
                "a committed-but-identical Multi value must not republish");
            Assert.That(workspace.Document.AudioRevision, Is.EqualTo(audioStart + number));
            Assert.That(workspace.Document.DocumentRevision, Is.EqualTo(documentStart + number));
            Assert.That(publications, Is.EqualTo(number));
            Assert.That(changed, Is.EqualTo(number));
            Assert.That(actual.GetChannelState(0).CurrentVoice, Is.SameAs(held));
            Assert.That(actual.CountOlderAudioVoices(), Is.Zero);
        });
    }

    private sealed class Resolver(ObjectId synthId)
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
