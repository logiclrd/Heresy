using AwesomeAssertions;

using Avalonia.Input;

using Heresy.UserInterface.PatternEditing;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class PatternOctaveKeyboardTests
{
	[Test]
	public void NumpadMultiplyRaisesOctave()
	{
		PatternOctaveKeyboard.TryAdjust(
				PhysicalKey.NumPadMultiply,
				KeyModifiers.None,
				currentOctave: 4,
				out int octave)
			.Should().BeTrue();

		octave.Should().Be(5);
	}

	[Test]
	public void NumpadDivideLowersOctave()
	{
		PatternOctaveKeyboard.TryAdjust(
				PhysicalKey.NumPadDivide,
				KeyModifiers.None,
				currentOctave: 4,
				out int octave)
			.Should().BeTrue();

		octave.Should().Be(3);
	}

	[TestCase(PhysicalKey.NumPadMultiply, 8)]
	[TestCase(PhysicalKey.NumPadDivide, 0)]
	public void OctaveAdjustmentClampsAtTrackerLimits(
		PhysicalKey key,
		int currentOctave)
	{
		PatternOctaveKeyboard.TryAdjust(
				key,
				KeyModifiers.None,
				currentOctave,
				out int octave)
			.Should().BeTrue();

		octave.Should().Be(currentOctave);
	}


	[TestCase(PhysicalKey.NumPadMultiply)]
	[TestCase(PhysicalKey.NumPadDivide)]
	public void ModifiedNumpadOperatorsAreReservedForOtherCommands(
		PhysicalKey key)
	{
		PatternOctaveKeyboard.TryAdjust(
				key,
				KeyModifiers.Control | KeyModifiers.Alt,
				currentOctave: 4,
				out int octave)
			.Should().BeFalse();

		octave.Should().Be(4);
	}


	[TestCase(PhysicalKey.Slash)]
	[TestCase(PhysicalKey.Digit8)]
	[TestCase(PhysicalKey.Equal)]
	public void NonNumpadKeysAreNotOctaveCommands(
		PhysicalKey key)
	{
		PatternOctaveKeyboard.TryAdjust(
				key,
				KeyModifiers.None,
				currentOctave: 4,
				out int octave)
			.Should().BeFalse();

		octave.Should().Be(4);
	}
}
