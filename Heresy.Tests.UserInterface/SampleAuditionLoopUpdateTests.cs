using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using AwesomeAssertions;

using Heresy.Core.Assets;
using Heresy.Core.Objects;
using Heresy.Core.Samples;
using Heresy.Core.Sequencing;
using Heresy.Render.Realtime;
using Heresy.UserInterface.Dialogs;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.Keyboard;
using Heresy.UserInterface.PatternEditing;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class SampleAuditionLoopUpdateTests
{
    [AvaloniaTest]
    public void WaveformCommitRestartsLatchedNoteAtItsOriginalPitch()
    {
        var (dialog, sample, playback, events) = Setup();
        playback.HandleAsync(new PianoPreviewNoteOn(1, 0, 1, true))
            .GetAwaiter().GetResult();
        events.Clear();

        SampleLoop updated = new(SampleLoopMode.Forward, 12, 40);
        Invoke(dialog, "CommitWaveformLoop", updated);
        sample.Loop.Should().Be(updated);
        events.Should().ContainSingle();
        events[0].Target.Should().Be(
            ChannelTarget.Virtual(PianoPreviewPlaybackController.SampleVoiceIdBase));
        events[0].Commands.Should().Equal(
            new NoteCutCommand(),
            new StartNoteCommand(sample.Id, PitchMultiplier: 1.0));

        Invoke(dialog, "CommitWaveformLoop",
            new SampleLoop(SampleLoopMode.Forward, 25, 45));
        events.Should().HaveCount(2,
            "latched preview voices remain eligible for loop-edit restarts");
        events[1].Target.Should().Be(events[0].Target);
    }

    [AvaloniaTest]
    public void FocusLossCommitRestartsAllLatchedVoicesWithoutApply()
    {
        var (dialog, sample, playback, events) = Setup();
        playback.HandleAsync(new PianoPreviewNoteOn(1, 0, 1, true))
            .GetAwaiter().GetResult();
        double fifth = Math.Pow(2, 7.0 / 12);
        playback.HandleAsync(new PianoPreviewNoteOn(2, 7, fifth, true))
            .GetAwaiter().GetResult();
        events.Clear();
        Field<ComboBox>(dialog, "_loopMode").SelectedItem =
            SampleLoopMode.Forward;
        Field<TextBox>(dialog, "_loopStart").Text = "7";
        Field<TextBox>(dialog, "_loopEnd").Text = "35";

        Invoke(dialog, "CommitLoopFields");

        sample.Loop.Should().Be(
            new SampleLoop(SampleLoopMode.Forward, 7, 35));
        events.Should().HaveCount(2);
        events[0].Target.Should().Be(
            ChannelTarget.Virtual(PianoPreviewPlaybackController.SampleVoiceIdBase));
        events[1].Target.Should().Be(
            ChannelTarget.Virtual(PianoPreviewPlaybackController.SampleVoiceIdBase + 1));
        events[0].Commands.Should().Equal(
            new NoteCutCommand(),
            new StartNoteCommand(sample.Id, PitchMultiplier: 1));
        events[1].Commands.Should().Equal(
            new NoteCutCommand(),
            new StartNoteCommand(sample.Id, PitchMultiplier: fifth));
    }

    [AvaloniaTest]
    public void NoLoopChangeDoesNotRetriggerLatchedPreview()
    {
        var (dialog, sample, playback, events) = Setup();
        playback.HandleAsync(new PianoPreviewNoteOn(1, 0, 1, true))
            .GetAwaiter().GetResult();
        events.Clear();
        Invoke(dialog, "CommitWaveformLoop", sample.Loop);
        Invoke(dialog, "CommitLoopFields");
        events.Should().BeEmpty();
    }

    [AvaloniaTest]
    public void StoppedNotesAreNotRestartedBySubsequentLoopChanges()
    {
        var (dialog, sample, playback, events) = Setup();
        playback.HandleAsync(new PianoPreviewNoteOn(1, 0, 1, true))
            .GetAwaiter().GetResult();
        playback.HandleAsync(new PianoPreviewStopAll())
            .GetAwaiter().GetResult();
        events.Clear();
        Invoke(dialog, "CommitWaveformLoop",
            new SampleLoop(SampleLoopMode.Forward, 10, 20));
        events.Should().BeEmpty();
    }

    private static (SampleEditorDialog Dialog, SampleDefinition Sample,
        PianoPreviewPlaybackController Playback, List<LivePlaybackEvent> Events)
        Setup()
    {
        DocumentWorkspace workspace = new();
        ObjectId id = workspace.Document.AllocateObjectId();
        SampleDefinition sample = new(
            id, "Test Sample",
            new ExternalAssetReference(Path.GetFullPath(
                "test-sample-audition-does-not-exist.wav")));
        workspace.Document.Add(sample);
        List<LivePlaybackEvent> events = [];
        SampleEditorDialog dialog = new(
            workspace, sample,
            new PatternLiveAuditionActions(ev =>
            {
                events.Add(ev);
                return Task.CompletedTask;
            }));
        return (dialog, sample,
            Field<PianoPreviewPlaybackController>(dialog, "_previewPlayback"),
            events);
    }

    private static T Field<T>(SampleEditorDialog dialog, string name)
        => (T)typeof(SampleEditorDialog)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(dialog)!;

    private static void Invoke(SampleEditorDialog dialog,
        string method, params object[] arguments)
        => typeof(SampleEditorDialog)
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(dialog, arguments);
}
