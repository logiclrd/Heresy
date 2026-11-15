using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;

using Heresy.Core.Filters;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
using Heresy.Core.Sequencing;
using Heresy.Core.Samples;
using Heresy.Playback;
using Heresy.Render.Configuration;
using Heresy.Render.Envelopes;
using Heresy.Render.FmSynthesis;
using Heresy.Render.Filters;
using Heresy.Render.Playback;
using Heresy.Render.Realtime;
using Heresy.Render.Samples;
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

    [AvaloniaTest]
    public async Task ModulatedSvfNotchPerUseQInspectorRetunesContinuingProductionFilter()
    {
        // C2(h): exercise a non-OnePole, stateful Filter through the real
        // per-use inspector and the production transport. Its CutoffHz has
        // an existing absolute FM connection; Q is deliberately unbound.
        const int rate = 8000;
        DocumentWorkspace workspace = new();
        FilterDefinition definition = FilterDocumentEditor.CreateFilter(
            workspace, "C2(h) SVF notch");
        FilterStageDefinition stage = new(1, "stage1", FilterKind.SvfNotch);
        FilterDocumentEditor.ApplyStage(workspace, definition, 1,
            definition.GetStage(1), stage.Name, stage.Kind, stage.DefaultParameters);

        FmSynthDefinition synth = FmSynthDocumentEditor.CreateFmSynth(
            workspace, "C2(h) modulated notch");
        int oscillator = FmSynthDocumentEditor.AddOscillatorNode(
            workspace, synth, FmOscillatorWaveform.Sine, 190, 70, 80);
        int modulator = FmSynthDocumentEditor.AddConstantNode(
            workspace, synth, 280, 70, 220);
        int filterId = FmSynthDocumentEditor.AddFilterNode(
            workspace, synth, 320, 110);
        FmSynthDocumentEditor.SetFilterConfiguration(
            workspace, synth, filterId, new FilterInstance(definition));
        FmSynthDocumentEditor.ConnectNodes(
            workspace, synth, oscillator, filterId, 0);
        FmSynthDocumentEditor.ConnectNodes(
            workspace, synth, modulator, filterId, 1);
        FmSynthDocumentEditor.SetOutputNode(workspace, synth, filterId);
        FmFilterNode initialFilter = synth.Graph.Nodes
            .OfType<FmFilterNode>().Single();
        Assert.That(initialFilter.GetPorts().Single(p =>
            p.ParameterName == "CutoffHz").SourceNodeId, Is.EqualTo(modulator));
        Assert.That(initialFilter.GetPorts().Single(p =>
            p.ParameterName == "Q").SourceNodeId, Is.Null);

        FmSynthDefinition parent = FmSynthDocumentEditor.CreateFmSynth(
            workspace, "C2(h) dependent parent");
        FmSynthDocumentEditor.UpdateNode(workspace, parent,
            new FmSoundSourceNode(0, synth.Id,
                FmMultiNode.NeutralFrequencyHz));
        RenderConfiguration configuration = new(rate, [
            new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0),
        ]);
        TrackingFactory factory = new(configuration, synth.Id);
        ManualBackend backend = new();
        using SongPlaybackTransport transport = new(backend, factory);
        await transport.SendLiveEventAsync(workspace.Document,
            ChannelTarget.Virtual(45), [new StartNoteCommand(synth.Id)]);
        Assert.That(backend.OpenCount, Is.EqualTo(1));
        PlaybackSnapshotInfo baseSnapshot = transport.CurrentPlaybackSnapshot;

        // The independent, never-UI-edited oracle creates its own per-use
        // Filter instance and retains the same port identities and IIR state.
        Resolver oracleResolver = new(synth.Id), untouchedResolver = new(synth.Id);
        FmSynthGraph baseline = synth.Graph;
        FmSynthSound Compile(FilterInstance instance, Resolver resolver) =>
            new(new FmSynthGraph(baseline.Nodes.Select(node =>
                node.Id == filterId ? (FmSynthNode)new FmFilterNode(filterId,
                    oscillator, instance, initialFilter.Connections) : node),
                filterId), resolver, soundResolver: resolver,
                enableLiveMultiExpansion: true);
        FilterInstance oracleUse = new(definition);
        FmSynthSound previousOracle = Compile(oracleUse, oracleResolver);
        oracleResolver.Sound = previousOracle;
        untouchedResolver.Sound = Compile(new FilterInstance(definition),
            untouchedResolver);
        RenderContext renderContext = new(configuration);
        PlaybackSession Open(Resolver resolver) =>
            new(renderContext, new NoteScheduleBuilder().Freeze(), resolver);
        PlaybackSession oracle = Open(oracleResolver);
        PlaybackSession untouched = Open(untouchedResolver);
        foreach (PlaybackSession session in new[] { oracle, untouched })
            session.ApplyLiveEvent(ChannelTarget.Physical(0),
                [new StartNoteCommand(synth.Id)]);

        float[] initial = backend.Render(120);
        float[] initialReference = new float[120];
        oracle.Render(0, 120, initialReference);
        untouched.Render(0, 120, new float[120]);
        for (int i = 0; i < initial.Length; i++)
            Assert.That(initial[i], Is.EqualTo(initialReference[i]).Within(5e-5f),
                $"initial modulated notch PCM at frame {i}");

        int changed = 0, callbacks = 0;
        TaskCompletionSource published = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
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
        FilterInstanceEditorControl configured = inspector.Children
            .OfType<FilterInstanceEditorControl>().Single();
        Dictionary<string, CheckBox> overrides =
            (Dictionary<string, CheckBox>)typeof(FilterInstanceEditorControl)
                .GetField("_overrideToggles",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(configured)!;
        Dictionary<string, Control> fields =
            (Dictionary<string, Control>)typeof(FilterInstanceEditorControl)
                .GetField("_editors",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(configured)!;
        void Apply() => typeof(FilterInstanceEditorControl)
            .GetMethod("ApplyDraft",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(configured, null);
        uint audioRevision = workspace.Document.AudioRevision;
        uint documentRevision = workspace.Document.DocumentRevision;

        overrides["Q"].IsChecked = true;
        ((TextBox)fields["Q"]).Text = "NaN";
        Apply();
        Assert.Multiple(() =>
        {
            Assert.That(workspace.Document.AudioRevision,
                Is.EqualTo(audioRevision), "invalid Q must not publish");
            Assert.That(callbacks, Is.Zero);
            Assert.That(factory.Preparations, Is.Empty);
        });

        ((TextBox)fields["Q"]).Text = "9";
        Apply();
        await published.Task.WaitAsync(TimeSpan.FromSeconds(8));
        FmFilterNode committedFilter = synth.Graph.Nodes
            .OfType<FmFilterNode>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(workspace.Document.AudioRevision,
                Is.EqualTo(audioRevision + 1));
            Assert.That(workspace.Document.DocumentRevision,
                Is.EqualTo(documentRevision + 1));
            Assert.That(changed, Is.GreaterThanOrEqualTo(1));
            Assert.That(callbacks, Is.EqualTo(1));
            Assert.That(committedFilter.Configuration!.GetOverrides(1)["Q"]
                .NumericValue, Is.EqualTo(9));
            Assert.That(committedFilter.GetPorts().Single(p =>
                p.ParameterName == "CutoffHz").SourceNodeId,
                Is.EqualTo(modulator), "existing FM connection survives");
            Assert.That(factory.Preparations.Select(p => p.Id),
                Is.EqualTo(new[] { synth.Id, parent.Id }),
                "production worker must prepare child before parent");
            Assert.That(factory.Preparations[0].FilterQ, Is.EqualTo(9),
                "worker must receive the frozen UI-authored Q");
            Assert.That(backend.OpenCount, Is.EqualTo(1));
            Assert.That(transport.CurrentPlaybackSnapshot.AudioRevision,
                Is.EqualTo(baseSnapshot.AudioRevision));
        });

        oracleUse.SetStageParameter(1, "Q",
            FilterParameterValue.FromNumber(9));
        FmSynthSound revisedOracle = Compile(oracleUse, oracleResolver);
        oracle.AdoptLiveSynthParameters(synth.Id, previousOracle, revisedOracle);
        oracleResolver.Sound = revisedOracle;

        float[] observed = backend.Render(900);
        float[] expected = new float[900], original = new float[900];
        oracle.Render(120, 900, expected);
        untouched.Render(120, 900, original);
        double maxDifference = 0;
        for (int i = 0; i < observed.Length; i++)
        {
            Assert.That(observed[i], Is.EqualTo(expected[i]).Within(5e-4f),
                $"production SVF notch held PCM at frame {120 + i}");
            maxDifference = Math.Max(maxDifference,
                Math.Abs(observed[i] - original[i]));
        }
        Assert.That(maxDifference, Is.GreaterThan(0.025),
            "the actual Q inspector edit must audibly change the existing note");
        Assert.Multiple(() =>
        {
            Assert.That(factory.OlderAudioCount, Is.Zero,
                "a compatible modulated cutoff/unbound Q retune is current");
            Assert.That(backend.OpenCount, Is.EqualTo(1));
        });

        // The successful Apply rebuilt controls. Reapplying the freshly
        // loaded identical Q must not emit another revision/publication.
        Apply();
        await transport.PublishSynthEditAsync(workspace.Document, synth.Id);
        Assert.Multiple(() =>
        {
            Assert.That(workspace.Document.AudioRevision,
                Is.EqualTo(audioRevision + 1));
            Assert.That(callbacks, Is.EqualTo(1));
            Assert.That(factory.Preparations.Count, Is.EqualTo(2));
            Assert.That(factory.OlderAudioCount, Is.Zero);
        });
    }


    [AvaloniaTest]
    public async Task SourceSampleSelectorPublishesFrozenWorkerPreparedReplacementAndHeldPcm()
    {
        // C2(j): unlike an ordinary scalar edit, A->B needs a new
        // Sample invocation prepared on the separate FIR/Multi worker,
        // never constructed on the render path. The manual backend uses
        // the production source, and this fixture drives the exact
        // IFirPreparationAudioSource handshake that BufferedAudioOutputSource
        // performs around its producer render block.
        DocumentWorkspace workspace = new();
        SampleDefinition Imported(string name, short level)
        {
            ObjectId id = workspace.Document.AllocateObjectId();
            SampleDefinition sample = SampleDefinition.CreateImported(
                id, name, name + ".wav", FourFrameWave(level));
            sample.Loop = new SampleLoop(SampleLoopMode.Forward, 0, 4);
            workspace.Document.Add(sample);
            return sample;
        }
        SampleDefinition a = Imported("C2(j) sample A", 8192);
        SampleDefinition b = Imported("C2(j) sample B", 24576);
        FmSynthDefinition synth = FmSynthDocumentEditor.CreateFmSynth(
            workspace, "C2(j) Source selection");
        int nodeId = FmSynthDocumentEditor.AddSoundSourceNode(
            workspace, synth, 180, 90, a.Id, FmMultiNode.NeutralFrequencyHz);
        FmSynthDocumentEditor.SetOutputNode(workspace, synth, nodeId);
        FmSynthDefinition parent = FmSynthDocumentEditor.CreateFmSynth(
            workspace, "C2(j) dependent parent");
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
            ChannelTarget.Virtual(107), [new StartNoteCommand(synth.Id)]);
        float[] beginning = backend.Render(17);
        Assert.That(beginning, Is.All.EqualTo(0.25f).Within(3e-5f),
            "an imported looped Sample is actually sounding before editing");
        PlaybackSnapshotInfo originalSnapshot = transport.CurrentPlaybackSnapshot;
        Assert.That(backend.OpenCount, Is.EqualTo(1));
        int inspectorChanged = 0, callbacks = 0;
        List<Task> pending = [];
        FmSynthEditorControl editor = new(workspace, synth,
            () => { }, _ => inspectorChanged++,
            publishSynthEditAsync: () =>
            {
                callbacks++;
                Task publication = transport.PublishSynthEditAsync(
                    workspace.Document, synth.Id);
                pending.Add(publication);
                return publication;
            });
        StackPanel inspector = (StackPanel)typeof(FmSynthEditorControl)
            .GetField("_inspector", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(editor)!;
        int sourceLabel = inspector.Children.Select((control, i) => (control, i))
            .Single(x => x.control is TextBlock text
                && text.Text == "Source (Sample / Instrument / Synth)").i;
        ComboBox selector = (ComboBox)inspector.Children[sourceLabel + 1];
        object bChoice = selector.Items.OfType<object>().Single(item =>
            item.ToString()!.Contains("C2(j) sample B"));
        object noneChoice = selector.Items.OfType<object>().Single(item =>
            item.ToString() == "(None)");
        uint oldAudio = workspace.Document.AudioRevision;
        uint oldDocument = workspace.Document.DocumentRevision;

        selector.SelectedItem = bChoice;
        await Task.WhenAll(pending.ToArray());
        Assert.Multiple(() =>
        {
            Assert.That(workspace.Document.AudioRevision,
                Is.EqualTo(oldAudio + 1));
            Assert.That(workspace.Document.DocumentRevision,
                Is.EqualTo(oldDocument + 1));
            Assert.That(callbacks, Is.EqualTo(1));
            Assert.That(inspectorChanged, Is.EqualTo(1));
            Assert.That(synth.Graph.Nodes.OfType<FmSoundSourceNode>()
                .Single().SourceId, Is.EqualTo(b.Id));
            Assert.That(factory.Preparations.Select(p => p.Id),
                Is.EqualTo(new[] { synth.Id, parent.Id }),
                "the detached transport worker prepares child before parent");
            Assert.That(factory.Preparations[0].SampleSourceId,
                Is.EqualTo(b.Id), "worker compiles the frozen replacement Sample");
            Assert.That(factory.Preparations.All(p =>
                p.AudioRevision == oldAudio + 1), Is.True);
            Assert.That(backend.OpenCount, Is.EqualTo(1));
            Assert.That(transport.CurrentPlaybackSnapshot.AudioRevision,
                Is.EqualTo(originalSnapshot.AudioRevision));
        });

        IFirPreparationAudioSource preparing =
            (IFirPreparationAudioSource)backend.Source!;
        using FirPreparationScheduler scheduler = new();
        // First PCM boundary publishes the replacement *intent*, not an
        // unprepared new Source: old A must still render and the producer
        // then queues the separate worker preparation.
        preparing.AdoptPreparedFirControls();
        Assert.That(backend.Render(1)[0],
            Is.EqualTo(0.25f).Within(3e-5f));
        preparing.QueuePendingFirControls(scheduler);
        Assert.That(SpinWait.SpinUntil(() =>
            scheduler.CompletedMultiExpansionCount >= 1,
            TimeSpan.FromSeconds(8)), Is.True,
            "Sample B invocation must be constructed on the preparation worker");
        Assert.That(scheduler.Fault, Is.Null);
        preparing.AdoptPreparedFirControls();
        float[] toB = backend.Render(101);
        CheckSmoothstep(toB, 0.25f, 0.75f, 18);
        Assert.Multiple(() =>
        {
            Assert.That(factory.OlderAudioCount, Is.Zero);
            Assert.That(backend.OpenCount, Is.EqualTo(1));
        });

        selector.SelectedItem = bChoice; // unchanged real UI choice
        await transport.PublishSynthEditAsync(workspace.Document, synth.Id);
        Assert.Multiple(() =>
        {
            Assert.That(workspace.Document.AudioRevision,
                Is.EqualTo(oldAudio + 1));
            Assert.That(callbacks, Is.EqualTo(1));
            Assert.That(factory.Preparations.Count, Is.EqualTo(2));
        });

        // The bounded Sample->None path requires no child preparation.
        // The outgoing Sample's original clock is kept for the 100-ms fade.
        selector.SelectedItem = noneChoice;
        await Task.WhenAll(pending.ToArray());
        Assert.Multiple(() =>
        {
            Assert.That(synth.Graph.Nodes.OfType<FmSoundSourceNode>()
                .Single().SourceId, Is.EqualTo(ObjectId.None));
            Assert.That(workspace.Document.AudioRevision,
                Is.EqualTo(oldAudio + 2));
            Assert.That(workspace.Document.DocumentRevision,
                Is.EqualTo(oldDocument + 2));
            Assert.That(callbacks, Is.EqualTo(2));
            Assert.That(factory.Preparations.Select(p => p.Id),
                Is.EqualTo(new[] { synth.Id, parent.Id, synth.Id, parent.Id }));
            Assert.That(factory.Preparations[2].SampleSourceId,
                Is.EqualTo(ObjectId.None));
            Assert.That(backend.OpenCount, Is.EqualTo(1));
        });
        preparing.AdoptPreparedFirControls();
        float[] toSilence = backend.Render(101);
        CheckSmoothstep(toSilence, 0.75f, 0f, 119);
        Assert.Multiple(() =>
        {
            Assert.That(factory.OlderAudioCount, Is.Zero);
            Assert.That(backend.Render(12),
                Is.All.EqualTo(0f).Within(3e-5f));
        });
        selector.SelectedItem = noneChoice;
        await transport.PublishSynthEditAsync(workspace.Document, synth.Id);
        Assert.Multiple(() =>
        {
            Assert.That(callbacks, Is.EqualTo(2));
            Assert.That(workspace.Document.AudioRevision,
                Is.EqualTo(oldAudio + 2));
            Assert.That(factory.Preparations.Count, Is.EqualTo(4));
            Assert.That(transport.CurrentPlaybackSnapshot.AudioRevision,
                Is.EqualTo(originalSnapshot.AudioRevision));
            Assert.That(scheduler.Fault, Is.Null);
        });
    }



    [AvaloniaTest]
    public async Task SourceInspectorSampleSupersessionDefersOnlyLatestThroughRealTransport()
    {
        // C2(p): actual Source selector, not direct revision API calls.
        // A→B has a worker-prepared onset and 100-ms fade. C and D arrive
        // during that fade: neither interrupts A/B histories, only D gets
        // a prepared invocation once the original fade has finished.
        DocumentWorkspace workspace = new();
        SampleDefinition Imported(string name, short value)
        {
            ObjectId id = workspace.Document.AllocateObjectId();
            SampleDefinition result = SampleDefinition.CreateImported(
                id, name, name + ".wav", FourFrameWave(value));
            result.Loop = new SampleLoop(SampleLoopMode.Forward, 0, 4);
            workspace.Document.Add(result);
            return result;
        }
        SampleDefinition a = Imported("C2(p) Sample A", 8192);
        SampleDefinition b = Imported("C2(p) Sample B", 24576);
        SampleDefinition c = Imported("C2(p) Sample C superseded", 28672);
        SampleDefinition d = Imported("C2(p) Sample D latest", 16384);
        FmSynthDefinition synth = FmSynthDocumentEditor.CreateFmSynth(
            workspace, "C2(p) latest-only inspector");
        int source = FmSynthDocumentEditor.AddSoundSourceNode(
            workspace, synth, 140, 95, a.Id, FmMultiNode.NeutralFrequencyHz);
        FmSynthDocumentEditor.SetOutputNode(workspace, synth, source);
        FmSynthDefinition parent = MakeDependentParent(
            workspace, synth, "C2(p) dependent Synth");

        TrackingFactory factory = new(Mono(), synth.Id);
        ManualBackend backend = new();
        using SongPlaybackTransport transport = new(backend, factory);
        await transport.SendLiveEventAsync(workspace.Document,
            ChannelTarget.Virtual(107), [new StartNoteCommand(synth.Id)]);
        Assert.That(backend.Render(17), Is.All.EqualTo(0.25f).Within(3e-5f));
        PlaybackSnapshotInfo baseSnapshot = transport.CurrentPlaybackSnapshot;
        uint audio = workspace.Document.AudioRevision;
        uint document = workspace.Document.DocumentRevision;
        int changed = 0, callbacks = 0;
        List<Task> pending = [];
        FmSynthEditorControl editor = new(workspace, synth, () => { },
            _ => changed++,
            publishSynthEditAsync: () =>
            {
                callbacks++;
                Task task = transport.PublishSynthEditAsync(
                    workspace.Document, synth.Id);
                pending.Add(task);
                return task;
            });
        async Task Select(SampleDefinition selected, int ordinal)
        {
            ComboBox selector = Field<ComboBox>(Inspector(editor),
                "Source (Sample / Instrument / Synth)");
            object item = selector.Items.Cast<object>().Single(choice =>
                choice.ToString()!.Contains(selected.Name,
                    StringComparison.Ordinal));
            selector.SelectedItem = item;
            await Task.WhenAll(pending.ToArray())
                .WaitAsync(TimeSpan.FromSeconds(8));
            Assert.Multiple(() =>
            {
                Assert.That(synth.Graph.Nodes.OfType<FmSoundSourceNode>()
                    .Single().SourceId, Is.EqualTo(selected.Id));
                Assert.That(workspace.Document.AudioRevision,
                    Is.EqualTo(audio + ordinal));
                Assert.That(workspace.Document.DocumentRevision,
                    Is.EqualTo(document + ordinal));
                Assert.That(callbacks, Is.EqualTo(ordinal));
                Assert.That(changed, Is.EqualTo(ordinal));
                Assert.That(factory.Preparations.Count, Is.EqualTo(ordinal * 2));
                Assert.That(factory.Preparations.Skip((ordinal - 1) * 2)
                    .Select(p => p.Id),
                    Is.EqualTo(new[] { synth.Id, parent.Id }));
                Assert.That(factory.Preparations[(ordinal - 1) * 2]
                    .SampleSourceId, Is.EqualTo(selected.Id));
                Assert.That(factory.Preparations.Skip((ordinal - 1) * 2)
                    .All(p => p.AudioRevision == audio + ordinal), Is.True);
                Assert.That(backend.OpenCount, Is.EqualTo(1));
                Assert.That(transport.CurrentPlaybackSnapshot.AudioRevision,
                    Is.EqualTo(baseSnapshot.AudioRevision));
            });
        }

        IFirPreparationAudioSource preparing =
            (IFirPreparationAudioSource)backend.Source!;
        using FirPreparationScheduler scheduler = new();
        await Select(b, 1);
        preparing.AdoptPreparedFirControls();
        Assert.That(backend.Render(1)[0], Is.EqualTo(0.25f).Within(3e-5f),
            "unprepared B cannot start on the PCM thread");
        preparing.QueuePendingFirControls(scheduler);
        Assert.That(SpinWait.SpinUntil(() =>
            scheduler.CompletedMultiExpansionCount == 1,
            TimeSpan.FromSeconds(8)), Is.True,
            "B requires one detached replacement onset");
        preparing.AdoptPreparedFirControls();

        // Check the audible A→B fade over 101 consecutive sample frames.
        int firstIndex = 0;
        void FirstFade(int count, string label)
        {
            float[] pcm = backend.Render(count);
            for (int j = 0; j < count; j++)
            {
                int i = firstIndex + j;
                double t = i / 100.0;
                double w = t * t * (3 - 2 * t);
                float expected = (float)(0.25 * (1 - w) + 0.75 * w);
                Assert.That(pcm[j], Is.EqualTo(expected).Within(3e-5f),
                    label + ": original A→B frame " + i);
            }
            firstIndex += count;
            Assert.That(factory.OlderAudioCount, Is.Zero,
                label + ": a safely pending Source is not an older voice");
        }
        FirstFade(25, "A→B first 25 frames");
        await Select(c, 2);
        preparing.QueuePendingFirControls(scheduler);
        Assert.That(scheduler.CompletedMultiExpansionCount, Is.EqualTo(1),
            "C must not be prepared while A→B still sounds");
        FirstFade(20, "C arrives but A→B is unchanged");
        await Select(d, 3);
        preparing.QueuePendingFirControls(scheduler);
        Assert.That(scheduler.CompletedMultiExpansionCount, Is.EqualTo(1),
            "D's worker preparation must also wait for the first fade");
        FirstFade(56, "D supersedes C but first fade finishes");
        Assert.That(firstIndex, Is.EqualTo(101));
        Assert.That(scheduler.CompletedMultiExpansionCount, Is.EqualTo(1));

        // The initial fade is over. NOW and only now the worker may
        // construct latest D: C must never have a prepared onset.
        preparing.AdoptPreparedFirControls();
        preparing.QueuePendingFirControls(scheduler);
        Assert.That(SpinWait.SpinUntil(() =>
            scheduler.CompletedMultiExpansionCount >= 2,
            TimeSpan.FromSeconds(8)), Is.True,
            "only the latest D begins its new detached preparation");
        preparing.AdoptPreparedFirControls();
        float[] last = backend.Render(101);
        CheckSmoothstep(last, 0.75f, 0.5f, 119);
        Assert.That(backend.Render(9),
            Is.All.EqualTo(0.5f).Within(3e-5f));
        Assert.Multiple(() =>
        {
            Assert.That(scheduler.CompletedMultiExpansionCount, Is.EqualTo(2),
                "exactly B then latest D, never superseded C");
            Assert.That(scheduler.Fault, Is.Null);
            Assert.That(factory.OlderAudioCount, Is.Zero);
            Assert.That(factory.Preparations.Count, Is.EqualTo(6));
            Assert.That(callbacks, Is.EqualTo(3));
            Assert.That(changed, Is.EqualTo(3));
            Assert.That(backend.OpenCount, Is.EqualTo(1));
        });
        await transport.PublishSynthEditAsync(workspace.Document, synth.Id);
        Assert.That(factory.Preparations.Count, Is.EqualTo(6),
            "same revision cannot reprepare a third Source onset");
    }

    [AvaloniaTest]
    public async Task OscillatorInspectorShapeAndPitchPublishThroughTransportWithHeldPcmOracle()
    {
        // C2(k): the real Oscillator controls must not stop at the
        // in-process C2(d) callback. Check frozen child-before-parent worker
        // publication and same-session producer output against independent
        // authored FmSynthSound revisions that retain the original voice.
        DocumentWorkspace workspace = new();
        FmSynthDefinition synth = FmSynthDocumentEditor.CreateFmSynth(
            workspace, "C2(k) Oscillator transport");
        int multiplier = FmSynthDocumentEditor.AddConstantNode(
            workspace, synth, 2, 60, 80);
        int id = FmSynthDocumentEditor.AddOscillatorNode(
            workspace, synth, FmOscillatorWaveform.Sine, 80, 270, 90,
            minimum: -0.6, maximum: 0.6);
        FmOscillatorNode initial = new(id, FmOscillatorWaveform.Sine, 80,
            -0.6, 0.6, multiplierNodeId: multiplier);
        FmSynthDocumentEditor.UpdateNode(workspace, synth, initial);
        FmSynthDocumentEditor.SetOutputNode(workspace, synth, id);
        FmSynthDefinition parent = MakeDependentParent(workspace, synth,
            "C2(k) Oscillator parent");

        RenderConfiguration config = Mono();
        TrackingFactory factory = new(config, synth.Id);
        ManualBackend backend = new();
        using SongPlaybackTransport transport = new(backend, factory);
        await transport.SendLiveEventAsync(workspace.Document,
            ChannelTarget.Virtual(121), [new StartNoteCommand(synth.Id)]);

        Resolver oracleResolver = new(synth.Id), originalResolver = new(synth.Id);
        FmSynthSound Author(FmOscillatorNode authored, Resolver resolver)
            => new(new FmSynthGraph(synth.Graph.Nodes.Select(node =>
                node.Id == id ? (FmSynthNode)authored : node), id),
                resolver, soundResolver: resolver, enableLiveMultiExpansion: true);
        FmSynthSound oracleSound = Author(initial, oracleResolver);
        oracleResolver.Sound = oracleSound;
        originalResolver.Sound = Author(initial, originalResolver);
        PlaybackSession oracle = IndependentSession(config, oracleResolver);
        PlaybackSession original = IndependentSession(config, originalResolver);
        StartIndependent(oracle, synth.Id);
        StartIndependent(original, synth.Id);

        float[] first = backend.Render(19);
        float[] firstOracle = new float[19];
        oracle.Render(0, 19, firstOracle);
        original.Render(0, 19, new float[19]);
        Assert.That(first, Is.EqualTo(firstOracle).Within(3e-5f),
            "initial independent oscillator and production PCM must agree");
        PlaybackSnapshotInfo frozenBase = transport.CurrentPlaybackSnapshot;
        uint audio = workspace.Document.AudioRevision;
        uint document = workspace.Document.DocumentRevision;
        List<Task> pending = [];
        int changed = 0, callbacks = 0;
        FmSynthEditorControl editor = new(workspace, synth, () => { },
            _ => changed++,
            publishSynthEditAsync: () =>
            {
                callbacks++;
                Task task = transport.PublishSynthEditAsync(
                    workspace.Document, synth.Id);
                pending.Add(task);
                return task;
            });
        StackPanel inspector = Inspector(editor);
        StackPanel waveformControls = inspector.Children.OfType<Grid>().Single()
            .Children.OfType<StackPanel>().Single();
        ComboBox waveform = waveformControls.Children.OfType<ComboBox>().Single();
        TextBox curvature = waveformControls.Children.OfType<TextBox>().Single();
        TextBox frequency = Field<TextBox>(inspector, "Frequency Hz");
        TextBox minimum = Field<TextBox>(inspector, "Vmin");
        TextBox maximum = Field<TextBox>(inspector, "Vmax");
        CheckBox fixedPitch = inspector.Children.OfType<CheckBox>()
            .Single(box => Equals(box.Content, "Constant"));
        CheckBox exponential = inspector.Children.OfType<CheckBox>()
            .Single(box => Equals(box.Content,
                "Exponential multiplier (semitones)"));
        int frame = 19, operations = 0;

        async Task Change(Action edit, FmOscillatorNode authored, bool audible)
        {
            edit();
            await Task.WhenAll(pending.ToArray());
            operations++;
            FmOscillatorNode committed = synth.Graph.Nodes
                .OfType<FmOscillatorNode>().Single();
            Assert.Multiple(() =>
            {
                Assert.That(SameOscillator(committed, authored), Is.True);
                Assert.That(workspace.Document.AudioRevision,
                    Is.EqualTo(audio + operations));
                Assert.That(workspace.Document.DocumentRevision,
                    Is.EqualTo(document + operations));
                Assert.That(changed, Is.EqualTo(operations));
                Assert.That(callbacks, Is.EqualTo(operations));
                Assert.That(factory.Preparations.Count, Is.EqualTo(operations * 2));
                Assert.That(factory.Preparations.Skip((operations - 1) * 2)
                    .Select(p => p.Id), Is.EqualTo(new[] { synth.Id, parent.Id }));
                Assert.That(SameOscillator(
                    factory.Preparations[(operations - 1) * 2].Oscillator,
                    authored), Is.True, "transport worker sees frozen UI Oscillator");
                Assert.That(factory.Preparations.Skip((operations - 1) * 2)
                    .All(p => p.AudioRevision == audio + operations), Is.True);
                Assert.That(backend.OpenCount, Is.EqualTo(1));
            });

            FmSynthSound revised = Author(authored, oracleResolver);
            oracle.AdoptLiveSynthParameters(synth.Id, oracleSound, revised);
            oracleResolver.Sound = revised;
            oracleSound = revised;
            Assert.That(oracle.CountOlderAudioVoices(), Is.Zero);
            float[] observed = backend.Render(141);
            float[] expectedPcm = new float[141], unchanged = new float[141];
            oracle.Render(frame, 141, expectedPcm);
            original.Render(frame, 141, unchanged);
            for (int j = 0; j < observed.Length; j++)
                Assert.That(observed[j], Is.EqualTo(expectedPcm[j])
                    .Within(3e-5f),
                    $"C2(k) Oscillator production edit {operations} at {frame + j}");
            if (audible)
                Assert.That(observed.Zip(unchanged,
                    (a, b) => Math.Abs(a - b)).Max(), Is.GreaterThan(0.03),
                    "this oscillator change must audibly differ from unedited PCM");
            Assert.Multiple(() =>
            {
                Assert.That(factory.OlderAudioCount, Is.Zero);
                Assert.That(transport.CurrentPlaybackSnapshot.AudioRevision,
                    Is.EqualTo(frozenBase.AudioRevision));
                Assert.That(backend.OpenCount, Is.EqualTo(1));
            });
            frame += 141;
        }

        await Change(() => waveform.SelectedItem = FmOscillatorWaveform.Square,
            new FmOscillatorNode(id, FmOscillatorWaveform.Square, 80,
                -0.6, 0.6, multiplier), true);
        await Change(() => Enter(curvature, "1.5"),
            new FmOscillatorNode(id, FmOscillatorWaveform.Square, 80,
                -0.6, 0.6, multiplier, curvature: 1.5), false);
        await Change(() => Enter(minimum, "-0.25"),
            new FmOscillatorNode(id, FmOscillatorWaveform.Square, 80,
                -0.25, 0.6, multiplier, curvature: 1.5), true);
        await Change(() => Enter(maximum, "0.9"),
            new FmOscillatorNode(id, FmOscillatorWaveform.Square, 80,
                -0.25, 0.9, multiplier, curvature: 1.5), true);
        await Change(() => Enter(frequency, "117"),
            new FmOscillatorNode(id, FmOscillatorWaveform.Square, 117,
                -0.25, 0.9, multiplier, curvature: 1.5), true);
        await Change(() => exponential.IsChecked = true,
            new FmOscillatorNode(id, FmOscillatorWaveform.Square, 117,
                -0.25, 0.9, multiplier, exponentialMultiplier: true,
                curvature: 1.5), true);
        await Change(() => fixedPitch.IsChecked = true,
            new FmOscillatorNode(id, FmOscillatorWaveform.Square, 117,
                -0.25, 0.9, multiplier, exponentialMultiplier: true,
                constant: true, curvature: 1.5), false);

        Enter(frequency, "117");
        waveform.SelectedItem = FmOscillatorWaveform.Square;
        exponential.IsChecked = true;
        fixedPitch.IsChecked = true;
        await transport.PublishSynthEditAsync(workspace.Document, synth.Id);
        Assert.Multiple(() =>
        {
            Assert.That(workspace.Document.AudioRevision, Is.EqualTo(audio + 7));
            Assert.That(callbacks, Is.EqualTo(7));
            Assert.That(factory.Preparations.Count, Is.EqualTo(14));
            Assert.That(factory.OlderAudioCount, Is.Zero);
        });
    }

    [AvaloniaTest]
    public async Task SourcePitchAndModesPublishThroughTransportWithHeldSamplePcmOracle()
    {
        // C2(k): same selected Sample, different Source pitch/mode fields.
        // Its readhead must not reset, even though the producer receives a
        // fresh frozen Synth per real inspector edit.
        DocumentWorkspace workspace = new();
        short[] wave = [
            8192, 16384, -8192, -16384, 24576, 0, -24576, 4096,
            -4096, 12288, 20480, -20480, 6144, -12288, 28672, -28672,
        ];
        ObjectId sampleId = workspace.Document.AllocateObjectId();
        SampleDefinition sample = SampleDefinition.CreateImported(
            sampleId, "C2(k) modulated pitch Sample", "source.wav",
            MonoWave16(wave));
        sample.Loop = new SampleLoop(SampleLoopMode.Forward, 0, wave.Length);
        workspace.Document.Add(sample);
        FmSynthDefinition synth = FmSynthDocumentEditor.CreateFmSynth(
            workspace, "C2(k) Source transport");
        int multiplier = FmSynthDocumentEditor.AddConstantNode(
            workspace, synth, 2, 60, 80);
        double neutral = FmMultiNode.NeutralFrequencyHz;
        int id = FmSynthDocumentEditor.AddSoundSourceNode(
            workspace, synth, 240, 100, sampleId, neutral);
        FmSoundSourceNode initial = new(id, sampleId, neutral,
            multiplierNodeId: multiplier);
        FmSynthDocumentEditor.UpdateNode(workspace, synth, initial);
        FmSynthDocumentEditor.SetOutputNode(workspace, synth, id);
        FmSynthDefinition parent = MakeDependentParent(workspace, synth,
            "C2(k) Source parent");

        RenderConfiguration config = Mono();
        TrackingFactory factory = new(config, synth.Id);
        ManualBackend backend = new();
        using SongPlaybackTransport transport = new(backend, factory);
        await transport.SendLiveEventAsync(workspace.Document,
            ChannelTarget.Virtual(123), [new StartNoteCommand(synth.Id)]);

        Resolver oracleResolver = new(synth.Id), originalResolver = new(synth.Id);
        SampleSound data = new(sample, new MemorySampleData(Rate, 1,
            wave.Select(value => value / 32768f).ToArray()));
        oracleResolver.AddSample(sampleId, data);
        originalResolver.AddSample(sampleId, data);
        FmSynthSound Author(FmSoundSourceNode authored, Resolver resolver)
            => new(new FmSynthGraph(synth.Graph.Nodes.Select(node =>
                node.Id == id ? (FmSynthNode)authored : node), id),
                resolver, soundResolver: resolver, enableLiveMultiExpansion: true);
        FmSynthSound oracleSound = Author(initial, oracleResolver);
        oracleResolver.Sound = oracleSound;
        originalResolver.Sound = Author(initial, originalResolver);
        PlaybackSession oracle = IndependentSession(config, oracleResolver);
        PlaybackSession original = IndependentSession(config, originalResolver);
        StartIndependent(oracle, synth.Id);
        StartIndependent(original, synth.Id);

        float[] initialPcm = backend.Render(17);
        float[] initialOracle = new float[17];
        oracle.Render(0, 17, initialOracle);
        original.Render(0, 17, new float[17]);
        for (int j = 0; j < initialPcm.Length; j++)
            Assert.That(initialPcm[j], Is.EqualTo(initialOracle[j])
                .Within(3e-5f), $"source initial PCM frame {j}");
        PlaybackSnapshotInfo frozenBase = transport.CurrentPlaybackSnapshot;
        uint audio = workspace.Document.AudioRevision;
        uint document = workspace.Document.DocumentRevision;
        List<Task> pending = [];
        int changed = 0, callbacks = 0;
        FmSynthEditorControl editor = new(workspace, synth, () => { },
            _ => changed++,
            publishSynthEditAsync: () =>
            {
                callbacks++;
                Task task = transport.PublishSynthEditAsync(
                    workspace.Document, synth.Id);
                pending.Add(task);
                return task;
            });
        StackPanel inspector = Inspector(editor);
        TextBox frequency = Field<TextBox>(inspector, "Frequency Hz");
        CheckBox fixedPitch = inspector.Children.OfType<CheckBox>()
            .Single(box => Equals(box.Content, "Constant"));
        CheckBox exponential = inspector.Children.OfType<CheckBox>()
            .Single(box => Equals(box.Content,
                "Exponential multiplier (semitones)"));
        int frame = 17, operations = 0;

        async Task Change(Action edit, FmSoundSourceNode authored, bool audible)
        {
            edit();
            await Task.WhenAll(pending.ToArray());
            operations++;
            FmSoundSourceNode committed = synth.Graph.Nodes
                .OfType<FmSoundSourceNode>().Single();
            Assert.Multiple(() =>
            {
                Assert.That(SameSource(committed, authored), Is.True);
                Assert.That(workspace.Document.AudioRevision,
                    Is.EqualTo(audio + operations));
                Assert.That(workspace.Document.DocumentRevision,
                    Is.EqualTo(document + operations));
                Assert.That(changed, Is.EqualTo(operations));
                Assert.That(callbacks, Is.EqualTo(operations));
                Assert.That(factory.Preparations.Count, Is.EqualTo(operations * 2));
                Assert.That(factory.Preparations.Skip((operations - 1) * 2)
                    .Select(p => p.Id), Is.EqualTo(new[] { synth.Id, parent.Id }));
                Assert.That(SameSource(
                    factory.Preparations[(operations - 1) * 2].Source,
                    authored), Is.True, "frozen worker Source frequency and modes");
                Assert.That(factory.Preparations.Skip((operations - 1) * 2)
                    .All(p => p.AudioRevision == audio + operations), Is.True);
                Assert.That(backend.OpenCount, Is.EqualTo(1));
            });

            FmSynthSound revised = Author(authored, oracleResolver);
            oracle.AdoptLiveSynthParameters(synth.Id, oracleSound, revised);
            oracleResolver.Sound = revised;
            oracleSound = revised;
            Assert.That(oracle.CountOlderAudioVoices(), Is.Zero);
            float[] observed = backend.Render(141);
            float[] expectedPcm = new float[141], unchanged = new float[141];
            oracle.Render(frame, 141, expectedPcm);
            original.Render(frame, 141, unchanged);
            for (int j = 0; j < observed.Length; j++)
                Assert.That(observed[j], Is.EqualTo(expectedPcm[j])
                    .Within(4e-5f),
                    $"C2(k) Source production edit {operations} at {frame + j}");
            if (audible)
                Assert.That(observed.Zip(unchanged,
                    (a, b) => Math.Abs(a - b)).Max(), Is.GreaterThan(0.03),
                    "a pitch/mode edit must alter already-sounding Sample PCM");
            Assert.Multiple(() =>
            {
                Assert.That(factory.OlderAudioCount, Is.Zero);
                Assert.That(transport.CurrentPlaybackSnapshot.AudioRevision,
                    Is.EqualTo(frozenBase.AudioRevision));
                Assert.That(backend.OpenCount, Is.EqualTo(1));
            });
            frame += 141;
        }

        await Change(() => Enter(frequency, (neutral * 1.5).ToString("R",
                CultureInfo.InvariantCulture)),
            new FmSoundSourceNode(id, sampleId, neutral * 1.5,
                multiplierNodeId: multiplier), true);
        await Change(() => exponential.IsChecked = true,
            new FmSoundSourceNode(id, sampleId, neutral * 1.5,
                multiplierNodeId: multiplier, exponentialMultiplier: true),
            true);
        await Change(() => fixedPitch.IsChecked = true,
            new FmSoundSourceNode(id, sampleId, neutral * 1.5,
                multiplierNodeId: multiplier, exponentialMultiplier: true,
                constant: true), false);

        Enter(frequency, (neutral * 1.5).ToString("R",
            CultureInfo.InvariantCulture));
        exponential.IsChecked = true;
        fixedPitch.IsChecked = true;
        await transport.PublishSynthEditAsync(workspace.Document, synth.Id);
        Assert.Multiple(() =>
        {
            Assert.That(workspace.Document.AudioRevision, Is.EqualTo(audio + 3));
            Assert.That(callbacks, Is.EqualTo(3));
            Assert.That(factory.Preparations.Count, Is.EqualTo(6));
            Assert.That(factory.OlderAudioCount, Is.Zero);
        });
    }


    [AvaloniaTest]
    public async Task MultiInspectorControlsPublishWorkerPreparedHeldProductionPcm()
    {
        // C2(l): join the real Multi inspector to the frozen production
        // transport and the SAME running source's PCM, explicitly driving
        // the producer/worker mailbox around each edit. An independent,
        // separately authored Multi/Session owns its own onset histories.
        DocumentWorkspace workspace = new();
        FmSynthDefinition synth = FmSynthDocumentEditor.CreateFmSynth(
            workspace, "C2(l) Multi production");
        int oscillator = FmSynthDocumentEditor.AddOscillatorNode(
            workspace, synth, FmOscillatorWaveform.Sine, 83, 70, 80);
        int id = FmSynthDocumentEditor.AddMultiNode(
            workspace, synth, 310, 100);
        FmSynthDocumentEditor.ConnectNodes(workspace, synth, oscillator, id, 0);
        FmSynthDocumentEditor.SetOutputNode(workspace, synth, id);
        FmMultiNode authored = new(id, oscillator,
            detune: 15, voiceCount: 2, randomizePhase: false,
            gainCompensation: true);
        FmSynthDocumentEditor.UpdateNode(workspace, synth, authored);
        FmSynthDefinition parent = MakeDependentParent(
            workspace, synth, "C2(l) dependent parent");

        RenderConfiguration config = Mono();
        TrackingFactory factory = new(config, synth.Id);
        ManualBackend backend = new();
        using SongPlaybackTransport transport = new(backend, factory);
        await transport.SendLiveEventAsync(workspace.Document,
            ChannelTarget.Physical(0), [new StartNoteCommand(synth.Id)]);
        Resolver expectedResolver = new(synth.Id),
            originalResolver = new(synth.Id);
        FmSynthSound Build(FmMultiNode layout, Resolver resolver)
            => new(new FmSynthGraph(synth.Graph.Nodes.Select(node =>
                node.Id == id ? (FmSynthNode)layout : node), id),
                resolver, soundResolver: resolver,
                reserveMultiHeadroom: true, enableLiveMultiExpansion: true);
        FmSynthSound expectedSound = Build(authored, expectedResolver);
        expectedResolver.Sound = expectedSound;
        originalResolver.Sound = Build(authored, originalResolver);
        PlaybackSession oracle = IndependentSession(config, expectedResolver);
        PlaybackSession original = IndependentSession(config, originalResolver);
        StartIndependent(oracle, synth.Id);
        StartIndependent(original, synth.Id);
        float[] initial = backend.Render(12), initialOracle = new float[12];
        oracle.Render(0, 12, initialOracle);
        original.Render(0, 12, new float[12]);
        Assert.That(initial, Is.EqualTo(initialOracle).Within(3e-5f),
            "production and independent Multi begin at the same sound clock");

        PlaybackSnapshotInfo baseSnapshot = transport.CurrentPlaybackSnapshot;
        uint audio = workspace.Document.AudioRevision;
        uint document = workspace.Document.DocumentRevision;
        int changes = 0, callbacks = 0, operations = 0, frame = 12;
        List<Task> pending = [];
        FmSynthEditorControl editor = new(workspace, synth,
            () => { }, _ => changes++,
            publishSynthEditAsync: () =>
            {
                callbacks++;
                Task task = transport.PublishSynthEditAsync(
                    workspace.Document, synth.Id);
                pending.Add(task);
                return task;
            });
        StackPanel inspector = Inspector(editor);
        ComboBox presets = Field<ComboBox>(inspector, "Presets");
        ComboBox distribution = Field<ComboBox>(inspector, "Distribution");
        TextBox detune = Field<TextBox>(inspector, "Detune (total cents)");
        TextBox count = Field<TextBox>(inspector, "Voice Count");
        TextBox components = Field<TextBox>(inspector, "Components");
        TextBox cycle = Field<TextBox>(inspector,
            "Cycle Length (samples at C4 / 48 kHz)");
        CheckBox randomize = inspector.Children.OfType<CheckBox>()
            .Single(x => Equals(x.Content, "Randomize Phase"));
        CheckBox gain = inspector.Children.OfType<CheckBox>()
            .Single(x => Equals(x.Content, "Gain Compensation"));
        CheckBox overrideCycle = inspector.Children.OfType<CheckBox>()
            .Single(x => Equals(x.Content, "Override cycle"));
        IFirPreparationAudioSource preparing =
            (IFirPreparationAudioSource)backend.Source!;
        using FirPreparationScheduler scheduler = new();

        async Task Change(string label, Action edit, FmMultiNode next,
            bool requiresWorker, bool mustDiffer = false)
        {
            uint previousRevision = workspace.Document.AudioRevision;
            edit();
            await Task.WhenAll(pending.ToArray())
                .WaitAsync(TimeSpan.FromSeconds(8));
            operations++;
            FmMultiNode committed = synth.Graph.Nodes.OfType<FmMultiNode>()
                .Single();
            Assert.Multiple(() =>
            {
                Assert.That(SameMulti(committed, next), Is.True,
                    label + ": actual inspector fields");
                Assert.That(workspace.Document.AudioRevision,
                    Is.EqualTo(previousRevision + 1), label + ": semantic audio revision");
                Assert.That(workspace.Document.AudioRevision,
                    Is.EqualTo(audio + operations));
                Assert.That(workspace.Document.DocumentRevision,
                    Is.EqualTo(document + operations));
                Assert.That(changes, Is.EqualTo(operations));
                Assert.That(callbacks, Is.EqualTo(operations));
                Assert.That(factory.Preparations.Count, Is.EqualTo(operations * 2));
                Assert.That(factory.Preparations.Skip((operations - 1) * 2)
                    .Select(x => x.Id), Is.EqualTo(new[] { synth.Id, parent.Id }),
                    label + ": child-before-parent frozen order");
                Assert.That(SameMulti(factory.Preparations[(operations - 1) * 2].Multi,
                    next), Is.True, label + ": worker snapshot");
                Assert.That(factory.Preparations.Skip((operations - 1) * 2)
                    .All(x => x.AudioRevision == audio + operations), Is.True);
                Assert.That(backend.OpenCount, Is.EqualTo(1));
            });

            FmSynthSound revised = Build(next, expectedResolver);
            oracle.AdoptLiveSynthParameters(synth.Id, expectedSound, revised);
            expectedSound = revised;
            expectedResolver.Sound = revised;
            authored = next;

            // Render one boundary to apply the transport revision. For
            // expansion/rephase, old children must remain audible until the
            // independently scheduled worker produces fresh onsets.
            preparing.AdoptPreparedFirControls();
            oracle.AdoptPreparedFirControls();
            float[] actualFirst = backend.Render(1);
            float[] expectedFirst = new float[1];
            oracle.Render(frame, 1, expectedFirst);
            Assert.That(actualFirst[0], Is.EqualTo(expectedFirst[0])
                .Within(5e-5f), label + ": initial PCM boundary");
            long before = scheduler.CompletedMultiExpansionCount;
            preparing.QueuePendingFirControls(scheduler);
            oracle.QueuePendingFirControls(scheduler);
            if (requiresWorker)
                Assert.That(SpinWait.SpinUntil(() =>
                    scheduler.CompletedMultiExpansionCount >= before + 2,
                    TimeSpan.FromSeconds(8)), Is.True,
                    label + ": separately prepared production and oracle Multi onsets");
            Assert.That(scheduler.Fault, Is.Null);
            preparing.AdoptPreparedFirControls();
            oracle.AdoptPreparedFirControls();

            float[] observed = backend.Render(140);
            float[] expectedPcm = new float[140], unchanged = new float[141];
            oracle.Render(frame + 1, 140, expectedPcm);
            original.Render(frame, 141, unchanged);
            // The never-edited reference advances on the same clock, so
            // the preset-onset check is nonvacuous.
            for (int j = 0; j < observed.Length; j++)
            {
                Assert.That(float.IsFinite(observed[j]), Is.True,
                    label + ": output must remain finite");
                Assert.That(observed[j], Is.EqualTo(expectedPcm[j])
                    .Within(7e-5f), label + ": held output frame " + (frame + 1 + j));
            }
            if (mustDiffer)
                Assert.That(observed.Zip(unchanged.Skip(1),
                    (revisedPcm, oldPcm) => Math.Abs(revisedPcm - oldPcm)).Max(),
                    Is.GreaterThan(0.04), label + ": real Multi onset must sound different");
            Assert.Multiple(() =>
            {
                Assert.That(factory.OlderAudioCount, Is.Zero,
                    label + ": compatible pending/onset revision is current");
                Assert.That(oracle.CountOlderAudioVoices(), Is.Zero);
                Assert.That(backend.OpenCount, Is.EqualTo(1));
                Assert.That(transport.CurrentPlaybackSnapshot.AudioRevision,
                    Is.EqualTo(baseSnapshot.AudioRevision));
                Assert.That(scheduler.Fault, Is.Null);
            });
            frame += 141;
        }

        // Real preset action enables randomized phase on the prior
        // Spread(2) sound. Cycle edits then require new worker onsets.
        await Change("Unison preset",
            () => presets.SelectedItem = "Unison",
            authored.WithPreset("Unison"), true, mustDiffer: true);
        Assert.That(presets.SelectedIndex, Is.Zero,
            "preset is an action, not a sticky ComboBox value");
        double defaultCycle = FmMultiNode.ReferenceSampleRate
            / FmMultiNode.NeutralFrequencyHz;
        await Change("Cycle override enabled",
            () => overrideCycle.IsChecked = true,
            authored.With(authored.InputNodeId, authored.Distribution,
                authored.Detune, authored.VoiceCount,
                authored.HarmonicComponents, authored.ChordComponents,
                authored.RandomizePhase, authored.GainCompensation,
                defaultCycle), true);
        await Change("Cycle override length",
            () => Enter(cycle, "1120"),
            authored.With(authored.InputNodeId, authored.Distribution,
                authored.Detune, authored.VoiceCount,
                authored.HarmonicComponents, authored.ChordComponents,
                authored.RandomizePhase, authored.GainCompensation, 1120), true);
        await Change("Cycle override disabled",
            () => overrideCycle.IsChecked = false,
            authored.With(authored.InputNodeId, authored.Distribution,
                authored.Detune, authored.VoiceCount,
                authored.HarmonicComponents, authored.ChordComponents,
                authored.RandomizePhase, authored.GainCompensation, null), true);

        await Change("Spread detune", () => Enter(detune, "30"),
            authored.With(authored.InputNodeId, authored.Distribution,
                30, authored.VoiceCount, authored.HarmonicComponents,
                authored.ChordComponents, authored.RandomizePhase,
                authored.GainCompensation, authored.CycleLength), false);
        // The production compiler reserves Multi headroom. Growing 2→3
        // and later 3→4 within that capacity must use those already
        // prepared slots, not fabricate a requirement for another worker
        // completion. Phase-policy/cycle changes above still require the
        // genuine detached onset preparation path.
        await Change("Spread voice growth", () => Enter(count, "3"),
            authored.With(authored.InputNodeId, authored.Distribution,
                authored.Detune, 3, authored.HarmonicComponents,
                authored.ChordComponents, authored.RandomizePhase,
                authored.GainCompensation, authored.CycleLength), false);
        await Change("Gain compensation", () => gain.IsChecked = false,
            authored.With(authored.InputNodeId, authored.Distribution,
                authored.Detune, authored.VoiceCount, authored.HarmonicComponents,
                authored.ChordComponents, authored.RandomizePhase,
                false, authored.CycleLength), false);
        await Change("Randomize phase", () => randomize.IsChecked = false,
            authored.With(authored.InputNodeId, authored.Distribution,
                authored.Detune, authored.VoiceCount, authored.HarmonicComponents,
                authored.ChordComponents, false,
                authored.GainCompensation, authored.CycleLength), true);

        await Change("Harmonic distribution",
            () => distribution.SelectedItem = FmMultiDistribution.Harmonic,
            authored.WithDistribution(FmMultiDistribution.Harmonic), false);
        await Change("Harmonic components",
            () => Enter(components, "1 3/2 2"),
            authored.With(authored.InputNodeId, authored.Distribution,
                authored.Detune, authored.VoiceCount, "1 3/2 2",
                authored.ChordComponents, authored.RandomizePhase,
                authored.GainCompensation, authored.CycleLength), false);
        await Change("Chord distribution",
            () => distribution.SelectedItem = FmMultiDistribution.Chord,
            authored.WithDistribution(FmMultiDistribution.Chord), false);
        await Change("Chord components",
            () => Enter(components, "0 3 7"),
            authored.With(authored.InputNodeId, authored.Distribution,
                authored.Detune, authored.VoiceCount,
                authored.HarmonicComponents, "0 3 7", authored.RandomizePhase,
                authored.GainCompensation, authored.CycleLength), false);
        await Change("Chorus preset",
            () => presets.SelectedItem = "Chorus",
            authored.WithPreset("Chorus"), false);
        Assert.That(presets.SelectedIndex, Is.Zero);

        // Submitting unchanged Multi drafts or re-requesting the same
        // document revision must not schedule a phantom onset/revision.
        Enter(detune, "25");
        Enter(count, "4");
        await transport.PublishSynthEditAsync(workspace.Document, synth.Id);
        Assert.Multiple(() =>
        {
            Assert.That(operations, Is.EqualTo(13));
            Assert.That(callbacks, Is.EqualTo(13));
            Assert.That(workspace.Document.AudioRevision, Is.EqualTo(audio + 13));
            Assert.That(factory.Preparations.Count, Is.EqualTo(26));
            Assert.That(factory.OlderAudioCount, Is.Zero);
            Assert.That(backend.OpenCount, Is.EqualTo(1));
            Assert.That(scheduler.Fault, Is.Null);
        });
    }

    private static bool SameMulti(FmMultiNode? actual, FmMultiNode expected)
        => actual is not null
            && actual.Id == expected.Id
            && actual.InputNodeId == expected.InputNodeId
            && actual.Distribution == expected.Distribution
            && actual.Detune == expected.Detune
            && actual.VoiceCount == expected.VoiceCount
            && actual.HarmonicComponents == expected.HarmonicComponents
            && actual.ChordComponents == expected.ChordComponents
            && actual.RandomizePhase == expected.RandomizePhase
            && actual.GainCompensation == expected.GainCompensation
            && actual.CycleLength == expected.CycleLength;


    [AvaloniaTest]
    public async Task FilterDefinitionSelectorKeepsCompatibleHistoryAndRefusesOtherDefinitions()
    {
        // C2(m): a new Filter *identity* is not a compatible live plan,
        // even when its OnePole stage IDs, kinds and defaults are identical.
        // Within the SAME identity, the inspector's replacement instance
        // may reset per-use numeric overrides and retune continuing IIR.
        DocumentWorkspace workspace = new();
        FilterDefinition Filter(string name)
        {
            FilterDefinition definition =
                FilterDocumentEditor.CreateFilter(workspace, name);
            FilterStageDefinition old = definition.GetStage(1);
            FilterStageDefinition revised = old.WithDefaults([
                new KeyValuePair<string, FilterParameterValue>(
                    "CutoffHz", FilterParameterValue.FromNumber(165)),
            ]);
            FilterDocumentEditor.ApplyStage(workspace, definition, 1, old,
                revised.Name, revised.Kind, revised.DefaultParameters);
            return definition;
        }
        FilterDefinition a = Filter("C2(m) Filter A");
        FilterDefinition b = Filter("C2(m) Filter B");
        Assert.That(a.GetStage(1).DefaultParameters,
            Is.EquivalentTo(b.GetStage(1).DefaultParameters),
            "different Filter IDs are deliberately given identical stage plans");

        FmSynthDefinition synth = FmSynthDocumentEditor.CreateFmSynth(
            workspace, "C2(m) Filter selector transport");
        int oscillator = FmSynthDocumentEditor.AddOscillatorNode(
            workspace, synth, FmOscillatorWaveform.Sine, 91, 70, 90);
        int filterId = FmSynthDocumentEditor.AddFilterNode(
            workspace, synth, 310, 90);
        FilterInstance initialUse = new(a);
        initialUse.SetStageParameter(1, "CutoffHz",
            FilterParameterValue.FromNumber(45));
        FmSynthDocumentEditor.SetFilterConfiguration(
            workspace, synth, filterId, initialUse);
        FmSynthDocumentEditor.ConnectNodes(
            workspace, synth, oscillator, filterId, 0);
        FmSynthDocumentEditor.SetOutputNode(workspace, synth, filterId);
        FmSynthDefinition parent = MakeDependentParent(
            workspace, synth, "C2(m) parent");

        RenderConfiguration config = Mono();
        TrackingFactory factory = new(config, synth.Id);
        ManualBackend backend = new();
        using SongPlaybackTransport transport = new(backend, factory);
        await transport.SendLiveEventAsync(workspace.Document,
            ChannelTarget.Physical(0), [new StartNoteCommand(synth.Id)]);

        // Separate immutable graph authoring and independent IIR state for
        // the PCM oracle, never the inspector callback or production source.
        FmSynthGraph baseline = synth.Graph;
        Resolver oracleResolver = new(synth.Id), originalResolver = new(synth.Id);
        FmSynthSound Author(FilterInstance? configUse, Resolver resolver)
            => new(new FmSynthGraph(baseline.Nodes.Select(node =>
                node.Id == filterId
                    ? (FmSynthNode)new FmFilterNode(filterId, oscillator, configUse)
                    : node), filterId),
                resolver, soundResolver: resolver, enableLiveMultiExpansion: true);
        FmSynthSound oracleSound = Author(initialUse, oracleResolver);
        oracleResolver.Sound = oracleSound;
        originalResolver.Sound = Author(initialUse, originalResolver);
        PlaybackSession oracle = IndependentSession(config, oracleResolver);
        PlaybackSession unchanged = IndependentSession(config, originalResolver);
        StartIndependent(oracle, synth.Id);
        StartIndependent(unchanged, synth.Id);

        int frame = 0;
        void Compare(int count, string stage, bool audibleDifference = false)
        {
            float[] actual = backend.Render(count);
            float[] reference = new float[count], original = new float[count];
            oracle.Render(frame, count, reference);
            unchanged.Render(frame, count, original);
            for (int j = 0; j < count; j++)
                Assert.That(actual[j], Is.EqualTo(reference[j])
                    .Within(6e-5f), stage + " held PCM at " + (frame + j));
            if (audibleDifference)
                Assert.That(actual.Zip(original,
                    (x, y) => Math.Abs(x - y)).Max(),
                    Is.GreaterThan(0.025),
                    stage + ": same-definition numeric change must be audible");
            Assert.That(backend.OpenCount, Is.EqualTo(1),
                stage + ": no output session restart");
            frame += count;
        }
        Compare(35, "starting overridden A");

        PlaybackSnapshotInfo originalSnapshot =
            transport.CurrentPlaybackSnapshot;
        uint audio = workspace.Document.AudioRevision;
        uint document = workspace.Document.DocumentRevision;
        int callbacks = 0, changed = 0;
        List<Task> pending = [];
        FmSynthEditorControl editor = new(workspace, synth, () => { },
            _ => changed++,
            publishSynthEditAsync: () =>
            {
                callbacks++;
                Task task = transport.PublishSynthEditAsync(
                    workspace.Document, synth.Id);
                pending.Add(task);
                return task;
            });
        ComboBox Selector()
        {
            StackPanel row = Field<StackPanel>(
                Inspector(editor), "Shared Filter definition");
            return (ComboBox)row.Children[0];
        }
        object Choice(ComboBox selector, string name)
            => selector.Items.Cast<object>().Single(item =>
                item.ToString()!.Contains(name, StringComparison.Ordinal));

        // A temporarily cleared ComboBox selection is NOT a model edit.
        // Rechoosing the original Filter creates a new default instance
        // (same DefinitionId/stage topology), thereby removing the initial
        // per-use cutoff override 45 and restoring the shared default 165.
        ComboBox selector = Selector();
        object aChoice = Choice(selector, "C2(m) Filter A");
        Assert.That(selector.SelectedItem, Is.EqualTo(aChoice));
        selector.SelectedItem = null;
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(audio));
        selector.SelectedItem = aChoice;
        await Task.WhenAll(pending.ToArray()).WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Multiple(() =>
        {
            Assert.That(synth.Graph.Nodes.OfType<FmFilterNode>().Single()
                .Configuration!.GetStageParameters(1).GetParameter("CutoffHz")
                .NumericValue, Is.EqualTo(165));
            Assert.That(workspace.Document.AudioRevision, Is.EqualTo(audio + 1));
            Assert.That(workspace.Document.DocumentRevision,
                Is.EqualTo(document + 1));
            Assert.That(callbacks, Is.EqualTo(1));
            Assert.That(factory.Preparations.Select(x => x.Id),
                Is.EqualTo(new[] { synth.Id, parent.Id }));
            Assert.That(factory.Preparations[0].FilterDefinitionId,
                Is.EqualTo(a.Id));
            Assert.That(factory.Preparations[0].FilterCutoffHz,
                Is.EqualTo(165));
            Assert.That(factory.Preparations.All(x =>
                x.AudioRevision == audio + 1), Is.True);
        });
        FmSynthSound resetToDefault = Author(new FilterInstance(a), oracleResolver);
        oracle.AdoptLiveSynthParameters(synth.Id, oracleSound, resetToDefault);
        oracleResolver.Sound = resetToDefault;
        oracleSound = resetToDefault;
        Compare(900, "same Filter identity numeric retune", audibleDifference: true);
        Assert.Multiple(() =>
        {
            Assert.That(factory.OlderAudioCount, Is.Zero);
            Assert.That(oracle.CountOlderAudioVoices(), Is.Zero);
            Assert.That(transport.CurrentPlaybackSnapshot.AudioRevision,
                Is.EqualTo(originalSnapshot.AudioRevision));
        });

        // The other definition has exactly the same stage identity/layout
        // and default coefficients, but a DIFFERENT persisted Filter ID.
        // It must not steal IIR history from the earlier generation.
        selector = Selector();
        selector.SelectedItem = Choice(selector, "C2(m) Filter B");
        await Task.WhenAll(pending.ToArray()).WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Multiple(() =>
        {
            Assert.That(workspace.Document.AudioRevision, Is.EqualTo(audio + 2));
            Assert.That(workspace.Document.DocumentRevision,
                Is.EqualTo(document + 2));
            Assert.That(callbacks, Is.EqualTo(2));
            Assert.That(factory.Preparations.Select(x => x.Id),
                Is.EqualTo(new[] { synth.Id, parent.Id, synth.Id, parent.Id }));
            Assert.That(factory.Preparations[2].FilterDefinitionId,
                Is.EqualTo(b.Id));
            Assert.That(factory.Preparations[2].FilterCutoffHz,
                Is.EqualTo(165));
        });
        FmSynthSound newB = Author(new FilterInstance(b), oracleResolver);
        oracle.AdoptLiveSynthParameters(synth.Id, oracleSound, newB);
        oracleResolver.Sound = newB;
        oracleSound = newB;
        Compare(96, "equivalent stage plan but different Filter ID");
        Assert.Multiple(() =>
        {
            Assert.That(factory.OlderAudioCount, Is.EqualTo(1),
                "definition identity mismatch is deliberate older generation");
            Assert.That(oracle.CountOlderAudioVoices(), Is.EqualTo(1));
        });

        // New physical note gets the selected B; old A continues its own
        // history while both mix through the original session.
        await transport.SendLiveEventAsync(workspace.Document,
            ChannelTarget.Physical(1), [new StartNoteCommand(synth.Id)]);
        oracle.ApplyLiveEvent(ChannelTarget.Physical(1),
            [new StartNoteCommand(synth.Id)]);
        Assert.That(oracle.GetChannelState(1).CurrentVoice!.Sound,
            Is.SameAs(newB));
        Compare(96, "new B note plus retained A");
        Assert.That(factory.OlderAudioCount, Is.EqualTo(1));

        // Removing the selected Filter is also incompatible for existing
        // IIR processing. New notes bypass, but old A and B remain audible
        // and independently count as older until each actually retires.
        selector = Selector();
        object none = selector.Items.Cast<object>().ElementAt(1);
        Assert.That(((ComboBoxItem)none).Content, Is.EqualTo("(None)"));
        selector.SelectedItem = none;
        await Task.WhenAll(pending.ToArray()).WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Multiple(() =>
        {
            Assert.That(synth.Graph.Nodes.OfType<FmFilterNode>().Single()
                .Configuration, Is.Null);
            Assert.That(workspace.Document.AudioRevision, Is.EqualTo(audio + 3));
            Assert.That(workspace.Document.DocumentRevision,
                Is.EqualTo(document + 3));
            Assert.That(callbacks, Is.EqualTo(3));
            Assert.That(factory.Preparations.Select(x => x.Id),
                Is.EqualTo(new[] { synth.Id, parent.Id, synth.Id, parent.Id,
                    synth.Id, parent.Id }));
            Assert.That(factory.Preparations[4].FilterDefinitionId, Is.Null);
        });
        FmSynthSound withoutFilter = Author(null, oracleResolver);
        oracle.AdoptLiveSynthParameters(synth.Id, newB, withoutFilter);
        oracleResolver.Sound = withoutFilter;
        Compare(96, "removed Filter preserves older A and B");
        Assert.Multiple(() =>
        {
            Assert.That(factory.OlderAudioCount, Is.EqualTo(2));
            Assert.That(oracle.CountOlderAudioVoices(), Is.EqualTo(2));
        });
        await transport.SendLiveEventAsync(workspace.Document,
            ChannelTarget.Physical(2), [new StartNoteCommand(synth.Id)]);
        oracle.ApplyLiveEvent(ChannelTarget.Physical(2),
            [new StartNoteCommand(synth.Id)]);
        Assert.That(oracle.GetChannelState(2).CurrentVoice!.Sound,
            Is.SameAs(withoutFilter));
        Compare(96, "new bypass note plus older A and B");

        // New... cannot open a modal name prompt in an unattached editor.
        // The selector must revert without creating a Filter or publishing
        // a fourth semantic revision.
        selector = Selector();
        selector.SelectedIndex = 0;
        await Task.Yield();
        Assert.Multiple(() =>
        {
            Assert.That(synth.Graph.Nodes.OfType<FmFilterNode>().Single()
                .Configuration, Is.Null);
            Assert.That(workspace.Document.AudioRevision, Is.EqualTo(audio + 3));
            Assert.That(callbacks, Is.EqualTo(3));
            Assert.That(factory.Preparations.Count, Is.EqualTo(6));
        });

        // Retirement is the only event allowed to clear the sticky older
        // currency. The newer bypass note remains audible after both cuts.
        foreach (int channel in new[] { 0, 1 })
        {
            await transport.SendLiveEventAsync(workspace.Document,
                ChannelTarget.Physical(channel), [new NoteCutCommand()]);
            oracle.ApplyLiveEvent(ChannelTarget.Physical(channel),
                [new NoteCutCommand()]);
        }
        Compare(40, "both original Filter generations retired");
        Assert.Multiple(() =>
        {
            Assert.That(factory.OlderAudioCount, Is.Zero);
            Assert.That(oracle.CountOlderAudioVoices(), Is.Zero);
            Assert.That(callbacks, Is.EqualTo(3));
            Assert.That(changed, Is.EqualTo(3));
            Assert.That(backend.OpenCount, Is.EqualTo(1));
            Assert.That(transport.CurrentPlaybackSnapshot.AudioRevision,
                Is.EqualTo(originalSnapshot.AudioRevision));
        });
        await transport.PublishSynthEditAsync(workspace.Document, synth.Id);
        Assert.That(factory.Preparations.Count, Is.EqualTo(6));
    }


    [AvaloniaTest]
    public async Task MultistageBiquadCombFirInspectorRetunesHeldProductionPcm()
    {
        // C2(n): actual per-use stage editor -> frozen child-before-parent
        // transport -> real continuing output, compared against separately
        // authored serial Biquad/Comb/FIR state histories. Each new FIR plan
        // is prepared at publication, outside the producer's PCM callback.
        const int rate = 8000;
        DocumentWorkspace workspace = new();
        FilterDefinition definition = FilterDocumentEditor.CreateFilter(
            workspace, "C2(n) serial Biquad Comb FIR");
        FilterStageDefinition one = definition.GetStage(1);
        FilterDocumentEditor.ApplyStage(workspace, definition, 1, one,
            one.Name, FilterKind.BiquadPeaking,
            new FilterStageDefinition(1, one.Name, FilterKind.BiquadPeaking)
                .DefaultParameters);
        FilterStageDefinition comb = FilterDocumentEditor.AddStage(
            workspace, definition, FilterKind.CombFeedforward);
        FilterStageDefinition fir = FilterDocumentEditor.AddStage(
            workspace, definition, FilterKind.FirLowPass);
        Assert.That(definition.Stages.Select(x => x.Kind), Is.EqualTo(new[]
        {
            FilterKind.BiquadPeaking, FilterKind.CombFeedforward,
            FilterKind.FirLowPass,
        }));
        FmSynthDefinition synth = FmSynthDocumentEditor.CreateFmSynth(
            workspace, "C2(n) serial inspector");
        int osc = FmSynthDocumentEditor.AddOscillatorNode(
            workspace, synth, FmOscillatorWaveform.Sine, 231, 75, 85);
        int id = FmSynthDocumentEditor.AddFilterNode(
            workspace, synth, 320, 100);
        FmSynthDocumentEditor.SetFilterConfiguration(
            workspace, synth, id, new FilterInstance(definition));
        FmSynthDocumentEditor.ConnectNodes(workspace, synth, osc, id, 0);
        FmSynthDocumentEditor.SetOutputNode(workspace, synth, id);
        FmSynthDefinition parent = MakeDependentParent(workspace, synth,
            "C2(n) dependent Synth");

        RenderConfiguration configuration = new(rate, [
            new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0),
        ]);
        TrackingFactory factory = new(configuration, synth.Id);
        ManualBackend backend = new();
        using SongPlaybackTransport transport = new(backend, factory);
        await transport.SendLiveEventAsync(workspace.Document,
            ChannelTarget.Physical(0), [new StartNoteCommand(synth.Id)]);

        FmSynthGraph initialGraph = synth.Graph;
        Resolver resolver = new(synth.Id), originalResolver = new(synth.Id);
        FmSynthSound Author(FilterInstance use, Resolver target)
        {
            // A separate immutable plan and separate IIR/FIR histories:
            // this does not import production's prepared DSP state.
            PreparedToneFilterStageFactory plans = new(rate);
            plans.Prewarm(use);
            plans.Freeze();
            return new(new FmSynthGraph(initialGraph.Nodes.Select(node =>
                node.Id == id
                    ? (FmSynthNode)new FmFilterNode(id, osc, use)
                    : node), id),
                target, soundResolver: target,
                preparedChildFilterFactory: plans,
                enableLiveMultiExpansion: true);
        }
        FilterInstance authored = new(definition);
        FmSynthSound referenceSound = Author(authored, resolver);
        resolver.Sound = referenceSound;
        originalResolver.Sound = Author(new FilterInstance(definition),
            originalResolver);
        PlaybackSession reference = IndependentSession(configuration, resolver);
        PlaybackSession original = IndependentSession(
            configuration, originalResolver);
        StartIndependent(reference, synth.Id);
        StartIndependent(original, synth.Id);
        int frame = 0;
        void Compare(int count, string label, bool differs)
        {
            float[] observed = backend.Render(count), expected = new float[count];
            float[] untouched = new float[count];
            reference.Render(frame, count, expected);
            original.Render(frame, count, untouched);
            for (int i = 0; i < count; i++)
            {
                Assert.That(float.IsFinite(observed[i]), Is.True,
                    label + ": finite PCM");
                Assert.That(observed[i], Is.EqualTo(expected[i]).Within(7e-4f),
                    label + ": production vs independent serial Filter frame " + (frame + i));
            }
            if (differs)
                Assert.That(observed.Zip(untouched,
                    (a, b) => Math.Abs(a - b)).Max(), Is.GreaterThan(0.005),
                    label + ": the committed stage edit must affect PCM");
            Assert.That(backend.OpenCount, Is.EqualTo(1));
            frame += count;
        }
        Compare(280, "initial serial Filter", false);
        PlaybackSnapshotInfo baseSnapshot = transport.CurrentPlaybackSnapshot;
        uint audio = workspace.Document.AudioRevision;
        uint document = workspace.Document.DocumentRevision;
        int callbacks = 0, changed = 0, steps = 0;
        List<Task> pending = [];
        FmSynthEditorControl editor = new(workspace, synth,
            () => { }, _ => changed++,
            publishSynthEditAsync: () =>
            {
                callbacks++;
                Task task = transport.PublishSynthEditAsync(
                    workspace.Document, synth.Id);
                pending.Add(task);
                return task;
            });
        FilterInstanceEditorControl embedded = Inspector(editor).Children
            .OfType<FilterInstanceEditorControl>().Single();
        ListBox stages = (ListBox)typeof(FilterInstanceEditorControl)
            .GetField("_stages", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(embedded)!;
        void Edit(int stageIndex, string parameter, string value)
        {
            stages.SelectedIndex = stageIndex;
            var toggles = (Dictionary<string, CheckBox>)
                typeof(FilterInstanceEditorControl).GetField(
                    "_overrideToggles", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .GetValue(embedded)!;
            var fields = (Dictionary<string, Control>)
                typeof(FilterInstanceEditorControl).GetField(
                    "_editors", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .GetValue(embedded)!;
            toggles[parameter].IsChecked = true;
            ((TextBox)fields[parameter]).Text = value;
            typeof(FilterInstanceEditorControl).GetMethod("ApplyDraft",
                BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(embedded, null);
        }
        async Task Change(string label, int stageIndex,
            string parameter, double nextValue)
        {
            Edit(stageIndex, parameter, nextValue.ToString("R",
                CultureInfo.InvariantCulture));
            await Task.WhenAll(pending.ToArray())
                .WaitAsync(TimeSpan.FromSeconds(12));
            steps++;
            Assert.Multiple(() =>
            {
                Assert.That(workspace.Document.AudioRevision,
                    Is.EqualTo(audio + steps), label + " audio revision");
                Assert.That(workspace.Document.DocumentRevision,
                    Is.EqualTo(document + steps), label + " document revision");
                Assert.That(callbacks, Is.EqualTo(steps), label + " publication count");
                Assert.That(changed, Is.GreaterThanOrEqualTo(steps));
                Assert.That(factory.Preparations.Count, Is.EqualTo(steps * 2));
                Assert.That(factory.Preparations.Skip((steps - 1) * 2)
                    .Select(p => p.Id), Is.EqualTo(new[] { synth.Id, parent.Id }),
                    label + " worker child before parent");
                Assert.That(factory.Preparations.Skip((steps - 1) * 2)
                    .All(p => p.AudioRevision == audio + steps), Is.True);
                Assert.That(factory.Preparations[(steps - 1) * 2]
                    .FilterDefinitionId, Is.EqualTo(definition.Id));
                Assert.That(factory.Preparations[(steps - 1) * 2]
                    .FilterStageCount, Is.EqualTo(3));
                Assert.That(backend.OpenCount, Is.EqualTo(1));
            });
            FilterInstance actualUse = synth.Graph.Nodes
                .OfType<FmFilterNode>().Single().Configuration!;
            Assert.That(actualUse.GetStageParameters(stageIndex + 1)
                .GetParameter(parameter).NumericValue,
                Is.EqualTo(nextValue), label + " committed inspector parameter");

            authored = authored.Clone();
            authored.SetStageParameter(stageIndex + 1, parameter,
                FilterParameterValue.FromNumber(nextValue));
            FmSynthSound next = Author(authored, resolver);
            reference.AdoptLiveSynthParameters(
                synth.Id, referenceSound, next);
            resolver.Sound = next;
            referenceSound = next;
            Compare(1300, label, true);
            Assert.Multiple(() =>
            {
                Assert.That(factory.OlderAudioCount, Is.Zero,
                    label + " retains live IIR/FIR state");
                Assert.That(reference.CountOlderAudioVoices(), Is.Zero);
                Assert.That(transport.CurrentPlaybackSnapshot.AudioRevision,
                    Is.EqualTo(baseSnapshot.AudioRevision));
            });
        }

        await Change("BiquadPeaking GainDb", 0, "GainDb", 12);
        await Change("CombFeedforward Mix", 1, "Mix", 0.9);
        await Change("FirLowPass CutoffHz", 2, "CutoffHz", 1500);

        // A real invalid draft and an unchanged re-apply cannot spawn a
        // phantom FIR redesign, audio revision or transport publication.
        Edit(2, "CutoffHz", "NaN");
        Edit(2, "CutoffHz", "1500");
        await transport.PublishSynthEditAsync(workspace.Document, synth.Id);
        Assert.Multiple(() =>
        {
            Assert.That(workspace.Document.AudioRevision, Is.EqualTo(audio + 3));
            Assert.That(callbacks, Is.EqualTo(3));
            Assert.That(factory.Preparations.Count, Is.EqualTo(6));
            Assert.That(factory.OlderAudioCount, Is.Zero);
            Assert.That(backend.OpenCount, Is.EqualTo(1));
        });
    }


    [AvaloniaTest]
    public async Task TailInspectorChangesAlreadyRingingFiniteSampleFilterDeadline()
    {
        // C2(n): a finite Sample has ended but its OnePole still rings.
        // Real inspector Tail edits must change the eligible original
        // output's *exclusive* retirement deadline, not restart its
        // Sample or replace its IIR integrators.
        DocumentWorkspace workspace = new();
        ObjectId sampleId = workspace.Document.AllocateObjectId();
        SampleDefinition sample = SampleDefinition.CreateImported(
            sampleId, "C2(n) finite 4 frame input", "finite.wav",
            FourFrameWave(24576));
        workspace.Document.Add(sample);
        FilterDefinition definition = FilterDocumentEditor.CreateFilter(
            workspace, "C2(n) tail OnePole");
        FilterInstance configured = new(definition);
        configured.SetStageParameter(1, "CutoffHz",
            FilterParameterValue.FromNumber(20));
        FmSynthDefinition synth = FmSynthDocumentEditor.CreateFmSynth(
            workspace, "C2(n) finite-source tail");
        int sourceId = FmSynthDocumentEditor.AddSoundSourceNode(
            workspace, synth, 90, 110, sampleId, FmMultiNode.NeutralFrequencyHz);
        int filterId = FmSynthDocumentEditor.AddFilterNode(
            workspace, synth, 310, 110);
        FmSynthDocumentEditor.SetFilterConfiguration(
            workspace, synth, filterId, configured);
        FmSynthDocumentEditor.ConnectNodes(workspace, synth,
            sourceId, filterId, 0);
        FmSynthDocumentEditor.UpdateNode(workspace, synth,
            synth.Graph.Nodes.OfType<FmFilterNode>().Single().WithTail(0.012));
        FmSynthDocumentEditor.SetOutputNode(workspace, synth, filterId);
        FmSynthDefinition parent = MakeDependentParent(
            workspace, synth, "C2(n) tail dependent Synth");

        RenderConfiguration config = Mono();
        TrackingFactory factory = new(config, synth.Id);
        ManualBackend backend = new();
        using SongPlaybackTransport transport = new(backend, factory);
        await transport.SendLiveEventAsync(workspace.Document,
            ChannelTarget.Physical(0), [new StartNoteCommand(synth.Id)]);

        Resolver oracleResolver = new(synth.Id), untouchedResolver = new(synth.Id);
        SampleSound pcm = new(sample, new MemorySampleData(Rate, 1,
            new float[] { 0.75f, 0.75f, 0.75f, 0.75f }));
        oracleResolver.AddSample(sampleId, pcm);
        untouchedResolver.AddSample(sampleId, pcm);
        FmSynthGraph baseGraph = synth.Graph;
        FmSynthSound Author(double seconds, Resolver resolver)
            => new(new FmSynthGraph(baseGraph.Nodes.Select(node =>
                node.Id == filterId
                    ? (FmSynthNode)new FmFilterNode(filterId, sourceId,
                        configured, tailSeconds: seconds)
                    : node), filterId),
                resolver, soundResolver: resolver, enableLiveMultiExpansion: true);
        FmSynthSound oracleSound = Author(0.012, oracleResolver);
        oracleResolver.Sound = oracleSound;
        untouchedResolver.Sound = Author(0.012, untouchedResolver);
        PlaybackSession oracle = IndependentSession(config, oracleResolver);
        PlaybackSession untouched = IndependentSession(config, untouchedResolver);
        StartIndependent(oracle, synth.Id);
        StartIndependent(untouched, synth.Id);
        int frame = 0;
        float[] Compare(int count, string label)
        {
            float[] actual = backend.Render(count), reference = new float[count],
                unedited = new float[count];
            oracle.Render(frame, count, reference);
            untouched.Render(frame, count, unedited);
            for (int i = 0; i < actual.Length; i++)
                Assert.That(actual[i], Is.EqualTo(reference[i]).Within(4e-5f),
                    label + ": continuing finite Sample/IIR PCM at " + (frame + i));
            frame += count;
            Assert.That(backend.OpenCount, Is.EqualTo(1));
            return actual;
        }
        float[] initial = Compare(7, "4 PCM frames plus initial 3 tail");
        Assert.That(initial[5], Is.GreaterThan(0.02f),
            "OnePole must be audibly ringing after the finite Sample ends");
        PlaybackSnapshotInfo baseSnapshot = transport.CurrentPlaybackSnapshot;

        uint audio = workspace.Document.AudioRevision;
        uint document = workspace.Document.DocumentRevision;
        int changes = 0, callbacks = 0;
        List<Task> pending = [];
        FmSynthEditorControl editor = new(workspace, synth,
            () => { }, _ => changes++,
            publishSynthEditAsync: () =>
            {
                callbacks++;
                Task task = transport.PublishSynthEditAsync(
                    workspace.Document, synth.Id);
                pending.Add(task);
                return task;
            });
        TextBox tail = Field<TextBox>(Inspector(editor),
            "Tail (seconds, nonnegative)");

        async Task<float[]> Change(double next, int count, string label)
        {
            Enter(tail, next.ToString("R", CultureInfo.InvariantCulture));
            await Task.WhenAll(pending.ToArray())
                .WaitAsync(TimeSpan.FromSeconds(8));
            FmFilterNode node = synth.Graph.Nodes.OfType<FmFilterNode>().Single();
            Assert.Multiple(() =>
            {
                Assert.That(node.TailSeconds, Is.EqualTo(next));
                Assert.That(workspace.Document.AudioRevision,
                    Is.EqualTo(audio + callbacks));
                Assert.That(workspace.Document.DocumentRevision,
                    Is.EqualTo(document + callbacks));
                Assert.That(factory.Preparations.Count, Is.EqualTo(callbacks * 2));
                Assert.That(factory.Preparations.Skip((callbacks - 1) * 2)
                    .Select(x => x.Id), Is.EqualTo(new[] { synth.Id, parent.Id }));
                Assert.That(factory.Preparations[(callbacks - 1) * 2]
                    .FilterDefinitionId, Is.EqualTo(definition.Id));
                Assert.That(factory.Preparations[(callbacks - 1) * 2]
                    .FilterTailSeconds, Is.EqualTo(next));
                Assert.That(changes, Is.EqualTo(callbacks));
            });
            FmSynthSound revised = Author(next, oracleResolver);
            oracle.AdoptLiveSynthParameters(synth.Id, oracleSound, revised);
            oracleResolver.Sound = revised;
            oracleSound = revised;
            float[] observed = Compare(count, label);
            Assert.Multiple(() =>
            {
                Assert.That(factory.OlderAudioCount, Is.Zero,
                    label + ": no older generation after pure tail edit");
                Assert.That(oracle.CountOlderAudioVoices(), Is.Zero);
                Assert.That(transport.CurrentPlaybackSnapshot.AudioRevision,
                    Is.EqualTo(baseSnapshot.AudioRevision));
            });
            return observed;
        }

        // Sample was naturally finished at frame 4, but the Filter state
        // stays active until the 4+12=16 exclusive deadline. Extend while
        // that residual sound is already playing.
        float[] extended = await Change(0.018, 10,
            "extend tail beyond original cutoff");
        Assert.That(frame, Is.EqualTo(17));
        // The original tail was exclusive at 4+12=16. Frame 16 is
        // definitely *past* that deadline but must still ring on this
        // same original voice after its eligible tail extension.
        Assert.That(extended[9], Is.GreaterThan(1e-5f),
            "tail extension must keep the old IIR audible beyond its old end");
        // Shorten while the revised 4+18=22 tail is still active, to
        // 4+15=19. Frames 17 and 18 retain the existing ringing history;
        // frame 19 onward must be exactly silent.
        float[] shortened = await Change(0.015, 12,
            "shorten still-ringing tail to new deadline");
        Assert.That(frame, Is.EqualTo(29));
        Assert.Multiple(() =>
        {
            Assert.That(shortened[0], Is.GreaterThan(1e-5f));
            Assert.That(shortened[1], Is.GreaterThan(1e-5f));
            Assert.That(shortened.Skip(2), Is.All.EqualTo(0f).Within(1e-7f),
                "the newly authored exclusive deadline is 19");
            Assert.That(backend.Render(5),
                Is.All.EqualTo(0f).Within(1e-7f));
        });
        Assert.Multiple(() =>
        {
            Assert.That(factory.OlderAudioCount, Is.Zero);
            Assert.That(callbacks, Is.EqualTo(2));
            Assert.That(changes, Is.EqualTo(2));
            Assert.That(backend.OpenCount, Is.EqualTo(1));
        });
        Enter(tail, "0.015");
        await transport.PublishSynthEditAsync(workspace.Document, synth.Id);
        Assert.That(callbacks, Is.EqualTo(2),
            "identical Tail submission must not publish a new generation");
        Assert.That(factory.Preparations.Count, Is.EqualTo(4));
    }

    private static bool SameOscillator(
        FmOscillatorNode? actual, FmOscillatorNode expected)
        => actual is not null
            && actual.Id == expected.Id
            && actual.Waveform == expected.Waveform
            && actual.FrequencyHz == expected.FrequencyHz
            && actual.Minimum == expected.Minimum
            && actual.Maximum == expected.Maximum
            && actual.Curvature == expected.Curvature
            && actual.MultiplierNodeId == expected.MultiplierNodeId
            && actual.ExponentialMultiplier == expected.ExponentialMultiplier
            && actual.Constant == expected.Constant;

    private static bool SameSource(
        FmSoundSourceNode? actual, FmSoundSourceNode expected)
        => actual is not null
            && actual.Id == expected.Id
            && actual.SourceId == expected.SourceId
            && actual.FrequencyHz == expected.FrequencyHz
            && actual.MultiplierNodeId == expected.MultiplierNodeId
            && actual.ExponentialMultiplier == expected.ExponentialMultiplier
            && actual.Constant == expected.Constant;

    private static RenderConfiguration Mono()
        => new(Rate, [
            new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0),
        ]);

    private static FmSynthDefinition MakeDependentParent(
        DocumentWorkspace workspace, FmSynthDefinition child, string name)
    {
        FmSynthDefinition parent = FmSynthDocumentEditor.CreateFmSynth(
            workspace, name);
        FmSynthDocumentEditor.UpdateNode(workspace, parent,
            new FmSoundSourceNode(0, child.Id,
                FmMultiNode.NeutralFrequencyHz));
        return parent;
    }

    private static PlaybackSession IndependentSession(
        RenderConfiguration configuration, Resolver resolver)
        => new(new RenderContext(configuration),
            new NoteScheduleBuilder().Freeze(), resolver);

    private static void StartIndependent(PlaybackSession session, ObjectId id)
        => session.ApplyLiveEvent(ChannelTarget.Physical(0),
            [new StartNoteCommand(id)]);

    private static T Field<T>(StackPanel panel, string label) where T : Control
    {
        int index = panel.Children.Select((child, i) => (child, i))
            .Single(x => x.child is TextBlock text && text.Text == label).i;
        return (T)panel.Children[index + 1];
    }

    private static StackPanel Inspector(FmSynthEditorControl editor)
        => (StackPanel)typeof(FmSynthEditorControl).GetField("_inspector",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;

    private static void Enter(TextBox box, string value)
    {
        box.Text = value;
        box.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Enter,
        });
    }

    private static byte[] MonoWave16(short[] wave)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8);
        writer.Write(36 + wave.Length * 2);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write(Rate);
        writer.Write(Rate * 2);
        writer.Write((ushort)2);
        writer.Write((ushort)16);
        writer.Write("data"u8);
        writer.Write(wave.Length * 2);
        foreach (short sample in wave)
            writer.Write(sample);
        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] FourFrameWave(short amplitude)
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
            writer.Write(amplitude);
        writer.Flush();
        return stream.ToArray();
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
        double? LowConstant, int WorkerThread, double? FilterQ = null,
        ObjectId? SampleSourceId = null, FmOscillatorNode? Oscillator = null,
        FmSoundSourceNode? Source = null, FmMultiNode? Multi = null,
        ObjectId? FilterDefinitionId = null, double? FilterCutoffHz = null,
        int? FilterStageCount = null, double? FilterTailSeconds = null);

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
                    .SingleOrDefault(n => n.Id == 1)
                : null;
            FmFilterNode? filter = sourceId == editedSynthId
                ? frozen.Graph.Nodes.OfType<FmFilterNode>().SingleOrDefault()
                : null;
            double? q = null;
            if (sourceId == editedSynthId)
            {
                if (filter?.Configuration?.GetOverrides(1)
                    .TryGetValue("Q", out FilterParameterValue? value) == true)
                    q = value.NumericValue;
            }
            ObjectId? sampleSourceId = sourceId == editedSynthId
                ? frozen.Graph.Nodes.OfType<FmSoundSourceNode>()
                    .SingleOrDefault()?.SourceId : null;
            FmOscillatorNode? oscillator = sourceId == editedSynthId
                ? frozen.Graph.Nodes.OfType<FmOscillatorNode>()
                    .SingleOrDefault() : null;
            FmSoundSourceNode? sourceNode = sourceId == editedSynthId
                ? frozen.Graph.Nodes.OfType<FmSoundSourceNode>()
                    .SingleOrDefault() : null;
            Preparations.Add(new Preparation(sourceId,
                snapshot.AudioRevision, operation?.Operation,
                low?.Value, Environment.CurrentManagedThreadId, q,
                sampleSourceId, oscillator, sourceNode,
                sourceId == editedSynthId
                    ? frozen.Graph.Nodes.OfType<FmMultiNode>().SingleOrDefault()
                    : null,
                sourceId == editedSynthId ? filter?.Configuration?.DefinitionId : null,
                sourceId == editedSynthId && filter?.Configuration is FilterInstance use
                    ? use.GetStageParameters(1).GetParameter("CutoffHz").NumericValue
                    : null,
                sourceId == editedSynthId
                    ? filter?.Configuration?.GetStages().Count : null,
                sourceId == editedSynthId ? filter?.TailSeconds : null));
            return _actual.PrepareLiveSynthSource(snapshot, sourceId);
        }
    }

    private sealed class Resolver(ObjectId id)
        : ISoundResolver, IEnvelopeCurveResolver
    {
        private readonly Dictionary<ObjectId, ISound> _samples = [];
        public ISound? Sound { get; set; }
        public void AddSample(ObjectId sampleId, ISound sound)
            => _samples.Add(sampleId, sound);
        public bool TryResolve(ObjectId requested, bool mixdown, out ISound? sound)
        {
            sound = mixdown ? null : requested == id
                ? Sound : _samples.GetValueOrDefault(requested);
            return sound is not null;
        }
        public bool TryResolve(ObjectId requested, out IEnvelopeCurve? curve)
        {
            curve = null;
            return false;
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
