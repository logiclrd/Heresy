using System;
using System.Linq;

using AwesomeAssertions;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.ViewModels;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class SequenceDocumentEditorTests
{
	[Test]
	public void CreateSequenceAddsCanonicalPlacementAndMarksAudio()
	{
		DocumentWorkspace workspace = new();
		uint documentRevision = workspace.Document.DocumentRevision;
		uint audioRevision = workspace.Document.AudioRevision;

		DataSequenceDefinition sequence =
			SequenceDocumentEditor.CreateDataSequence(
				workspace,
				"Arrangement");

		sequence.Name.Should().Be("Arrangement");
		workspace.Document.DocumentRevision.Should().Be(documentRevision + 1);
		workspace.Document.AudioRevision.Should().Be(audioRevision + 1);
		workspace.Document.RootSequenceId.Should().Be(ObjectId.None);

		SongTreeObject node =
			workspace.Document.GetSectionRoot(SongTreeSection.Sequences)
				.Children.Cast<SongTreeObject>()
				.Single();
		node.ObjectId.Should().Be(sequence.Id);
	}

	[Test]
	public void SetRootSequenceMarksAudioAndSameValueIsNoOp()
	{
		DocumentWorkspace workspace = new();
		DataSequenceDefinition sequence =
			SequenceDocumentEditor.CreateDataSequence(
				workspace,
				"Arrangement");
		uint documentRevision = workspace.Document.DocumentRevision;
		uint audioRevision = workspace.Document.AudioRevision;

		SequenceDocumentEditor.SetRootSequence(
			workspace,
			sequence);

		workspace.Document.RootSequenceId.Should().Be(sequence.Id);
		workspace.Document.DocumentRevision.Should().Be(documentRevision + 1);
		workspace.Document.AudioRevision.Should().Be(audioRevision + 1);

		SequenceDocumentEditor.SetRootSequence(
			workspace,
			sequence);

		workspace.Document.DocumentRevision.Should().Be(documentRevision + 1);
		workspace.Document.AudioRevision.Should().Be(audioRevision + 1);
	}

	[Test]
	public void InsertEntryAcceptsLivePatternAndMarksOneAudioRevision()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(workspace, "Verse");
		DataSequenceDefinition sequence =
			SequenceDocumentEditor.CreateDataSequence(
				workspace,
				"Arrangement");
		uint documentRevision = workspace.Document.DocumentRevision;
		uint audioRevision = workspace.Document.AudioRevision;

		SequenceDocumentEditor.InsertEntry(
			workspace,
			sequence,
			index: 0,
			pattern.Id,
			startRow: 12);

		sequence.Entries.Should().ContainSingle()
			.Which.Should().Be(
				new SequenceEntry(pattern.Id, 12));
		workspace.Document.DocumentRevision.Should().Be(documentRevision + 1);
		workspace.Document.AudioRevision.Should().Be(audioRevision + 1);
	}

	[Test]
	public void InsertEntryRejectsNonPatternObject()
	{
		DocumentWorkspace workspace = new();
		DataSequenceDefinition first =
			SequenceDocumentEditor.CreateDataSequence(workspace, "First");
		DataSequenceDefinition second =
			SequenceDocumentEditor.CreateDataSequence(workspace, "Second");

		var action = () =>
			SequenceDocumentEditor.InsertEntry(
				workspace,
				first,
				index: 0,
				second.Id,
				startRow: 0);

		action.Should().Throw<InvalidOperationException>();
		first.Entries.Should().BeEmpty();
	}

	[Test]
	public void UpdateEntryCanChangePatternAndStartRow()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition first =
			PatternDocumentEditor.CreateDataPattern(workspace, "A");
		DataPatternDefinition second =
			PatternDocumentEditor.CreateDataPattern(workspace, "B");
		DataSequenceDefinition sequence =
			SequenceDocumentEditor.CreateDataSequence(workspace, "Sequence");
		sequence.Entries.Add(new SequenceEntry(first.Id, 0));
		uint documentRevision = workspace.Document.DocumentRevision;
		uint audioRevision = workspace.Document.AudioRevision;

		SequenceDocumentEditor.UpdateEntry(
			workspace,
			sequence,
			index: 0,
			second.Id,
			startRow: 8);

		sequence.Entries[0].Should().Be(
			new SequenceEntry(second.Id, 8));
		workspace.Document.DocumentRevision.Should().Be(documentRevision + 1);
		workspace.Document.AudioRevision.Should().Be(audioRevision + 1);
	}

	[Test]
	public void UpdatingEntryToSameValueIsNoOp()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(workspace, "A");
		DataSequenceDefinition sequence =
			SequenceDocumentEditor.CreateDataSequence(workspace, "Sequence");
		sequence.Entries.Add(new SequenceEntry(pattern.Id, 3));
		uint documentRevision = workspace.Document.DocumentRevision;
		uint audioRevision = workspace.Document.AudioRevision;

		SequenceDocumentEditor.UpdateEntry(
			workspace,
			sequence,
			index: 0,
			pattern.Id,
			startRow: 3);

		workspace.Document.DocumentRevision.Should().Be(documentRevision);
		workspace.Document.AudioRevision.Should().Be(audioRevision);
	}

	[Test]
	public void RemoveEntryMarksAudioAndPreservesRemainingOrder()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition a =
			PatternDocumentEditor.CreateDataPattern(workspace, "A");
		DataPatternDefinition b =
			PatternDocumentEditor.CreateDataPattern(workspace, "B");
		DataSequenceDefinition sequence =
			SequenceDocumentEditor.CreateDataSequence(workspace, "Sequence");
		sequence.Entries.Add(new SequenceEntry(a.Id));
		sequence.Entries.Add(new SequenceEntry(b.Id));
		uint audioRevision = workspace.Document.AudioRevision;

		SequenceDocumentEditor.RemoveEntry(
			workspace,
			sequence,
			index: 0);

		sequence.Entries.Should().Equal(
			new SequenceEntry(b.Id));
		workspace.Document.AudioRevision.Should().Be(audioRevision + 1);
	}

	[Test]
	public void MoveEntryReordersAndMarksAudioOnce()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition a =
			PatternDocumentEditor.CreateDataPattern(workspace, "A");
		DataPatternDefinition b =
			PatternDocumentEditor.CreateDataPattern(workspace, "B");
		DataPatternDefinition c =
			PatternDocumentEditor.CreateDataPattern(workspace, "C");
		DataSequenceDefinition sequence =
			SequenceDocumentEditor.CreateDataSequence(workspace, "Sequence");
		sequence.Entries.Add(new SequenceEntry(a.Id));
		sequence.Entries.Add(new SequenceEntry(b.Id, 2));
		sequence.Entries.Add(new SequenceEntry(c.Id));
		uint audioRevision = workspace.Document.AudioRevision;

		SequenceDocumentEditor.MoveEntry(
			workspace,
			sequence,
			fromIndex: 1,
			toIndex: 0);

		sequence.Entries.Should().Equal(
			new SequenceEntry(b.Id, 2),
			new SequenceEntry(a.Id),
			new SequenceEntry(c.Id));
		workspace.Document.AudioRevision.Should().Be(audioRevision + 1);
	}

	[Test]
	public void MovingEntryToSameIndexIsNoOp()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(workspace, "A");
		DataSequenceDefinition sequence =
			SequenceDocumentEditor.CreateDataSequence(workspace, "Sequence");
		sequence.Entries.Add(new SequenceEntry(pattern.Id));
		uint audioRevision = workspace.Document.AudioRevision;

		SequenceDocumentEditor.MoveEntry(
			workspace,
			sequence,
			fromIndex: 0,
			toIndex: 0);

		workspace.Document.AudioRevision.Should().Be(audioRevision);
	}

	[Test]
	public void EntryProjectionResolvesLivePatternAndStartRow()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(workspace, "Verse");
		DataSequenceDefinition sequence =
			SequenceDocumentEditor.CreateDataSequence(workspace, "Sequence");
		sequence.Entries.Add(new SequenceEntry(pattern.Id, 12));

		SequenceEntryViewModel view =
			SequenceEntryViewModel.Create(
				workspace.Document,
				sequence,
				0);

		view.Index.Should().Be(0);
		view.PatternId.Should().Be(pattern.Id);
		view.PatternText.Should().Be($"Verse <{pattern.Id.Value}>");
		view.StartRow.Should().Be(12);
		view.IsMissing.Should().BeFalse();
	}

	[Test]
	public void EntryProjectionUsesTombstoneForDeletedPattern()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(workspace, "Verse");
		DataSequenceDefinition sequence =
			SequenceDocumentEditor.CreateDataSequence(workspace, "Sequence");
		sequence.Entries.Add(new SequenceEntry(pattern.Id, 4));
		workspace.Document.Remove(pattern.Id);

		SequenceEntryViewModel view =
			SequenceEntryViewModel.Create(
				workspace.Document,
				sequence,
				0);

		view.PatternText.Should().Be(
			$"⚠ Verse <{pattern.Id.Value}>");
		view.IsMissing.Should().BeTrue();
	}

	[Test]
	public void PatternCatalogContainsDataAndScriptPatternsOnly()
	{
		DocumentWorkspace workspace = new();
		ObjectId dataId = workspace.Document.AllocateObjectId();
		workspace.Document.Add(
			new DataPatternDefinition(dataId, "Data"));
		ObjectId scriptId = workspace.Document.AllocateObjectId();
		workspace.Document.Add(
			new ScriptPatternDefinition(scriptId, "Script"));
		SequenceDocumentEditor.CreateDataSequence(workspace, "Sequence");

		SequencePatternOption[] options =
			SequencePatternCatalog.GetPatterns(
				workspace.Document);

		options.Select(option => option.Id)
			.Should().Equal(dataId, scriptId);
	}
	[Test]
	public void StartRowCanBeEditedWhileExistingPatternReferenceIsMissing()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(workspace, "Verse");
		DataSequenceDefinition sequence =
			SequenceDocumentEditor.CreateDataSequence(workspace, "Sequence");
		sequence.Entries.Add(new SequenceEntry(pattern.Id, 2));
		workspace.Document.Remove(pattern.Id);

		SequenceDocumentEditor.UpdateEntry(
			workspace,
			sequence,
			index: 0,
			pattern.Id,
			startRow: 9);

		sequence.Entries[0].Should().Be(
			new SequenceEntry(pattern.Id, 9));
	}

}
