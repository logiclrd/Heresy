using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;

using Heresy.Core.Envelopes;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Samples;
using Heresy.Core.Sequencing;
using Heresy.Playback;
using Heresy.Render.Configuration;
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
        Click(apply);
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

        // A shared Node curve is evaluated at the retained envelope clock.
        // Unlike replacement of the Envelope SourceId, numeric edits of
        // its shared node values take effect at the next producer boundary.
        float[] edited = backend.Render(120);
        Assert.That(edited, Is.All.EqualTo(0.75f).Within(2e-5f),
            "the original held note must play the revised Node envelope");
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
        return body.Children.OfType<StackPanel>()
            .SelectMany(panel => panel.Children.OfType<Button>())
            .Single(button => Equals(button.Content, "Apply envelope"));
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
        double EnvelopeLevel);

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
            NodeEnvelopeDefinition frozen = (NodeEnvelopeDefinition)
                snapshot.Document.Objects[envelopeId];
            Preparations.Add(new Prepared(sourceId,
                snapshot.AudioRevision, frozen.Nodes[0].Value));
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
