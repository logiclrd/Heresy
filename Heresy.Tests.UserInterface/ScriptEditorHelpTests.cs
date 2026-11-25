using System;

using Avalonia.Controls;
using Avalonia.Headless.NUnit;

using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class ScriptEditorHelpTests
{
    [Test]
    public void PatternReferenceCoversAllCurrentAuthorCallableHelpers()
    {
        string reference = ScriptEditorHelp.PatternReference;
        string[] methods =
        [
            "_O", "Random", "Checkpoint", "Note", "NoteVirtual",
            "NoteFilter", "NoteFilterVirtual", "ChannelFilter",
            "ChannelFilterVirtual", "VolumeVirtual", "SourceFrameVirtual",
            "DisplacementVirtual", "PastVirtual", "OffVirtual", "CutVirtual",
            "OffVirtualInScope", "CutVirtualInScope", "OffAllVirtual",
            "CutAllVirtual", "Off", "Cut", "Tempo", "Speed", "Seek",
            "Pan", "Surround", "Filter", "FilterCutoff", "FilterResonance",
            "Semitone", "Cent", "Octave", "SemitoneOffset",
        ];
        foreach (string method in methods)
            Assert.That(reference,
                Does.Contain(method + "("),
                $"The current runtime helper {method} needs editor documentation.");

        Assert.That(reference, Does.Contain(
            "double pitchMultiplier = 1.0"));
        Assert.That(reference, Does.Contain("Semitone(7)"));
        Assert.That(reference, Does.Contain("Cent(-25)"));
        Assert.That(reference, Does.Contain("Octave(-1)"));
        Assert.That(reference,
            Does.Contain("native source frames"));
        Assert.That(reference,
            Does.Contain("playback in seconds"));
    }

    [Test]
    public void PatternReferenceExplainsOrderingModesAndTheirTradeoffs()
    {
        string reference = ScriptEditorHelp.PatternReference;
        Assert.That(reference, Does.Contain("#order strict").And.Contain("default if omitted"));
        Assert.That(reference, Does.Contain("#order relaxed").And.Contain("before the first C# statement"));
        Assert.That(reference, Does.Contain("Equal rows retain original emission order"));
        Assert.That(reference, Does.Contain("Generation must complete successfully"));
        Assert.That(reference, Does.Contain("additional memory").And.Contain("cooperative cancellation"));
        Assert.That(reference, Does.Contain("synchronous lambdas").And.Contain("method-group delegates"));
        Assert.That(reference, Does.Not.Contain("future design"));
        Assert.That(reference, Does.Contain("F6 highlights an active authored delegate invocation"));
    }

    [Test]
    public void SequenceReferenceDescribesActualPatternOrderContract()
    {
        string reference = ScriptEditorHelp.SequenceReference;
        Assert.That(reference, Does.Contain(
            "Play(ObjectId patternId, int startRow = 0)"));
        Assert.That(reference, Does.Contain(
            "previousSequenceIndex"));
        Assert.That(reference, Does.Contain(
            "falling through ends the Sequence"));
        Assert.That(reference, Does.Contain(
            "not a Sequence ID"));
    }

    [Test]
    public void ScalarScriptReferencesDoNotPromiseProceduralToneConstruction()
    {
        string envelopeReference = ScriptEditorHelp.EnvelopeReference;
        string instrumentReference = ScriptEditorHelp.InstrumentReference;
        Assert.That(envelopeReference,
            Does.Contain("releaseSeconds"));
        Assert.That(instrumentReference,
            Does.Contain("pitchMultiplier"));
        Assert.That(instrumentReference,
            Does.Contain("declared ToneSpecifications"));
        foreach (string reference in new[]
        {
            ScriptEditorHelp.SequenceReference,
            ScriptEditorHelp.PatternReference,
            envelopeReference,
            instrumentReference,
        })
        {
            foreach (string helper in new[]
                { "Semitone", "Cent", "Octave", "SemitoneOffset" })
                Assert.That(reference, Does.Contain(helper + "("));
        }
    }

    [AvaloniaTest]
    public void ReferenceViewerIsMultilineReadOnlySelectableAndHeightBounded()
    {
        TextBox viewer = ScriptEditorHelp.CreateReferenceViewer(
            ScriptEditorHelp.PatternReference);
        Assert.That(viewer.IsReadOnly, Is.True);
        Assert.That(viewer.AcceptsReturn, Is.True);
        Assert.That(viewer.Text, Is.EqualTo(ScriptEditorHelp.PatternReference));
        Assert.That(viewer.Height, Is.GreaterThan(150));
        Assert.That(viewer.Height, Is.LessThan(350));
        Assert.That(viewer.IsEnabled, Is.True,
            "Reference must remain selectable for copying");
    }
}
