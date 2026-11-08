using System;
using System.Linq;

using AwesomeAssertions;
using Avalonia.Input;

using Heresy.UserInterface.FmEditing;
using Heresy.UserInterface.Keyboard;
using Heresy.UserInterface.PatternEditing;
using Heresy.UserInterface.SampleEditing;

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
        state.IsHeld(PhysicalKey.Z).Should().BeTrue();
        state.CollectReleased(140).Should().Equal(PhysicalKey.Z);
        state.IsHeld(PhysicalKey.Z).Should().BeFalse();
        state.KeyDown(PhysicalKey.Z).Should().BeTrue();
    }

    [Test]
    public void XephyrReleasePressPairsDoNotCauseNoteOffOrRestart()
    {
        HeldPhysicalKeyRepeatState state = new();
        FmSynthAuditionKeyboard synth = new();
        int starts = 0, releases = 0;
        if (state.KeyDown(PhysicalKey.Z))
        {
            synth.KeyDown(PhysicalKey.Z, KeyModifiers.None).Should().NotBeNull();
            starts++;
        }
        // X server: Press, synthetic Release+Press, Release+Press, real Release.
        for (int i = 0; i < 12; i++)
        {
            long time = 100 + i * 40L;
            state.KeyUp(PhysicalKey.Z, time).Should().BeTrue();
            state.CollectReleased(time + 1).Should().BeEmpty();
            if (state.KeyDown(PhysicalKey.Z))
            {
                synth.KeyDown(PhysicalKey.Z, KeyModifiers.None);
                starts++;
            }
        }
        starts.Should().Be(1);
        synth.ActiveNoteCount.Should().Be(1);
        state.KeyUp(PhysicalKey.Z, 800).Should().BeTrue();
        foreach (PhysicalKey key in state.CollectReleased(840))
            if (synth.KeyUp(key) is not null)
                releases++;
        releases.Should().Be(1);
        synth.ActiveNoteCount.Should().Be(0);
        state.KeyDown(PhysicalKey.Z).Should().BeTrue();
        synth.KeyDown(PhysicalKey.Z, KeyModifiers.None).Should().NotBeNull();
    }

    [Test]
    public void SamplePreviewDoesNotCreateAdditionalVoicesDuringRepeatPair()
    {
        HeldPhysicalKeyRepeatState state = new();
        SampleAuditionKeyboard sample = new();
        state.KeyDown(PhysicalKey.X).Should().BeTrue();
        sample.KeyDown(PhysicalKey.X, KeyModifiers.None, looping: false)
            .Should().NotBeNull();

        for (int i = 0; i < 20; i++)
        {
            state.KeyUp(PhysicalKey.X, i * 25L).Should().BeTrue();
            state.KeyDown(PhysicalKey.X).Should().BeFalse();
        }
        sample.OngoingNotes.Should().ContainSingle();
        sample.PressedKeyCount.Should().Be(1);
        state.KeyUp(PhysicalKey.X, 550).Should().BeTrue();
        foreach (PhysicalKey key in state.CollectReleased(590))
            sample.KeyUp(key).Should().BeTrue();
        sample.PressedKeyCount.Should().Be(0);
        sample.OngoingNotes.Should().ContainSingle(
            "Sample Test keeps playing released notes");
        state.KeyDown(PhysicalKey.X).Should().BeTrue();
        sample.KeyDown(PhysicalKey.X, KeyModifiers.None, looping: false)
            .Should().NotBeNull();
        sample.OngoingNotes.Should().HaveCount(2);
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
        state.CollectReleased(1000).Should().BeEmpty(
            "an old timer must not release a note after focus was regained");
        state.HeldCount.Should().Be(1);
        state.PendingReleaseCount.Should().Be(0);
        state.KeyUp(PhysicalKey.Z, 1000).Should().BeFalse();
    }

    [Test]
    public void RepeatedKeyupEventsCannotKeepPostponingRealRelease()
    {
        HeldPhysicalKeyRepeatState state = new();
        state.KeyDown(PhysicalKey.Z).Should().BeTrue();
        state.KeyUp(PhysicalKey.Z, 10).Should().BeTrue();
        state.KeyUp(PhysicalKey.Z, 30).Should().BeTrue();
        state.CollectReleased(49).Should().BeEmpty();
        state.CollectReleased(50).Should().Equal(PhysicalKey.Z);
    }
}
