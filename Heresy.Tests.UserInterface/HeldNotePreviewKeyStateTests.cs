using System;
using System.Linq;

using AwesomeAssertions;
using Avalonia.Input;
using Heresy.UserInterface.PatternEditing;
using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class HeldNotePreviewKeyStateTests
{
    [Test]
    public void OrdinaryTrackerKeyOrCapsLockNeverEnablesPreview()
    {
        HeldNotePreviewKeyState state = new();
        state.KeyDown(PhysicalKey.Z, 4, KeyModifiers.None).Should().BeNull();
        state.KeyDown(PhysicalKey.CapsLock, 4, KeyModifiers.None).Should().BeNull();
        state.KeyDown(PhysicalKey.X, 4, KeyModifiers.None).Should().BeNull();
        state.ActiveNoteCount.Should().Be(0);
    }

    [Test]
    public void ShiftPianoEnablesPreviewWithoutTreatingCtrlAltShortcutsAsNotes()
    {
        HeldNotePreviewKeyState state = new();
        PatternPreviewKeyRouting.IsPreviewOnly(
            state, PhysicalKey.Z, KeyModifiers.Shift).Should().BeTrue();
        PatternPreviewKeyRouting.IsPreviewOnly(
            state, PhysicalKey.Z, KeyModifiers.Control | KeyModifiers.Shift)
            .Should().BeFalse();
        PatternPreviewKeyRouting.IsPreviewOnly(
            state, PhysicalKey.Z, KeyModifiers.Alt | KeyModifiers.Shift)
            .Should().BeFalse();
        PatternPreviewKeyRouting.IsPreviewOnly(
            state, PhysicalKey.Tab, KeyModifiers.Shift).Should().BeFalse();
        PatternPreviewKeyRouting.IsPreviewOnly(
            state, PhysicalKey.Left, KeyModifiers.Shift).Should().BeFalse();

        state.KeyDown(PhysicalKey.Z, 4, KeyModifiers.Shift)
            .Should().Be(new StartHeldNotePreviewAction(
                PhysicalKey.Z, 0, 1.0));
        state.ActiveNoteCount.Should().Be(1);
    }

    [Test]
    public void RepeatsRemainPreviewOnlyEvenIfShiftReleasedBeforePianoKey()
    {
        HeldNotePreviewKeyState state = new();
        state.KeyDown(PhysicalKey.X, 4, KeyModifiers.Shift)
            .Should().BeOfType<StartHeldNotePreviewAction>();
        PatternPreviewKeyRouting.IsPreviewOnly(
            state, PhysicalKey.X, KeyModifiers.None).Should().BeTrue();
        state.KeyDown(PhysicalKey.X, 4, KeyModifiers.None).Should().BeNull();
        state.ActiveNoteCount.Should().Be(1);
        state.KeyUp(PhysicalKey.X)
            .Should().Be(new ReleaseHeldNotePreviewAction(
                PhysicalKey.X, 2));
        state.KeyUp(PhysicalKey.X).Should().BeNull();
        PatternPreviewKeyRouting.IsPreviewOnly(
            state, PhysicalKey.X, KeyModifiers.None).Should().BeFalse();
    }

    [Test]
    public void ReleaseAllStopsEachIndependentPreviewAndClearsKeys()
    {
        HeldNotePreviewKeyState state = new();
        state.KeyDown(PhysicalKey.Z, 4, KeyModifiers.Shift);
        state.KeyDown(PhysicalKey.X, 4, KeyModifiers.Shift);
        state.ReleaseAll().OrderBy(e => e.VoiceId).Should().Equal(
            new ReleaseHeldNotePreviewAction(PhysicalKey.Z, 0),
            new ReleaseHeldNotePreviewAction(PhysicalKey.X, 2));
        state.ActiveNoteCount.Should().Be(0);
        state.KeyUp(PhysicalKey.Z).Should().BeNull();
        state.KeyDown(PhysicalKey.Z, 4, KeyModifiers.Shift)
            .Should().BeOfType<StartHeldNotePreviewAction>();
    }

    [Test]
    public void PreviewPitchCapturedAtOriginalOctave()
    {
        HeldNotePreviewKeyState state = new();
        state.KeyDown(PhysicalKey.Z, 4, KeyModifiers.Shift);
        StartHeldNotePreviewAction second =
            state.KeyDown(PhysicalKey.X, 5, KeyModifiers.Shift)
                .Should().BeOfType<StartHeldNotePreviewAction>().Subject;
        second.PitchMultiplier.Should().BeApproximately(
            Math.Pow(2, 14.0 / 12), 1e-12);
        state.ActiveNoteCount.Should().Be(2);
    }
}
