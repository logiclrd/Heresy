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
            Assert.That(ScriptEditorHelp.PatternReference,
                Does.Contain(method + "("),
                $"The current runtime helper {method} needs editor documentation.");

        Assert.That(ScriptEditorHelp.PatternReference, Does.Contain(
            "double pitchMultiplier = 1.0"));
        Assert.That(ScriptEditorHelp.PatternReference,
            Does.Contain("Semitone(...) is NOT currently an implemented"));
        Assert.That(ScriptEditorHelp.PatternReference,
            Does.Contain("native source frames"));
        Assert.That(ScriptEditorHelp.PatternReference,
            Does.Contain("playback in seconds"));
    }

    [Test]
    public void SequenceReferenceDescribesActualPatternOrderContract()
    {
        Assert.That(ScriptEditorHelp.SequenceReference, Does.Contain(
            "Play(ObjectId patternId, int startRow = 0)"));
        Assert.That(ScriptEditorHelp.SequenceReference, Does.Contain(
            "previousSequenceIndex"));
        Assert.That(ScriptEditorHelp.SequenceIntroduction, Does.Contain(
            "reaching the end without returning"));
        Assert.That(ScriptEditorHelp.SequenceReference, Does.Contain(
            "not a Sequence ID"));
    }

    [Test]
    public void ScalarScriptReferencesDoNotPromiseProceduralToneConstruction()
    {
        Assert.That(ScriptEditorHelp.EnvelopeReference,
            Does.Contain("releaseSeconds"));
        Assert.That(ScriptEditorHelp.InstrumentReference,
            Does.Contain("pitchMultiplier"));
        Assert.That(ScriptEditorHelp.InstrumentReference,
            Does.Contain("predeclared"));
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
