using System.Linq;

using AwesomeAssertions;

using Heresy.Core.Objects;
using Heresy.Scripting.Analysis;

using NUnit.Framework;

namespace Heresy.Tests.Scripting;

[TestFixture]
public sealed class ScriptReferenceAnalyzerTests
{
	[Test]
	public void ExecutableLookupCallsProduceStableObjectReferencesAndSpans()
	{
		string source =
			"var first = _O(137);\n"
			+ "var second = _O(42);";

		ScriptReferenceAnalysis analysis =
			ScriptReferenceAnalyzer.Analyze(source);

		analysis.IsReliable.Should().BeTrue();
		analysis.Diagnostics.Should().BeEmpty();
		analysis.References.Select(reference => reference.Id)
			.Should().Equal((ObjectId)137U, (ObjectId)42U);
		analysis.References
			.Select(reference =>
				source.Substring(
					reference.Span.Start,
					reference.Span.Length))
			.Should().Equal("_O(137)", "_O(42)");
	}

	[Test]
	public void CommentsAndStringFormsDoNotCreateObjectReferences()
	{
		string source = """"
			var ordinary = "_O(1)";
			var raw = """_O(2)""";
			// _O(3)
			/* _O(4) */
			var ch = '_';
			var live = _O(5);
			"""";

		ScriptReferenceAnalysis analysis =
			ScriptReferenceAnalyzer.Analyze(source);

		analysis.IsReliable.Should().BeTrue();
		analysis.Diagnostics.Should().BeEmpty();
		analysis.References.Should().ContainSingle();
		analysis.References[0].Id.Should().Be((ObjectId)5U);
	}

	[Test]
	public void MalformedLookupCallsProduceDiagnosticsRatherThanGuessedReferences()
	{
		string source =
			"_O(1 + 2);\n"
			+ "_O();\n"
			+ "_O(-3);";

		ScriptReferenceAnalysis analysis =
			ScriptReferenceAnalyzer.Analyze(source);

		analysis.IsReliable.Should().BeFalse();
		analysis.References.Should().BeEmpty();
		analysis.Diagnostics.Should().HaveCount(3);
		analysis.Diagnostics
			.Should().OnlyContain(diagnostic =>
				diagnostic.Code == "HRS1001"
				&& diagnostic.Severity == ScriptDiagnosticSeverity.Error);
	}

	[Test]
	public void ShadowingLookupFunctionDoesNotCreateObjectReferences()
	{
		string source = """
			object _O(uint id) => id;
			var value = _O(137);
			""";

		ScriptReferenceAnalysis analysis =
			ScriptReferenceAnalyzer.Analyze(source);

		analysis.IsReliable.Should().BeTrue();
		analysis.Diagnostics.Should().BeEmpty();
		analysis.References.Should().BeEmpty();
	}

	[Test]
	public void SyntaxErrorNearLookupIsReportedWithoutGuessingReference()
	{
		ScriptReferenceAnalysis analysis =
			ScriptReferenceAnalyzer.Analyze(
				"var value = _O(7");

		analysis.IsReliable.Should().BeFalse();
		analysis.References.Should().BeEmpty();
		analysis.Diagnostics.Should().NotBeEmpty();
		analysis.Diagnostics
			.Should().Contain(diagnostic =>
				diagnostic.Severity == ScriptDiagnosticSeverity.Error);
	}
}
