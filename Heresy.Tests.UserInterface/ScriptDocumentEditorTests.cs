using System;

using AwesomeAssertions;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Scripting;
using Heresy.Core.Sequences;
using Heresy.UserInterface.Documents;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class ScriptDocumentEditorTests
{
	[Test]
	public void CreateScriptPatternAddsCanonicalPatternPlacementAndMarksAudio()
	{
		DocumentWorkspace workspace = new();
		uint documentRevision = workspace.Document.DocumentRevision;
		uint audioRevision = workspace.Document.AudioRevision;

		ScriptPatternDefinition pattern =
			ScriptDocumentEditor.CreateScriptPattern(
				workspace,
				"Generated",
				rowCount: 32,
				channelCount: 4);

		pattern.Name.Should().Be("Generated");
		pattern.RowCount.Should().Be(32);
		pattern.ChannelCount.Should().Be(4);
		workspace.Document.DocumentRevision.Should().Be(documentRevision + 1);
		workspace.Document.AudioRevision.Should().Be(audioRevision + 1);
		workspace.Document.GetSectionRoot(SongTreeSection.Patterns)
			.Children.Should().ContainSingle();
	}

	[Test]
	public void CreateScriptSequenceAddsCanonicalSequencePlacementAndMarksAudio()
	{
		DocumentWorkspace workspace = new();

		ScriptSequenceDefinition sequence =
			ScriptDocumentEditor.CreateScriptSequence(
				workspace,
				"Generated");

		sequence.Name.Should().Be("Generated");
		workspace.Document.GetSectionRoot(SongTreeSection.Sequences)
			.Children.Should().ContainSingle();
		workspace.Document.AudioRevision.Should().Be(1);
	}

	[Test]
	public void UpdateSourceMarksAudioAndEqualSourceIsNoOp()
	{
		DocumentWorkspace workspace = new();
		ScriptPatternDefinition pattern =
			ScriptDocumentEditor.CreateScriptPattern(
				workspace,
				"Generated");
		uint documentRevision = workspace.Document.DocumentRevision;
		uint audioRevision = workspace.Document.AudioRevision;

		ScriptDocumentEditor.UpdateSource(
			workspace,
			pattern,
			"yield return _O(12);");

		pattern.Source.Should().Be("yield return _O(12);");
		workspace.Document.DocumentRevision.Should().Be(documentRevision + 1);
		workspace.Document.AudioRevision.Should().Be(audioRevision + 1);

		ScriptDocumentEditor.UpdateSource(
			workspace,
			pattern,
			"yield return _O(12);");

		workspace.Document.DocumentRevision.Should().Be(documentRevision + 1);
		workspace.Document.AudioRevision.Should().Be(audioRevision + 1);
	}

	[Test]
	public void UpdateScriptPatternLayoutUsesSameValidationAsDataPatterns()
	{
		DocumentWorkspace workspace = new();
		ScriptPatternDefinition pattern =
			ScriptDocumentEditor.CreateScriptPattern(
				workspace,
				"Generated");

		ScriptDocumentEditor.UpdatePatternLayout(
			workspace,
			pattern,
			rowCount: 48,
			channelCount: 6,
			minorHighlightRows: 3,
			majorHighlightRows: 12);

		pattern.RowCount.Should().Be(48);
		pattern.ChannelCount.Should().Be(6);
		pattern.MinorHighlightRows.Should().Be(3);
		pattern.MajorHighlightRows.Should().Be(12);
	}

	[TestCase(-1, 1, 4, 16)]
	[TestCase(1, 0, 4, 16)]
	[TestCase(1, 1, -1, 16)]
	[TestCase(1, 1, 4, -1)]
	public void InvalidScriptPatternLayoutIsRejected(
		int rows,
		int channels,
		int minor,
		int major)
	{
		DocumentWorkspace workspace = new();
		ScriptPatternDefinition pattern =
			ScriptDocumentEditor.CreateScriptPattern(
				workspace,
				"Generated");

		var action = () =>
			ScriptDocumentEditor.UpdatePatternLayout(
				workspace,
				pattern,
				rows,
				channels,
				minor,
				major);

		action.Should().Throw<ArgumentOutOfRangeException>();
	}

	[Test]
	public void ScriptSequenceCanBeSetAsRoot()
	{
		DocumentWorkspace workspace = new();
		ScriptSequenceDefinition sequence =
			ScriptDocumentEditor.CreateScriptSequence(
				workspace,
				"Generated");

		ScriptDocumentEditor.SetRootSequence(
			workspace,
			sequence);

		workspace.Document.RootSequenceId.Should().Be(sequence.Id);
	}

	[Test]
	public void FormattingObjectReferenceUsesCanonicalPersistedSyntax()
	{
		ScriptObjectReferenceOption option =
			new((ObjectId)137U, "Piano", SongObjectKind.Sample);

		option.ReferenceText.Should().Be(
			ScriptObjectReferenceSyntax.Format((ObjectId)137U));
		option.ReferenceText.Should().Be("_O(137)");
		option.ToString().Should().Contain("Piano");
		option.ToString().Should().Contain("137");
	}

	[Test]
	public void ReferenceCatalogContainsEveryLiveSongObject()
	{
		DocumentWorkspace workspace = new();
		ScriptPatternDefinition pattern =
			ScriptDocumentEditor.CreateScriptPattern(
				workspace,
				"Script");
		ScriptSequenceDefinition sequence =
			ScriptDocumentEditor.CreateScriptSequence(
				workspace,
				"Sequence");

		ScriptObjectReferenceOption[] options =
			ScriptDocumentEditor.GetObjectReferences(
				workspace.Document);

		options.Should().Contain(option => option.Id == pattern.Id);
		options.Should().Contain(option => option.Id == sequence.Id);
		options.Should().HaveCount(workspace.Document.Objects.Count);
	}

	[Test]
	public void EditingForeignScriptObjectIsRejected()
	{
		DocumentWorkspace workspace = new();
		ScriptPatternDefinition foreign =
			new((ObjectId)999U, "Foreign");

		var action = () =>
			ScriptDocumentEditor.UpdateSource(
				workspace,
				foreign,
				"source");

		action.Should().Throw<InvalidOperationException>();
	}
}
