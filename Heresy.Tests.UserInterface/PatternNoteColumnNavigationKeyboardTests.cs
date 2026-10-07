using AwesomeAssertions;

using Avalonia.Input;

using Heresy.UserInterface.PatternEditing;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class PatternNoteColumnNavigationKeyboardTests
{
	[Test]
	public void TabMovesToNextNoteColumn()
	{
		PatternNoteColumnNavigationKeyboard.TryGetDelta(
				Key.Tab,
				KeyModifiers.None,
				out int delta)
			.Should().BeTrue();

		delta.Should().Be(1);
	}

	[Test]
	public void ShiftTabMovesToPreviousNoteColumn()
	{
		PatternNoteColumnNavigationKeyboard.TryGetDelta(
				Key.Tab,
				KeyModifiers.Shift,
				out int delta)
			.Should().BeTrue();

		delta.Should().Be(-1);
	}

	[TestCase(KeyModifiers.Control)]
	[TestCase(KeyModifiers.Alt)]
	[TestCase(KeyModifiers.Meta)]
	[TestCase(KeyModifiers.Control | KeyModifiers.Shift)]
	[TestCase(KeyModifiers.Alt | KeyModifiers.Shift)]
	public void OtherModifiedTabsAreReserved(
		KeyModifiers modifiers)
	{
		PatternNoteColumnNavigationKeyboard.TryGetDelta(
				Key.Tab,
				modifiers,
				out int delta)
			.Should().BeFalse();

		delta.Should().Be(0);
	}

	[Test]
	public void OtherKeysAreNotNoteColumnNavigation()
	{
		PatternNoteColumnNavigationKeyboard.TryGetDelta(
				Key.Right,
				KeyModifiers.None,
				out int delta)
			.Should().BeFalse();

		delta.Should().Be(0);
	}
}
