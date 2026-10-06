using System.Linq;

using AwesomeAssertions;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.UserInterface.Documents;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class ScriptSourceProjectionTests
{
	[Test]
	public void ProjectionReplacesOnlySemanticReferencesWithNamedTokens()
	{
		DocumentWorkspace workspace = new();
		ObjectId pianoId = workspace.Document.AllocateObjectId();
		workspace.Document.Add(
			new ScriptPatternDefinition(pianoId, "Piano"));

		const string source =
			"// _O(1) is documentation\n"
			+ "Note(0, 0, _O(1));";
		ScriptSourceDocumentAnalysis analysis =
			ScriptSourceDocumentAnalyzer.Analyze(
				workspace,
				source);

		ScriptSourceProjection projection =
			ScriptSourceProjection.Create(
				source,
				analysis.References);

		projection.Source.Should().Be(source);
		projection.Text.Should().Be(
			"// _O(1) is documentation\n"
			+ "Note(0, 0, ⟦Piano⟧);");
		projection.Tokens.Should().ContainSingle();
		projection.Tokens[0].Id.Should().Be(pianoId);
	}

	[Test]
	public void RebuildingProjectionAfterRenameUpdatesNameWithoutChangingSource()
	{
		DocumentWorkspace workspace = new();
		ObjectId id = workspace.Document.AllocateObjectId();
		ScriptPatternDefinition pattern =
			new(id, "Original");
		workspace.Document.Add(pattern);
		const string source = "var p = _O(1);";

		ScriptSourceProjection before =
			Create(workspace, source);

		pattern.Name = "Renamed";

		ScriptSourceProjection after =
			Create(workspace, source);

		before.Text.Should().Contain("⟦Original⟧");
		after.Text.Should().Contain("⟦Renamed⟧");
		after.Source.Should().Be(source);
	}

	[Test]
	public void MissingReferenceStillProjectsAsAtomicRawIdToken()
	{
		DocumentWorkspace workspace = new();

		ScriptSourceProjection projection =
			Create(workspace, "var p = _O(77);");

		projection.Text.Should().Be("var p = ⟦_O(77)⟧;");
		projection.Tokens.Single().Resolution.Should().Be(
			Heresy.Scripting.Analysis.ScriptObjectReferenceResolution.Missing);
	}

	[Test]
	public void BackspaceAtTokenEndDeletesWholePersistedReference()
	{
		DocumentWorkspace workspace = WorkspaceWithPiano();
		ScriptSourceProjection projection =
			Create(workspace, "x + _O(1) + y");
		ScriptProjectedReferenceToken token =
			projection.Tokens.Single();

		ScriptProjectionEdit edit =
			projection.DeleteBackward(
				token.DisplaySpan.End,
				token.DisplaySpan.End);

		edit.Source.Should().Be("x +  + y");
		edit.SourceCaret.Should().Be(4);
	}

	[Test]
	public void DeleteAtTokenStartDeletesWholePersistedReference()
	{
		DocumentWorkspace workspace = WorkspaceWithPiano();
		ScriptSourceProjection projection =
			Create(workspace, "x + _O(1) + y");
		ScriptProjectedReferenceToken token =
			projection.Tokens.Single();

		ScriptProjectionEdit edit =
			projection.DeleteForward(
				token.DisplaySpan.Start,
				token.DisplaySpan.Start);

		edit.Source.Should().Be("x +  + y");
		edit.SourceCaret.Should().Be(4);
	}

	[Test]
	public void TypingWithCaretInsideTokenReplacesWholeReference()
	{
		DocumentWorkspace workspace = WorkspaceWithPiano();
		ScriptSourceProjection projection =
			Create(workspace, "x + _O(1) + y");
		ScriptProjectedReferenceToken token =
			projection.Tokens.Single();
		int middle =
			token.DisplaySpan.Start
				+ token.DisplaySpan.Length / 2;

		ScriptProjectionEdit edit =
			projection.Replace(
				middle,
				middle,
				"replacement");

		edit.Source.Should().Be("x + replacement + y");
		edit.SourceCaret.Should().Be(
			"x + replacement".Length);
	}

	[Test]
	public void CaretMovementSkipsReferenceTokensAtomically()
	{
		DocumentWorkspace workspace = WorkspaceWithPiano();
		ScriptSourceProjection projection =
			Create(workspace, "var p = _O(1);");
		ScriptProjectedReferenceToken token =
			projection.Tokens.Single();

		projection.MoveCaret(
				token.DisplaySpan.Start,
				1)
			.Should().Be(token.DisplaySpan.End);
		projection.MoveCaret(
				token.DisplaySpan.End,
				-1)
			.Should().Be(token.DisplaySpan.Start);
	}

	[Test]
	public void CopyingProjectedSelectionReturnsCanonicalPersistedSource()
	{
		DocumentWorkspace workspace = WorkspaceWithPiano();
		ScriptSourceProjection projection =
			Create(workspace, "var p = _O(1);");
		ScriptProjectedReferenceToken token =
			projection.Tokens.Single();

		string copied =
			projection.GetSourceText(
				token.DisplaySpan.Start,
				token.DisplaySpan.End);

		copied.Should().Be("_O(1)");
	}

	[Test]
	public void PartialSelectionExpandsToWholeToken()
	{
		DocumentWorkspace workspace = WorkspaceWithPiano();
		ScriptSourceProjection projection =
			Create(workspace, "var p = _O(1);");
		ScriptProjectedReferenceToken token =
			projection.Tokens.Single();

		ScriptProjectionSelection normalized =
			projection.NormalizeSelection(
				token.DisplaySpan.Start + 1,
				token.DisplaySpan.End - 1);

		normalized.Start.Should().Be(token.DisplaySpan.Start);
		normalized.End.Should().Be(token.DisplaySpan.End);
	}

	[Test]
	public void ReconcileOrdinaryTextBoxEditUpdatesRawSource()
	{
		DocumentWorkspace workspace = WorkspaceWithPiano();
		ScriptSourceProjection projection =
			Create(workspace, "var p = _O(1);");

		ScriptProjectionEdit edit =
			projection.ReconcileTextChange(
				"X" + projection.Text);

		edit.Source.Should().Be("Xvar p = _O(1);");
		edit.SourceCaret.Should().Be(1);
	}

	[Test]
	public void ReconcilePartialVisualTokenDeletionDeletesWholeRawReference()
	{
		DocumentWorkspace workspace = WorkspaceWithPiano();
		ScriptSourceProjection projection =
			Create(workspace, "var p = _O(1);");
		ScriptProjectedReferenceToken token =
			projection.Tokens.Single();
		string changed =
			projection.Text.Remove(
				token.DisplaySpan.End - 1,
				1);

		ScriptProjectionEdit edit =
			projection.ReconcileTextChange(changed);

		edit.Source.Should().Be("var p = ;");
	}


	private static DocumentWorkspace WorkspaceWithPiano()
	{
		DocumentWorkspace workspace = new();
		ObjectId id = workspace.Document.AllocateObjectId();
		workspace.Document.Add(
			new ScriptPatternDefinition(id, "Piano"));
		return workspace;
	}

	private static ScriptSourceProjection Create(
		DocumentWorkspace workspace,
		string source)
	{
		ScriptSourceDocumentAnalysis analysis =
			ScriptSourceDocumentAnalyzer.Analyze(
				workspace,
				source);
		return ScriptSourceProjection.Create(
			source,
			analysis.References);
	}
}
