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
	public void TrackerKeyWithoutCapsLockDoesNotPreview()
	{
		HeldNotePreviewKeyState state = new();

		state.KeyDown(
				PhysicalKey.Z,
				baseOctave: 4)
			.Should().BeNull();
		state.ActiveNoteCount.Should().Be(0);
	}

	[Test]
	public void CapsLockHeldMakesTrackerKeyStartPreview()
	{
		HeldNotePreviewKeyState state = new();
		state.KeyDown(
			PhysicalKey.CapsLock,
			baseOctave: 4);

		HeldNotePreviewAction? action =
			state.KeyDown(
				PhysicalKey.Z,
				baseOctave: 4);

		action.Should().Be(
			new StartHeldNotePreviewAction(
				PhysicalKey.Z,
				VoiceId: 0,
				PitchMultiplier: 1.0));
		state.ActiveNoteCount.Should().Be(1);
	}

	[Test]
	public void RepeatedKeyDownDoesNotRestartHeldPreview()
	{
		HeldNotePreviewKeyState state = new();
		state.KeyDown(PhysicalKey.CapsLock, 4);
		state.KeyDown(PhysicalKey.X, 4);

		state.KeyDown(
				PhysicalKey.X,
				baseOctave: 4)
			.Should().BeNull();
		state.ActiveNoteCount.Should().Be(1);
	}

	[Test]
	public void KeyUpReleasesPreviewEvenAfterCapsLockWasReleased()
	{
		HeldNotePreviewKeyState state = new();
		state.KeyDown(PhysicalKey.CapsLock, 4);
		state.KeyDown(PhysicalKey.S, 4);
		state.KeyUp(PhysicalKey.CapsLock);

		state.KeyUp(PhysicalKey.S)
			.Should().Be(
				new ReleaseHeldNotePreviewAction(
					PhysicalKey.S,
					VoiceId: 1));
		state.ActiveNoteCount.Should().Be(0);
	}


	[Test]
	public void ReleaseAllStopsEveryHeldVoiceAndClearsModifierState()
	{
		HeldNotePreviewKeyState state = new();
		state.KeyDown(PhysicalKey.CapsLock, 4);
		state.KeyDown(PhysicalKey.Z, 4);
		state.KeyDown(PhysicalKey.X, 4);

		ReleaseHeldNotePreviewAction[] releases =
			state.ReleaseAll()
				.OrderBy(action => action.VoiceId)
				.ToArray();

		releases.Should().Equal(
			new ReleaseHeldNotePreviewAction(
				PhysicalKey.Z,
				VoiceId: 0),
			new ReleaseHeldNotePreviewAction(
				PhysicalKey.X,
				VoiceId: 2));
		state.ActiveNoteCount.Should().Be(0);
		state.CapsLockHeld.Should().BeFalse();
		state.KeyUp(PhysicalKey.Z).Should().BeNull();
	}


	[Test]
	public void AdditionalHeldKeyUsesIndependentVirtualVoice()
	{
		HeldNotePreviewKeyState state = new();
		state.KeyDown(PhysicalKey.CapsLock, 4);
		state.KeyDown(PhysicalKey.Z, 4);

		HeldNotePreviewAction? second =
			state.KeyDown(
				PhysicalKey.X,
				baseOctave: 4);

		StartHeldNotePreviewAction start =
			second.Should()
				.BeOfType<StartHeldNotePreviewAction>()
				.Subject;
		start.VoiceId.Should().Be(2);
		start.PitchMultiplier.Should().BeApproximately(
			Math.Pow(2.0, 2.0 / 12.0),
			1e-12);
		state.ActiveNoteCount.Should().Be(2);
	}
}
