using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;

using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;

using Heresy.Core.Envelopes;
using Heresy.Core.Filters;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
using Heresy.Core.Sequencing;
using Heresy.Playback;
using Heresy.Render.Configuration;
using Heresy.Render.Envelopes;
using Heresy.Render.FmSynthesis;
using Heresy.Render.Playback;
using Heresy.Render.Realtime;
using Heresy.Render.Sounds;
using Heresy.Scripting.Compilation;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

/// <summary>
/// C2(c): real shared-object editors, precise workspace notifications,
/// production dependency-order publication and separate held-voice PCM
/// oracles. The transport probe intentionally models its audio output as a
/// recorder; its publication ordering is not mislabeled hardware PCM.
/// </summary>
[TestFixture]
public sealed class FmSynthSharedEditorLivePcmTests
{
    private const int Rate = 8000;

    [AvaloniaTest]
    public void ScriptEnvelopeApplyPublishesDependentSynthsAndCrossfadesHeldPcm()
    {
        DocumentWorkspace workspace = new();
        ScriptEnvelopeDefinition envelope =
            EnvelopeDocumentEditor.CreateScriptEnvelope(
                workspace, "C2(c) shared expression", "0.25");
        FmSynthDefinition root = FmSynthDocumentEditor.CreateFmSynth(
            workspace, "Script child");
        FmSynthDocumentEditor.UpdateNode(workspace, root,
            new FmEnvelopeNode(0, envelope.Id));
        FmSynthDefinition parent = NestedParent(workspace, root);

        PcmResolver resolver = new(root.Id, envelope.Id);
        PcmResolver oracleResolver = new(root.Id, envelope.Id);
        resolver.Curve = Compile(envelope);
        oracleResolver.Curve = Compile(new ScriptEnvelopeDefinition(
            envelope.Id, "independent original") { Source = "0.25" });
        FmSynthSound current = new(root.Graph, resolver,
            soundResolver: resolver, enableLiveMultiExpansion: true);
        FmSynthSound oracleCurrent = new(root.Graph, oracleResolver,
            soundResolver: oracleResolver, enableLiveMultiExpansion: true);
        resolver.Sound = current;
        oracleResolver.Sound = oracleCurrent;
        PlaybackSession actual = Session(resolver);
        PlaybackSession oracle = Session(oracleResolver);
        Start(actual, root.Id);
        Start(oracle, root.Id);
        float[] initial = new float[40], oracleInitial = new float[40];
        actual.Render(0, 40, initial);
        oracle.Render(0, 40, oracleInitial);
        Assert.That(initial, Is.All.EqualTo(0.25f).Within(1e-5f));
        Assert.That(initial, Is.EqualTo(oracleInitial));
        PlaybackVoice voice = actual.GetChannelState(0).CurrentVoice!;

        using TransportProbe transport = new();
        transport.Start(workspace.Document, parent.Id);
        transport.ClearRevisions();
        List<SharedAudioObjectCommittedEventArgs> notifications = [];
        workspace.SharedAudioObjectCommitted += (_, e) =>
        {
            notifications.Add(e);
            transport.Publish(workspace.Document, e.ObjectId);
            resolver.Curve = Compile(envelope);
            FmSynthSound next = new(root.Graph, resolver,
                soundResolver: resolver, enableLiveMultiExpansion: true);
            actual.AdoptLiveSynthParameters(root.Id, current, next);
            resolver.Sound = next;
            current = next;
        };

        ScriptEnvelopeEditorControl editor = new(workspace, envelope,
            () => { }, _ => { });
        TextBox source = Field<TextBox>(editor, "_source");
        TextBlock message = Field<TextBlock>(editor, "_message");
        ScrollViewer scroll = (ScrollViewer)editor.Content!;
        StackPanel fields = (StackPanel)scroll.Content!;
        Button apply = fields.Children.OfType<Button>()
            .Single(button => Equals(button.Content, "Apply expression"));
        void Click() => apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        uint originalRevision = workspace.Document.AudioRevision;

        source.Text = "System.IO.File.ReadAllText(\"secret\")";
        Click();
        Assert.Multiple(() =>
        {
            Assert.That(message.Text, Does.Contain("Cannot apply Envelope"));
            Assert.That(workspace.Document.AudioRevision, Is.EqualTo(originalRevision));
            Assert.That(notifications, Is.Empty);
            Assert.That(transport.Factory.PreparedIds, Is.Empty);
            Assert.That(voice.Sound, Is.SameAs(current));
        });

        source.Text = "0.75";
        Click();
        Assert.Multiple(() =>
        {
            Assert.That(envelope.Source, Is.EqualTo("0.75"));
            Assert.That(workspace.Document.AudioRevision, Is.EqualTo(originalRevision + 1));
            Assert.That(notifications.Count, Is.EqualTo(1));
            Assert.That(notifications[0].ObjectId, Is.EqualTo(envelope.Id));
            Assert.That(notifications[0].AudioRevision,
                Is.EqualTo(workspace.Document.AudioRevision));
            Assert.That(transport.Factory.PreparedIds,
                Is.EqualTo(new[] { root.Id, parent.Id }),
                "production transport must compile child before dependent parent");
            Assert.That(transport.Factory.Source.Order,
                Is.EqualTo(new[] { "Update:" + root.Id.Value,
                    "Update:" + parent.Id.Value }));
            Assert.That(voice.Sound, Is.SameAs(current));
            Assert.That(actual.CountOlderAudioVoices(), Is.Zero);
        });

        source.Text = "0.75";
        Click(); // no-op authoring must not republish even if UI acknowledges it
        Assert.That(notifications.Count, Is.EqualTo(1));
        Assert.That(transport.Factory.PreparedIds.Count, Is.EqualTo(2));
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(originalRevision + 1));

        // Independently compile the oracle expression (not copied from the
        // edited document) and publish it to the continuing reference voice.
        oracleResolver.Curve = Compile(new ScriptEnvelopeDefinition(
            envelope.Id, "independent replacement") { Source = "0.75" });
        FmSynthSound oracleNext = new(root.Graph, oracleResolver,
            soundResolver: oracleResolver, enableLiveMultiExpansion: true);
        oracle.AdoptLiveSynthParameters(root.Id, oracleCurrent, oracleNext);
        oracleResolver.Sound = oracleNext;

        float[] got = new float[801], expected = new float[801];
        actual.Render(40, got.Length, got);
        oracle.Render(40, expected.Length, expected);
        for (int i = 0; i < got.Length; i++)
        {
            double t = i / 800.0;
            double w = t * t * (3.0 - 2.0 * t);
            float handComputed = (float)(0.25 * (1 - w) + 0.75 * w);
            Assert.Multiple(() =>
            {
                Assert.That(got[i], Is.EqualTo(expected[i]).Within(2e-5f),
                    $"independent Script compile and held PCM at frame {40 + i}");
                Assert.That(got[i], Is.EqualTo(handComputed).Within(2e-5f),
                    $"100-ms frozen Script Envelope transition at frame {40 + i}");
            });
        }
        Assert.That(actual.GetChannelState(0).CurrentVoice, Is.SameAs(voice));
        Assert.That(actual.CountOlderAudioVoices(), Is.Zero);
    }

    [AvaloniaTest]
    public void SharedFilterStageEditPublishesDependentsAndRetunesContinuingOnePole()
    {
        DocumentWorkspace workspace = new();
        FilterDefinition filter =
            FilterDocumentEditor.CreateFilter(workspace, "C2(c) shared LP");
        FmSynthDefinition root = FmSynthDocumentEditor.CreateFmSynth(
            workspace, "Filter child");
        int oscillator = FmSynthDocumentEditor.AddOscillatorNode(
            workspace, root, FmOscillatorWaveform.Sine, 330, 40, 40);
        int filterNode = FmSynthDocumentEditor.AddFilterNode(
            workspace, root, 260, 40);
        FmSynthDocumentEditor.UpdateNode(workspace, root,
            new FmFilterNode(filterNode, oscillator, new FilterInstance(filter)));
        FmSynthDocumentEditor.SetOutputNode(workspace, root, filterNode);
        FmSynthDefinition parent = NestedParent(workspace, root);

        FilterDefinition oracleFilter = new(filter.Id, "independent shared LP");
        FilterDefinition unchangedFilter = new(filter.Id, "never edited LP");
        FmSynthSound Build(FilterDefinition definition, PcmResolver resolver)
            => new(new FmSynthGraph(root.Graph.Nodes.Select(node =>
                node.Id == filterNode
                    ? (FmSynthNode)new FmFilterNode(
                        filterNode, oscillator, new FilterInstance(definition))
                    : node), filterNode),
                resolver, soundResolver: resolver, enableLiveMultiExpansion: true);
        PcmResolver actualResolver = new(root.Id, ObjectId.None);
        PcmResolver oracleResolver = new(root.Id, ObjectId.None);
        PcmResolver unchangedResolver = new(root.Id, ObjectId.None);
        FmSynthSound current = Build(filter, actualResolver);
        FmSynthSound oracleCurrent = Build(oracleFilter, oracleResolver);
        FmSynthSound unchangedSound = Build(unchangedFilter, unchangedResolver);
        actualResolver.Sound = current;
        oracleResolver.Sound = oracleCurrent;
        unchangedResolver.Sound = unchangedSound;
        PlaybackSession actual = Session(actualResolver);
        PlaybackSession oracle = Session(oracleResolver);
        PlaybackSession unchanged = Session(unchangedResolver);
        Start(actual, root.Id);
        Start(oracle, root.Id);
        Start(unchanged, root.Id);
        float[] before = new float[160], independentBefore = new float[160];
        actual.Render(0, 160, before);
        oracle.Render(0, 160, independentBefore);
        unchanged.Render(0, 160, new float[160]);
        Assert.That(before, Is.EqualTo(independentBefore),
            "both frozen initial shared Filter instances need identical histories");
        PlaybackVoice held = actual.GetChannelState(0).CurrentVoice!;

        using TransportProbe transport = new();
        transport.Start(workspace.Document, parent.Id);
        transport.ClearRevisions();
        List<SharedAudioObjectCommittedEventArgs> notifications = [];
        workspace.SharedAudioObjectCommitted += (_, e) =>
        {
            notifications.Add(e);
            transport.Publish(workspace.Document, e.ObjectId);
            FmSynthSound next = Build(filter, actualResolver);
            actual.AdoptLiveSynthParameters(root.Id, current, next);
            actualResolver.Sound = next;
            current = next;
        };
        FilterEditorControl editor = new(workspace, filter, () => { }, _ => { });
        Dictionary<string, Control> controls =
            Field<Dictionary<string, Control>>(editor, "_editors");
        TextBox cutoff = (TextBox)controls["CutoffHz"];
        TextBlock message = Field<TextBlock>(editor, "_message");
        void Apply() => typeof(FilterEditorControl)
            .GetMethod("ApplyDraft", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(editor, null);
        uint originalRevision = workspace.Document.AudioRevision;
        cutoff.Text = "NaN";
        Apply();
        Assert.Multiple(() =>
        {
            Assert.That(workspace.Document.AudioRevision, Is.EqualTo(originalRevision));
            Assert.That(notifications, Is.Empty);
            Assert.That(transport.Factory.PreparedIds, Is.Empty);
            Assert.That(message.Text, Does.Contain("finite"));
        });

        cutoff.Text = "45";
        Apply();
        Assert.Multiple(() =>
        {
            Assert.That(filter.GetStage(1).DefaultParameters["CutoffHz"].NumericValue,
                Is.EqualTo(45));
            Assert.That(workspace.Document.AudioRevision, Is.EqualTo(originalRevision + 1));
            Assert.That(notifications.Count, Is.EqualTo(1));
            Assert.That(notifications[0].ObjectId, Is.EqualTo(filter.Id));
            Assert.That(transport.Factory.PreparedIds,
                Is.EqualTo(new[] { root.Id, parent.Id }));
            Assert.That(transport.Factory.Source.Order,
                Is.EqualTo(new[] { "Update:" + root.Id.Value,
                    "Update:" + parent.Id.Value }));
            Assert.That(held.Sound, Is.SameAs(current),
                "held source adopts the new frozen coefficients, not only new notes");
            Assert.That(actual.CountOlderAudioVoices(), Is.Zero);
        });

        Apply();
        Assert.That(notifications.Count, Is.EqualTo(1));
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(originalRevision + 1));

        oracleFilter.SetStageDefaults(1,
            [new("CutoffHz", FilterParameterValue.FromNumber(45))]);
        FmSynthSound oracleNext = Build(oracleFilter, oracleResolver);
        oracle.AdoptLiveSynthParameters(root.Id, oracleCurrent, oracleNext);
        oracleResolver.Sound = oracleNext;
        float[] got = new float[1600], expected = new float[1600],
            original = new float[1600];
        actual.Render(160, got.Length, got);
        oracle.Render(160, expected.Length, expected);
        unchanged.Render(160, original.Length, original);
        for (int i = 0; i < got.Length; i++)
            Assert.That(got[i], Is.EqualTo(expected[i]).Within(3e-5f),
                $"same ongoing OnePole IIR state after shared editor retune, frame {160 + i}");
        Assert.Multiple(() =>
        {
            Assert.That(got.Zip(original, (a, b) => Math.Abs(a - b)).Max(),
                Is.GreaterThan(0.05), "the edited shared Filter must affect audible PCM");
            Assert.That(actual.GetChannelState(0).CurrentVoice, Is.SameAs(held));
            Assert.That(actual.CountOlderAudioVoices(), Is.Zero);
        });
    }

    private static FmSynthDefinition NestedParent(
        DocumentWorkspace workspace, FmSynthDefinition child)
    {
        FmSynthDefinition parent = FmSynthDocumentEditor.CreateFmSynth(
            workspace, "Parent referencing shared child");
        FmSynthDocumentEditor.UpdateNode(workspace, parent,
            new FmSoundSourceNode(0, child.Id, FmMultiNode.NeutralFrequencyHz));
        return parent;
    }

    private static IEnvelopeCurve Compile(ScriptEnvelopeDefinition definition)
    {
        var compiled = ScriptCompiler.CompileEnvelope(definition);
        Assert.That(compiled.Success, Is.True);
        Assert.That(compiled.Program, Is.Not.Null);
        return new ScriptEnvelopeCurve(compiled.Program!.Evaluate,
            definition.ReleaseDuration);
    }

    private static PlaybackSession Session(PcmResolver resolver)
        => new(new RenderContext(new RenderConfiguration(Rate, [
            new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0),
        ])), new NoteScheduleBuilder().Freeze(), resolver);

    private static void Start(PlaybackSession session, ObjectId id)
        => session.ApplyLiveEvent(ChannelTarget.Physical(0),
            [new StartNoteCommand(id)]);

    private static T Field<T>(object target, string name)
        => (T)target.GetType().GetField(name,
            BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;

    private sealed class PcmResolver(ObjectId sourceId, ObjectId envelopeId)
        : ISoundResolver, IEnvelopeCurveResolver
    {
        public ISound? Sound { get; set; }
        public IEnvelopeCurve? Curve { get; set; }
        public bool TryResolve(ObjectId id, bool mixdown, out ISound? sound)
        {
            sound = id == sourceId && !mixdown ? Sound : null;
            return sound is not null;
        }
        public bool TryResolve(ObjectId id, out IEnvelopeCurve? curve)
        {
            curve = id == envelopeId ? Curve : null;
            return curve is not null;
        }
    }

    private sealed class TransportProbe : IDisposable
    {
        public ProbeFactory Factory { get; } = new();
        private readonly ProbeBackend _backend = new();
        private readonly SongPlaybackTransport _transport;
        public TransportProbe() => _transport = new(_backend, Factory);
        public void Start(SongDocument document, ObjectId parentId)
            => _transport.SendLiveEventAsync(document, ChannelTarget.Virtual(91),
                [new StartNoteCommand(parentId)]).GetAwaiter().GetResult();
        public void Publish(SongDocument document, ObjectId changedId)
            => _transport.PublishSharedObjectEditAsync(document, changedId)
                .GetAwaiter().GetResult();
        public void ClearRevisions()
        {
            Factory.PreparedIds.Clear();
            Factory.Source.Order.Clear();
        }
        public void Dispose() => _transport.Dispose();
    }

    private sealed class ProbeFactory :
        IBackgroundPlaybackSourceFactory, ILiveSynthSourcePreparationFactory
    {
        public List<ObjectId> PreparedIds { get; } = [];
        public ProbeOutput Source { get; } = new();
        public IAudioOutputSource Create(PlaybackRequest request) => Source;
        public ISound PrepareLiveSynthSource(
            SongDocumentSnapshot snapshot, ObjectId sourceId)
        {
            Assert.That(snapshot.Document.TryGet(sourceId, out SongObject? obj),
                Is.True);
            Assert.That(obj, Is.TypeOf<FmSynthDefinition>());
            PreparedIds.Add(sourceId);
            return new ProbeSound();
        }
    }

    private sealed class ProbeOutput :
        ILiveAudioOutputSource, ILiveSynthSourceRevisionOutput
    {
        public List<string> Order { get; } = [];
        public AudioOutputFormat Format => new(48000, 2);
        public void EnqueueLiveSynthSource(ObjectId sourceId, ISound sound)
            => Order.Add("Source:" + sourceId.Value);
        public void EnqueueLiveSynthParameters(ObjectId sourceId, ISound sound)
            => Order.Add("Update:" + sourceId.Value);
        public void EnqueueLiveEvent(ChannelTarget target,
            IReadOnlyList<NoteCommand> commands)
            => Order.Add("Event");
        public void Render(int frameCount, Span<float> destination)
            => destination.Clear();
    }

    private sealed class ProbeState : SoundState { }
    private sealed class ProbeSound : ISound
    {
        public NoteConfigurationSnapshot SnapshotNoteConfiguration()
            => NoteConfigurationSnapshot.Default;
        public SoundState CreateState() => new ProbeState();
        public long? GetEndFrameExclusive(RenderContext context, SoundState state)
            => null;
        public void Render(RenderContext context, SoundState state,
            long startFrame, int frameCount, Span<float> destination)
            => destination.Clear();
    }

    private sealed class ProbeBackend : IAudioOutputBackend
    {
        public IAudioOutputSession Open(AudioOutputFormat format,
            IAudioOutputSource source) => new ProbeSession(format);
        public void Dispose() { }
    }

    private sealed class ProbeSession(AudioOutputFormat format)
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
