using AwesomeAssertions;

using Avalonia.Input;

using Heresy.UserInterface.PatternEditing;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class PatternRowMutationKeyboardTests
{
	[TestCase(Key.Insert, PatternRowMutationKind.Insert)]
	[TestCase(Key.Delete, PatternRowMutationKind.Delete)]
	public void PlainInsertDeleteShiftCurrentChannel(
		Key key,
		PatternRowMutationKind expectedKind)
	{
		PatternRowMutationKeyboard.TryGet(
				key,
				KeyModifiers.None,
				effectField: false,
				out PatternRowMutationKind kind,
				out bool allChannels)
			.Should().BeTrue();

		kind.Should().Be(expectedKind);
		allChannels.Should().BeFalse();
	}

	[TestCase(Key.Insert, PatternRowMutationKind.Insert)]
	[TestCase(Key.Delete, PatternRowMutationKind.Delete)]
	public void AltInsertDeleteShiftFullWidthOutsideEffectFields(
		Key key,
		PatternRowMutationKind expectedKind)
	{
		PatternRowMutationKeyboard.TryGet(
				key,
				KeyModifiers.Alt,
				effectField: false,
				out PatternRowMutationKind kind,
				out bool allChannels)
			.Should().BeTrue();

		kind.Should().Be(expectedKind);
		allChannels.Should().BeTrue();
	}

	[TestCase(Key.Insert)]
	[TestCase(Key.Delete)]
	public void AltInsertDeleteRemainEffectCommandsInEffectFields(
		Key key)
	{
		PatternRowMutationKeyboard.TryGet(
				key,
				KeyModifiers.Alt,
				effectField: true,
				out _,
				out _)
			.Should().BeFalse();
	}

	[TestCase(KeyModifiers.Shift)]
	[TestCase(KeyModifiers.Control)]
	[TestCase(KeyModifiers.Meta)]
	[TestCase(KeyModifiers.Alt | KeyModifiers.Shift)]
	public void OtherModifierFormsAreReserved(
		KeyModifiers modifiers)
	{
		PatternRowMutationKeyboard.TryGet(
				Key.Insert,
				modifiers,
				effectField: false,
				out _,
				out _)
			.Should().BeFalse();
	}
}
