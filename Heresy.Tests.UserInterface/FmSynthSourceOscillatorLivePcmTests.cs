using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;

using Heresy.Core.Assets;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
using Heresy.Core.Samples;
using Heresy.Core.Sequencing;
using Heresy.Render.Configuration;
using Heresy.Render.Envelopes;
using Heresy.Render.Filters;
using Heresy.Render.FmSynthesis;
using Heresy.Render.Playback;
using Heresy.Render.Samples;
using Heresy.Render.Sounds;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

/// <summary>
/// C2(d): actual nonstructural Source/Oscillator inspector commits, held-voice
/// adoption and independently authored sample-by-sample PCM references.
/// The inspector callback is intentionally connected to a real in-process
/// PlaybackSession. Production transport ordering is separate C2 evidence.
/// </summary>
[TestFixture]
public sealed class FmSynthSourceOscillatorLivePcmTests
{
    private const int Rate = 1000;
    private const int Window = 141;

    [AvaloniaTest]
    public void OscillatorInspectorShapeAndPitchControlsKeepOneLiveVoiceAndMatchAuthoredPcm()
    {
        DocumentWorkspace workspace = new();
        FmSynthDefinition synth = FmSynthDocumentEditor.CreateFmSynth(
            workspace, "C2(d) Oscillator");
        int multiplierId = FmSynthDocumentEditor.AddConstantNode(
            workspace, synth, 2, 60, 80);
        int oscillatorId = FmSynthDocumentEditor.AddOscillatorNode(
            workspace, synth, FmOscillatorWaveform.Sine, 80, 280, 80,
            minimum: -0.6, maximum: 0.6);
        FmOscillatorNode expected = new(oscillatorId,
            FmOscillatorWaveform.Sine, 80, -0.6, 0.6,
            multiplierNodeId: multiplierId);
        FmSynthDocumentEditor.UpdateNode(workspace, synth, expected);
        FmSynthDocumentEditor.SetOutputNode(workspace, synth, oscillatorId);

        SynthResolver liveResolver = new(synth.Id), oracleResolver = new(synth.Id),
            originalResolver = new(synth.Id);
        FmSynthSound Author(FmOscillatorNode authored, SynthResolver resolver)
            => new(new FmSynthGraph(synth.Graph.Nodes.Select(n =>
                n.Id == oscillatorId ? (FmSynthNode)authored : n), oscillatorId),
                resolver, soundResolver: resolver, enableLiveMultiExpansion: true);
        FmSynthSound live = Author(expected, liveResolver);
        FmSynthSound oracle = Author(expected, oracleResolver);
        FmSynthSound unchanged = Author(expected, originalResolver);
        liveResolver.Sound = live;
        oracleResolver.Sound = oracle;
        originalResolver.Sound = unchanged;
        PlaybackSession actualSession = Session(liveResolver),
            oracleSession = Session(oracleResolver),
            untouchedSession = Session(originalResolver);
        Start(actualSession, synth.Id);
        Start(oracleSession, synth.Id);
        Start(untouchedSession, synth.Id);
        float[] initial = new float[19], referenceInitial = new float[19];
        actualSession.Render(0, 19, initial);
        oracleSession.Render(0, 19, referenceInitial);
        untouchedSession.Render(0, 19, new float[19]);
        Assert.That(initial, Is.EqualTo(referenceInitial).Within(1e-6f));
        PlaybackVoice held = actualSession.GetChannelState(0).CurrentVoice!;

        int changes = 0, publications = 0;
        FmSynthEditorControl editor = new(workspace, synth, () => { },
            _ => changes++, publishSynthEditAsync: () =>
            {
                FmSynthSound newSound = new(synth.Graph, liveResolver,
                    soundResolver: liveResolver, enableLiveMultiExpansion: true);
                actualSession.AdoptLiveSynthParameters(synth.Id, live, newSound);
                live = newSound;
                liveResolver.Sound = newSound;
                publications++;
                return Task.CompletedTask;
            });
        StackPanel inspector = Inspector(editor);
        StackPanel left = inspector.Children.OfType<Grid>().Single()
            .Children.OfType<StackPanel>().Single();
        ComboBox waveform = left.Children.OfType<ComboBox>().Single();
        TextBox curvature = left.Children.OfType<TextBox>().Single();
        TextBox frequency = Field<TextBox>(inspector, "Frequency Hz");
        TextBox minimum = Field<TextBox>(inspector, "Vmin");
        TextBox maximum = Field<TextBox>(inspector, "Vmax");
        CheckBox constant = inspector.Children.OfType<CheckBox>()
            .Single(c => Equals(c.Content, "Constant"));
        CheckBox exponential = inspector.Children.OfType<CheckBox>()
            .Single(c => Equals(c.Content,
                "Exponential multiplier (semitones)"));
        uint initialAudio = workspace.Document.AudioRevision;
        uint initialDocument = workspace.Document.DocumentRevision;
        int frame = 19, operations = 0;

        void Change(Action edit, FmOscillatorNode authored, bool mustDiffer)
        {
            uint revision = workspace.Document.AudioRevision;
            edit();
            operations++;
            expected = authored;
            FmOscillatorNode committed = synth.Graph.Nodes.OfType<FmOscillatorNode>()
                .Single(n => n.Id == oscillatorId);
            Assert.Multiple(() =>
            {
                Assert.That(committed.Waveform, Is.EqualTo(expected.Waveform));
                Assert.That(committed.Curvature, Is.EqualTo(expected.Curvature));
                Assert.That(committed.FrequencyHz, Is.EqualTo(expected.FrequencyHz));
                Assert.That(committed.Minimum, Is.EqualTo(expected.Minimum));
                Assert.That(committed.Maximum, Is.EqualTo(expected.Maximum));
                Assert.That(committed.MultiplierNodeId, Is.EqualTo(expected.MultiplierNodeId));
                Assert.That(committed.ExponentialMultiplier, Is.EqualTo(expected.ExponentialMultiplier));
                Assert.That(committed.Constant, Is.EqualTo(expected.Constant));
                Assert.That(workspace.Document.AudioRevision, Is.EqualTo(revision + 1));
                Assert.That(publications, Is.EqualTo(operations));
                Assert.That(changes, Is.EqualTo(operations));
                Assert.That(actualSession.CountOlderAudioVoices(), Is.Zero);
                Assert.That(actualSession.GetChannelState(0).CurrentVoice,
                    Is.SameAs(held));
                Assert.That(held.Sound, Is.SameAs(live));
            });
            FmSynthSound newOracle = Author(expected, oracleResolver);
            oracleSession.AdoptLiveSynthParameters(synth.Id, oracle, newOracle);
            oracle = newOracle;
            oracleResolver.Sound = newOracle;
            Assert.That(oracleSession.GetChannelState(0).CurrentVoice!.Sound,
                Is.SameAs(oracle), "independently authored revision must adopt");

            float[] observed = new float[Window], target = new float[Window],
                original = new float[Window];
            actualSession.Render(frame, Window, observed);
            oracleSession.Render(frame, Window, target);
            untouchedSession.Render(frame, Window, original);
            for (int i = 0; i < Window; i++)
                Assert.That(observed[i],
                    Is.EqualTo(target[i]).Within(3e-5f),
                    $"Oscillator field edit #{operations}, frame {frame + i}");
            if (mustDiffer)
                Assert.That(observed.Zip(original,
                    (a, b) => Math.Abs(a - b)).Max(), Is.GreaterThan(0.03),
                    "the changed Oscillator must audibly differ from an unedited note");
            Assert.That(actualSession.GetChannelState(0).CurrentVoice,
                Is.SameAs(held));
            Assert.That(actualSession.CountOlderAudioVoices(), Is.Zero);
            frame += Window;
        }

        Change(() => waveform.SelectedItem = FmOscillatorWaveform.Square,
            new FmOscillatorNode(oscillatorId, FmOscillatorWaveform.Square,
                80, -0.6, 0.6, multiplierId), true);
        Change(() => Enter(curvature, "1.5"),
            new FmOscillatorNode(oscillatorId, FmOscillatorWaveform.Square,
                80, -0.6, 0.6, multiplierId, curvature: 1.5), false);
        Change(() => Enter(minimum, "-0.25"),
            new FmOscillatorNode(oscillatorId, FmOscillatorWaveform.Square,
                80, -0.25, 0.6, multiplierId, curvature: 1.5), true);
        Change(() => Enter(maximum, "0.9"),
            new FmOscillatorNode(oscillatorId, FmOscillatorWaveform.Square,
                80, -0.25, 0.9, multiplierId, curvature: 1.5), true);
        Change(() => Enter(frequency, "117"),
            new FmOscillatorNode(oscillatorId, FmOscillatorWaveform.Square,
                117, -0.25, 0.9, multiplierId, curvature: 1.5), true);
        Change(() => exponential.IsChecked = true,
            new FmOscillatorNode(oscillatorId, FmOscillatorWaveform.Square,
                117, -0.25, 0.9, multiplierId, exponentialMultiplier: true,
                curvature: 1.5), true);
        Change(() => constant.IsChecked = true,
            new FmOscillatorNode(oscillatorId, FmOscillatorWaveform.Square,
                117, -0.25, 0.9, multiplierId, exponentialMultiplier: true,
                constant: true, curvature: 1.5), false);
        uint beforeNoop = workspace.Document.AudioRevision;
        Enter(frequency, "117");
        exponential.IsChecked = true;
        waveform.SelectedItem = FmOscillatorWaveform.Square;
        Assert.Multiple(() =>
        {
            Assert.That(workspace.Document.AudioRevision, Is.EqualTo(beforeNoop));
            Assert.That(workspace.Document.AudioRevision,
                Is.EqualTo(initialAudio + operations));
            Assert.That(workspace.Document.DocumentRevision,
                Is.EqualTo(initialDocument + operations));
            Assert.That(publications, Is.EqualTo(operations));
            Assert.That(changes, Is.EqualTo(operations));
        });
    }

    [AvaloniaTest]
    public void SourceInspectorFrequencyModesAndSampleSelectionMatchPreparedHeldPcm()
    {
        DocumentWorkspace workspace = new();
        ObjectId aId = workspace.Document.AllocateObjectId();
        ObjectId bId = workspace.Document.AllocateObjectId();
        workspace.Document.Add(new SampleDefinition(aId, "A_Frozen",
            new ExternalAssetReference("c2-source-a.wav")));
        workspace.Document.Add(new SampleDefinition(bId, "B_Frozen",
            new ExternalAssetReference("c2-source-b.wav")));
        FmSynthDefinition synth = FmSynthDocumentEditor.CreateFmSynth(
            workspace, "C2(d) Source");
        int multiplierId = FmSynthDocumentEditor.AddConstantNode(
            workspace, synth, 2, 40, 80);
        double neutral = FmMultiNode.NeutralFrequencyHz;
        int sourceNodeId = FmSynthDocumentEditor.AddSoundSourceNode(
            workspace, synth, 270, 80, aId, neutral);
        FmSoundSourceNode expected = new(sourceNodeId, aId, neutral,
            multiplierNodeId: multiplierId);
        FmSynthDocumentEditor.UpdateNode(workspace, synth, expected);
        FmSynthDocumentEditor.SetOutputNode(workspace, synth, sourceNodeId);

        SynthResolver liveResolver = new(synth.Id), oracleResolver = new(synth.Id),
            originalResolver = new(synth.Id);
        SampleSound Sample(ObjectId id, Func<int, float> value)
            => new(new SampleDefinition(id, "frozen PCM",
                new ExternalAssetReference("c2-in-memory.wav")),
                new MemorySampleData(Rate, 1,
                    Enumerable.Range(0, 16000).Select(value).ToArray()));
        SampleSound a = Sample(aId, i =>
            (float)(0.1 + (i % 111) * 0.004));
        SampleSound b = Sample(bId, i =>
            (float)(0.65 - (i % 89) * 0.006));
        foreach (SynthResolver resolver in new[] {
            liveResolver, oracleResolver, originalResolver
        })
        {
            resolver.Add(aId, a);
            resolver.Add(bId, b);
        }

        FmSynthSound Author(FmSoundSourceNode authored, SynthResolver resolver)
            => new(new FmSynthGraph(synth.Graph.Nodes.Select(n =>
                n.Id == sourceNodeId ? (FmSynthNode)authored : n), sourceNodeId),
                resolver, soundResolver: resolver, enableLiveMultiExpansion: true);
        FmSynthSound live = Author(expected, liveResolver);
        FmSynthSound oracle = Author(expected, oracleResolver);
        FmSynthSound unchanged = Author(expected, originalResolver);
        liveResolver.Sound = live;
        oracleResolver.Sound = oracle;
        originalResolver.Sound = unchanged;
        PlaybackSession actualSession = Session(liveResolver),
            oracleSession = Session(oracleResolver),
            untouchedSession = Session(originalResolver);
        Start(actualSession, synth.Id);
        Start(oracleSession, synth.Id);
        Start(untouchedSession, synth.Id);
        float[] initial = new float[17], referenceInitial = new float[17];
        actualSession.Render(0, 17, initial);
        oracleSession.Render(0, 17, referenceInitial);
        untouchedSession.Render(0, 17, new float[17]);
        Assert.That(initial, Is.EqualTo(referenceInitial).Within(1e-6f));
        PlaybackVoice held = actualSession.GetChannelState(0).CurrentVoice!;

        int changes = 0, publications = 0;
        FmSynthEditorControl editor = new(workspace, synth, () => { },
            _ => changes++, publishSynthEditAsync: () =>
            {
                FmSynthSound revised = new(synth.Graph, liveResolver,
                    soundResolver: liveResolver, enableLiveMultiExpansion: true);
                actualSession.AdoptLiveSynthParameters(synth.Id, live, revised);
                live = revised;
                liveResolver.Sound = revised;
                publications++;
                return Task.CompletedTask;
            });
        StackPanel inspector = Inspector(editor);
        TextBox frequency = Field<TextBox>(inspector, "Frequency Hz");
        CheckBox constant = inspector.Children.OfType<CheckBox>()
            .Single(c => Equals(c.Content, "Constant"));
        CheckBox exponential = inspector.Children.OfType<CheckBox>()
            .Single(c => Equals(c.Content,
                "Exponential multiplier (semitones)"));
        ComboBox source = (ComboBox)Field<Control>(inspector,
            "Source (Sample / Instrument / Synth)");
        object choiceB = source.Items.OfType<object>()
            .Single(v => v.ToString()!.Contains("B_Frozen"));
        object choiceNone = source.Items.OfType<object>()
            .Single(v => v.ToString()!.Contains("(None)"));
        uint initialAudio = workspace.Document.AudioRevision;
        uint initialDocument = workspace.Document.DocumentRevision;
        using FirPreparationScheduler worker = new();
        int frame = 17, operations = 0;

        void Change(Action edit, FmSoundSourceNode authored,
            bool needsWorker, bool mustDiffer, bool expectRetire = false)
        {
            uint revision = workspace.Document.AudioRevision;
            edit();
            operations++;
            expected = authored;
            FmSoundSourceNode committed = synth.Graph.Nodes
                .OfType<FmSoundSourceNode>().Single(n => n.Id == sourceNodeId);
            Assert.Multiple(() =>
            {
                Assert.That(committed.SourceId, Is.EqualTo(expected.SourceId));
                Assert.That(committed.FrequencyHz, Is.EqualTo(expected.FrequencyHz));
                Assert.That(committed.MultiplierNodeId, Is.EqualTo(expected.MultiplierNodeId));
                Assert.That(committed.ExponentialMultiplier, Is.EqualTo(expected.ExponentialMultiplier));
                Assert.That(committed.Constant, Is.EqualTo(expected.Constant));
                Assert.That(workspace.Document.AudioRevision, Is.EqualTo(revision + 1));
                Assert.That(publications, Is.EqualTo(operations));
                Assert.That(changes, Is.EqualTo(operations));
                Assert.That(actualSession.CountOlderAudioVoices(), Is.Zero);
                Assert.That(actualSession.GetChannelState(0).CurrentVoice,
                    Is.SameAs(held));
            });
            FmSynthSound newOracle = Author(expected, oracleResolver);
            oracleSession.AdoptLiveSynthParameters(synth.Id, oracle, newOracle);
            oracle = newOracle;
            oracleResolver.Sound = newOracle;
            long preparedBefore = worker.CompletedMultiExpansionCount;
            actualSession.QueuePendingFirControls(worker);
            oracleSession.QueuePendingFirControls(worker);
            if (needsWorker)
                Assert.That(SpinWait.SpinUntil(() =>
                    worker.CompletedMultiExpansionCount >= preparedBefore + 2,
                    TimeSpan.FromSeconds(5)), Is.True,
                    "SourceId replacement must prepare both new sample states off PCM");
            actualSession.AdoptPreparedFirControls();
            oracleSession.AdoptPreparedFirControls();
            Assert.Multiple(() =>
            {
                Assert.That(held.Sound, Is.SameAs(live),
                    "the existing physical voice must adopt prepared Source revision");
                Assert.That(oracleSession.GetChannelState(0).CurrentVoice!.Sound,
                    Is.SameAs(oracle));
                Assert.That(actualSession.CountOlderAudioVoices(), Is.Zero);
                Assert.That(worker.Fault, Is.Null);
            });

            float[] observed = new float[Window], target = new float[Window],
                original = new float[Window];
            actualSession.Render(frame, Window, observed);
            oracleSession.Render(frame, Window, target);
            untouchedSession.Render(frame, Window, original);
            for (int i = 0; i < Window; i++)
                Assert.That(observed[i],
                    Is.EqualTo(target[i]).Within(3e-5f),
                    $"Source edit #{operations} at frame {frame + i}");
            if (mustDiffer)
                Assert.That(observed.Zip(original,
                    (x, y) => Math.Abs(x - y)).Max(), Is.GreaterThan(0.04),
                    "Source mode/identity edit must audibly alter the held PCM");
            if (!expectRetire)
                Assert.That(actualSession.GetChannelState(0).CurrentVoice,
                    Is.SameAs(held));
            Assert.That(actualSession.CountOlderAudioVoices(), Is.Zero);
            frame += Window;
        }

        Change(() => Enter(frequency,
                (neutral * 1.5).ToString("R", CultureInfo.InvariantCulture)),
            new FmSoundSourceNode(sourceNodeId, aId, neutral * 1.5,
                multiplierNodeId: multiplierId), false, true);
        Change(() => exponential.IsChecked = true,
            new FmSoundSourceNode(sourceNodeId, aId, neutral * 1.5,
                multiplierNodeId: multiplierId, exponentialMultiplier: true),
            false, true);
        Change(() => constant.IsChecked = true,
            new FmSoundSourceNode(sourceNodeId, aId, neutral * 1.5,
                multiplierNodeId: multiplierId, exponentialMultiplier: true,
                constant: true), false, false);
        Change(() => source.SelectedItem = choiceB,
            new FmSoundSourceNode(sourceNodeId, bId, neutral * 1.5,
                multiplierNodeId: multiplierId, exponentialMultiplier: true,
                constant: true), true, true);
        Change(() => source.SelectedItem = choiceNone,
            new FmSoundSourceNode(sourceNodeId, ObjectId.None, neutral * 1.5,
                multiplierNodeId: multiplierId, exponentialMultiplier: true,
                constant: true), false, true, expectRetire: true);
        uint noOpRevision = workspace.Document.AudioRevision;
        source.SelectedItem = choiceNone;
        constant.IsChecked = true;
        Assert.Multiple(() =>
        {
            Assert.That(workspace.Document.AudioRevision, Is.EqualTo(noOpRevision));
            Assert.That(workspace.Document.AudioRevision,
                Is.EqualTo(initialAudio + operations));
            Assert.That(workspace.Document.DocumentRevision,
                Is.EqualTo(initialDocument + operations));
            Assert.That(publications, Is.EqualTo(operations));
            Assert.That(changes, Is.EqualTo(operations));
        });
    }

    private static void Enter(TextBox box, string value)
    {
        box.Text = value;
        box.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Enter,
        });
    }

    private static StackPanel Inspector(FmSynthEditorControl editor)
        => (StackPanel)typeof(FmSynthEditorControl)
            .GetField("_inspector", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(editor)!;

    private static T Field<T>(StackPanel inspector, string label) where T : Control
    {
        int i = inspector.Children.Select((c, index) => (c, index))
            .Single(p => p.c is TextBlock text && text.Text == label).index;
        return (T)inspector.Children[i + 1];
    }

    private static PlaybackSession Session(SynthResolver resolver)
        => new(new RenderContext(new RenderConfiguration(Rate, [
            new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0),
        ])), new NoteScheduleBuilder().Freeze(), resolver);

    private static void Start(PlaybackSession session, ObjectId id)
        => session.ApplyLiveEvent(ChannelTarget.Physical(0),
            [new StartNoteCommand(id)]);

    private sealed class SynthResolver(ObjectId synthId)
        : ISoundResolver, IEnvelopeCurveResolver
    {
        private readonly Dictionary<ObjectId, ISound> _other = [];
        public ISound? Sound { get; set; }
        public void Add(ObjectId id, ISound sound) => _other.Add(id, sound);
        public bool TryResolve(ObjectId id, bool mixdown, out ISound? sound)
        {
            sound = !mixdown && id == synthId
                ? Sound : !mixdown && _other.TryGetValue(id, out ISound? result)
                    ? result : null;
            return sound is not null;
        }
        public bool TryResolve(ObjectId id, out IEnvelopeCurve? curve)
        {
            curve = null;
            return false;
        }
    }
}
