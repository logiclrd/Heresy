using AwesomeAssertions;

using Heresy.UserInterface.PatternEditing;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class PatternPatternNavigationKeyboardTests
{
	[TestCase('+', 1)]
	[TestCase('-', -1)]
	public void PlusMinusSelectAdjacentPattern(
		char value,
		int expectedDelta)
	{
		PatternPatternNavigationKeyboard.TryGetDelta(
				value,
				out int delta)
			.Should().BeTrue();

		delta.Should().Be(expectedDelta);
	}

	[TestCase('<')]
	[TestCase('>')]
	[TestCase('.')]
	[TestCase('=')]
	public void OtherTextIsNotPatternNavigation(
		char value)
	{
		PatternPatternNavigationKeyboard.TryGetDelta(
				value,
				out int delta)
			.Should().BeFalse();

		delta.Should().Be(0);
	}
}
