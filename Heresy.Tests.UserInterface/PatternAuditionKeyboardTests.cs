using AwesomeAssertions;

using Avalonia.Input;

using Heresy.UserInterface.PatternEditing;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class PatternAuditionKeyboardTests
{
	[TestCase(
		PhysicalKey.Digit4,
		PatternAuditionKind.Note)]
	[TestCase(
		PhysicalKey.Digit8,
		PatternAuditionKind.Row)]
	public void TopRowDigitsSelectAuditionKind(
		PhysicalKey key,
		PatternAuditionKind expected)
	{
		PatternAuditionKeyboard.TryGetKind(
				key,
				out PatternAuditionKind actual)
			.Should().BeTrue();
		actual.Should().Be(expected);
	}

	[TestCase(PhysicalKey.Numpad4)]
	[TestCase(PhysicalKey.Numpad8)]
	[TestCase(PhysicalKey.Digit5)]
	public void OtherPhysicalKeysAreNotAuditionCommands(
		PhysicalKey key)
	{
		PatternAuditionKeyboard.TryGetKind(
				key,
				out _)
			.Should().BeFalse();
	}
}
