using System.Linq;

using AwesomeAssertions;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.UserInterface.Documents;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class ScriptSyntaxHighlightingTests
{
	[Test]
	public void CatalogClassifiesRoslynLexicalSpans()
	{
		DocumentWorkspace workspace = new();
		ObjectId id = workspace.Document.AllocateObjectId();
		workspace.Document.Add(
			new ScriptPatternDefinition(id, "Piano"));

		const string source = """
			#if DEBUG
			var number = 42;
			var text = "hello";
			// comment
			if (true)
				Note(0, 0, _O(1));
			#endif
			""";

		ScriptSourceDocumentAnalysis analysis =
			ScriptSourceDocumentAnalyzer.Analyze(
				workspace,
				source);
		var references =
			ScriptReferenceVisualTokenCatalog.Create(
				analysis.References);

		ScriptSyntaxHighlightSpan[] spans =
			ScriptSyntaxHighlightCatalog.Create(
					analysis.SyntaxSnapshot,
					references)
				.ToArray();

		Texts(source, spans, ScriptSyntaxHighlightKind.Keyword)
			.Should().Contain(["var", "if", "true"]);
		Texts(source, spans, ScriptSyntaxHighlightKind.Number)
			.Should().Contain("42");
		Texts(source, spans, ScriptSyntaxHighlightKind.String)
			.Should().Contain("\"hello\"");
		Texts(source, spans, ScriptSyntaxHighlightKind.Comment)
			.Should().Contain("// comment");
		spans.Should().Contain(
			span =>
				span.Kind == ScriptSyntaxHighlightKind.Preprocessor
					&& source.Substring(
						span.Span.Start,
						span.Span.Length)
					.StartsWith(
						"#if",
						System.StringComparison.Ordinal));
	}

	[Test]
	public void ObjectReferenceUsesWholeSemanticSpanAndSuppressesNestedLexicalSpans()
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
		var references =
			ScriptReferenceVisualTokenCatalog.Create(
				analysis.References);

		ScriptSyntaxHighlightSpan[] spans =
			ScriptSyntaxHighlightCatalog.Create(
					analysis.SyntaxSnapshot,
					references)
				.ToArray();

		ScriptSyntaxHighlightSpan reference =
			spans.Single(
				span =>
					span.Kind
						== ScriptSyntaxHighlightKind.ObjectReference);
		source.Substring(
				reference.Span.Start,
				reference.Span.Length)
			.Should().Be("_O(1)");

		spans.Should().NotContain(
			span =>
				span.Kind == ScriptSyntaxHighlightKind.Number
					&& span.Span.Start >= reference.Span.Start
					&& span.Span.End <= reference.Span.End);
	}

	[Test]
	public void IncompleteLaterCodeDoesNotDiscardEarlierHighlighting()
	{
		DocumentWorkspace workspace = new();

		const string source =
			"var number = 42;\n"
			+ "if (";
		ScriptSourceDocumentAnalysis analysis =
			ScriptSourceDocumentAnalyzer.Analyze(
				workspace,
				source);

		ScriptSyntaxHighlightSpan[] spans =
			ScriptSyntaxHighlightCatalog.Create(
					analysis.SyntaxSnapshot,
					[])
				.ToArray();

		Texts(source, spans, ScriptSyntaxHighlightKind.Keyword)
			.Should().Contain(["var", "if"]);
		Texts(source, spans, ScriptSyntaxHighlightKind.Number)
			.Should().Contain("42");
	}

	[Test]
	public void RawAndInterpolatedStringTextAreClassifiedAsStrings()
	{
		DocumentWorkspace workspace = new();

		const string source = """"
			var raw = """hello""";
			var value = 3;
			var interpolated = $"number {value}";
			"""";
		ScriptSourceDocumentAnalysis analysis =
			ScriptSourceDocumentAnalyzer.Analyze(
				workspace,
				source);

		ScriptSyntaxHighlightSpan[] spans =
			ScriptSyntaxHighlightCatalog.Create(
					analysis.SyntaxSnapshot,
					[])
				.ToArray();

		Texts(source, spans, ScriptSyntaxHighlightKind.String)
			.Should().Contain(text =>
				text.Contains("hello", System.StringComparison.Ordinal));
		Texts(source, spans, ScriptSyntaxHighlightKind.String)
			.Should().Contain(text =>
				text.Contains("number ", System.StringComparison.Ordinal));
		Texts(source, spans, ScriptSyntaxHighlightKind.Number)
			.Should().Contain("3");
	}

	private static string[] Texts(
		string source,
		System.Collections.Generic.IEnumerable<ScriptSyntaxHighlightSpan> spans,
		ScriptSyntaxHighlightKind kind)
		=> spans
			.Where(span => span.Kind == kind)
			.Select(span =>
				source.Substring(
					span.Span.Start,
					span.Span.Length))
			.ToArray();
}
