using System;
using System.Linq;

using AwesomeAssertions;

using Avalonia.Input;

using Heresy.Core.FmSynthesis;
using Heresy.Core.Sequencing;
using Heresy.UserInterface.FmEditing;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class FmSynthAuditionKeyboardTests
{
	[Test]
	public void TrackerKeysAuditionOnlyWithinTestAreaWithoutModifier()
	{
		FmSynthAuditionKeyboard state = new();

		state.KeyDown(PhysicalKey.Z, KeyModifiers.None)
			.Should().Be(new StartFmSynthAuditionNote(
				PhysicalKey.Z,
				FmSynthAuditionKeyboard.VoiceIdBase,
				1.0));
		state.KeyDown(PhysicalKey.S, KeyModifiers.Shift)
			.Should().BeOfType<StartFmSynthAuditionNote>()
			.Which.PitchMultiplier.Should().BeApproximately(
				Math.Pow(2, 1.0 / 12), 1e-12);
		state.KeyDown(PhysicalKey.Digit2, KeyModifiers.None)
			.Should().BeOfType<StartFmSynthAuditionNote>()
			.Which.PitchMultiplier.Should().BeApproximately(
				Math.Pow(2, 13.0 / 12), 1e-12);
		state.ActiveNoteCount.Should().Be(3);

		state.KeyDown(PhysicalKey.C, KeyModifiers.Control).Should().BeNull();
		state.KeyDown(PhysicalKey.Z, KeyModifiers.Alt).Should().BeNull();
		state.KeyDown(PhysicalKey.F5, KeyModifiers.None).Should().BeNull();
		state.ActiveNoteCount.Should().Be(3);
	}

	[Test]
	public void AutorepeatDoesNotRetriggerAndKeyUpReleasesCorrectVoice()
	{
		FmSynthAuditionKeyboard state = new();
		StartFmSynthAuditionNote start =
			state.KeyDown(PhysicalKey.X, KeyModifiers.None)
				.Should().BeOfType<StartFmSynthAuditionNote>().Subject;

		state.KeyDown(PhysicalKey.X, KeyModifiers.None).Should().BeNull();
		state.ActiveNoteCount.Should().Be(1);
		state.KeyUp(PhysicalKey.X)
			.Should().Be(new ReleaseFmSynthAuditionNote(
				PhysicalKey.X, start.VoiceId));
		state.KeyUp(PhysicalKey.X).Should().BeNull();
		state.ActiveNoteCount.Should().Be(0);
	}

	[Test]
	public void NumpadOctaveKeysUseTrackerBoundsAndDoNotRetriggerHeldNotes()
	{
		FmSynthAuditionKeyboard state = new();
		state.BaseOctave.Should().Be(4);
		StartFmSynthAuditionNote held =
			state.KeyDown(PhysicalKey.Z, KeyModifiers.None)
				.Should().BeOfType<StartFmSynthAuditionNote>().Subject;

		state.KeyDown(PhysicalKey.NumPadMultiply, KeyModifiers.None)
			.Should().BeNull();
		state.BaseOctave.Should().Be(5);
		state.KeyDown(PhysicalKey.Z, KeyModifiers.None).Should().BeNull();
		state.KeyUp(PhysicalKey.Z).Should().Be(
			new ReleaseFmSynthAuditionNote(PhysicalKey.Z, held.VoiceId));

		state.KeyDown(PhysicalKey.Z, KeyModifiers.None)
			.Should().BeOfType<StartFmSynthAuditionNote>()
			.Which.PitchMultiplier.Should().Be(2.0);
		state.KeyDown(PhysicalKey.NumPadDivide, KeyModifiers.Control)
			.Should().BeNull();
		state.BaseOctave.Should().Be(5);

		for (int i = 0; i < 20; i++)
			state.KeyDown(PhysicalKey.NumPadDivide, KeyModifiers.None);
		state.BaseOctave.Should().Be(0);
		for (int i = 0; i < 20; i++)
			state.KeyDown(PhysicalKey.NumPadMultiply, KeyModifiers.None);
		state.BaseOctave.Should().Be(8);
	}

	[Test]
	public void ReleaseAllEndsIndependentVoicesAndClearsHeldKeys()
	{
		FmSynthAuditionKeyboard state = new();
		state.KeyDown(PhysicalKey.Z, KeyModifiers.None);
		state.KeyDown(PhysicalKey.X, KeyModifiers.None);

		state.ReleaseAll().OrderBy(x => x.VoiceId).Should().Equal(
			new ReleaseFmSynthAuditionNote(
				PhysicalKey.Z, FmSynthAuditionKeyboard.VoiceIdBase),
			new ReleaseFmSynthAuditionNote(
				PhysicalKey.X, FmSynthAuditionKeyboard.VoiceIdBase + 2));
		state.ReleaseAll().Should().BeEmpty();
		state.KeyUp(PhysicalKey.Z).Should().BeNull();
		state.ActiveNoteCount.Should().Be(0);
	}

	[Test]
	public void CompileUsesFmSynthSourceAndSameVirtualVoiceForNoteOff()
	{
		FmSynthAuditionKeyboard state = new();
		StartFmSynthAuditionNote start =
			state.KeyDown(PhysicalKey.M, KeyModifiers.None)
				.Should().BeOfType<StartFmSynthAuditionNote>().Subject;
		var source = new Heresy.Core.Objects.ObjectId(42);
		var startEvent = FmSynthAuditionCompiler.Start(source, start);
		var releaseEvent = FmSynthAuditionCompiler.Release(
			new ReleaseFmSynthAuditionNote(start.Key, start.VoiceId));

		startEvent.Target.Should().Be(ChannelTarget.Virtual(start.VoiceId));
		startEvent.Commands.Should().ContainSingle()
			.Which.Should().Be(new StartNoteCommand(
				source,
				PitchMultiplier: Math.Pow(2, 11.0 / 12)));
		releaseEvent.Target.Should().Be(startEvent.Target);
		releaseEvent.Commands.Should().ContainSingle()
			.Which.Should().BeOfType<NoteOffCommand>();
	}
}
