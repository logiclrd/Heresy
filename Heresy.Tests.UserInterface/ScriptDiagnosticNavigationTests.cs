using AwesomeAssertions;

using Heresy.Scripting.Analysis;
using Heresy.UserInterface.Documents;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class ScriptDiagnosticNavigationTests
{
	[Test]
	public void NonEmptyOriginalSpanIsSelected()
	{
		ScriptDiagnosticVisualMarker marker =
			Marker(
				visibleStart: 4,
				visibleLength: 3,
				originalStart: 4,
				originalLength: 3);

		ScriptDiagnosticNavigation.GetTarget(
				10,
				marker)
			.Should().Be(
				new ScriptDiagnosticNavigationTarget(
					4,
					3,
					4));
	}

	[Test]
	public void ZeroLengthEndOfFileDiagnosticNavigatesToOriginalEnd()
	{
		ScriptDiagnosticVisualMarker marker =
			Marker(
				visibleStart: 4,
				visibleLength: 1,
				originalStart: 5,
				originalLength: 0);

		ScriptDiagnosticNavigation.GetTarget(
				5,
				marker)
			.Should().Be(
				new ScriptDiagnosticNavigationTarget(
					5,
					0,
					5));
	}

	[Test]
	public void OriginalSpanIsClampedToCurrentDocument()
	{
		ScriptDiagnosticVisualMarker marker =
			Marker(
				visibleStart: 8,
				visibleLength: 2,
				originalStart: 8,
				originalLength: 50);

		ScriptDiagnosticNavigation.GetTarget(
				10,
				marker)
			.Should().Be(
				new ScriptDiagnosticNavigationTarget(
					8,
					2,
					8));
	}

	[Test]
	public void OriginalStartPastDocumentNavigatesToEnd()
	{
		ScriptDiagnosticVisualMarker marker =
			Marker(
				visibleStart: 9,
				visibleLength: 1,
				originalStart: 20,
				originalLength: 4);

		ScriptDiagnosticNavigation.GetTarget(
				10,
				marker)
			.Should().Be(
				new ScriptDiagnosticNavigationTarget(
					10,
					0,
					10));
	}

	private static ScriptDiagnosticVisualMarker Marker(
		int visibleStart,
		int visibleLength,
		int originalStart,
		int originalLength)
		=> new(
			new ScriptSourceSpan(
				visibleStart,
				visibleLength),
			new ScriptSourceSpan(
				originalStart,
				originalLength),
			ScriptDiagnosticSeverity.Error,
			"TEST",
			"Diagnostic.");
}
