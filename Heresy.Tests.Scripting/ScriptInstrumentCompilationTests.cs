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
    public void CompiledPitchScaleIsIndependentOfToneSelection()
    {
        ScriptInstrumentDefinition definition = new((ObjectId)9U, "Dynamic pitch")
        {
            Source = "0",
            PitchExpression = "pitchMultiplier < 1.5 ? 2.0 : 0.25",
        };
        var result = ScriptCompiler.CompileInstrumentPitch(definition);
        Assert.That(result.Success, Is.True,
            string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.Multiple(() =>
        {
            Assert.That(result.Program!.GetPitchScale(1.0), Is.EqualTo(2.0));
            Assert.That(result.Program!.GetPitchScale(2.0), Is.EqualTo(0.25));
        });
        definition.PitchExpression = "System.IO.File.ReadAllText(\"path\")";
        Assert.That(ScriptCompiler.CompileInstrumentPitch(definition).Success,
            Is.False);
    }

    [Test]
    public void ScriptedNnaEvaluatesSameIncomingPitchAsToneSelector()
    {
        ScriptInstrumentDefinition definition = new((ObjectId)91U, "Script NNA")
        {
            Source = "pitchMultiplier < 1.5 ? 0 : 1",
            NnaExpression =
                "pitchMultiplier < 1.5 ? InstrumentNnaAction.Inherit : " +
                "pitchMultiplier < 3.0 ? InstrumentNnaAction.Off : InstrumentNnaAction.Fade",
            NnaFadeDuration = TimeSpan.FromMilliseconds(42),
        };
        var compiled = ScriptCompiler.CompileInstrumentNna(definition);
        Assert.That(compiled.Success, Is.True,
            string.Join("; ", compiled.Diagnostics.Select(d => d.Message)));
        Assert.That(compiled.Program!.SelectNna(1.0), Is.EqualTo(InstrumentNnaAction.Inherit));
        Assert.That(compiled.Program!.SelectNna(2.0), Is.EqualTo(InstrumentNnaAction.Off));
        Assert.That(compiled.Program!.SelectNna(4.0), Is.EqualTo(InstrumentNnaAction.Fade));
        ToneSpecification tone = new() { SourceId = (ObjectId)92U,
            Nna = new InstrumentNnaSetting(InstrumentNnaAction.Continue) };
        Assert.That(definition.ResolveNna(1, tone, compiled.Program.SelectNna).Action,
            Is.EqualTo(InstrumentNnaAction.Continue));
        Assert.That(definition.ResolveNna(2, tone, compiled.Program.SelectNna).Action,
            Is.EqualTo(InstrumentNnaAction.Off));
        Assert.That(definition.ResolveNna(4, tone, compiled.Program.SelectNna),
            Is.EqualTo(InstrumentNnaSetting.Fade(TimeSpan.FromMilliseconds(42))));
    }

    [TestCase("System.IO.File.ReadAllText(\"file\")")]
    [TestCase("new object()")]
    [TestCase("InstrumentNnaAction.NotAValue")]
    [TestCase("(InstrumentNnaAction)999")]
    public void InvalidOrUnsafeScriptedNnaFailsCompileOrActionValidation(string expression)
    {
        ScriptInstrumentDefinition definition = new((ObjectId)92U, "Invalid NNA")
        { NnaExpression = expression };
        var compiled = ScriptCompiler.CompileInstrumentNna(definition);
        if (expression.Contains("999", StringComparison.Ordinal))
        {
            Assert.That(compiled.Success, Is.True);
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                definition.ResolveNna(1,
                    new ToneSpecification { SourceId = (ObjectId)93U },
                    compiled.Program!.SelectNna));
        }
        else Assert.That(compiled.Success, Is.False);
    }

    [Test]
    public void HugeSelectorIsRejectedByComplexityBudget()
    {
        ScriptInstrumentDefinition definition = new((ObjectId)1U, "Too long")
        { Source = new string('1', 2200) };
        Assert.That(ScriptCompiler.CompileInstrument(definition).Success, Is.False);
    }
}
