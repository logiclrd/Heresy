using System;
using System.Linq;

using AwesomeAssertions;
using Avalonia.Input;

using Heresy.UserInterface.Keyboard;
using Heresy.UserInterface.PatternEditing;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class HeldPhysicalKeyRepeatFilterTests
{
    [Test]
    public void NormalDuplicateKeydownsStayHeldUntilConfirmedRelease()
    {
        HeldPhysicalKeyRepeatState state = new();
        state.KeyDown(PhysicalKey.Z).Should().BeTrue();
        state.KeyDown(PhysicalKey.Z).Should().BeFalse();
        state.IsHeld(PhysicalKey.Z).Should().BeTrue();
        state.KeyUp(PhysicalKey.Z, 100).Should().BeTrue();
        state.CollectReleased(139).Should().BeEmpty();
        state.CollectReleased(140).Should().Equal(PhysicalKey.Z);
        state.IsHeld(PhysicalKey.Z).Should().BeFalse();
        state.KeyDown(PhysicalKey.Z).Should().BeTrue();
    }

    [Test]
    public void XephyrReleasePressPairsDoNotCauseSynthNoteOffOrRestart()
    {
        HeldPhysicalKeyRepeatState state = new();
        PianoPreviewKeyboard synth = new();
        int starts = 0, releases = 0;
        if (state.KeyDown(PhysicalKey.Z))
        {
            synth.KeyDown(PhysicalKey.Z, KeyModifiers.None, false)
                .Should().BeOfType<PianoPreviewNoteOn>();
            starts++;
        }
        for (int i = 0; i < 12; i++)
        {
            long time = 100 + i * 40L;
            state.KeyUp(PhysicalKey.Z, time).Should().BeTrue();
            state.CollectReleased(time + 1).Should().BeEmpty();
            if (state.KeyDown(PhysicalKey.Z))
            {
                synth.KeyDown(PhysicalKey.Z, KeyModifiers.None, false);
                starts++;
            }
        }
        starts.Should().Be(1);
        synth.HeldCount.Should().Be(1);
        state.KeyUp(PhysicalKey.Z, 800).Should().BeTrue();
        foreach (PhysicalKey key in state.CollectReleased(840))
            if (synth.KeyUp(key) is PianoPreviewNoteOff)
                releases++;
        releases.Should().Be(1);
        synth.HeldCount.Should().Be(0);
        state.KeyDown(PhysicalKey.Z).Should().BeTrue();
        synth.KeyDown(PhysicalKey.Z, KeyModifiers.None, false)
            .Should().BeOfType<PianoPreviewNoteOn>();
    }

    [Test]
    public void LatchedSamplePreviewDoesNotStartAdditionalVoicesOnRepeat()
    {
        HeldPhysicalKeyRepeatState state = new();
        PianoPreviewKeyboard sample = new();
        PianoPreviewVoiceController voice = new(500);
        state.KeyDown(PhysicalKey.X).Should().BeTrue();
        voice.Apply(sample.KeyDown(PhysicalKey.X, KeyModifiers.None, true)!)
            .Should().ContainSingle();
        for (int i = 0; i < 20; i++)
        {
            state.KeyUp(PhysicalKey.X, i * 25L).Should().BeTrue();
            state.KeyDown(PhysicalKey.X).Should().BeFalse();
        }
        sample.HeldCount.Should().Be(1);
        voice.Voices.Should().ContainSingle();
        state.KeyUp(PhysicalKey.X, 550).Should().BeTrue();
        foreach (PhysicalKey key in state.CollectReleased(590))
            sample.KeyUp(key).Should().BeNull();
        sample.HeldCount.Should().Be(0);
        voice.Voices.Should().ContainSingle();
        state.KeyDown(PhysicalKey.X).Should().BeTrue();
        voice.Apply(sample.KeyDown(PhysicalKey.X, KeyModifiers.None, true)!)
            .Select(x => x.Kind).Should().Equal(
                PianoVoiceActionKind.Cut, PianoVoiceActionKind.Start);
        voice.Voices.Should().ContainSingle(
            "the exact-pitch neighbour is suppressed, not stacked");
    }

    [Test]
    public void ShiftPreviewAndFollowToggleAreIndependentAndRepeatSafe()
    {
        HeldPhysicalKeyRepeatState previewKeys = new();
        HeldNotePreviewKeyState preview = new();
        HeldPhysicalKeyRepeatState follow = new();

        previewKeys.KeyDown(PhysicalKey.Z).Should().BeTrue();
        preview.KeyDown(PhysicalKey.Z, 4, KeyModifiers.Shift)
            .Should().BeOfType<StartHeldNotePreviewAction>();
        previewKeys.KeyUp(PhysicalKey.Z, 5).Should().BeTrue();
        previewKeys.KeyDown(PhysicalKey.Z).Should().BeFalse();
        preview.KeyDown(PhysicalKey.Z, 4, KeyModifiers.None).Should().BeNull();
        preview.ActiveNoteCount.Should().Be(1);

        int toggles = 0;
        if (follow.KeyDown(PhysicalKey.NumPadDecimal)) toggles++;
        for (int i = 0; i < 8; i++)
        {
            follow.KeyUp(PhysicalKey.NumPadDecimal, 100 + i * 40)
                .Should().BeTrue();
            if (follow.KeyDown(PhysicalKey.NumPadDecimal)) toggles++;
        }
        toggles.Should().Be(1);
        follow.KeyUp(PhysicalKey.NumPadDecimal, 500).Should().BeTrue();
        follow.CollectReleased(540).Should().Equal(PhysicalKey.NumPadDecimal);
        if (follow.KeyDown(PhysicalKey.NumPadDecimal)) toggles++;
        toggles.Should().Be(2);
    }

    [Test]
    public void IndependentReleaseTimersAndFocusCleanupDoNotLeakOldReleases()
    {
        HeldPhysicalKeyRepeatState state = new();
        state.KeyDown(PhysicalKey.Z).Should().BeTrue();
        state.KeyDown(PhysicalKey.X).Should().BeTrue();
        state.KeyUp(PhysicalKey.Z, 0).Should().BeTrue();
        state.KeyUp(PhysicalKey.X, 20).Should().BeTrue();
        state.CollectReleased(40).Should().Equal(PhysicalKey.Z);
        state.IsHeld(PhysicalKey.X).Should().BeTrue();
        state.KeyDown(PhysicalKey.X).Should().BeFalse();
        state.CollectReleased(200).Should().BeEmpty();

        state.KeyUp(PhysicalKey.X, 210).Should().BeTrue();
        state.Clear();
        state.KeyDown(PhysicalKey.X).Should().BeTrue();
        state.CollectReleased(1000).Should().BeEmpty();
        state.HeldCount.Should().Be(1);
        state.PendingReleaseCount.Should().Be(0);
        state.KeyUp(PhysicalKey.Z, 1000).Should().BeFalse();
    }

    [Test]
    public void RepeatedKeyupEventsCannotPostponeRealRelease()
    {
        HeldPhysicalKeyRepeatState state = new();
        state.KeyDown(PhysicalKey.Z).Should().BeTrue();
        state.KeyUp(PhysicalKey.Z, 10).Should().BeTrue();
        state.KeyUp(PhysicalKey.Z, 30).Should().BeTrue();
        state.CollectReleased(49).Should().BeEmpty();
        state.CollectReleased(50).Should().Equal(PhysicalKey.Z);
    }
}
