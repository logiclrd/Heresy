using System;
using System.Linq;

using Heresy.Core.Envelopes;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.Core.Sequencing;
using Heresy.Scripting.Compilation;
using Heresy.Scripting.Runtime;

using NUnit.Framework;

namespace Heresy.Tests.Scripting;

[TestFixture]
public sealed class ScriptPitchMathTests
{
    [TestCase(0.0, 1.0)]
    [TestCase(12.0, 2.0)]
    [TestCase(-12.0, 0.5)]
    [TestCase(7.0, 1.4983070768766815)]
    [TestCase(0.5, 1.029302236643492)]
    public void SemitonesProduceTwelveTetFrequencyRatios(double amount, double expected)
        => Assert.That(ScriptPitchMath.Semitone(amount),
            Is.EqualTo(expected).Within(1e-12));

    [Test]
    public void CentsAndOctavesUseTheSameReferenceScale()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ScriptPitchMath.Cent(0.0), Is.EqualTo(1.0));
            Assert.That(ScriptPitchMath.Cent(1200.0), Is.EqualTo(2.0));
            Assert.That(ScriptPitchMath.Cent(-1200.0), Is.EqualTo(0.5));
            Assert.That(ScriptPitchMath.Cent(100.0),
                Is.EqualTo(ScriptPitchMath.Semitone(1.0)).Within(1e-12));
            Assert.That(ScriptPitchMath.Octave(0.0), Is.EqualTo(1.0));
            Assert.That(ScriptPitchMath.Octave(1.0), Is.EqualTo(2.0));
            Assert.That(ScriptPitchMath.Octave(-1.0), Is.EqualTo(0.5));
            Assert.That(ScriptPitchMath.Octave(0.5),
                Is.EqualTo(Math.Sqrt(2.0)).Within(1e-12));
        });
    }

    [Test]
    public void InverseAcceptsAnyPositiveFiniteRatioIncludingSubnormal()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ScriptPitchMath.SemitoneOffset(1.0), Is.EqualTo(0.0));
            Assert.That(ScriptPitchMath.SemitoneOffset(2.0), Is.EqualTo(12.0));
            Assert.That(ScriptPitchMath.SemitoneOffset(0.5), Is.EqualTo(-12.0));
            Assert.That(ScriptPitchMath.SemitoneOffset(double.Epsilon),
                Is.EqualTo(-12888.0).Within(1e-9));
            Assert.That(ScriptPitchMath.SemitoneOffset(
                ScriptPitchMath.Semitone(-7.25)),
                Is.EqualTo(-7.25).Within(1e-12));
        });
    }

    [TestCase(double.NaN)]
    [TestCase(double.PositiveInfinity)]
    [TestCase(double.NegativeInfinity)]
    [TestCase(double.MaxValue)]
    [TestCase(-double.MaxValue)]
    public void NonFiniteAndUnrepresentableOffsetsAreRejected(double offset)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ScriptPitchMath.Semitone(offset));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ScriptPitchMath.Cent(offset));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ScriptPitchMath.Octave(offset));
    }

    [Test]
    public void RepresentableSubnormalIsKeptWhileOverflowAndUnderflowAreRejected()
    {
        Assert.That(ScriptPitchMath.Octave(-1074.0),
            Is.EqualTo(double.Epsilon));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ScriptPitchMath.Octave(-1076.0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ScriptPitchMath.Octave(1024.0));
        Assert.That(ScriptPitchMath.Octave(1023.0),
            Is.GreaterThan(0).And.LessThan(double.PositiveInfinity));
    }

    [TestCase(0.0)]
    [TestCase(-0.0)]
    [TestCase(-1.0)]
    [TestCase(double.NaN)]
    [TestCase(double.PositiveInfinity)]
    [TestCase(double.NegativeInfinity)]
    public void InverseRejectsNonpositiveAndNonfiniteInputs(double ratio)
        => Assert.Throws<ArgumentOutOfRangeException>(() =>
            ScriptPitchMath.SemitoneOffset(ratio));

    [Test]
    public void EagerAndIncrementalPatternsEmitTheSamePitchRatiosAndOrder()
    {
        const string source = """
            Note(0, 0, _O(17), pitchMultiplier: Semitone(7));
            NoteVirtual(1, 3U, _O(17), pitchMultiplier: Cent(-25));
            Note(2, 0, _O(17), pitchMultiplier: Octave(-1) * Semitone(4));
            """;
        ScriptPatternDefinition pattern = new((ObjectId)1U, "Conversions")
        {
            Source = source,
            RowCount = 4,
            ChannelCount = 1,
        };
        var eager = ScriptCompiler.CompilePattern(pattern);
        var incremental = ScriptCompiler.CompileIncrementalPattern(pattern);
        Assert.Multiple(() =>
        {
            Assert.That(eager.Success, Is.True,
                string.Join("; ", eager.Diagnostics.Select(x => x.Message)));
            Assert.That(incremental.Success, Is.True,
                string.Join("; ", incremental.Diagnostics.Select(x => x.Message)));
        });

        NoteScheduleBuilder output = new();
        eager.Program!.GenerateRawNotes(new SequencingContext(), output, out _);
        NoteEvent[] eagerNotes = output.Freeze().ToArray();
        NoteEvent[] streamingNotes = incremental.Program!
            .EnumerateRawSteps(new SequencingContext())
            .OfType<RawPatternStep.Emit>()
            .Select(x => x.Note)
            .ToArray();
        Assert.That(eagerNotes.Length, Is.EqualTo(3));
        Assert.That(streamingNotes.Length, Is.EqualTo(3));
        double[] expected =
        [
            ScriptPitchMath.Semitone(7.0),
            ScriptPitchMath.Cent(-25.0),
            ScriptPitchMath.Octave(-1.0) * ScriptPitchMath.Semitone(4.0),
        ];

        for (int i = 0; i < expected.Length; i++)
        {
            Assert.That(eagerNotes[i].Offset.RowOffset,
                Is.EqualTo(streamingNotes[i].Offset.RowOffset));
            Assert.That(eagerNotes[i].Target, Is.EqualTo(streamingNotes[i].Target));
            double eagerPitch = eagerNotes[i].Commands.OfType<StartNoteCommand>()
                .Single().PitchMultiplier;
            double streamingPitch = streamingNotes[i].Commands.OfType<StartNoteCommand>()
                .Single().PitchMultiplier;
            Assert.That(eagerPitch, Is.EqualTo(expected[i]).Within(1e-12));
            Assert.That(streamingPitch, Is.EqualTo(eagerPitch).Within(1e-12));
        }
    }

    [Test]
    public void SequenceLookupCanUseConversionsInNonemittingExpressions()
    {
        ScriptSequenceDefinition sequence = new((ObjectId)1U, "Pitch constants")
        {
            Source = """
                if (Semitone(12) == 2.0 && Octave(-1) == 0.5)
                    return Play(_O(17));
                return default;
                """,
        };
        var result = ScriptCompiler.CompileIncrementalSequence(sequence);
        Assert.That(result.Success, Is.True,
            string.Join("; ", result.Diagnostics.Select(x => x.Message)));
        ISequenceEntryProvider provider = result.Program!.Create(new SequencingContext());
        Assert.That(provider.GetSequenceEntry(0, 0, -1),
            Is.EqualTo(new SequenceEntry((ObjectId)17U)));
    }

    [Test]
    public void InstrumentSelectorPitchScalingAndNnaShareTheConversions()
    {
        ScriptInstrumentDefinition instrument = new((ObjectId)2U, "Pitch")
        {
            Source = "SemitoneOffset(pitchMultiplier) >= 12.0 ? 1 : 0",
            PitchExpression = "Cent(-25) * Octave(1)",
            NnaExpression = "pitchMultiplier >= Semitone(12) ? InstrumentNnaAction.Cut : InstrumentNnaAction.Inherit",
        };
        var selector = ScriptCompiler.CompileInstrument(instrument);
        var scaling = ScriptCompiler.CompileInstrumentPitch(instrument);
        var nna = ScriptCompiler.CompileInstrumentNna(instrument);
        Assert.Multiple(() =>
        {
            Assert.That(selector.Success, Is.True,
                string.Join("; ", selector.Diagnostics.Select(x => x.Message)));
            Assert.That(scaling.Success, Is.True,
                string.Join("; ", scaling.Diagnostics.Select(x => x.Message)));
            Assert.That(nna.Success, Is.True,
                string.Join("; ", nna.Diagnostics.Select(x => x.Message)));
        });
        Assert.That(selector.Program!.SelectToneIndex(1.0), Is.Zero);
        Assert.That(selector.Program.SelectToneIndex(2.0), Is.EqualTo(1));
        Assert.That(scaling.Program!.GetPitchScale(1.0),
            Is.EqualTo(ScriptPitchMath.Cent(-25.0) * 2.0).Within(1e-12));
        Assert.That(nna.Program!.SelectNna(1.0), Is.EqualTo(InstrumentNnaAction.Inherit));
        Assert.That(nna.Program.SelectNna(2.0), Is.EqualTo(InstrumentNnaAction.Cut));
    }

    [Test]
    public void EnvelopeAndParametricExpressionsCanUseTheSameHelpers()
    {
        ScriptEnvelopeDefinition envelope = new((ObjectId)3U, "Envelope")
            { Source = "Semitone(12) + Cent(100) - Semitone(1)" };
        var compiledEnvelope = ScriptCompiler.CompileEnvelope(envelope);
        var parametric = ScriptCompiler.CompileParametric(
            "Octave(-1) + SemitoneOffset(2.0) / 12.0");
        Assert.Multiple(() =>
        {
            Assert.That(compiledEnvelope.Success, Is.True,
                string.Join("; ", compiledEnvelope.Diagnostics.Select(x => x.Message)));
            Assert.That(parametric.Success, Is.True,
                string.Join("; ", parametric.Diagnostics.Select(x => x.Message)));
        });
        Assert.That(compiledEnvelope.Program!.Evaluate(0.0, -1.0),
            Is.EqualTo(2.0).Within(1e-12));
        Assert.That(parametric.Program!.Evaluate(0.0, 0.0),
            Is.EqualTo(1.5).Within(1e-12));
    }

    [TestCase("System.IO.File.ReadAllText(\"secret\")")]
    [TestCase("ScriptPitchMath.Semitone(7)")]
    [TestCase("Math.Abs(Semitone(7)).ToString()")]
    [TestCase("double.IsFinite(Octave(1))")]
    public void RestrictedCompilerStillRejectsUnapprovedMembers(string expression)
    {
        var result = ScriptCompiler.CompileParametric(expression);
        Assert.That(result.Success, Is.False);
        Assert.That(result.Program, Is.Null);
    }

    [Test]
    public void ScalarHelpersAreAllowedInsideStreamingNoteArgumentsButEmissionStillRequiresStatements()
    {
        ScriptPatternDefinition valid = new((ObjectId)4U, "Nested conversion")
        {
            Source = "Note(0, 0, _O(17), pitchMultiplier: Math.Max(0.01, Semitone(-2)));",
            RowCount = 1,
            ChannelCount = 1,
        };
        Assert.That(ScriptCompiler.CompileIncrementalPattern(valid).Success, Is.True);

        valid.Source = "int ignore = Note(0, 0, _O(17), pitchMultiplier: Semitone(2));";
        Assert.That(ScriptCompiler.CompileIncrementalPattern(valid).Success, Is.False);
    }
}
