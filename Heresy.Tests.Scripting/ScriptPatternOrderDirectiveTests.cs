using System;
using System.Linq;

using AwesomeAssertions;

using Heresy.Scripting.Analysis;

using NUnit.Framework;

namespace Heresy.Tests.Scripting;

[TestFixture]
public sealed class ScriptPatternOrderDirectiveTests
{
	[TestCase("", ScriptPatternOrder.Strict)]
	[TestCase("Note(0, 0, _O(1));", ScriptPatternOrder.Strict)]
	[TestCase("#order strict", ScriptPatternOrder.Strict)]
	[TestCase("#order relaxed", ScriptPatternOrder.Relaxed)]
	[TestCase("  # order relaxed // comment\n", ScriptPatternOrder.Relaxed)]
	[TestCase("// lead-in\r\n/* explanation */\r\n#order relaxed\r\nNote(0, 0, _O(1));", ScriptPatternOrder.Relaxed)]
	public void RecognizesOptionalVisibleDirective(string source, ScriptPatternOrder order)
	{
		var result = ScriptPatternOrderDirective.Analyze(source);
		result.Order.Should().Be(order);
		result.Diagnostics.Should().BeEmpty();
		result.CompilerSource.Length.Should().Be(source.Length);
		for (int i = 0; i < source.Length; i++)
		{
			bool masked = result.Directives.Any(span => i >= span.Start && i < span.End);
			result.CompilerSource[i].Should().Be(masked ? ' ' : source[i]);
		}
	}

	[TestCase("#order")]
	[TestCase("#order unknown")]
	[TestCase("#order relaxed extra")]
	[TestCase("#order strict\n#order relaxed")]
	[TestCase("#order relaxed\n#order relaxed")]
	[TestCase("Note(0, 0, _O(1));\n#order relaxed")]
	public void MalformedDuplicateOrLateDirectiveIsDiagnosed(string source)
	{
		var result = ScriptPatternOrderDirective.Analyze(source);
		result.Diagnostics.Should().Contain(d => d.Code == "HRS1002"
			&& d.Severity == ScriptDiagnosticSeverity.Error);
	}

	[TestCase("// #order relaxed")]
	[TestCase("/*\n#order relaxed\n*/")]
	[TestCase("var s = \"#order relaxed\";")]
	[TestCase("var s = @\"\n#order relaxed\n\";")]
	[TestCase("var s = \"\"\"\n#order relaxed\n\"\"\";")]
	[TestCase("#if false\n#order relaxed\n#endif")]
	[TestCase("#ordering relaxed")]
	[TestCase("#line 1 \"heresy-script\"")]
	public void DoesNotInterpretOtherTriviaOrLiteralContent(string source)
	{
		var result = ScriptPatternOrderDirective.Analyze(source);
		result.Order.Should().Be(ScriptPatternOrder.Strict);
		result.Directives.Should().BeEmpty();
		result.CompilerSource.Should().Be(source);
	}

	[Test]
	public void PreservesObjectReferenceAndCallsiteCoordinates()
	{
		const string source = "// 😀\r\n#order relaxed\r\nNote(0, 0, _O(17));";
		var result = ScriptPatternOrderDirective.Analyze(source);
		result.CompilerSource.IndexOf("Note(", StringComparison.Ordinal)
			.Should().Be(source.IndexOf("Note(", StringComparison.Ordinal));
		result.CompilerSource.IndexOf("_O(", StringComparison.Ordinal)
			.Should().Be(source.IndexOf("_O(", StringComparison.Ordinal));
	}
}
