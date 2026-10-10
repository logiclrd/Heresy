using System;
using System.Linq;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Scripting.Compilation;
using NUnit.Framework;

namespace Heresy.Tests.Scripting;

[TestFixture]
public sealed class ScriptInstrumentCompilationTests
{
    [Test]
    public void CompiledSelectorUsesInvocationPitchAndReturnsDeclaredIndexOrSilence()
    {
        ScriptInstrumentDefinition definition = new((ObjectId)9U, "Selector")
        {
            Source = "pitchMultiplier < 0.8 ? -1 : pitchMultiplier < 2.0 ? 0 : 1",
        };
        var compiled = ScriptCompiler.CompileInstrument(definition);
        Assert.That(compiled.Success, Is.True,
            string.Join("; ", compiled.Diagnostics.Select(d => d.Message)));
        Assert.Multiple(() =>
        {
            Assert.That(compiled.Program!.SelectToneIndex(0.5), Is.EqualTo(-1));
            Assert.That(compiled.Program!.SelectToneIndex(1.0), Is.Zero);
            Assert.That(compiled.Program!.SelectToneIndex(2.0), Is.EqualTo(1));
            Assert.That(compiled.Program!.SelectToneIndex(2.0), Is.EqualTo(1));
        });
    }

    [TestCase("System.IO.File.ReadAllText(\"path\")")]
    [TestCase("new object()")]
    [TestCase("Math.Round(pitchMultiplier)")]
    [TestCase("pitchMultiplier > 1 ? 0 : missingName")]
    [TestCase("0; 1")]
    public void UnsafeOrInvalidSelectorDoesNotCompile(string source)
    {
        ScriptInstrumentDefinition definition = new((ObjectId)1U, "Invalid")
        { Source = source };
        var compiled = ScriptCompiler.CompileInstrument(definition);
        Assert.That(compiled.Success, Is.False);
        Assert.That(compiled.Program, Is.Null);
    }

    [Test]
    public void HugeSelectorIsRejectedByComplexityBudget()
    {
        ScriptInstrumentDefinition definition = new((ObjectId)1U, "Too long")
        { Source = new string('1', 2200) };
        Assert.That(ScriptCompiler.CompileInstrument(definition).Success, Is.False);
    }
}
