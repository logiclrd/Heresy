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
        ];
        foreach (string method in methods)
            Assert.That(reference,
                Does.Contain(method + "("),
                $"The current runtime helper {method} needs editor documentation.");

        Assert.That(reference, Does.Contain(
            "double pitchMultiplier = 1.0"));
        Assert.That(reference,
            Does.Contain("Semitone(...) is NOT currently an implemented"));
        Assert.That(reference,
            Does.Contain("native source frames"));
        Assert.That(reference,
            Does.Contain("playback in seconds"));
    }

    [Test]
    public void SequenceReferenceDescribesActualPatternOrderContract()
    {
        string reference = ScriptEditorHelp.SequenceReference;
        string introduction = ScriptEditorHelp.SequenceIntroduction;
        Assert.That(reference, Does.Contain(
            "Play(ObjectId patternId, int startRow = 0)"));
        Assert.That(reference, Does.Contain(
            "previousSequenceIndex"));
        Assert.That(introduction, Does.Contain(
            "reaching the end without returning"));
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
