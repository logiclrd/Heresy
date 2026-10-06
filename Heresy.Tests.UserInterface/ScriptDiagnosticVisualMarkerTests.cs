using System.Linq;

using AwesomeAssertions;

using Heresy.Core.Patterns;
using Heresy.Scripting.Analysis;
using Heresy.UserInterface.Documents;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class ScriptDiagnosticVisualMarkerTests
{
	[Test]
	public void CatalogPreservesDiagnosticMetadataAndNonEmptySpan()
	{
		ScriptAnalysisDiagnostic diagnostic =
			new(
				"HRS2001",
				ScriptDiagnosticSeverity.Error,
				"Not allowed.",
				new ScriptSourceSpan(4, 3));

		ScriptDiagnosticVisualMarker marker =
			ScriptDiagnosticVisualMarkerCatalog.Create(
					10,
					[diagnostic])
				.Should().ContainSingle().Subject;

		marker.Span.Should().Be(new ScriptSourceSpan(4, 3));
		marker.OriginalSpan.Should().Be(diagnostic.Span);
		marker.Code.Should().Be("HRS2001");
		marker.Message.Should().Be("Not allowed.");
		marker.Severity.Should().Be(ScriptDiagnosticSeverity.Error);
	}

	[Test]
	public void ZeroLengthDiagnosticAtEndAnchorsPreviousCharacter()
	{
		ScriptAnalysisDiagnostic diagnostic =
			new(
				"CS1026",
				ScriptDiagnosticSeverity.Error,
				") expected",
				new ScriptSourceSpan(5, 0));

		ScriptDiagnosticVisualMarker marker =
			ScriptDiagnosticVisualMarkerCatalog.Create(
					5,
					[diagnostic])
				.Single();

		marker.OriginalSpan.Should().Be(new ScriptSourceSpan(5, 0));
		marker.Span.Should().Be(new ScriptSourceSpan(4, 1));
	}

	[Test]
	public void ZeroLengthDiagnosticWithinDocumentAnchorsFollowingCharacter()
	{
		ScriptAnalysisDiagnostic diagnostic =
			new(
				"CS1002",
				ScriptDiagnosticSeverity.Error,
				"; expected",
				new ScriptSourceSpan(2, 0));

		ScriptDiagnosticVisualMarkerCatalog.Create(
				5,
				[diagnostic])
			.Single().Span.Should().Be(
				new ScriptSourceSpan(2, 1));
	}

	[Test]
	public void EmptyDocumentRetainsZeroLengthPointMarker()
	{
		ScriptAnalysisDiagnostic diagnostic =
			new(
				"CS1001",
				ScriptDiagnosticSeverity.Error,
				"Identifier expected",
				new ScriptSourceSpan(0, 0));

		ScriptDiagnosticVisualMarkerCatalog.Create(
				0,
				[diagnostic])
			.Single().Span.Should().Be(
				new ScriptSourceSpan(0, 0));
	}

	[Test]
	public void MarkerSpanIsClampedToCurrentDocument()
	{
		ScriptAnalysisDiagnostic diagnostic =
			new(
				"TEST",
				ScriptDiagnosticSeverity.Warning,
				"Old snapshot span.",
				new ScriptSourceSpan(8, 50));

		ScriptDiagnosticVisualMarkerCatalog.Create(
				10,
				[diagnostic])
			.Single().Span.Should().Be(
				new ScriptSourceSpan(8, 2));
	}

	[Test]
	public void IncompleteScriptProducesVisibleErrorMarker()
	{
		DocumentWorkspace workspace = new();
		ScriptPatternDefinition pattern =
			ScriptDocumentEditor.CreateScriptPattern(
				workspace,
				"Script");
		const string source =
			"if (";

		ScriptSourceDocumentAnalysis analysis =
			ScriptSourceDocumentAnalyzer.Analyze(
				workspace,
				pattern,
				source);
		ScriptDiagnosticVisualMarker[] markers =
			ScriptDiagnosticVisualMarkerCatalog.Create(
					source.Length,
					analysis.Diagnostics)
				.ToArray();

		markers.Should().Contain(
			marker =>
				marker.Severity
					== ScriptDiagnosticSeverity.Error);
		markers.Should().OnlyContain(
			marker =>
				marker.Span.Start >= 0
					&& marker.Span.End <= source.Length);
	}
}
