using AwesomeAssertions;

using Heresy.Core.Patterns;
using Heresy.UserInterface.PatternEditing;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class PatternEditMaskTests
{
	[Test]
	public void DefaultMaskIncludesNoteSourceAndVolume()
	{
		PatternEditMask.Default.Should().Be(
			PatternEditMask.Note
				| PatternEditMask.Source
				| PatternEditMask.Volume);
	}

	[TestCase(PatternCellField.Note, PatternEditMask.Source | PatternEditMask.Volume)]
	[TestCase(PatternCellField.Source, PatternEditMask.Note | PatternEditMask.Volume)]
	[TestCase(PatternCellField.Volume, PatternEditMask.Note | PatternEditMask.Source)]
	public void CommaTogglesCurrentEditableFieldOff(
		PatternCellField field,
		PatternEditMask expected)
	{
		PatternEditMaskEditor.Toggle(
			PatternEditMask.Default,
			field).Should().Be(expected);
	}

	[TestCase(PatternCellField.Note, PatternEditMask.Note)]
	[TestCase(PatternCellField.Source, PatternEditMask.Source)]
	[TestCase(PatternCellField.Volume, PatternEditMask.Volume)]
	public void CommaTogglesCurrentEditableFieldBackOn(
		PatternCellField field,
		PatternEditMask fieldMask)
	{
		PatternEditMask withoutField =
			PatternEditMask.Default & ~fieldMask;

		PatternEditMaskEditor.Toggle(
			withoutField,
			field).Should().Be(PatternEditMask.Default);
	}

	[Test]
	public void EffectFieldsAreNotPartOfCurrentEditMask()
	{
		PatternEditMaskEditor.TryToggle(
			PatternEditMask.Default,
			PatternCellField.EffectCommand,
			out PatternEditMask result).Should().BeFalse();

		result.Should().Be(PatternEditMask.Default);
	}

	[Test]
	public void DescriptionShowsEnabledFields()
	{
		PatternEditMaskEditor.Describe(
			PatternEditMask.Note | PatternEditMask.Source)
			.Should().Be("Note + Source");
	}
}
