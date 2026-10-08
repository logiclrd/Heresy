using System;

using AwesomeAssertions;

using Heresy.UserInterface.FmEditing;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class FmSynthParameterTextFieldTests
{
	[Test]
	public void EnterCommitBecomesTheNewEscapeBaseline()
	{
		FmSynthParameterTextField field = new("440");
		string stored = "440";

		field.Commit("880", value => stored = value).Should().BeTrue();
		field.CommittedText.Should().Be("880");
		stored.Should().Be("880");

		// Escape after refocusing may cancel a new draft, but cannot undo
		// a previously committed edit.
		field.Revert().Should().Be("880");
		field.Commit("880", value => stored = value).Should().BeFalse();
		stored.Should().Be("880");
	}

	[Test]
	public void FocusLossCommitsAndEscapeAfterReturnCannotUndo()
	{
		FmSynthParameterTextField frequency = new("440");
		FmSynthParameterTextField minimum = new("-1");
		string committedFrequency = "440";
		string committedMinimum = "-1";

		// Moving from frequency to minimum commits the prior draft.
		frequency.Commit("523.25", value => committedFrequency = value)
			.Should().BeTrue();
		// Editing the second field does not affect the first baseline.
		minimum.Commit("-0.5", value => committedMinimum = value)
			.Should().BeTrue();

		frequency.Revert().Should().Be("523.25");
		committedFrequency.Should().Be("523.25");
		committedMinimum.Should().Be("-0.5");
	}

	[Test]
	public void EscapeCancelsOnlyTheUncommittedDraft()
	{
		FmSynthParameterTextField field = new("1.0");
		int commits = 0;

		field.Revert().Should().Be("1.0");
		field.Commit(field.Revert(), _ => commits++)
			.Should().BeFalse();
		commits.Should().Be(0);
	}

	[Test]
	public void NoOpCommitDoesNotCreateAnotherDocumentRevision()
	{
		FmSynthParameterTextField field = new("440");
		int commits = 0;

		field.Commit("440", _ => commits++).Should().BeFalse();
		field.Commit("880", _ => commits++).Should().BeTrue();
		field.Commit("880", _ => commits++).Should().BeFalse();
		commits.Should().Be(1);
	}

	[Test]
	public void ValidationFailureNeverChangesEscapeBaseline()
	{
		FmSynthParameterTextField field = new("440");

		Action commit = () => field.Commit(
			"not-a-number",
			_ => throw new ArgumentException("Invalid frequency"));

		commit.Should().Throw<ArgumentException>();
		field.CommittedText.Should().Be("440");
		field.Revert().Should().Be("440");
		field.Commit("523", _ => { }).Should().BeTrue();
		field.Revert().Should().Be("523");
	}
}
