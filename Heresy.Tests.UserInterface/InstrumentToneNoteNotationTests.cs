using System;
using System.Linq;

using AwesomeAssertions;

using Heresy.UserInterface.InstrumentEditing;
using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class InstrumentToneNoteNotationTests
{
	[TestCase(12, new[] { "C-4", "C#-4", "D-4", "D#-4" })]
	[TestCase(24, new[] { "C-4", "C-4+", "C#-4", "C#-4+", "D-4" })]
	[TestCase(36, new[] { "C-4", "C-4+", "C#-4-", "C#-4", "C#-4+", "D-4-" })]
	[TestCase(48, new[] { "C-4", "C-4+", "C-4++", "C#-4-", "C#-4", "C#-4+", "C#-4++", "D-4-" })]
	public void DynamicSuffixesCountActualInstrumentIndexSteps(
		int divisions, string[] expected)
	{
		Enumerable.Range(0, expected.Length).Select(i =>
			InstrumentToneNoteNotation.Format(i, divisions, 0))
			.Should().Equal(expected);
	}

	[Test]
	public void FullFortyEightDivisionFixtureMatchesRequestedChromaticTieRule()
	{
		string[] expected =
		[
			"C-1", "C-1+", "C-1++", "C#-1-",
			"C#-1", "C#-1+", "C#-1++", "D-1-",
			"D-1", "D-1+", "D-1++", "D#-1-",
			"D#-1", "D#-1+", "D#-1++", "E-1-",
			"E-1", "E-1+", "E-1++", "F-1-",
			"F-1", "F-1+", "F-1++", "F#-1-",
			"F#-1", "F#-1+", "F#-1++", "G-1-",
			"G-1", "G-1+", "G-1++", "G#-1-",
			"G#-1", "G#-1+", "G#-1++", "A-1-",
			"A-1", "A-1+", "A-1++", "A#-1-",
			"A#-1", "A#-1+", "A#-1++", "B-1-",
			"B-1", "B-1+", "B-1++",
		];
		Enumerable.Range(0, 48).Select(i =>
			InstrumentToneNoteNotation.Format(i + 144, 48, 0))
			.Should().Equal(expected);
	}

	[Test]
	public void OffsetAndFractionalDivisionsAreIncludedInChromaticLookup()
	{
		InstrumentToneNoteNotation.Format(10, 12, 10)
			.Should().Be("C-4");
		InstrumentToneNoteNotation.Format(12, 24, 10)
			.Should().Be("C-4+");
		InstrumentToneNoteNotation.Format(10, 19, 10)
			.Should().Be("C-4");
		InstrumentToneNoteNotation.Format(12, 19, 10)
			.Should().Be("C#-4");
		InstrumentToneNoteNotation.Format(0, 12, 36)
			.Should().Be("C-1");
	}

	[Test]
	public void PitchOptionsAndSelectionUseActualDivisionResolution()
	{
		double entered = 0.107;
		var choices = InstrumentToneNoteNotation.Options(10, 48, 10, entered);
		choices.Should().NotBeEmpty();
		choices.Should().OnlyContain(c =>
			Math.Abs(c.LogarithmicOffset - entered) <= 0.3 + 1e-9);
		var selected = InstrumentToneNoteNotation.Nearest(choices, entered)!;
		selected.Index.Should().Be(15); // +5 of 48
		selected.Multiplier.Should().BeApproximately(
			Math.Pow(2, 5.0 / 48), 1e-12);
		InstrumentToneNoteNotation.MultiplierFromOffset(entered)
			.Should().NotBe(selected.Multiplier);
		// Only an explicit user selection snaps the multiplier.
		InstrumentToneNoteNotation.LogarithmicOffset(selected.Multiplier)
			.Should().BeApproximately(5.0 / 48, 1e-12);
	}

	[Test]
	public void NearestNoteTieChoosesLowerOption()
	{
		var choices = InstrumentToneNoteNotation.Options(20, 24, 20, 0);
		InstrumentToneNoteNotation.Nearest(choices, 0.5 / 24)!
			.Index.Should().Be(20);
	}

	[Test]
	public void InvalidLogarithmicOffsetsAreRejected()
	{
		Action negativeMultiplier = () =>
			InstrumentToneNoteNotation.LogarithmicOffset(0);
		negativeMultiplier.Should().Throw<ArgumentOutOfRangeException>();
		Action nan = () =>
			InstrumentToneNoteNotation.MultiplierFromOffset(double.NaN);
		nan.Should().Throw<ArgumentOutOfRangeException>();
	}
}
