using System.Linq;

using AwesomeAssertions;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.Core.Scripting;
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

	[Test]
	public void ProjectionUsesCurrentLiveNameWithoutChangingPersistedId()
	{
		SongDocument document = new();
		ObjectId id = document.AllocateObjectId();
		ScriptPatternDefinition pattern =
			new(id, "Original Name");
		document.Add(pattern);

		string source = "_O(1)";
		ScriptReferenceAnalysis analysis =
			ScriptReferenceAnalyzer.Analyze(source);

		ProjectedScriptObjectReference before =
			ScriptObjectReferenceProjector.Project(
				document,
				analysis).Single();

		pattern.Name = "Renamed Pattern";

		ProjectedScriptObjectReference after =
			ScriptObjectReferenceProjector.Project(
				document,
				analysis).Single();

		before.Id.Should().Be(id);
		before.Name.Should().Be("Original Name");
		before.Kind.Should().Be(SongObjectKind.Pattern);
		before.Resolution.Should().Be(ScriptObjectReferenceResolution.Live);
		after.Id.Should().Be(id);
		after.Name.Should().Be("Renamed Pattern");
		after.Resolution.Should().Be(ScriptObjectReferenceResolution.Live);
		source.Should().Be("_O(1)");
	}

	[Test]
	public void ProjectionUsesTombstoneNameAndKindForDeletedObject()
	{
		SongDocument document = new();
		ObjectId id = document.AllocateObjectId();
		document.Add(
			new ScriptSequenceDefinition(
				id,
				"Deleted Arrangement"));
		document.Remove(id);

		ProjectedScriptObjectReference projected =
			ScriptObjectReferenceProjector.Project(
				document,
				ScriptReferenceAnalyzer.Analyze("_O(1)"))
				.Single();

		projected.Id.Should().Be(id);
		projected.Name.Should().Be("Deleted Arrangement");
		projected.Kind.Should().Be(SongObjectKind.Sequence);
		projected.Resolution.Should().Be(
			ScriptObjectReferenceResolution.Tombstone);
		projected.DisplayName.Should().Be("Deleted Arrangement");
	}

	[Test]
	public void ProjectionFallsBackToCanonicalRawIdWhenNoObjectOrTombstoneExists()
	{
		SongDocument document = new();

		ProjectedScriptObjectReference projected =
			ScriptObjectReferenceProjector.Project(
				document,
				ScriptReferenceAnalyzer.Analyze("_O(999)"))
				.Single();

		projected.Id.Should().Be((ObjectId)999U);
		projected.Name.Should().BeNull();
		projected.Kind.Should().Be(SongObjectKind.Unknown);
		projected.Resolution.Should().Be(
			ScriptObjectReferenceResolution.Missing);
		projected.DisplayName.Should().Be("_O(999)");
	}

	[Test]
	public void CoreAdapterReturnsRoslynObjectIdsAndReliability()
	{
		ScriptObjectReferenceSet result =
			RoslynScriptObjectReferenceAnalyzer.Instance
				.AnalyzeObjectReferences(
					"_O(137); // _O(42)");

		result.IsReliable.Should().BeTrue();
		result.ObjectIds.Should().Equal((ObjectId)137U);
	}

	[Test]
	public void CoreAdapterPreservesUnreliableAnalysisState()
	{
		ScriptObjectReferenceSet result =
			RoslynScriptObjectReferenceAnalyzer.Instance
				.AnalyzeObjectReferences("_O()");

		result.IsReliable.Should().BeFalse();
		result.ObjectIds.Should().BeEmpty();
	}
}
