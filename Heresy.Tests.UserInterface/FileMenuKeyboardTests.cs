using AwesomeAssertions;

using Avalonia.Input;

using Heresy.UserInterface;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class FileMenuKeyboardTests
{
	[TestCase(Key.N, FileMenuCommand.New)]
	[TestCase(Key.O, FileMenuCommand.Open)]
	[TestCase(Key.S, FileMenuCommand.Save)]
	[TestCase(Key.Q, FileMenuCommand.Exit)]
	public void ControlAcceleratorsResolveToFileCommands(
		Key key,
		FileMenuCommand expected)
	{
		FileMenuKeyboard.TryGetCommand(
				key,
				KeyModifiers.Control,
				handledByFocusedControl: false,
				out FileMenuCommand command)
			.Should().BeTrue();
		command.Should().Be(expected);
	}

	[TestCase(KeyModifiers.None)]
	[TestCase(KeyModifiers.Alt)]
	[TestCase(KeyModifiers.Meta)]
	[TestCase(KeyModifiers.Control | KeyModifiers.Shift)]
	[TestCase(KeyModifiers.Control | KeyModifiers.Alt)]
	public void OtherModifiersDoNotTriggerFileAccelerators(
		KeyModifiers modifiers)
	{
		FileMenuKeyboard.TryGetCommand(
				Key.N,
				modifiers,
				handledByFocusedControl: false,
				out _)
			.Should().BeFalse();
	}

	[Test]
	public void UnassignedControlCombinationIsNotAFileCommand()
	{
		FileMenuKeyboard.TryGetCommand(
				Key.X,
				KeyModifiers.Control,
				handledByFocusedControl: false,
				out _)
			.Should().BeFalse();
	}

	[TestCase(Key.N)]
	[TestCase(Key.O)]
	[TestCase(Key.S)]
	[TestCase(Key.Q)]
	public void FocusedEditorHandlingTakesPrecedence(
		Key key)
	{
		FileMenuKeyboard.TryGetCommand(
				key,
				KeyModifiers.Control,
				handledByFocusedControl: true,
				out _)
			.Should().BeFalse();
	}
}
