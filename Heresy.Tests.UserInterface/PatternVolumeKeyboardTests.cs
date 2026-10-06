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

		PatternVolumeKeyboardEditor.Type(
			workspace,
			pattern,
			cursor,
			input,
			'4');
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

		PatternVolumeKeyboardEditor.Type(
			workspace,
			pattern,
			cursor,
			input,
			'.');

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

	[Test]
	public void ValuesAbove64AreRejectedWithoutAdvancing()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(workspace, "Pattern");
		PatternEffectCursor cursor =
			new(0, 0, PatternCellField.Volume);
		PatternVolumeInputState input = new();

		PatternVolumeKeyboardEditor.Type(
			workspace,
			pattern,
			cursor,
			input,
			'6');
		PatternVolumeInputResult result =
			PatternVolumeKeyboardEditor.Type(
				workspace,
				pattern,
				cursor,
				input,
				'5');

		result.Rejected.Should().BeTrue();
		pattern.Grid[0, 0].Should().BeNull();
		cursor.Row.Should().Be(0);
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
