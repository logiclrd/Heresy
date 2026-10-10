using System;
using System.Linq;

using Heresy.Core.Envelopes;
using Heresy.Core.Objects;
using Heresy.Scripting.Compilation;

using NUnit.Framework;

namespace Heresy.Tests.Scripting;

[TestFixture]
public sealed class ScriptEnvelopeCompilationTests
{
	[Test]
	public void ScalarExpressionCompilesDeterministicallyWithoutEvaluatingOnCompile()
	{
		ScriptEnvelopeDefinition definition = new((ObjectId)1U, "Envelope")
		{
			Source = "releaseSeconds < 0 ? Math.Sin(elapsedSeconds) : 1.0 - releaseSeconds",
			ReleaseDuration = TimeSpan.FromSeconds(1),
		};
		var result = ScriptCompiler.CompileEnvelope(definition);
		Assert.That(result.Success, Is.True,
			string.Join("; ", result.Diagnostics.Select(d => d.Message)));
		Assert.Multiple(() =>
		{
			Assert.That(result.Program!.Evaluate(Math.PI / 2, -1.0),
				Is.EqualTo(1.0).Within(1e-12));
			Assert.That(result.Program!.Evaluate(8, 0.25),
				Is.EqualTo(0.75));
			Assert.That(result.Program!.Evaluate(8, 0.25),
				Is.EqualTo(0.75));
		});
	}

	[TestCase("")]
	[TestCase("1.0; System.Console.WriteLine(42)")]
	[TestCase("System.IO.File.ReadAllText(\"secret\")")]
	[TestCase("new object()")]
	[TestCase("Math.Abs(2) + System.Environment.TickCount")]
	[TestCase("elapsedSeconds + unknownSymbol")]
	public void IllegalOrUnboundExpressionsFailBeforeGeneratingExecutableObjects(string source)
	{
		ScriptEnvelopeDefinition definition = new((ObjectId)1U, "Invalid")
			{ Source = source };
		var result = ScriptCompiler.CompileEnvelope(definition);
		Assert.That(result.Success, Is.False);
		Assert.That(result.Program, Is.Null);
		Assert.That(result.Diagnostics.Any(d =>
			d.Severity == Heresy.Scripting.Analysis.ScriptDiagnosticSeverity.Error),
			Is.True);
	}

	[Test]
	public void ExpressionHasAComplexityCap()
	{
		ScriptEnvelopeDefinition definition = new((ObjectId)1U, "Too long")
			{ Source = new string('1', 2050) };
		var result = ScriptCompiler.CompileEnvelope(definition);
		Assert.That(result.Success, Is.False);
		Assert.That(result.Program, Is.Null);
	}
}
