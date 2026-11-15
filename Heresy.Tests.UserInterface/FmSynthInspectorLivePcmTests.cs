using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Headless.NUnit;

using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
using Heresy.Core.Sequencing;
using Heresy.Render.Configuration;
using Heresy.Render.Envelopes;
using Heresy.Render.FmSynthesis;
using Heresy.Render.Playback;
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
