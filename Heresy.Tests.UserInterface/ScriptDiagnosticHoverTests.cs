using AwesomeAssertions;

using Heresy.Scripting.Analysis;
using Heresy.UserInterface.Documents;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class ScriptDiagnosticHoverTests
{
	[Test]
	public void HoverAggregatesOverlappingDiagnosticsInSeverityOrder()
	{
		ScriptDiagnosticVisualMarker warning =
			Marker(
				2,
				4,
				ScriptDiagnosticSeverity.Warning,
				"WARN",
				"Warning message.");
		ScriptDiagnosticVisualMarker error =
			Marker(
				3,
				2,
				ScriptDiagnosticSeverity.Error,
				"ERR",
				"Error message.");

		ScriptDiagnosticHoverInfo? hover =
			ScriptDiagnosticHoverCatalog.FindAtOffset(
				[warning, error],
				3);

		hover.Should().NotBeNull();
		hover!.Diagnostics.Should().Equal(error, warning);
		hover.Text.Should().Be(
			"Error ERR: Error message.\n"
				+ "Warning WARN: Warning message.");
	}

	[Test]
	public void RawSpanEndBoundaryStillMatchesForGeneratedElementHitTesting()
	{
		ScriptDiagnosticVisualMarker marker =
			Marker(
				10,
				7,
				ScriptDiagnosticSeverity.Error,
				"ERR",
				"Reference error.");

		ScriptDiagnosticHoverCatalog.FindAtOffset(
				[marker],
				17)
			.Should().NotBeNull();
	}

	[Test]
	public void OffsetOutsideMarkersHasNoHover()
	{
		ScriptDiagnosticVisualMarker marker =
			Marker(
				4,
				2,
				ScriptDiagnosticSeverity.Error,
				"ERR",
				"Error.");

		ScriptDiagnosticHoverCatalog.FindAtOffset(
				[marker],
				1)
			.Should().BeNull();
	}

	[Test]
	public void ZeroLengthPointMarkerMatchesItsExactOffset()
	{
		ScriptDiagnosticVisualMarker marker =
			Marker(
				0,
				0,
				ScriptDiagnosticSeverity.Error,
				"ERR",
				"Empty document error.");

		ScriptDiagnosticHoverCatalog.FindAtOffset(
				[marker],
				0)
			.Should().NotBeNull();
	}

	private static ScriptDiagnosticVisualMarker Marker(
		int start,
		int length,
		ScriptDiagnosticSeverity severity,
		string code,
		string message)
		=> new(
			new ScriptSourceSpan(start, length),
			new ScriptSourceSpan(start, length),
			severity,
			code,
			message);
}
