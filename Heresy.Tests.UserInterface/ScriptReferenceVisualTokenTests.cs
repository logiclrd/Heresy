using System.Linq;

using AwesomeAssertions;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Scripting.Analysis;
using Heresy.UserInterface.Documents;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class ScriptReferenceVisualTokenTests
{
	[Test]
	public void CatalogUsesRoslynSourceSpansWithoutRewritingDocumentText()
	{
		DocumentWorkspace workspace = new();
		ObjectId id = workspace.Document.AllocateObjectId();
		workspace.Document.Add(
			new ScriptPatternDefinition(id, "Piano"));

		const string source =
			"Note(0, 0, _O(1));";
		ScriptSourceDocumentAnalysis analysis =
			ScriptSourceDocumentAnalyzer.Analyze(
				workspace,
				source);

		ScriptReferenceVisualToken token =
			ScriptReferenceVisualTokenCatalog.Create(
				analysis.References)
			.Should().ContainSingle().Subject;

		token.Id.Should().Be(id);
		token.Text.Should().Be("Piano");
		source.Substring(
				token.SourceSpan.Start,
				token.SourceSpan.Length)
			.Should().Be("_O(1)");
	}

	[Test]
	public void CommentsAndStringsDoNotProduceVisualTokens()
	{
		DocumentWorkspace workspace = new();
		ObjectId id = workspace.Document.AllocateObjectId();
		workspace.Document.Add(
			new ScriptPatternDefinition(id, "Piano"));

		const string source =
			"// _O(1)\n"
			+ "var text = \"_O(1)\";\n"
			+ "Note(0, 0, _O(1));";
		ScriptSourceDocumentAnalysis analysis =
			ScriptSourceDocumentAnalyzer.Analyze(
				workspace,
				source);

		ScriptReferenceVisualTokenCatalog.Create(
				analysis.References)
			.Should().ContainSingle();
	}

	[Test]
	public void ValidReferenceStillProjectsWhileLaterCodeIsIncomplete()
	{
		DocumentWorkspace workspace = new();
		ObjectId id = workspace.Document.AllocateObjectId();
		workspace.Document.Add(
			new ScriptPatternDefinition(id, "Piano"));

		const string source =
			"Note(0, 0, _O(1));\n"
			+ "if (";
		ScriptSourceDocumentAnalysis analysis =
			ScriptSourceDocumentAnalyzer.Analyze(
				workspace,
				source);

		analysis.Diagnostics.Should().Contain(
			diagnostic =>
				diagnostic.Severity
					== ScriptDiagnosticSeverity.Error);
		ScriptReferenceVisualTokenCatalog.Create(
				analysis.References)
			.Should().ContainSingle(
				token =>
					token.Id == id
						&& token.Text == "Piano");
	}

	[Test]
	public void MissingReferenceUsesCanonicalRawReferenceAsTokenText()
	{
		DocumentWorkspace workspace = new();

		const string source =
			"var source = _O(77);";
		ScriptSourceDocumentAnalysis analysis =
			ScriptSourceDocumentAnalyzer.Analyze(
				workspace,
				source);

		ScriptReferenceVisualToken token =
			ScriptReferenceVisualTokenCatalog.Create(
				analysis.References)
			.Single();

		token.Text.Should().Be("_O(77)");
		token.Resolution.Should().Be(
			ScriptObjectReferenceResolution.Missing);
	}
}
