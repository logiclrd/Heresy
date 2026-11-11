using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Headless.NUnit;

using AwesomeAssertions;

using Heresy.Core.Envelopes;
using Heresy.Core.Filters;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Sequencing;
using Heresy.Playback;
using Heresy.Render.Realtime;
using Heresy.Render.Sounds;
using Heresy.UserInterface;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class SharedAudioEditLivePublicationTests
{
    [AvaloniaTest]
    public async Task ActualSharedEnvelopeAndFilterEditorsPublishThroughInstrumentIntoActiveSynth()
    {
        // This is a real UI -> DocumentWorkspace -> MainWindow event ->
        // SongPlaybackTransport -> background preparation -> live output
        // integration test. The fake audio backend only records publication;
        // it does not claim that an already-held Instrument Sample can
        // replace changed envelope or filter DSP history.
        TestBackend backend = new();
        PublishingFactory factory = new();
        SongPlaybackTransport playback = new(backend, factory);
        MainWindow window = new(playback);
        DocumentWorkspace workspace =
            (DocumentWorkspace)typeof(MainWindow).GetField("_workspace",
                BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(window)!;
        try
        {
            NodeEnvelopeDefinition envelope =
                EnvelopeDocumentEditor.CreateNodeEnvelope(workspace, "Tone volume");
            FilterDefinition filter =
                FilterDocumentEditor.CreateFilter(workspace, "Tone filter");
            ObjectId instrumentId = workspace.Document.AllocateObjectId();
            InstrumentDefinition instrument = new(instrumentId, "Tone");
            instrument.ToneSpecifications.Add(new ToneSpecification
            {
                SourceId = ObjectId.None,
                VolumeEnvelopeId = envelope.Id,
                FilterConfiguration = new FilterInstance(filter),
            });
            instrument.ToneTable.Add(0);
            workspace.Document.Add(instrument);

            ObjectId synthId = workspace.Document.AllocateObjectId();
            workspace.Document.Add(new FmSynthDefinition(synthId, "Instrument child",
                new FmSynthGraph([
                    new FmSoundSourceNode(0, instrumentId,
                        FmMultiNode.NeutralFrequencyHz)], 0)));
            ObjectId parentId = workspace.Document.AllocateObjectId();
            workspace.Document.Add(new FmSynthDefinition(parentId, "Audition root",
                new FmSynthGraph([
                    new FmSoundSourceNode(0, synthId,
                        FmMultiNode.NeutralFrequencyHz)], 0)));
            ObjectId unrelatedId = workspace.Document.AllocateObjectId();
            workspace.Document.Add(new FmSynthDefinition(unrelatedId, "Unrelated",
                new FmSynthGraph([new FmConstantNode(0, 0.6)], 0)));

            // Editing before an audition must not start one.
            NodeEnvelopeEditorControl envelopeEditor =
                new(workspace, envelope, _ => {});
            TextBox nodes = Field<TextBox>(envelopeEditor, "_nodesInput");
            nodes.Text = "not valid";
            uint revision = workspace.Document.AudioRevision;
            Invoke(envelopeEditor, "Apply");
            workspace.Document.AudioRevision.Should().Be(revision);
            factory.Prepared.Should().BeEmpty();
            backend.OpenCount.Should().Be(0);

            await playback.SendLiveEventAsync(workspace.Document,
                ChannelTarget.Virtual(91), [new StartNoteCommand(parentId)]);
            backend.OpenCount.Should().Be(1);
            factory.Source.Order.ToArray().Should().Equal("Start:91");

            // The node editor's Apply commits and emits the shared-object
            // notification. MainWindow forwards it to the active transport.
            nodes.Text = "0, 0\n0.01, 0.45\n0.11, 0.2\n0.41, 0";
            revision = workspace.Document.AudioRevision;
            Invoke(envelopeEditor, "Apply");
            workspace.Document.AudioRevision.Should().Be(revision + 1);
            await WaitForPreparedCountAsync(factory, 2);
            factory.Prepared.ToArray().Select(x => x.Id)
                .Should().Equal(synthId, parentId);
            factory.Prepared.ToArray().Select(x => x.Revision)
                .Should().OnlyContain(x => x == workspace.Document.AudioRevision);
            factory.Source.Order.ToArray().Should()
                .Equal("Start:91", "Update", "Update");
            backend.OpenCount.Should().Be(1);

            // Reapplying the unchanged shared Envelope cannot cause
            // another frozen-audio publication or extra audio session.
            revision = workspace.Document.AudioRevision;
            Invoke(envelopeEditor, "Apply");
            workspace.Document.AudioRevision.Should().Be(revision);

            // The real Filter editor has a separate stage draft/Apply
            // path, but must send the same shared-object notification.
            FilterEditorControl filterEditor =
                new(workspace, filter, () => {}, _ => {});
            Dictionary<string, Control> controls =
                Field<Dictionary<string, Control>>(filterEditor, "_editors");
            TextBox cutoff = (TextBox)controls["CutoffHz"];
            revision = workspace.Document.AudioRevision;
            cutoff.Text = "2400";
            Invoke(filterEditor, "ApplyDraft");
            workspace.Document.AudioRevision.Should().Be(revision + 1);
            await WaitForPreparedCountAsync(factory, 4);
            factory.Prepared.ToArray().Select(x => x.Id).Should()
                .Equal(synthId, parentId, synthId, parentId);
            factory.Prepared.ToArray().Skip(2).Select(x => x.Revision)
                .Should().OnlyContain(x => x == workspace.Document.AudioRevision);
            factory.Source.Order.ToArray().Should()
                .Equal("Start:91", "Update", "Update", "Update", "Update");
            factory.Prepared.ToArray().Select(x => x.Id)
                .Should().NotContain(unrelatedId);
            backend.OpenCount.Should().Be(1);

            revision = workspace.Document.AudioRevision;
            Invoke(filterEditor, "ApplyDraft");
            workspace.Document.AudioRevision.Should().Be(revision);
            // Flush the command queue without changing audio so the
            // assertions are stable even when the UI fires and forgets.
            await playback.PublishSharedObjectEditAsync(
                workspace.Document, filter.Id);
            factory.Prepared.Should().HaveCount(4,
                "unchanged Apply and duplicate notifications are per-revision no-ops");
        }
        finally
        {
            // MainWindow owns and disposes the playback transport. Reset the
            // unsaved authoring workspace to avoid opening a close dialog.
            workspace.New();
            window.Close();
        }
    }

    [AvaloniaTest]
    public async Task ScriptEnvelopeEditorPublishesInstrumentDependentSynthWithoutSessionRestart()
    {
        // Script Envelope authoring has a different Apply method from
        // node envelopes. It must follow the same workspace event and
        // MainWindow -> transport publication path.
        TestBackend backend = new();
        PublishingFactory factory = new();
        SongPlaybackTransport playback = new(backend, factory);
        MainWindow window = new(playback);
        DocumentWorkspace workspace =
            (DocumentWorkspace)typeof(MainWindow).GetField("_workspace",
                BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(window)!;
        try
        {
            ScriptEnvelopeDefinition envelope =
                EnvelopeDocumentEditor.CreateScriptEnvelope(
                    workspace, "Scripted tone volume");
            ObjectId instrumentId = workspace.Document.AllocateObjectId();
            InstrumentDefinition instrument = new(instrumentId, "Script volume tone");
            instrument.ToneSpecifications.Add(new ToneSpecification
            {
                SourceId = ObjectId.None,
                VolumeEnvelopeId = envelope.Id,
            });
            instrument.ToneTable.Add(0);
            workspace.Document.Add(instrument);
            ObjectId synthId = workspace.Document.AllocateObjectId();
            workspace.Document.Add(new FmSynthDefinition(synthId, "Script volume child",
                new FmSynthGraph([
                    new FmSoundSourceNode(0, instrumentId,
                        FmMultiNode.NeutralFrequencyHz)], 0)));
            await playback.SendLiveEventAsync(workspace.Document,
                ChannelTarget.Virtual(92), [new StartNoteCommand(synthId)]);
            backend.OpenCount.Should().Be(1);

            ScriptEnvelopeEditorControl editor =
                new(workspace, envelope, () => {}, _ => {});
            TextBox source = Field<TextBox>(editor, "_source");
            uint revision = workspace.Document.AudioRevision;
            source.Text = "releaseSeconds < 0 ? 0.75 : 0.25";
            Invoke(editor, "Apply");
            workspace.Document.AudioRevision.Should().Be(revision + 1);
            await WaitForPreparedCountAsync(factory, 1);
            factory.Prepared.ToArray().Select(x => x.Id).Should().Equal(synthId);
            factory.Prepared.ToArray().Select(x => x.Revision)
                .Should().OnlyContain(x => x == workspace.Document.AudioRevision);
            factory.Source.Order.ToArray().Should().Equal("Start:92", "Update");
            backend.OpenCount.Should().Be(1);

            // An invalid expression is rejected by the real editor before
            // either its revision or its shared-object event can change.
            revision = workspace.Document.AudioRevision;
            source.Text = "this is not valid C# !";
            Invoke(editor, "Apply");
            workspace.Document.AudioRevision.Should().Be(revision);
            await playback.PublishSharedObjectEditAsync(
                workspace.Document, envelope.Id);
            factory.Prepared.Should().HaveCount(1);
            factory.Source.Order.ToArray().Should().Equal("Start:92", "Update");
            backend.OpenCount.Should().Be(1);
        }
        finally
        {
            workspace.New();
            window.Close();
        }
    }

    private static T Field<T>(object editor, string name)
        => (T)editor.GetType().GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(editor)!;

    private static void Invoke(object editor, string method)
        => editor.GetType().GetMethod(method,
            BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(editor, []);

    private static async Task WaitForPreparedCountAsync(
        PublishingFactory factory, int expected)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while ((factory.Prepared.Count < expected
                || factory.Source.Order.Count < expected + 1)
            && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        factory.Prepared.Count.Should().Be(expected);
        factory.Source.Order.Count.Should().Be(expected + 1,
            "the worker must have enqueued each prepared Synth onto the live output");
    }

    private sealed class PublishingFactory :
        IBackgroundPlaybackSourceFactory, ILiveSynthSourcePreparationFactory
    {
        public PublishingSource Source { get; } = new();
        public ConcurrentQueue<(ObjectId Id, uint Revision)> Prepared { get; } = new();

        public IAudioOutputSource Create(PlaybackRequest request) => Source;

        public ISound PrepareLiveSynthSource(
            SongDocumentSnapshot snapshot, ObjectId sourceId)
        {
            snapshot.Document.TryGet(sourceId, out SongObject? obj)
                .Should().BeTrue();
            obj.Should().BeOfType<FmSynthDefinition>();
            Prepared.Enqueue((sourceId, snapshot.AudioRevision));
            return new DummySound();
        }
    }

    private sealed class PublishingSource :
        ILiveAudioOutputSource, ILiveSynthSourceRevisionOutput
    {
        public ConcurrentQueue<string> Order { get; } = new();
        public AudioOutputFormat Format => new(48000, 2);

        public void EnqueueLiveSynthSource(ObjectId id, ISound sound)
        {
            sound.Should().BeOfType<DummySound>();
            Order.Enqueue("Publish");
        }
        public void EnqueueLiveSynthParameters(ObjectId id, ISound sound)
        {
            sound.Should().BeOfType<DummySound>();
            Order.Enqueue("Update");
        }
        public void EnqueueLiveEvent(ChannelTarget target,
            IReadOnlyList<NoteCommand> commands)
        {
            Order.Enqueue(commands.Any(x => x is StartNoteCommand)
                ? "Start:" + target.VirtualChannelId : "Off:" + target.VirtualChannelId);
        }
        public void Render(int frameCount, Span<float> destination)
            => destination.Clear();
    }

    private sealed class DummyState : SoundState { }

    private sealed class DummySound : ISound
    {
        public NoteConfigurationSnapshot SnapshotNoteConfiguration()
            => NoteConfigurationSnapshot.Default;
        public SoundState CreateState() => new DummyState();
        public long? GetEndFrameExclusive(RenderContext context, SoundState state)
            => null;
        public void Render(RenderContext context, SoundState state,
            long startFrame, int frameCount, Span<float> destination)
            => destination.Clear();
    }

    private sealed class TestBackend : IAudioOutputBackend
    {
        public int OpenCount { get; private set; }
        public IAudioOutputSession Open(
            AudioOutputFormat format, IAudioOutputSource source)
        {
            OpenCount++;
            return new TestSession(format);
        }
        public void Dispose() { }
    }

    private sealed class TestSession(AudioOutputFormat format) : IAudioOutputSession
    {
        public AudioOutputFormat Format { get; } = format;
        public bool IsRunning { get; private set; }
        public Exception? Fault => null;
        public void Start() => IsRunning = true;
        public void Stop() => IsRunning = false;
        public void Dispose() { }
    }
}
