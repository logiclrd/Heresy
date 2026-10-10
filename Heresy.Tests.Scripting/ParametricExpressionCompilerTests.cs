using System;
using System.Linq;
using Heresy.Scripting.Compilation;
using NUnit.Framework;

namespace Heresy.Tests.Scripting;

[TestFixture]
public sealed class ParametricExpressionCompilerTests
{
    [Test]
    public void CompiledExpressionUsesElapsedWallSecondsAndOptionalRowTime()
    {
        var compiled = ScriptCompiler.CompileParametric(
            "Math.Sin(elapsedSeconds) + rowTime * 0.1");
        Assert.That(compiled.Success, Is.True,
            string.Join("; ", compiled.Diagnostics.Select(d => d.Message)));
        Assert.Multiple(() =>
        {
            Assert.That(compiled.Program!.Evaluate(0, 0), Is.Zero);
            Assert.That(compiled.Program!.Evaluate(Math.PI / 2, 2),
                Is.EqualTo(1.2).Within(1e-12));
        });
    }

    [TestCase("System.Environment.TickCount")]
    [TestCase("System.IO.File.ReadAllText(\"private\")")]
    [TestCase("new object()")]
    [TestCase("elapsedSeconds; return 0.0")]
    [TestCase("rowTime + missing")]
    [TestCase("")]
    public void BadExpressionsAreRejectedBeforePlayback(string source)
    {
        var result = ScriptCompiler.CompileParametric(source);
        Assert.That(result.Success, Is.False);
        Assert.That(result.Program, Is.Null);
    }

    [Test]
    public void BoundedExpressionBudgetRejectsGiantInput()
    {
        Assert.That(ScriptCompiler.AnalyzeParametricSource(
            new string('9', 2200)), Is.Not.Empty);
    }
}
