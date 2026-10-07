using AwesomeAssertions;

using Avalonia.Input;

using Heresy.UserInterface.PatternEditing;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class PatternRowBoundaryNavigationKeyboardTests
{
	[TestCase(Key.PageUp, true)]
	[TestCase(Key.PageDown, false)]
	public void ControlPageSelectsBoundaryRow(
		Key key,
		bool expectedFirst)
	{
		PatternRowBoundaryNavigationKeyboard.TryGetFirst(
				key,
				KeyModifiers.Control,
				out bool first)
			.Should().BeTrue();

		first.Should().Be(expectedFirst);
	}

	[TestCase(Key.PageUp)]
	[TestCase(Key.PageDown)]
	public void UnmodifiedPageIsNotBoundaryNavigation(
		Key key)
	{
		PatternRowBoundaryNavigationKeyboard.TryGetFirst(
				key,
				KeyModifiers.None,
				out _)
			.Should().BeFalse();
	}

	[TestCase(KeyModifiers.Control | KeyModifiers.Shift)]
	[TestCase(KeyModifiers.Control | KeyModifiers.Alt)]
	[TestCase(KeyModifiers.Control | KeyModifiers.Meta)]
	public void ModifiedControlPageIsReservedForOtherCommands(
		KeyModifiers modifiers)
	{
		PatternRowBoundaryNavigationKeyboard.TryGetFirst(
				Key.PageUp,
				modifiers,
				out _)
			.Should().BeFalse();
	}
}
