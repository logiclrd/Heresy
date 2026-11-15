using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;

using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
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
/// C2 residual T: the real inspector calls the production transport's
/// detached-snapshot compiler and FIFO live publication. The backend does
/// not play sound hardware; the *real* production audio source is driven
/// manually to verify same-session held PCM at the publication boundary.
/// </summary>
[TestFixture]
public sealed class FmSynthInspectorTransportPcmTests
{
    private const int Rate = 1000;

    [AvaloniaTest]
    public async Task ActualInspectorEditPublishesThroughTransportWorkerAndRetunesHeldProductionPcm()
    {
        DocumentWorkspace workspace = new();
        FmSynthDefinition synth = FmSynthDocumentEditor.CreateFmSynth(
            workspace, "C2 residual direct-inspector worker");
        int low = FmSynthDocumentEditor.AddConstantNode(
            workspace, synth, 0.2, 70, 80);
        int high = FmSynthDocumentEditor.AddConstantNode(
            workspace, synth, 0.8, 70, 210);
        int opId = FmSynthDocumentEditor.AddOperatorNode(
            workspace, synth, FmOperatorKind.Minimum,
            [low, high], 320, 130);
        FmSynthDocumentEditor.SetOutputNode(workspace, synth, opId);

        // A nested parent is not started, but is still recompiled after
        // the changed child to prove the production dependency ordering.
        FmSynthDefinition parent = FmSynthDocumentEditor.CreateFmSynth(
            workspace, "C2 parent dependency");
        FmSynthDocumentEditor.UpdateNode(workspace, parent,
            new FmSoundSourceNode(0, synth.Id,
                FmMultiNode.NeutralFrequencyHz));

        RenderConfiguration configuration = new(Rate, [
            new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0),
        ]);
        TrackingFactory factory = new(configuration, synth.Id);
        ManualBackend backend = new();
        using SongPlaybackTransport transport = new(backend, factory);

        await transport.SendLiveEventAsync(workspace.Document,
            ChannelTarget.Virtual(44), [new StartNoteCommand(synth.Id)]);
        Assert.Multiple(() =>
        {
            Assert.That(backend.OpenCount, Is.EqualTo(1));
            Assert.That(backend.Source, Is.Not.Null);
            Assert.That(factory.Preparations, Is.Empty);
            Assert.That(backend.Source!.Format.ChannelCount, Is.EqualTo(1));
        });

        float[] initial = backend.Render(13);
        Assert.That(initial, Is.All.EqualTo(0.2f).Within(2e-6f));
        PlaybackSnapshotInfo startingSnapshot = transport.CurrentPlaybackSnapshot;
        Assert.That(startingSnapshot.IsActive, Is.True);

        int changed = 0, callbacks = 0;
        TaskCompletionSource published = NewSignal();
        FmSynthEditorControl editor = new(workspace, synth,
            () => { }, _ => changed++,
            publishSynthEditAsync: async () =>
            {
                Interlocked.Increment(ref callbacks);
                try
                {
                    await transport.PublishSynthEditAsync(
                        workspace.Document, synth.Id);
                    published.TrySetResult();
                }
                catch (Exception ex)
                {
                    published.TrySetException(ex);
                    throw;
                }
            });
        StackPanel inspector = (StackPanel)typeof(FmSynthEditorControl)
            .GetField("_inspector", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(editor)!;
        Control Field(string label)
        {
            int index = inspector.Children.Select((c, i) => (c, i))
                .Single(x => x.c is TextBlock text && text.Text == label).i;
            return inspector.Children[index + 1];
        }
        static TaskCompletionSource NewSignal()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);
        static void Enter(TextBox box, string value)
        {
            box.Text = value;
            box.RaiseEvent(new KeyEventArgs {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.Enter,
            });
        }
        async Task WaitForPublication()
            => await published.Task.WaitAsync(TimeSpan.FromSeconds(8));

        ComboBox operation = (ComboBox)Field("Operation");
        uint audio = workspace.Document.AudioRevision;
        uint document = workspace.Document.DocumentRevision;
        operation.SelectedItem = FmOperatorKind.Maximum;
        await WaitForPublication();
        Assert.Multiple(() =>
        {
            Assert.That(workspace.Document.AudioRevision, Is.EqualTo(audio + 1));
            Assert.That(workspace.Document.DocumentRevision, Is.EqualTo(document + 1));
            Assert.That(changed, Is.EqualTo(1));
            Assert.That(callbacks, Is.EqualTo(1));
            Assert.That(backend.OpenCount, Is.EqualTo(1),
                "existing audition must not restart for a compatible edit");
            Assert.That(factory.Preparations.Select(x => x.Id),
                Is.EqualTo(new[] { synth.Id, parent.Id }),
                "production command worker must prepare child before parent");
            Assert.That(factory.Preparations.All(x =>
                x.AudioRevision == workspace.Document.AudioRevision), Is.True);
            Assert.That(factory.Preparations[0].Operation,
                Is.EqualTo(FmOperatorKind.Maximum),
                "the detached worker must see the newly committed graph");
        });

        // The producer has not rendered since the edit. Thus the queued
        // revision must be adopted at its next PCM boundary, without a
        // new NoteOn. This checks the production Render route, not a
        // separately simulated PlaybackSession callback.
        float[] max = backend.Render(101);
        CheckSmoothstep(max, 0.2f, 0.8f, 13);
        Assert.That(factory.OlderAudioCount, Is.Zero);
        Assert.That(transport.CurrentPlaybackSnapshot.AudioRevision,
            Is.EqualTo(startingSnapshot.AudioRevision),
            "a partial publication does not rewrite the base session snapshot");

        // Re-selecting an unchanged Operator choice is a real UI no-op:
        // neither transport worker nor audio revision should advance.
        operation.SelectedItem = FmOperatorKind.Maximum;
        await Task.Yield();
        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.EqualTo(1));
            Assert.That(callbacks, Is.EqualTo(1));
            Assert.That(factory.Preparations.Count, Is.EqualTo(2));
            Assert.That(workspace.Document.AudioRevision, Is.EqualTo(audio + 1));
        });

        // A second control family (Constant) traverses the same worker
        // queue and updates the *already sounding* Maximum operator.
        published = NewSignal();
        typeof(FmSynthEditorControl).GetMethod("SelectNode",
            BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(editor, [low]);
        Enter((TextBox)Field("Value"), "0.95");
        await WaitForPublication();
        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.EqualTo(2));
            Assert.That(callbacks, Is.EqualTo(2));
            Assert.That(backend.OpenCount, Is.EqualTo(1));
            Assert.That(factory.Preparations.Select(x => x.Id),
                Is.EqualTo(new[] { synth.Id, parent.Id, synth.Id, parent.Id }));
            Assert.That(factory.Preparations.Skip(2).All(x =>
                x.AudioRevision == workspace.Document.AudioRevision), Is.True);
            Assert.That(factory.Preparations[2].LowConstant, Is.EqualTo(0.95));
            Assert.That(factory.Preparations[2].Operation,
                Is.EqualTo(FmOperatorKind.Maximum));
        });
        float[] increased = backend.Render(101);
        CheckSmoothstep(increased, 0.8f, 0.95f, 114);
        Assert.Multiple(() =>
        {
            Assert.That(factory.OlderAudioCount, Is.Zero);
            Assert.That(backend.OpenCount, Is.EqualTo(1));
        });
        Enter((TextBox)Field("Value"), "0.95");
        Assert.Multiple(() =>
        {
            Assert.That(callbacks, Is.EqualTo(2));
            Assert.That(factory.Preparations.Count, Is.EqualTo(4));
            Assert.That(workspace.Document.AudioRevision, Is.EqualTo(audio + 2));
        });

        // The command gate must not accept redundant direct publication at
        // the same revision, even when invoked independently of the UI.
        await transport.PublishSynthEditAsync(workspace.Document, synth.Id);
        Assert.That(factory.Preparations.Count, Is.EqualTo(4));

        // Repeated current-note PCM must remain stable after the fades.
        Assert.That(backend.Render(32), Is.All.EqualTo(0.95f).Within(2e-6f));
        Assert.That(factory.OlderAudioCount, Is.Zero);
    }

    private static void CheckSmoothstep(float[] pcm, float from, float to,
        int startFrame)
    {
        for (int i = 0; i < pcm.Length; i++)
        {
            double t = i / 100.0;
            double w = t * t * (3 - 2 * t);
            float expectation = (float)(from * (1 - w) + to * w);
            Assert.That(pcm[i], Is.EqualTo(expectation).Within(2e-5f),
                $"production transport held note at frame {startFrame + i}");
        }
    }

    private sealed record Preparation(
        ObjectId Id, uint AudioRevision, FmOperatorKind? Operation,
        double? LowConstant, int WorkerThread);

    private sealed class TrackingFactory(RenderConfiguration configuration,
        ObjectId editedSynthId)
        : IBackgroundPlaybackSourceFactory,
          ILiveSynthSourcePreparationFactory
    {
        private readonly PlaybackRequestAudioSourceFactory _actual =
            new(configuration);
        public List<Preparation> Preparations { get; } = [];
        public PlaybackRequest? CurrentRequest { get; private set; }
        public int OlderAudioCount
        {
            get
            {
                Assert.That(CurrentRequest, Is.Not.Null);
                Assert.That(_actual.TryGetOlderAudioVoiceCount(
                    CurrentRequest!, out int count), Is.True);
                return count;
            }
        }
        public IAudioOutputSource Create(PlaybackRequest request)
        {
            CurrentRequest = request;
            return _actual.Create(request);
        }
        public ISound PrepareLiveSynthSource(
            SongDocumentSnapshot snapshot, ObjectId sourceId)
        {
            FmSynthDefinition frozen = (FmSynthDefinition)snapshot.Document
                .Objects[sourceId];
            FmOperatorNode? operation = frozen.Graph.Nodes
                .OfType<FmOperatorNode>().SingleOrDefault();
            FmConstantNode? low = sourceId == editedSynthId
                ? frozen.Graph.Nodes.OfType<FmConstantNode>()
                    .Single(n => n.Id == 1)
                : null;
            Preparations.Add(new Preparation(sourceId,
                snapshot.AudioRevision, operation?.Operation,
                low?.Value, Environment.CurrentManagedThreadId));
            return _actual.PrepareLiveSynthSource(snapshot, sourceId);
        }
    }

    private sealed class ManualBackend : IAudioOutputBackend
    {
        public IAudioOutputSource? Source { get; private set; }
        public int OpenCount { get; private set; }
        public IAudioOutputSession Open(AudioOutputFormat format,
            IAudioOutputSource source)
        {
            Source = source;
            OpenCount++;
            return new ManualSession(format);
        }
        public float[] Render(int frames)
        {
            Assert.That(Source, Is.Not.Null);
            float[] pcm = new float[frames * Source!.Format.ChannelCount];
            Source.Render(frames, pcm);
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
