using AwesomeAssertions;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.PatternEditing;
using Heresy.UserInterface.ViewModels;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class PatternVolumeKeyboardTests
{
	[Test]
	public void TwoDecimalDigitsSetNormalizedTrackerVolumeAndAdvance()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(workspace, "Pattern");
		PatternEffectCursor cursor =
			new(0, 0, PatternCellField.Volume);
		PatternVolumeInputState input = new();

		PatternVolumeInputResult first =
			PatternVolumeKeyboardEditor.Type(
				workspace,
				pattern,
				cursor,
				input,
				'4');
		first.Completed.Should().BeFalse();
		cursor.Row.Should().Be(0);

		PatternVolumeInputResult result =
			PatternVolumeKeyboardEditor.Type(
				workspace,
				pattern,
				cursor,
				input,
				'8');

		result.Changed.Should().BeTrue();
		result.Rejected.Should().BeFalse();
		result.Completed.Should().BeTrue();
		pattern.Grid[0, 0]!.Volume.Should().Be(48.0 / 64.0);
		cursor.Row.Should().Be(1);
		cursor.Field.Should().Be(PatternCellField.Volume);
	}

	[Test]
	public void DotClearsVolumeAndAdvances()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(workspace, "Pattern");
		pattern.Grid.GetOrCreateCell(0, 0).Volume = 0.5;
		PatternEffectCursor cursor =
			new(0, 0, PatternCellField.Volume);
		PatternVolumeInputState input = new();

		PatternVolumeInputResult result =
			PatternVolumeKeyboardEditor.Type(
				workspace,
				pattern,
				cursor,
				input,
				'.');

		result.Completed.Should().BeTrue();
		pattern.Grid[0, 0].Should().BeNull();
		cursor.Row.Should().Be(1);
	}

	[Test]
	public void VolumeEntryWorksOnRowsWithoutNotes()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(workspace, "Pattern");
		PatternEffectCursor cursor =
			new(0, 0, PatternCellField.Volume);
		PatternVolumeInputState input = new();

		foreach (char value in "3264")
			PatternVolumeKeyboardEditor.Type(
				workspace,
				pattern,
				cursor,
				input,
				value);

		pattern.Grid[0, 0]!.Note.Should().BeNull();
		pattern.Grid[0, 0]!.Volume.Should().Be(0.5);
		pattern.Grid[1, 0]!.Volume.Should().Be(1.0);
		cursor.Row.Should().Be(2);
	}

	[TestCase('6', '4', 64)]
	[TestCase('6', '5', 64)]
	[TestCase('6', '9', 64)]
	[TestCase('7', '0', 64)]
	[TestCase('8', '5', 64)]
	[TestCase('9', '9', 64)]
	[TestCase('5', '9', 59)]
	public void TwoDigitsClampTo64AndAdvance(
		char tens, char units, int expected)
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(workspace, "Pattern");
		PatternEffectCursor cursor =
			new(0, 0, PatternCellField.Volume);
		PatternVolumeInputState input = new();
		uint before = workspace.Document.AudioRevision;

		PatternVolumeInputResult first = PatternVolumeKeyboardEditor.Type(
			workspace, pattern, cursor, input, tens);
		first.Handled.Should().BeTrue();
		first.Rejected.Should().BeFalse();
		first.Completed.Should().BeFalse();
		cursor.Row.Should().Be(0);
		workspace.Document.AudioRevision.Should().Be(before);

		PatternVolumeInputResult completed = PatternVolumeKeyboardEditor.Type(
			workspace, pattern, cursor, input, units);
		completed.Handled.Should().BeTrue();
		completed.Rejected.Should().BeFalse();
		completed.Completed.Should().BeTrue();
		completed.Changed.Should().BeTrue();
		pattern.Grid[0, 0]!.Volume.Should().Be(expected / 64.0);
		cursor.Row.Should().Be(1);
	}

	[Test]
	public void ClampedSecondDigitIsNoOpIfExistingVolumeAlready64()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(workspace, "Pattern");
		PatternDocumentEditor.SetVolume(workspace, pattern, 0, 0, 1.0);
		PatternEffectCursor cursor = new(0, 0, PatternCellField.Volume);
		PatternVolumeInputState input = new();
		uint before = workspace.Document.AudioRevision;
		PatternVolumeKeyboardEditor.Type(workspace, pattern, cursor, input, '9');
		PatternVolumeInputResult result =
			PatternVolumeKeyboardEditor.Type(workspace, pattern, cursor, input, '5');
		result.Changed.Should().BeFalse();
		result.Completed.Should().BeTrue();
		result.Rejected.Should().BeFalse();
		workspace.Document.AudioRevision.Should().Be(before);
		cursor.Row.Should().Be(1);
	}

	[Test]
	public void CellProjectionShowsTrackerVolumeOrEmptyMarker()
	{
		SongDocument document = new();
		DataPatternDefinition pattern =
			new((ObjectId)1U, "Pattern");
		pattern.Grid.GetOrCreateCell(0, 0).Volume = 48.0 / 64.0;

		PatternCellViewModel populated =
			PatternCellViewModel.Create(document, pattern, 0, 0);
		PatternCellViewModel empty =
			PatternCellViewModel.Create(document, pattern, 1, 0);

		populated.VolumeText.Should().Be("48");
		empty.VolumeText.Should().Be("..");
	}
}
