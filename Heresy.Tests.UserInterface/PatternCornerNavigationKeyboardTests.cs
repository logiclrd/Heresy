using AwesomeAssertions;

using Avalonia.Input;

using Heresy.UserInterface.PatternEditing;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class PatternCornerNavigationKeyboardTests
{
	[TestCase(Key.Home, true)]
	[TestCase(Key.End, false)]
	public void ControlHomeEndSelectsPatternCorner(
		Key key,
		bool expectedTopLeft)
	{
		PatternCornerNavigationKeyboard.TryGetTopLeft(
				key,
				KeyModifiers.Control,
				out bool topLeft)
			.Should().BeTrue();

		topLeft.Should().Be(expectedTopLeft);
	}

	[TestCase(Key.Home)]
	[TestCase(Key.End)]
	public void UnmodifiedHomeEndIsNotCornerNavigation(
		Key key)
	{
		PatternCornerNavigationKeyboard.TryGetTopLeft(
				key,
				KeyModifiers.None,
				out _)
			.Should().BeFalse();
	}

	[TestCase(KeyModifiers.Control | KeyModifiers.Shift)]
	[TestCase(KeyModifiers.Control | KeyModifiers.Alt)]
	[TestCase(KeyModifiers.Control | KeyModifiers.Meta)]
	public void ModifiedControlHomeIsReservedForOtherCommands(
		KeyModifiers modifiers)
	{
		PatternCornerNavigationKeyboard.TryGetTopLeft(
				Key.Home,
				modifiers,
				out _)
			.Should().BeFalse();
	}
}
