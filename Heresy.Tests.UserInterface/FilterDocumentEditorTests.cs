using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Filters;
using Heresy.Core.Objects;
using Heresy.UserInterface.Documents;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class FilterDocumentEditorTests
{
	[Test]
	public void CreationPlacesOneStageFilterUnderCanonicalRootAndRevisesOnce()
	{
		DocumentWorkspace workspace = new();
		FilterDefinition filter = FilterDocumentEditor.CreateFilter(workspace, "  EQ  ");
		Assert.That(filter.Name, Is.EqualTo("EQ"));
		Assert.That(filter.Stages.Count, Is.EqualTo(1));
		Assert.That(filter.GetStage(1).Kind, Is.EqualTo(FilterKind.OnePole));
		Assert.That(((SongTreeObject)workspace.Document
			.GetSectionRoot(SongTreeSection.Filters).Children.Single()).ObjectId,
			Is.EqualTo(filter.Id));
		Assert.That(workspace.Document.AudioRevision, Is.EqualTo(1));
		Assert.That(workspace.Document.DocumentRevision, Is.EqualTo(1));
	}

	[Test]
	public void InvalidCreationDoesNotAllocateAnId()
	{
		DocumentWorkspace workspace = new();
		Assert.Throws<ArgumentException>(() =>
			FilterDocumentEditor.CreateFilter(workspace, " "));
		Assert.That(workspace.Document.AllocateObjectId(),
			Is.EqualTo((ObjectId)1U));
		Assert.That(workspace.Document.DocumentRevision, Is.Zero);
	}

	[Test]
	public void AtomicStageEditValidatesCombinedBandsDuplicatesAndOtherEditors()
	{
		DocumentWorkspace workspace = new();
		FilterDefinition definition =
			FilterDocumentEditor.CreateFilter(workspace, "Shared");
		FilterStageDefinition first = definition.GetStage(1);
		uint initial = workspace.Document.AudioRevision;
		Assert.Throws<ArgumentException>(() =>
			FilterDocumentEditor.ApplyStage(workspace, definition, 1, first,
				"stage1", FilterKind.FirBandPass, new[] {
					Pair("LowCutoffHz", FilterParameterValue.FromNumber(10000)),
					Pair("HighCutoffHz", FilterParameterValue.FromNumber(1000)),
				}));
		Assert.That(definition.GetStage(1), Is.SameAs(first));
		Assert.That(workspace.Document.AudioRevision, Is.EqualTo(initial));

		Assert.That(FilterDocumentEditor.ApplyStage(workspace, definition, 1,
			first, "treble", FilterKind.FirBandPass, new[] {
				Pair("LowCutoffHz", FilterParameterValue.FromNumber(7000)),
				Pair("HighCutoffHz", FilterParameterValue.FromNumber(12000)),
				Pair("TapCount", FilterParameterValue.FromNumber(255)),
			}), Is.True);
		Assert.That(definition.GetStage(1).Name, Is.EqualTo("treble"));
		Assert.That(definition.GetStage(1).DefaultParameters["TapCount"].NumericValue,
			Is.EqualTo(255));
		Assert.That(workspace.Document.AudioRevision, Is.EqualTo(initial + 1));

		Assert.Throws<InvalidOperationException>(() =>
			FilterDocumentEditor.ApplyStage(workspace, definition, 1,
				first, "stale", FilterKind.OnePole, Array.Empty<KeyValuePair<string, FilterParameterValue>>()));
		Assert.That(definition.GetStage(1).Name, Is.EqualTo("treble"));
		Assert.That(workspace.Document.AudioRevision, Is.EqualTo(initial + 1));
	}

	[Test]
	public void RepeatingSameStageEditIsNoOpAndDoesNotInvalidateOtherDraft()
	{
		DocumentWorkspace workspace = new();
		FilterDefinition filter =
			FilterDocumentEditor.CreateFilter(workspace, "No-op");
		FilterStageDefinition stage = filter.GetStage(1);
		uint revision = workspace.Document.AudioRevision;
		Assert.That(FilterDocumentEditor.ApplyStage(workspace, filter, 1,
			stage, stage.Name, stage.Kind, stage.DefaultParameters), Is.False);
		Assert.That(filter.GetStage(1), Is.SameAs(stage));
		Assert.That(workspace.Document.AudioRevision, Is.EqualTo(revision));
		Assert.That(workspace.Document.DocumentRevision, Is.EqualTo(revision));
	}

	[Test]
	public void StageOperationsPreserveOrderingAndRejectTheFinalRemoval()
	{
		DocumentWorkspace workspace = new();
		FilterDefinition filter =
			FilterDocumentEditor.CreateFilter(workspace, "Serial");
		FilterStageDefinition second =
			FilterDocumentEditor.AddStage(workspace, filter, FilterKind.SvfLowPass);
		Assert.That(filter.Stages.Select(s => s.Name),
			Is.EqualTo(new[] { "stage1", "stage2" }));
		uint audio = workspace.Document.AudioRevision;
		Assert.That(FilterDocumentEditor.MoveStage(workspace, filter,
			second.StageId, 0, second), Is.True);
		Assert.That(filter.Stages.Select(s => s.StageId),
			Is.EqualTo(new[] { 2, 1 }));
		Assert.That(workspace.Document.AudioRevision, Is.EqualTo(audio + 1));
		Assert.That(FilterDocumentEditor.MoveStage(workspace, filter,
			second.StageId, 0, second), Is.False);
		Assert.That(workspace.Document.AudioRevision, Is.EqualTo(audio + 1));

		Assert.That(FilterDocumentEditor.RemoveStage(workspace,
			filter, second.StageId, second), Is.True);
		audio = workspace.Document.AudioRevision;
		FilterStageDefinition remaining = filter.Stages.Single();
		Assert.Throws<ArgumentException>(() =>
			FilterDocumentEditor.RemoveStage(workspace, filter,
				remaining.StageId, remaining));
		Assert.That(workspace.Document.AudioRevision, Is.EqualTo(audio));
		Assert.That(filter.Stages.Count, Is.EqualTo(1));
	}

	[Test]
	public void DuplicateNamesAndStaleWorkspaceCannotMutateDocument()
	{
		DocumentWorkspace workspace = new();
		FilterDefinition filter = FilterDocumentEditor.CreateFilter(workspace, "EQ");
		FilterStageDefinition second =
			FilterDocumentEditor.AddStage(workspace, filter);
		uint audio = workspace.Document.AudioRevision;
		Assert.Throws<ArgumentException>(() =>
			FilterDocumentEditor.ApplyStage(workspace, filter, second.StageId,
				second, "STAGE1", second.Kind, second.DefaultParameters));
		Assert.That(workspace.Document.AudioRevision, Is.EqualTo(audio));
		Assert.That(filter.GetStage(second.StageId).Name, Is.EqualTo("stage2"));

		workspace.New();
		Assert.Throws<InvalidOperationException>(() =>
			FilterDocumentEditor.AddStage(workspace, filter));
		Assert.Throws<InvalidOperationException>(() =>
			FilterDocumentEditor.ApplyStage(workspace, filter, second.StageId,
				second, "unused", second.Kind, second.DefaultParameters));
		Assert.That(workspace.Document.DocumentRevision, Is.Zero);
	}

	private static KeyValuePair<string, FilterParameterValue> Pair(
		string name, FilterParameterValue value) => new(name, value);
}
