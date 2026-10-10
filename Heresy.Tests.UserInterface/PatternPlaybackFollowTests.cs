using System.Linq;

using Avalonia.Input;

using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.PatternEditing;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class PatternPlaybackFollowTests
{
	[TestCase(PhysicalKey.NumPadDecimal, KeyModifiers.None)]
	[TestCase(PhysicalKey.F, KeyModifiers.Control)]
	[TestCase(PhysicalKey.ScrollLock, KeyModifiers.None)]
	public void AllThreePhysicalShortcutsToggleFollow(
		PhysicalKey key, KeyModifiers modifiers)
		=> Assert.That(PatternPlaybackFollowKeyboard.IsToggle(key, modifiers),
			Is.True);

	[TestCase(PhysicalKey.Backquote, KeyModifiers.None)]
	[TestCase(PhysicalKey.Period, KeyModifiers.None)]
	[TestCase(PhysicalKey.Comma, KeyModifiers.None)]
	[TestCase(PhysicalKey.NumPadDecimal, KeyModifiers.Shift)]
	[TestCase(PhysicalKey.NumPadDecimal, KeyModifiers.Control)]
	[TestCase(PhysicalKey.F, KeyModifiers.None)]
	[TestCase(PhysicalKey.F, KeyModifiers.Control | KeyModifiers.Shift)]
	[TestCase(PhysicalKey.ScrollLock, KeyModifiers.Alt)]
	public void NoteEntryAndOtherModifierCombosRemainUntouched(
		PhysicalKey key, KeyModifiers modifiers)
		=> Assert.That(PatternPlaybackFollowKeyboard.IsToggle(key, modifiers),
			Is.False);

	[TestCase(true, ".", true)]
	[TestCase(true, ",", true)]
	[TestCase(false, ".", false)]
	[TestCase(false, ",", false)]
	[TestCase(true, "x", false)]
	public void NumpadDecimalTextIsConsumedWithoutSwallowingMainPeriod(
		bool held, string text, bool expected)
		=> Assert.That(
			PatternPlaybackFollowKeyboard.IsNumpadDecimalText(held, text),
			Is.EqualTo(expected));

	[TestCase(140, 100, 500, 90)]
	[TestCase(10, 100, 500, 0)]
	[TestCase(495, 100, 500, 400)]
	[TestCase(200, 100, 70, 0)]
	[TestCase(75, 100, 100, 0)]
	public void PlaybackRowCentersInViewportWithNaturalEdgeClamping(
		double rowCenter, double viewport, double extent,
		double expectedOffset)
		=> Assert.That(
			PatternPlaybackFollowScroll.CenteredOffset(
				rowCenter, viewport, extent),
			Is.EqualTo(expectedOffset).Within(1e-10));

	[Test]
	public void UnmeasuredViewportOrBadGeometryDoesNotScroll()
	{
		Assert.That(PatternPlaybackFollowScroll.CenteredOffset(
			50, 0, 500), Is.Zero);
		Assert.That(PatternPlaybackFollowScroll.CenteredOffset(
			double.NaN, 100, 500), Is.Zero);
	}

	[Test]
	public void RepeatedPatternFollowsNearestVisibleOccurrence()
	{
		var candidates = new[] { (0, 70.0), (12, 510.0), (23, 900.0) };
		Assert.That(PatternPlaybackFollowScroll.NearestDisplayRow(
			candidates, viewportCenter: 540), Is.EqualTo(12));
		Assert.That(PatternPlaybackFollowScroll.NearestDisplayRow(
			candidates, viewportCenter: 100), Is.EqualTo(0));
	}

	[Test]
	public void EmptyPlaybackMappingMustNotMoveViewport()
		=> Assert.That(
			PatternPlaybackFollowScroll.NearestDisplayRow(
				System.Array.Empty<(int, double)>(), 300),
			Is.Null);

	[Test]
	public void SequenceOccurrenceMappingDoesNotJumpToRepeatedOtherOrder()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(
				workspace, "Recurring", rowCount: 5);
		DataSequenceDefinition sequence =
			SequenceDocumentEditor.CreateDataSequence(workspace, "Sequence");
		sequence.Entries.Add(new SequenceEntry(pattern.Id));
		sequence.Entries.Add(new SequenceEntry(pattern.Id, startRow: 2));
		PatternEditorContext context = PatternEditorContext.ForSequence(
			workspace.Document, sequence, initialEntryIndex: 0);

		int[] mapped = context.FindPlaybackDisplayRows(
			pattern.Id, patternRow: 3, sequence.Id, sequenceEntryIndex: 1)
			.ToArray();
		Assert.That(mapped, Is.EqualTo(new[] { 6 }));
	}
}
