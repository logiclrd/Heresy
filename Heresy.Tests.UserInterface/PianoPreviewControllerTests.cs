using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Input;
using AwesomeAssertions;
using Heresy.Core.Objects;
using Heresy.Core.Sequencing;
using Heresy.Render.Realtime;
using Heresy.UserInterface.Keyboard;
using Heresy.UserInterface.PatternEditing;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class PianoPreviewControllerTests
{
    [Test]
    public void CapsOffEmitsNoteOnAndMatchingNoteOff()
    {
        PianoPreviewKeyboard input = new();
        PianoPreviewNoteOn start = input.KeyDown(
            PhysicalKey.Z, KeyModifiers.None, capsLockOn: false)
            .Should().BeOfType<PianoPreviewNoteOn>().Subject;
        start.ChromaticNote.Should().Be(0);
        start.PitchMultiplier.Should().Be(1);
        start.SuppressNeighbours.Should().BeFalse();
        input.KeyDown(PhysicalKey.Z, KeyModifiers.None, false).Should().BeNull();
        input.KeyUp(PhysicalKey.Z)
            .Should().Be(new PianoPreviewNoteOff(start.PressId));
        input.KeyUp(PhysicalKey.Z).Should().BeNull();
        PianoPreviewNoteOn again = input.KeyDown(
            PhysicalKey.Z, KeyModifiers.None, capsLockOn: false)
            .Should().BeOfType<PianoPreviewNoteOn>().Subject;
        again.PressId.Should().NotBe(start.PressId);
    }

    [Test]
    public void CapsOnSuppressesReleaseAndRequestsNeighbourSuppression()
    {
        PianoPreviewKeyboard input = new();
        PianoPreviewNoteOn start = input.KeyDown(
            PhysicalKey.Z, KeyModifiers.None, capsLockOn: true)
            .Should().BeOfType<PianoPreviewNoteOn>().Subject;
        start.SuppressNeighbours.Should().BeTrue();
        input.KeyUp(PhysicalKey.Z).Should().BeNull();
        input.KeyDown(PhysicalKey.F8, KeyModifiers.None, capsLockOn: true)
            .Should().BeOfType<PianoPreviewStopAll>();
    }

    [Test]
    public void ModeIsCapturedAtTheOriginalPressRegardlessOfLaterCapsState()
    {
        PianoPreviewKeyboard input = new();
        PianoPreviewNoteOn latched = input.KeyDown(
            PhysicalKey.Z, KeyModifiers.None, capsLockOn: true)
            .Should().BeOfType<PianoPreviewNoteOn>().Subject;
        PianoPreviewNoteOn held = input.KeyDown(
            PhysicalKey.X, KeyModifiers.None, capsLockOn: false)
            .Should().BeOfType<PianoPreviewNoteOn>().Subject;
        input.KeyUp(PhysicalKey.Z).Should().BeNull();
        input.KeyUp(PhysicalKey.X)
            .Should().Be(new PianoPreviewNoteOff(held.PressId));
        input.ReleaseHeld().Should().BeEmpty();
        latched.PressId.Should().NotBe(held.PressId);
    }

    [Test]
    public void FocusLossReleasesOnlyMomentaryKeysNotStackedNotes()
    {
        PianoPreviewKeyboard input = new();
        PianoPreviewNoteOn held = input.KeyDown(
            PhysicalKey.Z, KeyModifiers.None, false)
            .Should().BeOfType<PianoPreviewNoteOn>().Subject;
        input.KeyDown(PhysicalKey.X, KeyModifiers.None, true);
        input.ReleaseHeld().Should().Equal(
            new PianoPreviewNoteOff(held.PressId));
        input.HeldCount.Should().Be(0);
    }

    [Test]
    public void OctavesRespondOnceAndChordShortcutsCannotStartPianoPreview()
    {
        PianoPreviewKeyboard input = new();
        input.KeyDown(PhysicalKey.NumPadMultiply,
            KeyModifiers.None, false).Should().BeNull();
        input.BaseOctave.Should().Be(5);
        input.KeyDown(PhysicalKey.Z,
            KeyModifiers.Control | KeyModifiers.Alt, false).Should().BeNull();
        PianoPreviewNoteOn higher = input.KeyDown(
            PhysicalKey.Z, KeyModifiers.Shift, false)
            .Should().BeOfType<PianoPreviewNoteOn>().Subject;
        higher.ChromaticNote.Should().Be(12);
        higher.PitchMultiplier.Should().Be(2);
        input.KeyDown(PhysicalKey.NumPadDivide,
            KeyModifiers.None, false).Should().BeNull();
        input.BaseOctave.Should().Be(4);
        input.KeyUp(PhysicalKey.Z)
            .Should().Be(new PianoPreviewNoteOff(higher.PressId));
    }

    [Test]
    public void NeighbourSuppressionCutsExactOrAdjacentNotesButNotMinorThird()
    {
        PianoPreviewVoiceController voices = new(100);
        PianoPreviewVoice c = Start(voices, 1, 0, false);
        PianoPreviewVoice eFlat = Start(voices, 2, 3, false);
        PianoPreviewVoice sharp = Start(voices, 3, 1, false);
        IReadOnlyList<PianoVoiceAction> actions =
            voices.Apply(new PianoPreviewNoteOn(4, 2, 1.0, true));

        actions.Select(x => x.Kind).Should().Equal(
            PianoVoiceActionKind.Cut,
            PianoVoiceActionKind.Cut,
            PianoVoiceActionKind.Cut,
            PianoVoiceActionKind.Start);
        // Relative to new D: C is two semitones away, Eb one, C# one.
        actions.Take(3).Select(x => x.Voice.VoiceId)
            .Should().BeEquivalentTo(new[] { c.VoiceId, eFlat.VoiceId, sharp.VoiceId });
        // A minor third distance does not get cut.
        PianoPreviewVoice f = Start(voices, 5, 5, false);
        IReadOnlyList<PianoVoiceAction> more =
            voices.Apply(new PianoPreviewNoteOn(6, 2, 1.0, true));
        more.Where(x => x.Kind == PianoVoiceActionKind.Cut)
            .Should().NotContain(x => x.Voice.VoiceId == f.VoiceId);
    }

    [Test]
    public void SuppressionCutsReleasedTailsAndOldKeyUpIsHarmless()
    {
        PianoPreviewVoiceController voices = new(200);
        PianoPreviewVoice first = Start(voices, 1, 4, false);
        voices.Apply(new PianoPreviewNoteOff(1)).Should().ContainSingle()
            .Which.Kind.Should().Be(PianoVoiceActionKind.Release);
        IReadOnlyList<PianoVoiceAction> displaced =
            voices.Apply(new PianoPreviewNoteOn(2, 5, 1.0, true));
        displaced.Should().Contain(a =>
            a.Kind == PianoVoiceActionKind.Cut
            && a.Voice.VoiceId == first.VoiceId);
        voices.Apply(new PianoPreviewNoteOff(1)).Should().BeEmpty();
    }

    [Test]
    public void StopAllCutsEveryVoiceWithoutRearmingTheHeldKeyboardKey()
    {
        PianoPreviewKeyboard input = new();
        PianoPreviewVoiceController voices = new(300);
        PianoPreviewNoteOn press = input.KeyDown(
            PhysicalKey.Z, KeyModifiers.None, false)
            .Should().BeOfType<PianoPreviewNoteOn>().Subject;
        voices.Apply(press).Should().ContainSingle();
        IReadOnlyList<PianoVoiceAction> stopped =
            voices.Apply(new PianoPreviewStopAll());
        stopped.Should().ContainSingle()
            .Which.Kind.Should().Be(PianoVoiceActionKind.Cut);
        input.KeyDown(PhysicalKey.Z, KeyModifiers.None, false)
            .Should().BeNull("held physical keys must not restart after F8");
        voices.Apply(input.KeyUp(PhysicalKey.Z)!).Should().BeEmpty();
        voices.Voices.Should().BeEmpty();
        input.KeyDown(PhysicalKey.Z, KeyModifiers.None, false)
            .Should().BeOfType<PianoPreviewNoteOn>();
    }

    [Test]
    public async Task BothSourcesCompileTheSameReleaseAndCutCommands()
    {
        foreach (uint baseId in new[]
        {
            PianoPreviewPlaybackController.SynthVoiceIdBase,
            PianoPreviewPlaybackController.SampleVoiceIdBase,
        })
        {
            List<LivePlaybackEvent> sent = [];
            PianoPreviewPlaybackController playback = new(
                new ObjectId(73), baseId,
                new PatternLiveAuditionActions(e =>
                {
                    sent.Add(e);
                    return Task.CompletedTask;
                }));
            await playback.HandleAsync(new PianoPreviewNoteOn(
                1, 0, 1.0, SuppressNeighbours: false));
            await playback.HandleAsync(new PianoPreviewNoteOff(1));
            await playback.HandleAsync(new PianoPreviewNoteOn(
                2, 4, Math.Pow(2, 4.0 / 12), SuppressNeighbours: true));
            await playback.HandleAsync(new PianoPreviewStopAll());

            sent.Should().HaveCount(4);
            sent[0].Commands.Should().ContainSingle()
                .Which.Should().BeOfType<StartNoteCommand>();
            sent[1].Commands.Should().ContainSingle()
                .Which.Should().BeOfType<NoteOffCommand>();
            sent[2].Commands.Should().ContainSingle()
                .Which.Should().BeOfType<StartNoteCommand>();
            sent[3].Commands.Should().ContainSingle()
                .Which.Should().BeOfType<NoteCutCommand>();
            sent[0].Target.Should().Be(ChannelTarget.Virtual(baseId));
            sent[2].Target.Should().Be(ChannelTarget.Virtual(baseId + 1));
        }
    }

    [Test]
    public async Task RestartAfterSampleLoopChangePreservesEachVoiceIdentityAndPitch()
    {
        List<LivePlaybackEvent> sent = [];
        PianoPreviewPlaybackController playback = new(
            new ObjectId(18), PianoPreviewPlaybackController.SampleVoiceIdBase,
            new PatternLiveAuditionActions(e =>
            {
                sent.Add(e);
                return Task.CompletedTask;
            }));
        await playback.HandleAsync(new PianoPreviewNoteOn(1, 0, 1, false));
        await playback.HandleAsync(new PianoPreviewNoteOn(2, 7,
            Math.Pow(2, 7.0 / 12), false));
        await playback.HandleAsync(new PianoPreviewNoteOff(1));
        await playback.RestartSoundingAsync();
        sent.Should().HaveCount(4);
        sent[^1].Target.Should().Be(sent[1].Target);
        sent[^1].Commands.Should().Equal(
            new NoteCutCommand(),
            new StartNoteCommand(new ObjectId(18),
                PitchMultiplier: Math.Pow(2, 7.0 / 12)));
    }

    private static PianoPreviewVoice Start(
        PianoPreviewVoiceController voices, uint id, int pitch, bool suppress)
        => voices.Apply(new PianoPreviewNoteOn(id, pitch, 1.0, suppress))
            .Single(a => a.Kind == PianoVoiceActionKind.Start).Voice;
}
