using System;
using System.Linq;

using Avalonia.Input;
using AwesomeAssertions;

using Heresy.Core.Objects;
using Heresy.Core.Sequencing;
using Heresy.UserInterface.SampleEditing;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class SampleAuditionKeyboardTests
{
	[Test]
	public void PhysicalKeyUpDoesNotStopVoiceAndNewDownGetsUniqueVoice()
	{
		SampleAuditionKeyboard keyboard = new();
		SampleAuditionVoice first = keyboard.KeyDown(
			PhysicalKey.Z, KeyModifiers.None, looping: false)!.Note;
		keyboard.KeyDown(PhysicalKey.Z, KeyModifiers.None, looping: false)
			.Should().BeNull("held-key auto-repeat must be ignored");
		keyboard.KeyUp(PhysicalKey.Z).Should().BeTrue();
		keyboard.KeyUp(PhysicalKey.Z).Should().BeFalse();
		keyboard.PressedKeyCount.Should().Be(0);
		keyboard.OngoingNotes.Should().ContainSingle();

		SampleAuditionStart next = keyboard.KeyDown(
			PhysicalKey.Z, KeyModifiers.None, looping: false)!;
		next.Displaced.Should().BeEmpty();
		next.Note.VoiceId.Should().NotBe(first.VoiceId);
		next.Note.ChromaticIndex.Should().Be(first.ChromaticIndex);
		keyboard.OngoingNotes.Should().Equal(first, next.Note);
	}

	[Test]
	public void LoopingCutsOnlyNotesStrictlyCloserThanMinorThird()
	{
		SampleAuditionKeyboard keyboard = new();
		SampleAuditionVoice c = keyboard.KeyDown(
			PhysicalKey.Z, KeyModifiers.None, looping: true)!.Note;
		keyboard.KeyUp(PhysicalKey.Z);
		SampleAuditionVoice eFlat = keyboard.KeyDown(
			PhysicalKey.D, KeyModifiers.None, looping: true)!.Note;
		keyboard.KeyUp(PhysicalKey.D);
		keyboard.OngoingNotes.Should().Equal(c, eFlat);

		SampleAuditionStart d = keyboard.KeyDown(
			PhysicalKey.X, KeyModifiers.None, looping: true)!;
		// D is two semitones from C and one from E-flat.
		d.Displaced.Should().Equal(new[] { c, eFlat });
		keyboard.OngoingNotes.Should().Equal(d.Note);

		keyboard.KeyUp(PhysicalKey.X);
		SampleAuditionStart f = keyboard.KeyDown(
			PhysicalKey.V, KeyModifiers.None, looping: true)!;
		f.Displaced.Should().BeEmpty("F is exactly three semitones above D");
		keyboard.OngoingNotes.Should().Equal(d.Note, f.Note);
	}

	[Test]
	public void LoopingTreatsAllOctavesAndRepeatedNotesAsChromaticDistances()
	{
		SampleAuditionKeyboard keyboard = new();
		SampleAuditionVoice b = keyboard.KeyDown(
			PhysicalKey.M, KeyModifiers.None, looping: true)!.Note;
		keyboard.KeyUp(PhysicalKey.M);
		SampleAuditionStart c5 = keyboard.KeyDown(
			PhysicalKey.Q, KeyModifiers.None, looping: true)!;
		// B4 and C5 are separated by only one semitone.
		c5.Displaced.Should().Equal(new[] { b });
		keyboard.KeyUp(PhysicalKey.Q);
		SampleAuditionStart repeated = keyboard.KeyDown(
			PhysicalKey.Q, KeyModifiers.None, looping: true)!;
		repeated.Displaced.Should().Equal(c5.Note);
	}

	[Test]
	public void NoLoopModeNeverDisplacesNearbyNotes()
	{
		SampleAuditionKeyboard keyboard = new();
		SampleAuditionVoice c = keyboard.KeyDown(
			PhysicalKey.Z, KeyModifiers.None, looping: false)!.Note;
		keyboard.KeyUp(PhysicalKey.Z);
		SampleAuditionStart sharp = keyboard.KeyDown(
			PhysicalKey.S, KeyModifiers.None, looping: false)!;
		sharp.Displaced.Should().BeEmpty();
		keyboard.KeyUp(PhysicalKey.S);
		SampleAuditionStart again = keyboard.KeyDown(
			PhysicalKey.Z, KeyModifiers.None, looping: false)!;
		again.Displaced.Should().BeEmpty();
		keyboard.OngoingNotes.Should().Equal(c, sharp.Note, again.Note);
	}

	[Test]
	public void OctaveAdjustmentPreservesEarlierPitchForLaterLoopRestart()
	{
		SampleAuditionKeyboard keyboard = new();
		SampleAuditionVoice first = keyboard.KeyDown(
			PhysicalKey.Z, KeyModifiers.None, looping: false)!.Note;
		keyboard.KeyUp(PhysicalKey.Z);
		keyboard.KeyDown(PhysicalKey.NumPadMultiply,
			KeyModifiers.None, looping: false).Should().BeNull();
		SampleAuditionVoice second = keyboard.KeyDown(
			PhysicalKey.Z, KeyModifiers.None, looping: false)!.Note;
		first.PitchMultiplier.Should().Be(1.0);
		second.PitchMultiplier.Should().Be(2.0);
		keyboard.OngoingNotes.Should().Equal(first, second);
		ObjectId sample = new(42);
		SampleAuditionCompiler.Start(sample, first, restart: true).Commands
			.Should().Equal(new NoteCutCommand(),
				new StartNoteCommand(sample, PitchMultiplier: 1.0));
		SampleAuditionCompiler.Start(sample, second, restart: true).Commands
			.Should().Equal(new NoteCutCommand(),
				new StartNoteCommand(sample, PitchMultiplier: 2.0));
	}

	[Test]
	public void StopAllCutsPersistentVoicesAndClearsKeyPressTracking()
	{
		SampleAuditionKeyboard keyboard = new();
		SampleAuditionVoice a = keyboard.KeyDown(
			PhysicalKey.Z, KeyModifiers.None, looping: true)!.Note;
		keyboard.KeyUp(PhysicalKey.Z);
		SampleAuditionVoice b = keyboard.KeyDown(
			PhysicalKey.C, KeyModifiers.None, looping: true)!.Note;
		keyboard.StopAll().Should().Equal(a, b);
		keyboard.OngoingNotes.Should().BeEmpty();
		keyboard.PressedKeyCount.Should().Be(0);
		keyboard.KeyUp(PhysicalKey.C).Should().BeFalse();
		SampleAuditionCompiler.Cut(a).Commands.Should().ContainSingle()
			.Which.Should().BeOfType<NoteCutCommand>();
		SampleAuditionCompiler.Cut(a).Target.Should().Be(
			ChannelTarget.Virtual(a.VoiceId));
	}
}
