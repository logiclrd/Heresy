using System.Linq;

using AwesomeAssertions;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.PatternEditing;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class PatternPlaybackHighlightMappingTests
{
	[Test]
	public void SequencePlaybackMatchesOnlyThePlayingOccurrence()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(
				workspace,
				"Shared",
				rowCount: 3,
				channelCount: 1);
		DataSequenceDefinition sequence =
			SequenceDocumentEditor.CreateDataSequence(
				workspace,
				"Sequence");
		sequence.Entries.Add(
			new SequenceEntry(pattern.Id));
		sequence.Entries.Add(
			new SequenceEntry(
				pattern.Id,
				startRow: 1));

		PatternEditorContext context =
			PatternEditorContext.ForSequence(
				workspace.Document,
				sequence,
				initialEntryIndex: 0);

		context.FindPlaybackDisplayRows(
				pattern.Id,
				patternRow: 1,
				sequence.Id,
				sequenceEntryIndex: 1)
			.Should().Equal(3);
	}

	[Test]
	public void StandalonePatternPlaybackMatchesEveryVisibleOccurrence()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(
				workspace,
				"Shared",
				rowCount: 3,
				channelCount: 1);
		DataSequenceDefinition sequence =
			SequenceDocumentEditor.CreateDataSequence(
				workspace,
				"Sequence");
		sequence.Entries.Add(
			new SequenceEntry(pattern.Id));
		sequence.Entries.Add(
			new SequenceEntry(
				pattern.Id,
				startRow: 1));

		PatternEditorContext context =
			PatternEditorContext.ForSequence(
				workspace.Document,
				sequence,
				initialEntryIndex: 0);

		context.FindPlaybackDisplayRows(
				pattern.Id,
				patternRow: 1,
				sequenceId: null,
				sequenceEntryIndex: null)
			.Should().Equal(1, 3);
	}
}
