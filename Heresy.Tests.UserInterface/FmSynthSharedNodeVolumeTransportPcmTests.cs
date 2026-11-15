using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;

using Heresy.Core.Envelopes;
using Heresy.Core.Filters;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Samples;
using Heresy.Core.Sequencing;
using Heresy.Playback;
using Heresy.Render.Configuration;
using Heresy.Render.Envelopes;
using Heresy.Render.Realtime;
using Heresy.Render.Sounds;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

/// <summary>
/// C2(g): shared Node Envelope and modeless Instrument Volume UI, precise
/// shared-object notifications, actual dependency-ordered SongPlaybackTransport
/// preparation and same-session held PCM from the real production source.
/// Only the external audio device is replaced with a manually driven backend.
/// </summary>
[TestFixture]
public sealed class FmSynthSharedNodeVolumeTransportPcmTests
{
    private const int Rate = 1000;

    [AvaloniaTest]
    public async Task EmbeddedSharedNodeEnvelopeEditorPublishesOnceAndUpdatesHeldProductionPcm()
    {
        DocumentWorkspace workspace = new();
        NodeEnvelopeDefinition shared = EnvelopeDocumentEditor.CreateNodeEnvelope(
            workspace, "C2(g) shared Node envelope",
            [new EnvelopeNode(TimeSpan.Zero, 0.25)], 0, 0,
            EnvelopeSustainLoopMode.Forward);
        FmSynthDefinition child = FmSynthDocumentEditor.CreateFmSynth(
            workspace, "C2(g) direct envelope");
        int node = FmSynthDocumentEditor.AddEnvelopeNode(
            workspace, child, shared.Id, 190, 80);
        FmSynthDocumentEditor.SetOutputNode(workspace, child, node);
        FmSynthDefinition parent = ReferencingParent(workspace, child);

        TrackingFactory factory = new(MonoConfiguration(), shared.Id);
        ManualBackend backend = new();
        using SongPlaybackTransport transport = new(backend, factory);
        await transport.SendLiveEventAsync(workspace.Document,
            ChannelTarget.Virtual(63), [new StartNoteCommand(child.Id)]);
        float[] before = backend.Render(13);
        Assert.That(before, Is.All.EqualTo(0.25f).Within(2e-6f));
        uint frozenBaseRevision = transport.CurrentPlaybackSnapshot.AudioRevision;

        int inspectorChanged = 0, inspectorPublishes = 0;
        List<Task> pending = [];
        List<SharedAudioObjectCommittedEventArgs> notifications = [];
        workspace.SharedAudioObjectCommitted += (_, e) =>
        {
            notifications.Add(e);
            pending.Add(transport.PublishSharedObjectEditAsync(
                e.Document, e.ObjectId));
        };
        FmSynthEditorControl synthEditor = new(workspace, child,
            () => { }, _ => inspectorChanged++,
            publishSynthEditAsync: () =>
            {
                inspectorPublishes++;
                Task task = transport.PublishSynthEditAsync(
                    workspace.Document, child.Id);
                pending.Add(task);
                return task;
            });
        NodeEnvelopeEditorControl editor = Inspector(synthEditor)
            .Children.OfType<NodeEnvelopeEditorControl>().Single();
        TextBox nodes = Private<TextBox>(editor, "_nodesInput");
        TextBox vmax = Private<TextBox>(editor, "_vmax");
        TextBlock feedback = Private<TextBlock>(editor, "_message");
        Button apply = ApplyButton(editor);
        uint startingAudio = workspace.Document.AudioRevision;
        uint startingDocument = workspace.Document.DocumentRevision;

        // Display range is presentation-only. The embedded editor can
        // signal "changed", but neither global shared notification nor
        // the Synth's audio-revision gate may publish a new sound.
        vmax.Text = "1.5";
        vmax.RaiseEvent(new FocusChangedEventArgs(InputElement.LostFocusEvent));
        Assert.Multiple(() =>
        {
            Assert.That(shared.Vmax, Is.EqualTo(1.5));
            Assert.That(workspace.Document.AudioRevision, Is.EqualTo(startingAudio));
            Assert.That(workspace.Document.DocumentRevision,
                Is.EqualTo(startingDocument + 1));
            Assert.That(notifications, Is.Empty);
            Assert.That(inspectorPublishes, Is.Zero);
            Assert.That(pending, Is.Empty);
            Assert.That(factory.Preparations, Is.Empty);
            Assert.That(backend.OpenCount, Is.EqualTo(1));
        });

        nodes.Text = "0, 0.75\n0, 0.5";
        Click(apply);
        Assert.Multiple(() =>
        {
            Assert.That(feedback.Text, Does.Contain("increase strictly"));
            Assert.That(workspace.Document.AudioRevision, Is.EqualTo(startingAudio));
            Assert.That(notifications, Is.Empty);
            Assert.That(inspectorPublishes, Is.Zero);
        });

        nodes.Text = "0, 0.75";
        Click(apply);
        await Task.WhenAll(pending.ToArray());
        Assert.Multiple(() =>
        {
            Assert.That(workspace.Document.AudioRevision, Is.EqualTo(startingAudio + 1));
            Assert.That(workspace.Document.DocumentRevision,
                Is.EqualTo(startingDocument + 2));
            Assert.That(shared.Nodes.Single().Value, Is.EqualTo(0.75));
            Assert.That(inspectorChanged, Is.EqualTo(2),
                "only valid display and audio commits notify the inspector");
            Assert.That(inspectorPublishes, Is.EqualTo(1),
                "the Synth editor routes only semantic audio changes");
            Assert.That(notifications.Count, Is.EqualTo(1));
            Assert.That(notifications[0].ObjectId, Is.EqualTo(shared.Id));
            Assert.That(notifications[0].AudioRevision,
                Is.EqualTo(workspace.Document.AudioRevision));
            Assert.That(factory.Preparations.Select(p => p.SynthId),
                Is.EqualTo(new[] { child.Id, parent.Id }),
                "global shared publication must compile child before parent");
            Assert.That(factory.Preparations.All(p =>
                p.AudioRevision == startingAudio + 1
                && p.EnvelopeLevel == 0.75), Is.True);
            Assert.That(backend.OpenCount, Is.EqualTo(1));
        });

        // The shared Node curve retains the original note's envelope clock
        // and uses the same 100-ms smoothstep as other live parameter edits.
        // Independently calculate all frames, including the frozen old gain
        // at the revision boundary and the settled target after frame 100.
        float[] edited = backend.Render(120);
        for (int frame = 0; frame < edited.Length; frame++)
        {
            double t = Math.Min(1.0, frame / 100.0);
            double w = t * t * (3.0 - 2.0 * t);
            float target = (float)(0.25 + 0.5 * w);
            Assert.That(edited[frame], Is.EqualTo(target).Within(2e-5f),
                $"shared Node editor smoothstep on held PCM frame {13 + frame}");
        }
        Assert.Multiple(() =>
        {
            Assert.That(factory.OlderAudioCount, Is.Zero);
            Assert.That(transport.CurrentPlaybackSnapshot.AudioRevision,
                Is.EqualTo(frozenBaseRevision));
        });

        Click(apply);
        await Task.WhenAll(pending.ToArray());
        Assert.Multiple(() =>
        {
            Assert.That(workspace.Document.AudioRevision, Is.EqualTo(startingAudio + 1));
            Assert.That(notifications.Count, Is.EqualTo(1));
            Assert.That(inspectorPublishes, Is.EqualTo(1));
            Assert.That(factory.Preparations.Count, Is.EqualTo(2),
                "an identical shared draft cannot republish via either route");
        });
        await transport.PublishSharedObjectEditAsync(workspace.Document, shared.Id);
        Assert.That(factory.Preparations.Count, Is.EqualTo(2));
    }

    [AvaloniaTest]
    public async Task ModelessInstrumentVolumeEditorPublishesDependentSynthsAndFadesHeldSamples()
    {
        DocumentWorkspace workspace = new();
        ObjectId sampleId = workspace.Document.AllocateObjectId();
        SampleDefinition sample = SampleDefinition.CreateImported(
            sampleId, "C2(g) held Sample", "sample.wav", FourFrameWave(16384));
        sample.Loop = new SampleLoop(SampleLoopMode.Forward, 0, 4);
        workspace.Document.Add(sample);
        NodeEnvelopeDefinition volume = EnvelopeDocumentEditor.CreateNodeEnvelope(
            workspace, "C2(g) Instrument Volume",
            [new EnvelopeNode(TimeSpan.Zero, 1.0)], 0, 0,
            EnvelopeSustainLoopMode.Forward);
        ObjectId instrumentId = workspace.Document.AllocateObjectId();
        InstrumentDefinition instrument = new(instrumentId, "Data Instrument");
        instrument.ToneSpecifications.Add(new ToneSpecification
        {
            SourceId = sampleId, VolumeEnvelopeId = volume.Id,
        });
        instrument.ToneTable.Add(0);
        workspace.Document.Add(instrument);

        FmSynthDefinition child = FmSynthDocumentEditor.CreateFmSynth(
            workspace, "Two held Instrument Sources");
        int a = FmSynthDocumentEditor.AddSoundSourceNode(workspace, child,
            80, 80, instrumentId, FmMultiNode.NeutralFrequencyHz);
        int b = FmSynthDocumentEditor.AddSoundSourceNode(workspace, child,
            80, 220, instrumentId, FmMultiNode.NeutralFrequencyHz);
        int sum = FmSynthDocumentEditor.AddOperatorNode(workspace, child,
            FmOperatorKind.Add, [a, b], 320, 140);
        FmSynthDocumentEditor.SetOutputNode(workspace, child, sum);
        FmSynthDefinition parent = ReferencingParent(workspace, child);

        TrackingFactory factory = new(MonoConfiguration(), volume.Id);
        ManualBackend backend = new();
        using SongPlaybackTransport transport = new(backend, factory);
        await transport.SendLiveEventAsync(workspace.Document,
            ChannelTarget.Virtual(71), [new StartNoteCommand(child.Id)]);
        float[] initial = backend.Render(32);
        Assert.That(initial, Is.All.EqualTo(1.0f).Within(2e-5f),
            "two independent looping 0.5 PCM Sources share the Volume curve");
        uint frozenBaseRevision = transport.CurrentPlaybackSnapshot.AudioRevision;
        uint startingAudio = workspace.Document.AudioRevision;
        int changed = 0, directPublishes = 0;
        List<SharedAudioObjectCommittedEventArgs> notifications = [];
        List<Task> pending = [];
        workspace.SharedAudioObjectCommitted += (_, e) =>
        {
            notifications.Add(e);
            pending.Add(transport.PublishSharedObjectEditAsync(
                e.Document, e.ObjectId));
        };

        FmSynthEditorControl editor = new(workspace, child,
            () => { }, _ => changed++,
            publishSynthEditAsync: () =>
            {
                directPublishes++;
                Task task = transport.PublishSynthEditAsync(
                    workspace.Document, child.Id);
                pending.Add(task);
                return task;
            });
        Window owner = new()
        {
            Width = 960, Height = 720, Content = editor,
        };
        owner.Show();
        Window? volumeWindow = null;
        try
        {
            typeof(FmSynthEditorControl).GetMethod("SelectNode",
                BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(editor, [a]);
            StackPanel inspector = Inspector(editor);
            Button open = inspector.Children.OfType<Button>()
                .Single(x => Equals(x.Content, "Edit shared Volume envelope…"));
            Assert.That(inspector.Children.OfType<ComboBox>()
                .Count(), Is.GreaterThan(0));
            Click(open);

            Dictionary<ObjectId, Window> openWindows =
                Private<Dictionary<ObjectId, Window>>(editor, "_sharedVolumeEditors");
            Assert.That(openWindows.TryGetValue(volume.Id, out volumeWindow), Is.True,
                "the actual modeless Volume action must open the shared editor");
            Assert.That(volumeWindow, Is.Not.Null);
            NodeEnvelopeEditorControl volumeEditor =
                (NodeEnvelopeEditorControl)((ScrollViewer)volumeWindow!.Content!).Content!;
            TextBox nodes = Private<TextBox>(volumeEditor, "_nodesInput");
            Button apply = ApplyButton(volumeEditor);

            nodes.Text = "0, 0.25";
            Click(apply);
            await Task.WhenAll(pending.ToArray());

            Assert.Multiple(() =>
            {
                Assert.That(volume.Nodes.Single().Value, Is.EqualTo(0.25));
                Assert.That(workspace.Document.AudioRevision, Is.EqualTo(startingAudio + 1));
                Assert.That(notifications.Count, Is.EqualTo(1));
                Assert.That(notifications[0].ObjectId, Is.EqualTo(volume.Id));
                Assert.That(directPublishes, Is.EqualTo(1));
                Assert.That(changed, Is.EqualTo(1));
                Assert.That(factory.Preparations.Select(p => p.SynthId),
                    Is.EqualTo(new[] { child.Id, parent.Id }),
                    "global Volume event and editor callback must share one publish");
                Assert.That(factory.Preparations.All(p =>
                    p.AudioRevision == startingAudio + 1
                    && p.EnvelopeLevel == 0.25), Is.True);
                Assert.That(backend.OpenCount, Is.EqualTo(1));
            });

            // Both Sample-backed Instrument child states migrate together;
            // the old PCM clock and waveform continue without re-onset.
            float[] after = backend.Render(101);
            for (int frame = 0; frame < after.Length; frame++)
            {
                double t = frame / 100.0;
                double w = t * t * (3.0 - 2.0 * t);
                float expected = (float)(1.0 - 0.75 * w);
                Assert.That(after[frame], Is.EqualTo(expected).Within(3e-5f),
                    $"two held Instrument Sample Volumes at frame {32 + frame}");
            }
            Assert.Multiple(() =>
            {
                Assert.That(factory.OlderAudioCount, Is.Zero);
                Assert.That(transport.CurrentPlaybackSnapshot.AudioRevision,
                    Is.EqualTo(frozenBaseRevision));
            });

            Click(apply);
            await Task.WhenAll(pending.ToArray());
            Assert.Multiple(() =>
            {
                Assert.That(notifications.Count, Is.EqualTo(1));
                Assert.That(directPublishes, Is.EqualTo(1));
                Assert.That(factory.Preparations.Count, Is.EqualTo(2));
            });
            Assert.That(backend.Render(20),
                Is.All.EqualTo(0.25f).Within(3e-5f));
        }
        finally
        {
            volumeWindow?.Close();
            owner.Close();
        }
    }


    [AvaloniaTest]
    public async Task ModelessInstrumentVolumeWithPitchToneFilterOrNestedSynthAdoptsContinuingPcm()
    {
        // C2(ae), red-first: these are all real modeless data-Instrument
        // Volume editor actions in the *same* open Synth view. Neither the
        // Instrument selection nor any tone's SourceId/wiring is edited.
        // Existing independent Sample pitch, tone Filter and nested Synth
        // histories must remain at their original running clocks.
        foreach (string variant in new[] { "Pitch", "ToneFilter", "NestedSynth" })
        {
            DocumentWorkspace workspace = new();
            ObjectId sampleId = workspace.Document.AllocateObjectId();
            SampleDefinition sample = SampleDefinition.CreateImported(
                sampleId, "C2(ae) PCM " + variant, "sample.wav",
                FourFrameWave(16384));
            sample.Loop = new SampleLoop(SampleLoopMode.Forward, 0, 4);
            workspace.Document.Add(sample);

            NodeEnvelopeDefinition volume =
                EnvelopeDocumentEditor.CreateNodeEnvelope(
                    workspace, "C2(ae) shared Volume " + variant,
                    [new EnvelopeNode(TimeSpan.Zero, 1.0)], 0, 0,
                    EnvelopeSustainLoopMode.Forward);
            ObjectId? pitchId = null;
            if (variant == "Pitch")
                pitchId = EnvelopeDocumentEditor.CreateNodeEnvelope(
                    workspace, "C2(ae) unchanged pitch",
                    [new EnvelopeNode(TimeSpan.Zero, 0.3)], 0, 0,
                    EnvelopeSustainLoopMode.Forward).Id;

            FilterInstance? toneFilter = null;
            if (variant == "ToneFilter")
            {
                toneFilter = new FilterInstance(FilterDocumentEditor.CreateFilter(
                    workspace, "C2(ae) unchanged OnePole"));
                toneFilter.SetStageParameter(1, "CutoffHz",
                    FilterParameterValue.FromNumber(180));
            }
            ObjectId selectedSource = sampleId;
            if (variant == "NestedSynth")
            {
                FmSynthDefinition nested = FmSynthDocumentEditor.CreateFmSynth(
                    workspace, "C2(ae) nested Synth");
                int constant = FmSynthDocumentEditor.AddConstantNode(
                    workspace, nested, 0.5, 60, 70);
                FmSynthDocumentEditor.SetOutputNode(workspace, nested, constant);
                selectedSource = nested.Id;
            }

            ObjectId instrumentId = workspace.Document.AllocateObjectId();
            InstrumentDefinition instrument = new(instrumentId,
                "C2(ae) Instrument " + variant);
            instrument.ToneSpecifications.Add(new ToneSpecification
            {
                SourceId = selectedSource,
                VolumeEnvelopeId = volume.Id,
                PitchEnvelopeId = pitchId,
                FilterConfiguration = toneFilter,
            });
            instrument.ToneTable.Add(0);
            workspace.Document.Add(instrument);
            FmSynthDefinition child = FmSynthDocumentEditor.CreateFmSynth(
                workspace, "C2(ae) audition " + variant);
            int sourceId = FmSynthDocumentEditor.AddSoundSourceNode(
                workspace, child, 80, 90, instrumentId,
                FmMultiNode.NeutralFrequencyHz);
            FmSynthDocumentEditor.SetOutputNode(workspace, child, sourceId);
            FmSynthDefinition parent = ReferencingParent(workspace, child);

            TrackingFactory factory = new(MonoConfiguration(), volume.Id);
            ManualBackend backend = new();
            using SongPlaybackTransport transport = new(backend, factory);
            TrackingFactory referenceFactory = new(MonoConfiguration(), volume.Id);
            ManualBackend referenceBackend = new();
            using SongPlaybackTransport reference =
                new(referenceBackend, referenceFactory);
            await transport.SendLiveEventAsync(workspace.Document,
                ChannelTarget.Virtual(191), [new StartNoteCommand(child.Id)]);
            await reference.SendLiveEventAsync(workspace.Document,
                ChannelTarget.Virtual(191), [new StartNoteCommand(child.Id)]);
            float[] before = backend.Render(40);
            float[] initialReference = referenceBackend.Render(40);
            Assert.That(before, Is.EqualTo(initialReference).Within(3e-5f),
                variant + ": original authoring and reference PCM align");
            Assert.That(before.Max(x => Math.Abs(x)), Is.GreaterThan(0.05f),
                variant + ": selected tone must really be audible");
            uint baseRevision = transport.CurrentPlaybackSnapshot.AudioRevision;
            uint originalAudio = workspace.Document.AudioRevision;
            int publishes = 0, changed = 0;
            List<Task> pending = [];
            List<SharedAudioObjectCommittedEventArgs> notifications = [];
            workspace.SharedAudioObjectCommitted += (_, e) =>
            {
                notifications.Add(e);
                pending.Add(transport.PublishSharedObjectEditAsync(
                    e.Document, e.ObjectId));
            };

            FmSynthEditorControl editor = new(workspace, child, () => { },
                _ => changed++,
                publishSynthEditAsync: () =>
                {
                    publishes++;
                    Task t = transport.PublishSynthEditAsync(
                        workspace.Document, child.Id);
                    pending.Add(t);
                    return t;
                });
            Window owner = new() { Width = 960, Height = 720, Content = editor };
            owner.Show();
            Window? popup = null;
            try
            {
                typeof(FmSynthEditorControl).GetMethod("SelectNode",
                    BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(editor, [sourceId]);
                Click(Inspector(editor).Children.OfType<Button>()
                    .Single(x => Equals(x.Content, "Edit shared Volume envelope…")));
                var opened = Private<Dictionary<ObjectId, Window>>(
                    editor, "_sharedVolumeEditors");
                popup = opened[volume.Id];
                NodeEnvelopeEditorControl volumeEditor = (NodeEnvelopeEditorControl)
                    ((ScrollViewer)popup.Content!).Content!;
                Private<TextBox>(volumeEditor, "_nodesInput").Text = "0, 0.25";
                Click(ApplyButton(volumeEditor));
                await Task.WhenAll(pending.ToArray())
                    .WaitAsync(TimeSpan.FromSeconds(8));
                Assert.Multiple(() =>
                {
                    Assert.That(volume.Nodes.Single().Value, Is.EqualTo(0.25));
                    Assert.That(workspace.Document.AudioRevision,
                        Is.EqualTo(originalAudio + 1));
                    Assert.That(publishes, Is.EqualTo(1));
                    Assert.That(changed, Is.EqualTo(1));
                    Assert.That(notifications.Count, Is.EqualTo(1));
                    Assert.That(notifications[0].ObjectId, Is.EqualTo(volume.Id));
                    Assert.That(factory.Preparations.Select(x => x.SynthId),
                        Is.EqualTo(new[] { child.Id, parent.Id }));
                    Assert.That(factory.Preparations.All(x =>
                        x.AudioRevision == originalAudio + 1
                            && x.EnvelopeLevel == 0.25), Is.True);
                    Assert.That(backend.OpenCount, Is.EqualTo(1));
                });

                float[] actual = backend.Render(101);
                float[] untouched = referenceBackend.Render(101);
                for (int frame = 0; frame < actual.Length; frame++)
                {
                    double t = frame / 100.0;
                    double smooth = t * t * (3 - 2 * t);
                    Assert.That(actual[frame],
                        Is.EqualTo((float)(untouched[frame] *
                            (1.0 - 0.75 * smooth))).Within(5e-5f),
                        variant + ": original pitch/filter/nested state at " + frame);
                }
                Assert.Multiple(() =>
                {
                    Assert.That(factory.OlderAudioCount, Is.Zero,
                        variant + ": eligible shared gain must be current");
                    Assert.That(transport.CurrentPlaybackSnapshot.AudioRevision,
                        Is.EqualTo(baseRevision));
                    Assert.That(backend.OpenCount, Is.EqualTo(1));
                });
                float[] stabilized = backend.Render(31);
                float[] unchanged = referenceBackend.Render(31);
                for (int i = 0; i < stabilized.Length; i++)
                    Assert.That(stabilized[i],
                        Is.EqualTo(unchanged[i] * 0.25f).Within(5e-5f),
                        variant + ": settled gain keeps original running tone");
                if (variant == "Pitch")
                {
                    // A *different* shared Pitch curve edit is not the
                    // same-bound Volume migration we just accepted. Its
                    // frozen history cannot be replaced under the retained
                    // Sample playhead. This must be an explicit older
                    // generation, not a silent false-current result.
                    NodeEnvelopeDefinition pitch = (NodeEnvelopeDefinition)
                        workspace.Document.Objects[pitchId!.Value];
                    EnvelopeDocumentEditor.UpdateNodeEnvelope(
                        workspace, pitch,
                        [new EnvelopeNode(TimeSpan.Zero, 0.8)], 0, 0,
                        EnvelopeSustainLoopMode.Forward);
                    await Task.WhenAll(pending.ToArray())
                        .WaitAsync(TimeSpan.FromSeconds(8));
                    Assert.Multiple(() =>
                    {
                        Assert.That(workspace.Document.AudioRevision,
                            Is.EqualTo(originalAudio + 2));
                        Assert.That(notifications.Count, Is.EqualTo(2));
                        Assert.That(notifications[1].ObjectId, Is.EqualTo(pitch.Id));
                        Assert.That(factory.Preparations.Count, Is.EqualTo(4),
                            "Pitch dependent Synth still gets detached preparations");
                    });
                    float[] retainedPitch = backend.Render(29);
                    float[] originalPitch = referenceBackend.Render(29);
                    for (int i = 0; i < retainedPitch.Length; i++)
                        Assert.That(retainedPitch[i],
                            Is.EqualTo(originalPitch[i] * 0.25f)
                                .Within(5e-5f),
                            "unmigrated companion Pitch cannot replace old PCM");
                    Assert.That(factory.OlderAudioCount, Is.EqualTo(1),
                        "different retained Pitch must mark missed generation");

                    // A later compatible Volume edit cannot erase that
                    // deliberately older Pitch generation or recolor its
                    // frozen 0.25 gain. Fresh notes receive 0.4.
                    Private<TextBox>(volumeEditor, "_nodesInput").Text = "0, 0.4";
                    Click(ApplyButton(volumeEditor));
                    await Task.WhenAll(pending.ToArray())
                        .WaitAsync(TimeSpan.FromSeconds(8));
                    float[] afterVolume = backend.Render(25);
                    float[] originalVolume = referenceBackend.Render(25);
                    for (int i = 0; i < afterVolume.Length; i++)
                        Assert.That(afterVolume[i],
                            Is.EqualTo(originalVolume[i] * 0.25f)
                                .Within(5e-5f),
                            "refused old companion stays on previous Volume");
                    Assert.That(factory.OlderAudioCount, Is.EqualTo(1));
                    await transport.SendLiveEventAsync(workspace.Document,
                        ChannelTarget.Virtual(192),
                        [new StartNoteCommand(child.Id)]);
                    float[] mixed = backend.Render(8);
                    float[] oldOnly = referenceBackend.Render(8);
                    Assert.That(mixed.Zip(oldOnly, (a, b) => a - b * 0.25f)
                        .Max(), Is.GreaterThan(0.12f),
                        "fresh note must have authored new Volume and pitch");
                    Assert.That(factory.OlderAudioCount, Is.EqualTo(1));
                }
            }
            finally { popup?.Close(); owner.Close(); }
        }
    }

    [AvaloniaTest]
    public async Task EnvelopeChoiceNodeToNodeToScriptAndNonePublishesHeldProductionPcm()
    {
        // C2(i): the actual Envelope ComboBox, not a document setter.
        // Bound held-note changes retain a single producer and publish only
        // one frozen child-before-parent revision per semantic selection.
        DocumentWorkspace workspace = new();
        NodeEnvelopeDefinition first = EnvelopeDocumentEditor.CreateNodeEnvelope(
            workspace, "C2(i) first",
            [new EnvelopeNode(TimeSpan.Zero, 0.25)], 0, 0,
            EnvelopeSustainLoopMode.Forward);
        NodeEnvelopeDefinition second = EnvelopeDocumentEditor.CreateNodeEnvelope(
            workspace, "C2(i) second",
            [new EnvelopeNode(TimeSpan.Zero, 0.75)], 0, 0,
            EnvelopeSustainLoopMode.Forward);
        ScriptEnvelopeDefinition scripted =
            EnvelopeDocumentEditor.CreateScriptEnvelope(
                workspace, "C2(i) script", "0.4");
        FmSynthDefinition child = FmSynthDocumentEditor.CreateFmSynth(
            workspace, "C2(i) selected Envelope");
        int envelopeNode = FmSynthDocumentEditor.AddEnvelopeNode(
            workspace, child, first.Id, 200, 70);
        FmSynthDocumentEditor.SetOutputNode(workspace, child, envelopeNode);
        FmSynthDefinition parent = ReferencingParent(workspace, child);

        TrackingFactory factory = new(MonoConfiguration(), first.Id);
        ManualBackend backend = new();
        using SongPlaybackTransport transport = new(backend, factory);
        await transport.SendLiveEventAsync(workspace.Document,
            ChannelTarget.Virtual(83), [new StartNoteCommand(child.Id)]);
        Assert.That(backend.Render(13),
            Is.All.EqualTo(0.25f).Within(2e-6f));
        uint baseRevision = transport.CurrentPlaybackSnapshot.AudioRevision;
        uint audio = workspace.Document.AudioRevision;
        uint document = workspace.Document.DocumentRevision;
        int changed = 0, callbacks = 0;
        List<Task> publishes = [];
        FmSynthEditorControl editor = new(workspace, child,
            () => { }, _ => changed++,
            publishSynthEditAsync: () =>
            {
                callbacks++;
                Task task = transport.PublishSynthEditAsync(
                    workspace.Document, child.Id);
                publishes.Add(task);
                return task;
            });
        ComboBox Choice() => EnvelopeChoiceBox(editor);
        ObjectId SelectedId() => child.Graph.Nodes
            .OfType<FmEnvelopeNode>().Single().EnvelopeId;
        async Task Select(ObjectId target, int ordinal)
        {
            ComboBox choice = Choice();
            object item = choice.Items.OfType<object>().Single(option =>
                option.GetType().GetProperty("Id")?.GetValue(option)
                    is ObjectId id && id == target);
            choice.SelectedItem = item;
            await Task.WhenAll(publishes.ToArray());
            Assert.Multiple(() =>
            {
                Assert.That(SelectedId(), Is.EqualTo(target));
                Assert.That(workspace.Document.AudioRevision,
                    Is.EqualTo(audio + ordinal));
                Assert.That(workspace.Document.DocumentRevision,
                    Is.EqualTo(document + ordinal));
                Assert.That(callbacks, Is.EqualTo(ordinal),
                    "one selector commit requests one publication");
                Assert.That(changed, Is.EqualTo(2 * ordinal),
                    "one inspector parameter commit and one rebuilt-view "
                    + "notification are allowed; publication is once per revision");
                Assert.That(factory.Preparations.Count, Is.EqualTo(2 * ordinal));
                Assert.That(factory.Preparations.Skip(2 * (ordinal - 1))
                    .Select(p => p.SynthId),
                    Is.EqualTo(new[] { child.Id, parent.Id }));
                Assert.That(factory.Preparations[2 * (ordinal - 1)]
                    .SelectedEnvelopeId, Is.EqualTo(target));
                Assert.That(backend.OpenCount, Is.EqualTo(1));
            });
        }
        void Fade(float from, float to, int start)
        {
            float[] values = backend.Render(101);
            for (int i = 0; i < values.Length; i++)
            {
                double t = i / 100.0;
                double w = t * t * (3.0 - 2.0 * t);
                Assert.That(values[i],
                    Is.EqualTo((float)(from * (1 - w) + to * w))
                        .Within(3e-5f),
                    $"selected Envelope held PCM at {start + i}");
            }
            Assert.That(factory.OlderAudioCount, Is.Zero,
                "compatible selector changes are not missed generations");
        }

        // New... needs an open host window to present its modal prompt.
        // With this intentionally unmounted editor it must fail safely and
        // restore the previous selection without publishing an audio edit.
        ComboBox original = Choice();
        object newOption = original.Items.OfType<ComboBoxItem>().Single(item =>
            item.Content is TextBlock text && text.Text == "New...");
        original.SelectedItem = newOption;
        Assert.Multiple(() =>
        {
            Assert.That(SelectedId(), Is.EqualTo(first.Id));
            Assert.That(workspace.Document.AudioRevision, Is.EqualTo(audio));
            Assert.That(callbacks, Is.Zero);
            Assert.That(factory.Preparations, Is.Empty);
        });

        await Select(second.Id, 1);
        Fade(0.25f, 0.75f, 13);
        // Re-selecting the selected Envelope is a no-op.
        ComboBox current = Choice();
        current.SelectedItem = current.SelectedItem;
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(audio + 1));
        Assert.That(callbacks, Is.EqualTo(1));

        await Select(scripted.Id, 2);
        Fade(0.75f, 0.4f, 114);

        // An assigned Envelope may be cleared by the real (None) choice.
        // Its already-held audible output has the standard 100-ms fade to
        // silence; no old-voice warning is warranted for eligible adoption.
        ComboBox clear = Choice();
        clear.SelectedItem = clear.Items.OfType<ComboBoxItem>().Single(item =>
            Equals(item.Content, "(None)"));
        await Task.WhenAll(publishes.ToArray());
        Assert.Multiple(() =>
        {
            Assert.That(SelectedId(), Is.EqualTo(ObjectId.None));
            Assert.That(workspace.Document.AudioRevision, Is.EqualTo(audio + 3));
            Assert.That(callbacks, Is.EqualTo(3));
            Assert.That(factory.Preparations.Count, Is.EqualTo(6));
            Assert.That(factory.Preparations.Skip(4).Select(p => p.SynthId),
                Is.EqualTo(new[] { child.Id, parent.Id }));
            Assert.That(factory.Preparations[4].SelectedEnvelopeId,
                Is.EqualTo(ObjectId.None));
        });
        Fade(0.4f, 0.0f, 215);
        Assert.That(backend.Render(8), Is.All.EqualTo(0).Within(2e-6f));
        Assert.That(transport.CurrentPlaybackSnapshot.AudioRevision,
            Is.EqualTo(baseRevision),
            "partial Synth overlays must not advance the base snapshot");
    }

    [AvaloniaTest]
    public async Task EnvelopeSelectorAfterNoteOffRetainsOriginalReleaseAndStickyCurrency()
    {
        // C4(n) renderer contract exercised through the actual selector,
        // production worker and two independently continuing production
        // sessions: the untouched second session is the release PCM oracle.
        DocumentWorkspace workspace = new();
        static EnvelopeNode[] Release(int duration) =>
        [
            new(TimeSpan.Zero, 1.0),
            new(TimeSpan.FromMilliseconds(1), 1.0),
            new(TimeSpan.FromMilliseconds(duration), 0.0),
        ];
        NodeEnvelopeDefinition first = EnvelopeDocumentEditor.CreateNodeEnvelope(
            workspace, "C2(i) old release", Release(90), 1, 1,
            EnvelopeSustainLoopMode.Forward);
        NodeEnvelopeDefinition second = EnvelopeDocumentEditor.CreateNodeEnvelope(
            workspace, "C2(i) alternate release", Release(20), 1, 1,
            EnvelopeSustainLoopMode.Forward);
        FmSynthDefinition child = FmSynthDocumentEditor.CreateFmSynth(
            workspace, "C2(i) selected releasing Envelope");
        int id = FmSynthDocumentEditor.AddEnvelopeNode(
            workspace, child, first.Id, 200, 70);
        FmSynthDocumentEditor.SetOutputNode(workspace, child, id);
        FmSynthDefinition parent = ReferencingParent(workspace, child);

        TrackingFactory actualFactory = new(MonoConfiguration(), first.Id);
        TrackingFactory referenceFactory = new(MonoConfiguration(), first.Id);
        ManualBackend actualBackend = new(), referenceBackend = new();
        using SongPlaybackTransport transport = new(actualBackend, actualFactory);
        using SongPlaybackTransport reference = new(
            referenceBackend, referenceFactory);
        await transport.SendLiveEventAsync(workspace.Document,
            ChannelTarget.Virtual(91), [new StartNoteCommand(child.Id)]);
        await reference.SendLiveEventAsync(workspace.Document,
            ChannelTarget.Virtual(91), [new StartNoteCommand(child.Id)]);
        float[] initial = actualBackend.Render(12);
        Assert.That(initial, Is.All.EqualTo(1.0f).Within(2e-6f));
        Assert.That(referenceBackend.Render(12),
            Is.EqualTo(initial).Within(2e-6f));
        await transport.SendLiveEventAsync(workspace.Document,
            ChannelTarget.Virtual(91), [new NoteOffCommand()]);
        await reference.SendLiveEventAsync(workspace.Document,
            ChannelTarget.Virtual(91), [new NoteOffCommand()]);
        Assert.That(actualBackend.Render(5),
            Is.EqualTo(referenceBackend.Render(5)).Within(2e-6f));

        uint revision = workspace.Document.AudioRevision;
        int callbacks = 0;
        List<Task> publishes = [];
        FmSynthEditorControl editor = new(workspace, child, () => { }, _ => { },
            publishSynthEditAsync: () =>
            {
                callbacks++;
                Task task = transport.PublishSynthEditAsync(
                    workspace.Document, child.Id);
                publishes.Add(task);
                return task;
            });
        ComboBox choice = EnvelopeChoiceBox(editor);
        choice.SelectedItem = choice.Items.OfType<object>().Single(option =>
            option.GetType().GetProperty("Id")?.GetValue(option)
                is ObjectId selected && selected == second.Id);
        await Task.WhenAll(publishes.ToArray());
        Assert.Multiple(() =>
        {
            Assert.That(workspace.Document.AudioRevision,
                Is.EqualTo(revision + 1));
            Assert.That(callbacks, Is.EqualTo(1));
            Assert.That(actualFactory.Preparations.Select(p => p.SynthId),
                Is.EqualTo(new[] { child.Id, parent.Id }));
            Assert.That(actualFactory.Preparations[0].SelectedEnvelopeId,
                Is.EqualTo(second.Id));
            Assert.That(actualBackend.OpenCount, Is.EqualTo(1));
        });
        // The publication is consumed only at the PCM boundary. A refused
        // audible release changes neither original PCM nor end timestamp.
        float[] oldRelease = actualBackend.Render(20);
        float[] originalReference = referenceBackend.Render(20);
        for (int i = 0; i < oldRelease.Length; i++)
            Assert.That(oldRelease[i],
                Is.EqualTo(originalReference[i]).Within(2e-6f),
                $"refused original release at frame {17 + i}");
        Assert.That(actualFactory.OlderAudioCount, Is.EqualTo(1),
            "a definitively refused audible release must be sticky older");

        // New notes must use the newly selected curve even though the old
        // physical note keeps its original frozen release generation.
        await transport.SendLiveEventAsync(workspace.Document,
            ChannelTarget.Virtual(92), [new StartNoteCommand(child.Id)]);
        float[] withFresh = actualBackend.Render(2);
        float[] withoutFresh = referenceBackend.Render(2);
        Assert.That(withFresh.Zip(withoutFresh,
            (a, b) => Math.Abs(a - b)).Max(), Is.GreaterThan(0.9),
            "a new note uses the current second Envelope while old release continues");
        Assert.That(actualFactory.OlderAudioCount, Is.EqualTo(1));
        // Original release's end eventually retires the sticky count, while
        // the independently sounding newer sustained note remains current.
        actualBackend.Render(130);
        referenceBackend.Render(130);
        Assert.That(actualFactory.OlderAudioCount, Is.Zero);
        Assert.Multiple(() =>
        {
            Assert.That(referenceFactory.OlderAudioCount, Is.Zero);
            Assert.That(actualBackend.OpenCount, Is.EqualTo(1));
        });
    }


    [AvaloniaTest]
    public async Task SharedScriptEnvelopeEditorRetunesHeldPcmAndRefusesReleaseEdits()
    {
        // C2(o): the real embedded Script editor, shared-object event,
        // deduplicated Synth publication and continuing production PCM.
        // After Note Off, immutable release ownership must retain the old
        // script and deadline while new notes receive the revised script.
        DocumentWorkspace workspace = new();
        ScriptEnvelopeDefinition script =
            EnvelopeDocumentEditor.CreateScriptEnvelope(
                workspace, "C2(o) shared script", "0.25",
                TimeSpan.FromMilliseconds(90));
        FmSynthDefinition child = FmSynthDocumentEditor.CreateFmSynth(
            workspace, "C2(o) Script output");
        int id = FmSynthDocumentEditor.AddEnvelopeNode(
            workspace, child, script.Id, 200, 90);
        FmSynthDocumentEditor.SetOutputNode(workspace, child, id);
        FmSynthDefinition parent = ReferencingParent(workspace, child);

        TrackingFactory actualFactory = new(MonoConfiguration(), script.Id);
        ManualBackend backend = new();
        using SongPlaybackTransport transport = new(backend, actualFactory);
        await transport.SendLiveEventAsync(workspace.Document,
            ChannelTarget.Virtual(96), [new StartNoteCommand(child.Id)]);
        Assert.That(backend.Render(12),
            Is.All.EqualTo(0.25f).Within(3e-6f));
        uint baseAudio = transport.CurrentPlaybackSnapshot.AudioRevision;
        uint audio = workspace.Document.AudioRevision;
        uint document = workspace.Document.DocumentRevision;
        int changed = 0, callbacks = 0;
        List<Task> pending = [];
        List<SharedAudioObjectCommittedEventArgs> notifications = [];
        workspace.SharedAudioObjectCommitted += (_, evt) =>
        {
            notifications.Add(evt);
            pending.Add(transport.PublishSharedObjectEditAsync(
                evt.Document, evt.ObjectId));
        };
        FmSynthEditorControl synthEditor = new(workspace, child,
            () => { }, _ => changed++,
            publishSynthEditAsync: () =>
            {
                callbacks++;
                Task task = transport.PublishSynthEditAsync(
                    workspace.Document, child.Id);
                pending.Add(task);
                return task;
            });
        ScriptEnvelopeEditorControl editor = Inspector(synthEditor).Children
            .OfType<ScriptEnvelopeEditorControl>().Single();
        TextBox source = Private<TextBox>(editor, "_source");
        TextBox release = Private<TextBox>(editor, "_release");
        TextBlock feedback = Private<TextBlock>(editor, "_message");
        Button apply = ((StackPanel)((ScrollViewer)editor.Content!).Content!)
            .Children.OfType<Button>()
            .Single(button => Equals(button.Content, "Apply expression"));
        async Task ApplyAndCheck(string sourceText, string limit,
            string expectedSource, double expectedSeconds, int ordinal)
        {
            source.Text = sourceText;
            release.Text = limit;
            Click(apply);
            await Task.WhenAll(pending.ToArray())
                .WaitAsync(TimeSpan.FromSeconds(8));
            Assert.Multiple(() =>
            {
                Assert.That(script.Source, Is.EqualTo(expectedSource));
                Assert.That(script.ReleaseDuration,
                    Is.EqualTo(TimeSpan.FromSeconds(expectedSeconds)));
                Assert.That(workspace.Document.AudioRevision,
                    Is.EqualTo(audio + ordinal));
                Assert.That(workspace.Document.DocumentRevision,
                    Is.EqualTo(document + ordinal));
                Assert.That(notifications.Count, Is.EqualTo(ordinal));
                Assert.That(notifications.Last().ObjectId, Is.EqualTo(script.Id));
                Assert.That(callbacks, Is.EqualTo(ordinal));
                Assert.That(changed, Is.GreaterThanOrEqualTo(ordinal),
                    "Script editor may report a same-value UI Apply even when "
                    + "the Synth audio-revision gate correctly suppresses publication");
                Assert.That(actualFactory.Preparations.Count, Is.EqualTo(ordinal * 2));
                Assert.That(actualFactory.Preparations.Skip((ordinal - 1) * 2)
                    .Select(p => p.SynthId),
                    Is.EqualTo(new[] { child.Id, parent.Id }));
                Assert.That(actualFactory.Preparations[(ordinal - 1) * 2]
                    .ScriptSource, Is.EqualTo(expectedSource));
                Assert.That(actualFactory.Preparations[(ordinal - 1) * 2]
                    .ScriptReleaseDuration,
                    Is.EqualTo(TimeSpan.FromSeconds(expectedSeconds)));
                Assert.That(actualFactory.Preparations.Skip((ordinal - 1) * 2)
                    .All(p => p.AudioRevision == audio + ordinal), Is.True);
                Assert.That(backend.OpenCount, Is.EqualTo(1));
            });
        }

        source.Text = "this is not a valid expression";
        Click(apply);
        Assert.Multiple(() =>
        {
            Assert.That(feedback.Text, Does.Contain("Cannot apply"));
            Assert.That(workspace.Document.AudioRevision, Is.EqualTo(audio));
            Assert.That(callbacks, Is.Zero);
            Assert.That(notifications, Is.Empty);
            Assert.That(actualFactory.Preparations, Is.Empty);
        });

        await ApplyAndCheck("0.75", "0.09", "0.75", 0.09, 1);
        // Smoothstep on the same physical note, NOT a re-onset.
        float[] curve = backend.Render(101);
        for (int i = 0; i < curve.Length; i++)
        {
            double t = i / 100.0;
            double weight = t * t * (3 - 2 * t);
            Assert.That(curve[i], Is.EqualTo(
                (float)(0.25 + (0.75 - 0.25) * weight)).Within(3e-5f),
                $"actual shared script editor held smoothstep frame {12 + i}");
        }
        Assert.That(actualFactory.OlderAudioCount, Is.Zero);

        // Change only the finite release limit BEFORE Note Off. The held
        // voice retains current gain, but its future release duration
        // changes from 90 to 70 ms without an older-audio warning.
        await ApplyAndCheck("0.75", "0.07", "0.75", 0.07, 2);
        Assert.That(backend.Render(35), Is.All.EqualTo(0.75f).Within(3e-5f));
        Assert.That(actualFactory.OlderAudioCount, Is.Zero);
        // Script Apply reports a UI event even on a semantic no-op; the
        // workspace/audio-revision gate must prevent a transport publish.
        Click(apply);
        await Task.WhenAll(pending.ToArray());
        Assert.Multiple(() =>
        {
            Assert.That(workspace.Document.AudioRevision, Is.EqualTo(audio + 2));
            Assert.That(callbacks, Is.EqualTo(2));
            Assert.That(notifications.Count, Is.EqualTo(2));
            Assert.That(actualFactory.Preparations.Count, Is.EqualTo(4));
            Assert.That(transport.CurrentPlaybackSnapshot.AudioRevision,
                Is.EqualTo(baseAudio));
        });

        // Start the untouched release reference from the just-committed
        // shared definition. Its constant gain and time-relative release
        // make its independent active time safe to align at Note Off.
        TrackingFactory referenceFactory = new(MonoConfiguration(), script.Id);
        ManualBackend referenceBackend = new();
        using SongPlaybackTransport reference = new(
            referenceBackend, referenceFactory);
        await reference.SendLiveEventAsync(workspace.Document,
            ChannelTarget.Virtual(96), [new StartNoteCommand(child.Id)]);
        Assert.That(referenceBackend.Render(5),
            Is.All.EqualTo(0.75f).Within(3e-5f));
        await transport.SendLiveEventAsync(workspace.Document,
            ChannelTarget.Virtual(96), [new NoteOffCommand()]);
        await reference.SendLiveEventAsync(workspace.Document,
            ChannelTarget.Virtual(96), [new NoteOffCommand()]);
        Assert.That(backend.Render(7),
            Is.EqualTo(referenceBackend.Render(7)).Within(3e-5f));

        // Changing BOTH source value and release duration after Off must
        // not shorten or recolor the previous sounding release. It is a
        // deliberately missed generation until the old voice retires.
        await ApplyAndCheck("0.15", "0.02", "0.15", 0.02, 3);
        float[] originalRelease = referenceBackend.Render(21);
        float[] retained = backend.Render(21);
        Assert.That(retained, Is.EqualTo(originalRelease).Within(3e-5f),
            "old 70-ms release must remain physically identical");
        Assert.That(actualFactory.OlderAudioCount, Is.EqualTo(1),
            "refused in-flight Script release remains sticky older");
        Assert.That(actualFactory.Preparations[4].ScriptSource, Is.EqualTo("0.15"));

        await transport.SendLiveEventAsync(workspace.Document,
            ChannelTarget.Virtual(97), [new StartNoteCommand(child.Id)]);
        float[] mixed = backend.Render(4);
        float[] oldOnly = referenceBackend.Render(4);
        Assert.That(mixed.Zip(oldOnly, (a, b) => Math.Abs(a - b)).Max(),
            Is.GreaterThan(0.14),
            "fresh note must use revised 0.15 script alongside old release");
        Assert.That(actualFactory.OlderAudioCount, Is.EqualTo(1));
        backend.Render(90);
        referenceBackend.Render(90);
        Assert.Multiple(() =>
        {
            Assert.That(actualFactory.OlderAudioCount, Is.Zero,
                "old script release naturally retires its stale generation");
            Assert.That(referenceFactory.OlderAudioCount, Is.Zero);
            Assert.That(backend.OpenCount, Is.EqualTo(1));
            Assert.That(transport.CurrentPlaybackSnapshot.AudioRevision,
                Is.EqualTo(baseAudio));
        });
        await transport.PublishSharedObjectEditAsync(
            workspace.Document, script.Id);
        Assert.That(actualFactory.Preparations.Count, Is.EqualTo(6),
            "the same Script document revision must not be prepared again");
    }


    [AvaloniaTest]
    public async Task SharedNodeLoopModeAndSustainBoundaryRetuneHeldTimelineButNotRelease()
    {
        // C2(o): unlike a scalar Envelope level, the real Node editor's
        // Forward/PingPong mode and sustain boundary alter the held note's
        // time mapping. Compare the crossfade against two independently
        // frozen sample-addressable Node curves at the original note clock.
        DocumentWorkspace workspace = new();
        NodeEnvelopeDefinition node = EnvelopeDocumentEditor.CreateNodeEnvelope(
            workspace, "C2(o) loop and sustain", [
                new EnvelopeNode(TimeSpan.Zero, 0.2),
                new EnvelopeNode(TimeSpan.FromMilliseconds(20), 0.8),
                new EnvelopeNode(TimeSpan.FromMilliseconds(60), 0.4),
                new EnvelopeNode(TimeSpan.FromMilliseconds(120), 0.0),
            ], 1, 2, EnvelopeSustainLoopMode.Forward);
        FmSynthDefinition synth = FmSynthDocumentEditor.CreateFmSynth(
            workspace, "C2(o) held Node loop");
        int id = FmSynthDocumentEditor.AddEnvelopeNode(
            workspace, synth, node.Id, 230, 90);
        FmSynthDocumentEditor.SetOutputNode(workspace, synth, id);
        FmSynthDefinition parent = ReferencingParent(workspace, synth);
        TrackingFactory actualFactory = new(MonoConfiguration(), node.Id);
        TrackingFactory referenceFactory = new(MonoConfiguration(), node.Id);
        ManualBackend backend = new(), referenceBackend = new();
        using SongPlaybackTransport transport = new(backend, actualFactory);
        using SongPlaybackTransport reference = new(
            referenceBackend, referenceFactory);
        await transport.SendLiveEventAsync(workspace.Document,
            ChannelTarget.Virtual(98), [new StartNoteCommand(synth.Id)]);
        await reference.SendLiveEventAsync(workspace.Document,
            ChannelTarget.Virtual(98), [new StartNoteCommand(synth.Id)]);

        NodeEnvelopeCurve previous = new(node);
        int frame = 0;
        void Initial(int frames)
        {
            float[] actual = backend.Render(frames), untouched =
                referenceBackend.Render(frames);
            for (int i = 0; i < frames; i++)
            {
                Assert.That(actual[i], Is.EqualTo(untouched[i]).Within(2e-6f));
                Assert.That(actual[i], Is.EqualTo(
                    (float)previous.GetValue(frame + i, Rate, null))
                    .Within(2e-5f), "initial authored Forward curve");
            }
            frame += frames;
        }
        Initial(145);
        uint baseRevision = transport.CurrentPlaybackSnapshot.AudioRevision;
        uint originalAudio = workspace.Document.AudioRevision;
        int directPublishes = 0, changed = 0;
        List<Task> pending = [];
        List<SharedAudioObjectCommittedEventArgs> shared = [];
        workspace.SharedAudioObjectCommitted += (_, evt) =>
        {
            shared.Add(evt);
            pending.Add(transport.PublishSharedObjectEditAsync(
                evt.Document, evt.ObjectId));
        };
        FmSynthEditorControl synthEditor = new(workspace, synth,
            () => { }, _ => changed++,
            publishSynthEditAsync: () =>
            {
                directPublishes++;
                Task task = transport.PublishSynthEditAsync(
                    workspace.Document, synth.Id);
                pending.Add(task);
                return task;
            });
        NodeEnvelopeEditorControl editor = Inspector(synthEditor).Children
            .OfType<NodeEnvelopeEditorControl>().Single();
        ComboBox mode = Private<ComboBox>(editor, "_loopMode");
        TextBox start = Private<TextBox>(editor, "_sustainStart");
        TextBox nodes = Private<TextBox>(editor, "_nodesInput");
        Button apply = ApplyButton(editor);

        async Task Fade(string label, int ordinal,
            EnvelopeSustainLoopMode expectedMode, int expectedStart)
        {
            Assert.That(editor.TryCommitPendingFields(), Is.True,
                "a single independent live field commit replaces Apply");
            await Task.WhenAll(pending.ToArray())
                .WaitAsync(TimeSpan.FromSeconds(8));
            Assert.Multiple(() =>
            {
                Assert.That(workspace.Document.AudioRevision,
                    Is.EqualTo(originalAudio + ordinal));
                Assert.That(shared.Count, Is.EqualTo(ordinal));
                Assert.That(shared.Last().ObjectId, Is.EqualTo(node.Id));
                Assert.That(directPublishes, Is.EqualTo(ordinal));
                Assert.That(changed, Is.EqualTo(ordinal));
                Assert.That(node.SustainLoopMode, Is.EqualTo(expectedMode));
                Assert.That(node.SustainStartIndex, Is.EqualTo(expectedStart));
                Assert.That(actualFactory.Preparations.Count, Is.EqualTo(2 * ordinal));
                Assert.That(actualFactory.Preparations.Skip((ordinal - 1) * 2)
                    .Select(p => p.SynthId),
                    Is.EqualTo(new[] { synth.Id, parent.Id }));
                Assert.That(actualFactory.Preparations[2 * (ordinal - 1)]
                    .NodeLoopMode, Is.EqualTo(expectedMode));
                Assert.That(actualFactory.Preparations[2 * (ordinal - 1)]
                    .NodeSustainStart, Is.EqualTo(expectedStart));
            });

            NodeEnvelopeCurve revised = new(node);
            // Stateless output fades freeze the LAST AUDIBLE PCM sample,
            // not the old Envelope evaluated again at the next clock
            // position. The new curve still advances on the original
            // held-note timeline: no rewind or second envelope invocation.
            double heldOutgoing = previous.GetValue(frame - 1, Rate, null);
            float[] actual = backend.Render(101);
            double maxDifference = 0;
            for (int i = 0; i < actual.Length; i++)
            {
                double t = i / 100.0;
                double w = t * t * (3 - 2 * t);
                double newValue = revised.GetValue(frame + i, Rate, null);
                double expected = heldOutgoing * (1 - w) + newValue * w;
                maxDifference = Math.Max(maxDifference,
                    Math.Abs(heldOutgoing - newValue));
                Assert.That(actual[i], Is.EqualTo((float)expected)
                    .Within(3e-5f),
                    label + ": independent frozen-timeline PCM at " + (frame + i));
            }
            Assert.That(maxDifference, Is.GreaterThan(0.1),
                label + ": loop-timeline edit must be audible");
            // The untouched second transport accepts the same independently
            // prepared current shared revision but only after the actual
            // inspector path was validated. This second producer will act
            // as the *unchanged release* reference after Note Off.
            await reference.PublishSharedObjectEditAsync(
                workspace.Document, node.Id);
            Assert.That(referenceBackend.Render(101),
                Is.EqualTo(actual).Within(3e-5f),
                label + ": independent producer matches authored change");
            Assert.Multiple(() =>
            {
                Assert.That(actualFactory.OlderAudioCount, Is.Zero);
                Assert.That(referenceFactory.OlderAudioCount, Is.Zero);
                Assert.That(backend.OpenCount, Is.EqualTo(1));
                Assert.That(transport.CurrentPlaybackSnapshot.AudioRevision,
                    Is.EqualTo(baseRevision));
            });
            previous = revised;
            frame += 101;
        }

        mode.SelectedItem = EnvelopeSustainLoopMode.PingPong;
        await Fade("Forward to PingPong", 1,
            EnvelopeSustainLoopMode.PingPong, 1);
        start.Text = "0";
        await Fade("Sustain start 1 to 0", 2,
            EnvelopeSustainLoopMode.PingPong, 0);

        // Both producers have the same original-note clock and latest
        // compatible retained curve at this point.
        await transport.SendLiveEventAsync(workspace.Document,
            ChannelTarget.Virtual(98), [new NoteOffCommand()]);
        await reference.SendLiveEventAsync(workspace.Document,
            ChannelTarget.Virtual(98), [new NoteOffCommand()]);
        Assert.That(backend.Render(9),
            Is.EqualTo(referenceBackend.Render(9)).Within(3e-5f));

        // Change the first node's audible value and the final release
        // timestamp after Note Off. The new definition must NOT be spliced
        // into the existing release, which keeps its original clock/end.
        nodes.Text = "0, 0.9\n0.02, 0.8\n0.06, 0.4\n0.20, 0";
        Click(apply);
        await Task.WhenAll(pending.ToArray())
            .WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Multiple(() =>
        {
            Assert.That(workspace.Document.AudioRevision,
                Is.EqualTo(originalAudio + 3));
            Assert.That(shared.Count, Is.EqualTo(3));
            Assert.That(directPublishes, Is.EqualTo(3));
            Assert.That(node.Nodes[0].Value, Is.EqualTo(0.9));
            Assert.That(node.Nodes[^1].Time,
                Is.EqualTo(TimeSpan.FromMilliseconds(200)));
            Assert.That(actualFactory.Preparations.Count, Is.EqualTo(6));
            Assert.That(actualFactory.OlderAudioCount, Is.EqualTo(0),
                "currency is evaluated on the actual PCM boundary");
        });
        float[] originalRelease = referenceBackend.Render(24);
        float[] stillOld = backend.Render(24);
        Assert.That(stillOld, Is.EqualTo(originalRelease).Within(3e-5f),
            "already-releasing original Node timeline and PCM must survive");
        Assert.That(actualFactory.OlderAudioCount, Is.EqualTo(1),
            "release-lifetime change is a deliberate sticky older generation");

        await transport.SendLiveEventAsync(workspace.Document,
            ChannelTarget.Virtual(99), [new StartNoteCommand(synth.Id)]);
        float[] newAndOld = backend.Render(3),
            oldOnly = referenceBackend.Render(3);
        Assert.That(newAndOld.Zip(oldOnly,
            (a, b) => Math.Abs(a - b)).Max(), Is.GreaterThan(0.85),
            "new note must hear revised 0.9 initial Node level");
        Assert.That(actualFactory.OlderAudioCount, Is.EqualTo(1));
        backend.Render(550);
        referenceBackend.Render(550);
        Assert.Multiple(() =>
        {
            Assert.That(actualFactory.OlderAudioCount, Is.Zero);
            Assert.That(referenceFactory.OlderAudioCount, Is.Zero);
            Assert.That(backend.OpenCount, Is.EqualTo(1));
            Assert.That(transport.CurrentPlaybackSnapshot.AudioRevision,
                Is.EqualTo(baseRevision));
        });
        Click(apply);
        await Task.WhenAll(pending.ToArray());
        Assert.That(directPublishes, Is.EqualTo(3),
            "unchanged shared Node draft is a no-op");
        Assert.That(actualFactory.Preparations.Count, Is.EqualTo(6));
    }


    [AvaloniaTest]
    public async Task SharedScriptTimeVaryingExpressionsAndReleaseUseOriginalProductionClock()
    {
        // C2(u): three nonconstant Script commits via the real embedded
        // editor, including elapsedSeconds, releaseSeconds and a held-note
        // release-limit change. An already-running script never restarts its
        // elapsed clock, and a post-Off edit never steals the old release.
        const string initialSource = "0.2 + 0.5 * elapsedSeconds";
        const string firstSource = "0.35 + 0.25 * elapsedSeconds";
        const string secondSource =
            "releaseSeconds < 0 ? 0.7 - 0.4 * elapsedSeconds : 0.85 - 3.0 * releaseSeconds";
        DocumentWorkspace workspace = new();
        ScriptEnvelopeDefinition script =
            EnvelopeDocumentEditor.CreateScriptEnvelope(
                workspace, "C2(u) time-varying Script",
                initialSource, TimeSpan.FromMilliseconds(80));
        FmSynthDefinition synth = FmSynthDocumentEditor.CreateFmSynth(
            workspace, "C2(u) expressive Script output");
        int id = FmSynthDocumentEditor.AddEnvelopeNode(
            workspace, synth, script.Id, 200, 90);
        FmSynthDocumentEditor.SetOutputNode(workspace, synth, id);
        FmSynthDefinition parent = ReferencingParent(workspace, synth);
        TrackingFactory factory = new(MonoConfiguration(), script.Id);
        ManualBackend backend = new();
        using SongPlaybackTransport transport = new(backend, factory);
        await transport.SendLiveEventAsync(workspace.Document,
            ChannelTarget.Virtual(181), [new StartNoteCommand(synth.Id)]);

        int frame = 31;
        float[] first = backend.Render(frame);
        for (int i = 0; i < first.Length; i++)
            Assert.That(first[i],
                Is.EqualTo((float)(0.2 + 0.5 * i / Rate)).Within(3e-5f),
                "initial active clock must be nonconstant");
        uint baseAudio = transport.CurrentPlaybackSnapshot.AudioRevision;
        uint audio = workspace.Document.AudioRevision;
        uint document = workspace.Document.DocumentRevision;
        int changed = 0, callbacks = 0;
        List<Task> pending = [];
        List<SharedAudioObjectCommittedEventArgs> notifications = [];
        workspace.SharedAudioObjectCommitted += (_, evt) =>
        {
            notifications.Add(evt);
            pending.Add(transport.PublishSharedObjectEditAsync(
                evt.Document, evt.ObjectId));
        };
        FmSynthEditorControl synthEditor = new(workspace, synth,
            () => { }, _ => changed++,
            publishSynthEditAsync: () =>
            {
                callbacks++;
                Task task = transport.PublishSynthEditAsync(
                    workspace.Document, synth.Id);
                pending.Add(task);
                return task;
            });
        ScriptEnvelopeEditorControl editor = Inspector(synthEditor).Children
            .OfType<ScriptEnvelopeEditorControl>().Single();
        TextBox source = Private<TextBox>(editor, "_source");
        TextBox release = Private<TextBox>(editor, "_release");
        TextBlock feedback = Private<TextBlock>(editor, "_message");
        Button apply = ((StackPanel)((ScrollViewer)editor.Content!).Content!)
            .Children.OfType<Button>()
            .Single(button => Equals(button.Content, "Apply expression"));

        // A malformed variable name must not disturb the running state or
        // advance either semantic revision.
        source.Text = "unknownVariable + elapsedSeconds";
        Click(apply);
        Assert.Multiple(() =>
        {
            Assert.That(feedback.Text, Does.Contain("Cannot apply"));
            Assert.That(workspace.Document.AudioRevision, Is.EqualTo(audio));
            Assert.That(notifications, Is.Empty);
            Assert.That(callbacks, Is.Zero);
        });

        Func<double, double> oldHeld = seconds => 0.2 + 0.5 * seconds;
        Func<double, double> firstHeld = seconds => 0.35 + 0.25 * seconds;
        Func<double, double> secondHeld = seconds => 0.7 - 0.4 * seconds;
        async Task Change(string authored, string releaseText, int ordinal,
            Func<double, double> revisedHeld, bool mustDiffer)
        {
            source.Text = authored;
            release.Text = releaseText;
            Click(apply);
            await Task.WhenAll(pending.ToArray())
                .WaitAsync(TimeSpan.FromSeconds(8));
            Assert.Multiple(() =>
            {
                Assert.That(script.Source, Is.EqualTo(authored));
                Assert.That(script.ReleaseDuration, Is.EqualTo(
                    TimeSpan.FromSeconds(double.Parse(
                        releaseText, System.Globalization.CultureInfo.InvariantCulture))));
                Assert.That(workspace.Document.AudioRevision, Is.EqualTo(audio + ordinal));
                Assert.That(workspace.Document.DocumentRevision,
                    Is.EqualTo(document + ordinal));
                Assert.That(notifications.Count, Is.EqualTo(ordinal));
                Assert.That(notifications[^1].ObjectId, Is.EqualTo(script.Id));
                Assert.That(callbacks, Is.EqualTo(ordinal));
                Assert.That(changed, Is.GreaterThanOrEqualTo(ordinal));
                Assert.That(factory.Preparations.Count, Is.EqualTo(ordinal * 2));
                Assert.That(factory.Preparations.Skip((ordinal - 1) * 2)
                    .Select(p => p.SynthId),
                    Is.EqualTo(new[] { synth.Id, parent.Id }));
                Assert.That(factory.Preparations.Skip((ordinal - 1) * 2)
                    .All(p => p.AudioRevision == audio + ordinal), Is.True);
                Assert.That(factory.Preparations[(ordinal - 1) * 2]
                    .ScriptSource, Is.EqualTo(authored));
                Assert.That(factory.Preparations[(ordinal - 1) * 2]
                    .ScriptReleaseDuration, Is.EqualTo(script.ReleaseDuration));
                Assert.That(backend.OpenCount, Is.EqualTo(1));
            });

            double lastAudible = oldHeld((frame - 1) / (double)Rate);
            float[] actual = backend.Render(101);
            double maxChange = 0;
            for (int i = 0; i < actual.Length; i++)
            {
                double t = i / 100.0;
                double smooth = t * t * (3 - 2 * t);
                double revised = revisedHeld((frame + i) / (double)Rate);
                double expected = lastAudible * (1 - smooth) + revised * smooth;
                maxChange = Math.Max(maxChange, Math.Abs(revised - lastAudible));
                Assert.That(actual[i], Is.EqualTo((float)expected)
                    .Within(4e-5f),
                    $"Script {ordinal}: original elapsed clock at {frame + i}");
            }
            if (mustDiffer)
                Assert.That(maxChange, Is.GreaterThan(0.07),
                    "expression edit must have a meaningful PCM consequence");
            Assert.Multiple(() =>
            {
                Assert.That(factory.OlderAudioCount, Is.Zero);
                Assert.That(transport.CurrentPlaybackSnapshot.AudioRevision,
                    Is.EqualTo(baseAudio));
            });
            oldHeld = revisedHeld;
            frame += 101;
        }

        await Change(firstSource, "0.08", 1, firstHeld, true);
        await Change(secondSource, "0.08", 2, secondHeld, true);
        // Release-duration-only edit is a real shared semantic change.
        // Any envelope-output transition still uses its existing note clock.
        await Change(secondSource, "0.12", 3, secondHeld, false);
        Click(apply);
        await Task.WhenAll(pending.ToArray());
        await transport.PublishSharedObjectEditAsync(
            workspace.Document, script.Id);
        Assert.Multiple(() =>
        {
            Assert.That(workspace.Document.AudioRevision, Is.EqualTo(audio + 3));
            Assert.That(notifications.Count, Is.EqualTo(3));
            Assert.That(callbacks, Is.EqualTo(3));
            Assert.That(factory.Preparations.Count, Is.EqualTo(6));
        });

        await transport.SendLiveEventAsync(workspace.Document,
            ChannelTarget.Virtual(181), [new NoteOffCommand()]);
        int releaseFrame = 0;
        void OriginalRelease(int count, string label)
        {
            float[] audible = backend.Render(count);
            for (int i = 0; i < audible.Length; i++)
            {
                double releasedSeconds = (releaseFrame + i) / (double)Rate;
                float expected = releasedSeconds >= 0.12
                    ? 0.0f : (float)(0.85 - 3.0 * releasedSeconds);
                Assert.That(audible[i], Is.EqualTo(expected).Within(4e-5f),
                    label + ": original active release clock " + (releaseFrame + i));
            }
            releaseFrame += count;
        }
        OriginalRelease(23, "releaseSeconds expression");

        // Different release expression AND deadline after Off: the already
        // releasing original must keep its exact previous source and 120ms
        // exclusive retirement bound, while new notes see the revised one.
        source.Text = "releaseSeconds < 0 ? 0.1 + 0.2 * elapsedSeconds : 0.25 - releaseSeconds";
        release.Text = "0.03";
        Click(apply);
        await Task.WhenAll(pending.ToArray())
            .WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Multiple(() =>
        {
            Assert.That(workspace.Document.AudioRevision, Is.EqualTo(audio + 4));
            Assert.That(callbacks, Is.EqualTo(4));
            Assert.That(factory.Preparations.Count, Is.EqualTo(8));
            Assert.That(factory.Preparations[^2].ScriptSource, Is.EqualTo(
                source.Text));
        });
        OriginalRelease(17, "refused post-Off Script replacement");
        Assert.That(factory.OlderAudioCount, Is.EqualTo(1));
        // A new note must use the new expression while the existing old
        // release remains older and completes on its original clock.
        await transport.SendLiveEventAsync(workspace.Document,
            ChannelTarget.Virtual(182), [new StartNoteCommand(synth.Id)]);
        float[] mixed = backend.Render(4);
        for (int i = 0; i < 4; i++)
        {
            double prior = 0.85 - 3.0 * (releaseFrame + i) / Rate;
            double fresh = 0.1 + 0.2 * i / Rate;
            Assert.That(mixed[i], Is.EqualTo((float)(prior + fresh))
                .Within(5e-5f),
                "new Script starts at age zero beside the untouched old release");
        }
        releaseFrame += 4;
        backend.Render(140);
        Assert.Multiple(() =>
        {
            Assert.That(factory.OlderAudioCount, Is.Zero,
                "old release naturally retires after 120ms, not 30ms");
            Assert.That(backend.OpenCount, Is.EqualTo(1));
            Assert.That(transport.CurrentPlaybackSnapshot.AudioRevision,
                Is.EqualTo(baseAudio));
        });
    }

    [AvaloniaTest]
    public async Task ScriptReleaseLimitNullAndFiniteLiveEditsRespectDefiniteOffAndFrozenRelease()
    {
        // C2(ad): the actual embedded Script editor supports a blank
        // Release limit. Null is NOT an implicit sustain/release lifecycle,
        // even if the expression refers to releaseSeconds. A held voice
        // can adopt either direction without restarting its active clock.
        // Once an actual finite release begins, a new null definition must
        // not truncate the original release or lie about older-audio status.
        const string expression =
            "releaseSeconds < 0 ? 0.6 : 0.6 * Math.Max(0, 1 - releaseSeconds / 0.09)";
        foreach (bool initiallyFinite in new[] { true, false })
        {
            DocumentWorkspace workspace = new();
            ScriptEnvelopeDefinition script =
                EnvelopeDocumentEditor.CreateScriptEnvelope(
                    workspace, "C2(ad) nullable release " + initiallyFinite,
                    expression,
                    initiallyFinite ? TimeSpan.FromMilliseconds(90) : null);
            FmSynthDefinition child = FmSynthDocumentEditor.CreateFmSynth(
                workspace, "C2(ad) script output " + initiallyFinite);
            int envelopeId = FmSynthDocumentEditor.AddEnvelopeNode(
                workspace, child, script.Id, 200, 90);
            FmSynthDocumentEditor.SetOutputNode(workspace, child, envelopeId);
            FmSynthDefinition parent = ReferencingParent(workspace, child);
            TrackingFactory factory = new(MonoConfiguration(), script.Id);
            ManualBackend backend = new();
            using SongPlaybackTransport transport = new(backend, factory);
            uint channel = initiallyFinite ? 185u : 186u;
            await transport.SendLiveEventAsync(workspace.Document,
                ChannelTarget.Virtual(channel),
                [new StartNoteCommand(child.Id)]);
            Assert.That(backend.Render(27),
                Is.All.EqualTo(0.6f).Within(4e-5f));
            uint baseSnapshot = transport.CurrentPlaybackSnapshot.AudioRevision;
            uint audio = workspace.Document.AudioRevision;
            uint document = workspace.Document.DocumentRevision;
            int callbacks = 0, changed = 0;
            List<Task> pending = [];
            List<SharedAudioObjectCommittedEventArgs> notifications = [];
            workspace.SharedAudioObjectCommitted += (_, evt) =>
            {
                notifications.Add(evt);
                pending.Add(transport.PublishSharedObjectEditAsync(
                    evt.Document, evt.ObjectId));
            };
            FmSynthEditorControl synthEditor = new(workspace, child,
                () => { }, _ => changed++,
                publishSynthEditAsync: () =>
                {
                    callbacks++;
                    Task task = transport.PublishSynthEditAsync(
                        workspace.Document, child.Id);
                    pending.Add(task);
                    return task;
                });
            ScriptEnvelopeEditorControl editor = Inspector(synthEditor)
                .Children.OfType<ScriptEnvelopeEditorControl>().Single();
            TextBox source = Private<TextBox>(editor, "_source");
            TextBox release = Private<TextBox>(editor, "_release");
            Button apply = ((StackPanel)
                ((ScrollViewer)editor.Content!).Content!)
                .Children.OfType<Button>()
                .Single(button => Equals(button.Content, "Apply expression"));

            // First edit uses ONLY the real release field: no fake
            // source replacement or programmatic document mutation.
            Assert.That(source.Text, Is.EqualTo(expression));
            release.Text = initiallyFinite ? "" : "0.09";
            Click(apply);
            await Task.WhenAll(pending.ToArray())
                .WaitAsync(TimeSpan.FromSeconds(8));
            TimeSpan? revisedLimit = initiallyFinite
                ? null : TimeSpan.FromMilliseconds(90);
            Assert.Multiple(() =>
            {
                Assert.That(script.ReleaseDuration, Is.EqualTo(revisedLimit));
                Assert.That(script.Source, Is.EqualTo(expression));
                Assert.That(workspace.Document.AudioRevision,
                    Is.EqualTo(audio + 1));
                Assert.That(workspace.Document.DocumentRevision,
                    Is.EqualTo(document + 1));
                Assert.That(callbacks, Is.EqualTo(1));
                Assert.That(changed, Is.GreaterThanOrEqualTo(1));
                Assert.That(notifications.Count, Is.EqualTo(1));
                Assert.That(notifications[0].ObjectId, Is.EqualTo(script.Id));
                Assert.That(factory.Preparations.Count, Is.EqualTo(2));
                Assert.That(factory.Preparations.Select(p => p.SynthId),
                    Is.EqualTo(new[] { child.Id, parent.Id }));
                Assert.That(factory.Preparations[0].ScriptReleaseDuration,
                    Is.EqualTo(revisedLimit));
                Assert.That(factory.Preparations[0].ScriptSource,
                    Is.EqualTo(expression));
                Assert.That(factory.Preparations.All(p =>
                    p.AudioRevision == audio + 1), Is.True);
                Assert.That(factory.OlderAudioCount, Is.Zero);
                Assert.That(backend.OpenCount, Is.EqualTo(1));
            });
            Assert.That(backend.Render(101),
                Is.All.EqualTo(0.6f).Within(4e-5f),
                "held original-note clock and unchanged output during "
                    + "the release-only compatible revision");
            Assert.That(factory.OlderAudioCount, Is.Zero);

            // A semantic no-op cannot cause a second frozen publication.
            Click(apply);
            await Task.WhenAll(pending.ToArray());
            Assert.Multiple(() =>
            {
                Assert.That(workspace.Document.AudioRevision,
                    Is.EqualTo(audio + 1));
                Assert.That(callbacks, Is.EqualTo(1));
                Assert.That(factory.Preparations.Count, Is.EqualTo(2));
                Assert.That(transport.CurrentPlaybackSnapshot.AudioRevision,
                    Is.EqualTo(baseSnapshot));
            });
            await transport.SendLiveEventAsync(workspace.Document,
                ChannelTarget.Virtual(channel), [new NoteOffCommand()]);
            if (initiallyFinite)
            {
                // No declared limit => no implicit Script sustain. Definite
                // Note Off ends this direct-output Synth; the authored
                // releaseSeconds expression cannot invent a lifecycle.
                float[] afterOff = backend.Render(80);
                Assert.That(afterOff.Skip(1),
                    Is.All.EqualTo(0f).Within(1e-6f),
                    "null Script release cannot claim an ongoing tail");
                Assert.That(factory.OlderAudioCount, Is.Zero);
            }
            else
            {
                // Null -> finite before Note Off MUST introduce a real
                // 90-frame release; a post-Off deletion of the bound then
                // cannot rewrite that existing release's original timeline.
                float[] firstRelease = backend.Render(20);
                for (int i = 0; i < firstRelease.Length; i++)
                    Assert.That(firstRelease[i],
                        Is.EqualTo((float)(0.6 * (1 - i / 90.0)))
                            .Within(4e-5f),
                        "adopted finite release frame " + i);
                release.Text = "";
                Click(apply);
                await Task.WhenAll(pending.ToArray())
                    .WaitAsync(TimeSpan.FromSeconds(8));
                Assert.Multiple(() =>
                {
                    Assert.That(script.ReleaseDuration, Is.Null);
                    Assert.That(workspace.Document.AudioRevision,
                        Is.EqualTo(audio + 2));
                    Assert.That(callbacks, Is.EqualTo(2));
                    Assert.That(notifications.Count, Is.EqualTo(2));
                    Assert.That(factory.Preparations.Count, Is.EqualTo(4));
                    Assert.That(factory.Preparations[^2].ScriptReleaseDuration,
                        Is.Null);
                });
                // Observe actual PCM before assessing voice currency: this
                // distinguishes loss of original release from status drift.
                float[] frozen = backend.Render(20);
                for (int i = 0; i < frozen.Length; i++)
                    Assert.That(frozen[i],
                        Is.EqualTo((float)(0.6 * (1 - (20 + i) / 90.0)))
                            .Within(4e-5f),
                        "old release duration survives incompatible edit " + i);
                Assert.That(factory.OlderAudioCount, Is.EqualTo(1));
                // Fresh notes use null immediately even while the old
                // finite-generation release is still sounding.
                await transport.SendLiveEventAsync(workspace.Document,
                    ChannelTarget.Virtual(187),
                    [new StartNoteCommand(child.Id)]);
                float[] mixed = backend.Render(4);
                for (int i = 0; i < mixed.Length; i++)
                {
                    double old = 0.6 * (1 - (40 + i) / 90.0);
                    Assert.That(mixed[i],
                        Is.EqualTo((float)(old + 0.6)).Within(5e-5f),
                        "fresh unbounded Script coexists with old finite release");
                }
                await transport.SendLiveEventAsync(workspace.Document,
                    ChannelTarget.Virtual(187), [new NoteOffCommand()]);
                backend.Render(120);
                Assert.That(factory.OlderAudioCount, Is.Zero,
                    "original 90-ms release retires without a stale voice");
            }
            Assert.Multiple(() =>
            {
                Assert.That(backend.OpenCount, Is.EqualTo(1));
                Assert.That(transport.CurrentPlaybackSnapshot.AudioRevision,
                    Is.EqualTo(baseSnapshot));
            });
        }
    }

    [AvaloniaTest]
    public async Task SharedMultipointNodeShapeAndSinglePointSustainUseHeldClockAndPreserveRelease()
    {
        // C2(u): larger Node family batch: edit FIVE nodes' values,
        // multiple node times and sustain end, then collapse PingPong to
        // a single-node Forward sustain. Both shape and direction must
        // follow the original running note timeline, not restart at zero.
        DocumentWorkspace workspace = new();
        NodeEnvelopeDefinition node = EnvelopeDocumentEditor.CreateNodeEnvelope(
            workspace, "C2(u) multipoint Node", [
                new EnvelopeNode(TimeSpan.Zero, 0.1),
                new EnvelopeNode(TimeSpan.FromMilliseconds(20), 0.9),
                new EnvelopeNode(TimeSpan.FromMilliseconds(60), 0.3),
                new EnvelopeNode(TimeSpan.FromMilliseconds(100), 0.7),
                new EnvelopeNode(TimeSpan.FromMilliseconds(180), 0),
            ], 1, 3, EnvelopeSustainLoopMode.PingPong);
        FmSynthDefinition synth = FmSynthDocumentEditor.CreateFmSynth(
            workspace, "C2(u) changing Node output");
        int id = FmSynthDocumentEditor.AddEnvelopeNode(
            workspace, synth, node.Id, 210, 80);
        FmSynthDocumentEditor.SetOutputNode(workspace, synth, id);
        FmSynthDefinition parent = ReferencingParent(workspace, synth);
        TrackingFactory factory = new(MonoConfiguration(), node.Id);
        ManualBackend backend = new();
        using SongPlaybackTransport transport = new(backend, factory);
        await transport.SendLiveEventAsync(workspace.Document,
            ChannelTarget.Virtual(183), [new StartNoteCommand(synth.Id)]);
        NodeEnvelopeCurve previous = new(node);
        int frame = 152;
        float[] initial = backend.Render(frame);
        for (int i = 0; i < frame; i++)
            Assert.That(initial[i], Is.EqualTo(
                (float)previous.GetValue(i, Rate, null)).Within(3e-5f),
                "initial multipoint PingPong held PCM");
        uint baseAudio = transport.CurrentPlaybackSnapshot.AudioRevision;
        uint audio = workspace.Document.AudioRevision;
        uint document = workspace.Document.DocumentRevision;
        int callbacks = 0, changed = 0;
        List<Task> pending = [];
        List<SharedAudioObjectCommittedEventArgs> notifications = [];
        workspace.SharedAudioObjectCommitted += (_, evt) =>
        {
            notifications.Add(evt);
            pending.Add(transport.PublishSharedObjectEditAsync(
                evt.Document, evt.ObjectId));
        };
        FmSynthEditorControl synthEditor = new(workspace, synth,
            () => { }, _ => changed++,
            publishSynthEditAsync: () =>
            {
                callbacks++;
                Task task = transport.PublishSynthEditAsync(
                    workspace.Document, synth.Id);
                pending.Add(task);
                return task;
            });
        NodeEnvelopeEditorControl editor = Inspector(synthEditor).Children
            .OfType<NodeEnvelopeEditorControl>().Single();
        TextBox nodes = Private<TextBox>(editor, "_nodesInput");
        TextBox start = Private<TextBox>(editor, "_sustainStart");
        TextBox end = Private<TextBox>(editor, "_sustainEnd");
        ComboBox mode = Private<ComboBox>(editor, "_loopMode");
        Button apply = ApplyButton(editor);

        int committedEdits = 0;
        async Task Change(string label, string text, int nextStart,
            int nextEnd, EnvelopeSustainLoopMode nextMode, int ordinal)
        {
            // The new inspector accepts raw nodes separately from
            // individual focus-loss parameters. All published intermediate
            // states must still be prepared; after the last edit the
            // audible PCM follows the latest accepted shared definition.
            uint priorRevision = workspace.Document.AudioRevision;
            nodes.Text = text;
            start.Text = nextStart.ToString();
            end.Text = nextEnd.ToString();
            mode.SelectedItem = nextMode;
            Click(apply); // Set nodes only.
            Assert.That(editor.TryCommitPendingFields(), Is.True);
            committedEdits += (int)(workspace.Document.AudioRevision - priorRevision);
            await Task.WhenAll(pending.ToArray())
                .WaitAsync(TimeSpan.FromSeconds(8));
            Assert.Multiple(() =>
            {
                Assert.That(node.SustainStartIndex, Is.EqualTo(nextStart), label);
                Assert.That(node.SustainEndIndex, Is.EqualTo(nextEnd), label);
                Assert.That(node.SustainLoopMode, Is.EqualTo(nextMode), label);
                Assert.That(node.Nodes.Count, Is.EqualTo(5), label);
                Assert.That(workspace.Document.AudioRevision, Is.EqualTo(audio + committedEdits));
                Assert.That(workspace.Document.DocumentRevision,
                    Is.EqualTo(document + committedEdits));
                Assert.That(notifications.Count, Is.EqualTo(committedEdits));
                Assert.That(notifications[^1].ObjectId, Is.EqualTo(node.Id));
                Assert.That(callbacks, Is.EqualTo(committedEdits));
                Assert.That(changed, Is.EqualTo(committedEdits));
                Assert.That(factory.Preparations.Count, Is.EqualTo(committedEdits * 2));
                Assert.That(factory.Preparations.Skip((committedEdits - 1) * 2)
                    .Select(p => p.SynthId),
                    Is.EqualTo(new[] { synth.Id, parent.Id }));
                Assert.That(factory.Preparations[(committedEdits - 1) * 2]
                    .NodeLoopMode, Is.EqualTo(nextMode));
                Assert.That(factory.Preparations[(committedEdits - 1) * 2]
                    .NodeSustainStart, Is.EqualTo(nextStart));
                Assert.That(backend.OpenCount, Is.EqualTo(1));
            });
            NodeEnvelopeCurve revised = new(node);
            double lastSample = previous.GetValue(frame - 1, Rate, null);
            double maxChange = 0;
            float[] audible = backend.Render(101);
            for (int i = 0; i < audible.Length; i++)
            {
                double t = i / 100.0, weight = t * t * (3 - 2 * t);
                double target = revised.GetValue(frame + i, Rate, null);
                double expected = lastSample * (1 - weight) + target * weight;
                maxChange = Math.Max(maxChange, Math.Abs(target - lastSample));
                Assert.That(audible[i], Is.EqualTo((float)expected).Within(4e-5f),
                    label + ": frozen old sample and new original clock " + (frame + i));
            }
            Assert.That(maxChange, Is.GreaterThan(0.09),
                label + ": multipoint edit cannot be numerically vacuous");
            Assert.Multiple(() =>
            {
                Assert.That(factory.OlderAudioCount, Is.Zero);
                Assert.That(transport.CurrentPlaybackSnapshot.AudioRevision,
                    Is.EqualTo(baseAudio));
            });
            previous = revised;
            frame += 101;
        }

        await Change("five-node value edits",
            "0, 0.3\n0.02, 0.7\n0.06, 0.1\n0.10, 0.9\n0.18, 0",
            1, 3, EnvelopeSustainLoopMode.PingPong, 1);
        await Change("four changed times and end boundary",
            "0, 0.3\n0.015, 0.7\n0.055, 0.1\n0.12, 0.9\n0.20, 0",
            1, 2, EnvelopeSustainLoopMode.PingPong, 2);
        await Change("single-node Forward sustain",
            "0, 0.3\n0.015, 0.7\n0.055, 0.6\n0.12, 0.9\n0.20, 0",
            2, 2, EnvelopeSustainLoopMode.Forward, 3);

        Click(apply);
        await Task.WhenAll(pending.ToArray());
        Assert.Multiple(() =>
        {
            Assert.That(workspace.Document.AudioRevision,
                Is.EqualTo(audio + committedEdits));
            Assert.That(callbacks, Is.EqualTo(committedEdits));
            Assert.That(notifications.Count, Is.EqualTo(committedEdits));
            Assert.That(factory.Preparations.Count, Is.EqualTo(committedEdits * 2));
        });
        await transport.SendLiveEventAsync(workspace.Document,
            ChannelTarget.Virtual(183), [new NoteOffCommand()]);
        int off = frame, released = 0;
        float[] firstRelease = backend.Render(17);
        for (int i = 0; i < firstRelease.Length; i++)
            Assert.That(firstRelease[i], Is.EqualTo(
                (float)previous.GetValue(off + i, Rate, off)).Within(4e-5f),
                "single-node sustain resumes exact original forward release");
        released += 17;

        // A late multi-node time/shape edit cannot truncate or recolor the
        // old releasing generation even when it changes a release deadline.
        nodes.Text = "0, 0.95\n0.015, 0.7\n0.055, 0.6\n0.12, 0.9\n0.28, 0";
        Click(apply);
        await Task.WhenAll(pending.ToArray())
            .WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Multiple(() =>
        {
            Assert.That(workspace.Document.AudioRevision,
                Is.EqualTo(audio + committedEdits + 1));
            Assert.That(callbacks, Is.EqualTo(committedEdits + 1));
            Assert.That(notifications.Count, Is.EqualTo(committedEdits + 1));
            Assert.That(factory.Preparations.Count, Is.EqualTo((committedEdits + 1) * 2));
            Assert.That(node.Nodes[0].Value, Is.EqualTo(0.95));
            Assert.That(node.Nodes[^1].Time,
                Is.EqualTo(TimeSpan.FromMilliseconds(280)));
        });
        float[] retained = backend.Render(21);
        for (int i = 0; i < retained.Length; i++)
            Assert.That(retained[i], Is.EqualTo((float)previous.GetValue(
                off + released + i, Rate, off)).Within(4e-5f),
                "releasing Node must preserve old finite trajectory");
        released += retained.Length;
        Assert.That(factory.OlderAudioCount, Is.EqualTo(1));

        await transport.SendLiveEventAsync(workspace.Document,
            ChannelTarget.Virtual(184), [new StartNoteCommand(synth.Id)]);
        float[] mixed = backend.Render(4);
        for (int i = 0; i < mixed.Length; i++)
        {
            double oldValue = previous.GetValue(off + released + i, Rate, off);
            // The revised fresh Envelope starts from its authored t=0.
            double freshValue = new NodeEnvelopeCurve(node).GetValue(i, Rate, null);
            Assert.That(mixed[i], Is.EqualTo((float)(oldValue + freshValue))
                .Within(5e-5f), "new Node shape coexists with old released voice");
        }
        backend.Render(220);
        Assert.Multiple(() =>
        {
            Assert.That(factory.OlderAudioCount, Is.Zero,
                "old timeline is allowed to finish at original exclusive end");
            Assert.That(backend.OpenCount, Is.EqualTo(1));
            Assert.That(transport.CurrentPlaybackSnapshot.AudioRevision,
                Is.EqualTo(baseAudio));
        });
    }

    private static ComboBox EnvelopeChoiceBox(FmSynthEditorControl editor)
    {
        StackPanel panel = Inspector(editor);
        int labelIndex = panel.Children.Select((item, i) => (item, i))
            .Single(pair => pair.item is TextBlock text
                && text.Text == "Envelope").i;
        return (ComboBox)panel.Children[labelIndex + 1];
    }

    private static FmSynthDefinition ReferencingParent(
        DocumentWorkspace workspace, FmSynthDefinition child)
    {
        FmSynthDefinition parent = FmSynthDocumentEditor.CreateFmSynth(
            workspace, "Dependent Synth parent");
        FmSynthDocumentEditor.UpdateNode(workspace, parent,
            new FmSoundSourceNode(0, child.Id,
                FmMultiNode.NeutralFrequencyHz));
        return parent;
    }

    private static RenderConfiguration MonoConfiguration()
        => new(Rate, [
            new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0),
        ]);

    private static StackPanel Inspector(FmSynthEditorControl editor)
        => Private<StackPanel>(editor, "_inspector");

    private static T Private<T>(object instance, string name)
        => (T)instance.GetType().GetField(name,
            BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;

    private static Button ApplyButton(NodeEnvelopeEditorControl editor)
    {
        StackPanel body = (StackPanel)editor.Content!;
        return body.Children.OfType<Button>()
            .Single(button => Equals(button.Content, "Set nodes"));
    }

    private static void Click(Button button)
        => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static byte[] FourFrameWave(short value)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8);
        writer.Write(44);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write(Rate);
        writer.Write(Rate * 2);
        writer.Write((ushort)2);
        writer.Write((ushort)16);
        writer.Write("data"u8);
        writer.Write(8);
        for (int i = 0; i < 4; i++)
            writer.Write(value);
        writer.Flush();
        return stream.ToArray();
    }

    private sealed record Prepared(ObjectId SynthId, uint AudioRevision,
        double EnvelopeLevel, ObjectId? SelectedEnvelopeId = null,
        string? ScriptSource = null, TimeSpan? ScriptReleaseDuration = null,
        EnvelopeSustainLoopMode? NodeLoopMode = null,
        int? NodeSustainStart = null);

    private sealed class TrackingFactory(RenderConfiguration configuration,
        ObjectId envelopeId)
        : IBackgroundPlaybackSourceFactory, ILiveSynthSourcePreparationFactory
    {
        private readonly PlaybackRequestAudioSourceFactory _real =
            new(configuration);
        public List<Prepared> Preparations { get; } = [];
        private PlaybackRequest? _current;
        public int OlderAudioCount
        {
            get
            {
                Assert.That(_current, Is.Not.Null);
                Assert.That(_real.TryGetOlderAudioVoiceCount(
                    _current!, out int count), Is.True);
                return count;
            }
        }
        public IAudioOutputSource Create(PlaybackRequest request)
        {
            _current = request;
            return _real.Create(request);
        }
        public ISound PrepareLiveSynthSource(
            SongDocumentSnapshot snapshot, ObjectId sourceId)
        {
            EnvelopeDefinition frozen = (EnvelopeDefinition)
                snapshot.Document.Objects[envelopeId];
            NodeEnvelopeDefinition? frozenNode = frozen as NodeEnvelopeDefinition;
            ScriptEnvelopeDefinition? frozenScript = frozen as ScriptEnvelopeDefinition;
            ObjectId? selected = snapshot.Document.Objects[sourceId]
                is FmSynthDefinition synth
                ? synth.Graph.Nodes.OfType<FmEnvelopeNode>()
                    .SingleOrDefault()?.EnvelopeId : null;
            Preparations.Add(new Prepared(sourceId,
                snapshot.AudioRevision, frozenNode?.Nodes[0].Value ?? double.NaN,
                selected, frozenScript?.Source, frozenScript?.ReleaseDuration,
                frozenNode?.SustainLoopMode, frozenNode?.SustainStartIndex));
            return _real.PrepareLiveSynthSource(snapshot, sourceId);
        }
    }

    private sealed class ManualBackend : IAudioOutputBackend
    {
        private IAudioOutputSource? _source;
        public int OpenCount { get; private set; }
        public IAudioOutputSession Open(AudioOutputFormat format,
            IAudioOutputSource source)
        {
            _source = source;
            OpenCount++;
            return new ManualSession(format);
        }
        public float[] Render(int frames)
        {
            Assert.That(_source, Is.Not.Null);
            float[] pcm = new float[frames * _source!.Format.ChannelCount];
            _source.Render(frames, pcm);
            return pcm;
        }
        public void Dispose() { }
    }

    private sealed class ManualSession(AudioOutputFormat format)
        : IAudioOutputSession
    {
        public AudioOutputFormat Format { get; } = format;
        public bool IsRunning { get; private set; }
        public Exception? Fault => null;
        public void Start() => IsRunning = true;
        public void Stop() => IsRunning = false;
        public void Dispose() { }
    }
}
