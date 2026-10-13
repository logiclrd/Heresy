using System;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.FmEditing;
using Heresy.UserInterface.Views;
using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class FmNoiseEditorTests
{
    [AvaloniaTest]
    public void WhiteNoiseEditorIsOutputOnlyWithNoFrequencyControlAndSafeDeletion()
    {
        DocumentWorkspace workspace = new();
        FmSynthDefinition synth =
            FmSynthDocumentEditor.CreateFmSynth(workspace, "Noise");
        int noiseId = FmSynthDocumentEditor.AddNoiseNode(
            workspace, synth, 170, 85);
        FmNoiseNode noise = synth.Graph.Nodes.OfType<FmNoiseNode>().Single();
        Assert.That(noise.Id, Is.EqualTo(noiseId));
        Assert.That(FmSynthConnectionPorts.GetPorts(noise,
            new FmSynthLayoutRect(170, 85, 170, 72)).Length, Is.EqualTo(1),
            "native noise has only an output port, no pitch or other input");
        FmSynthDocumentEditor.SetOutputNode(workspace, synth, noiseId);
        FmSynthEditorControl editor = new(workspace, synth, () => {}, _ => {});
        StackPanel inspector = (StackPanel)typeof(FmSynthEditorControl)
            .GetField("_inspector", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(editor)!;
        Assert.That(inspector.Children.OfType<TextBlock>()
            .Any(t => t.Text?.Contains("native output sample rate",
                StringComparison.OrdinalIgnoreCase) == true), Is.True);
        Assert.That(inspector.Children.OfType<TextBox>(), Is.Empty,
            "there must be no Frequency Hz or implicit shaping control");
        FmSynthDocumentEditor.SetOutputNode(workspace, synth, 0);
        uint revision = workspace.Document.AudioRevision;
        FmSynthDocumentEditor.RemoveNode(workspace, synth, noiseId);
        Assert.That(synth.Graph.Nodes.OfType<FmNoiseNode>(), Is.Empty);
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(revision + 1));
    }

    [Test]
    public void NoiseSurvivesBothSimpleAndSourceGraphCrossSongImports()
    {
        SongDocument original = new();
        ObjectId simpleId = original.AllocateObjectId();
        FmSynthDefinition simple = new(simpleId, "Noise",
            new FmSynthGraph([new FmNoiseNode(17)], 17));
        original.Add(simple);
        ObjectId parentId = original.AllocateObjectId();
        FmSynthDefinition parent = new(parentId, "Nested Noise",
            new FmSynthGraph([
                new FmSoundSourceNode(0, simpleId,
                    440.0 * Math.Pow(2, -9.0 / 12.0)),
            ], 0));
        original.Add(parent);
        SongFmSynthImportSource source = new("source.hm.json",
            original, [simple, parent]);
        DocumentWorkspace target = new();
        FmSynthDefinition simpleCopy = FmSynthDocumentEditor.ImportFromSong(
            target, source, [simpleId]).Single();
        Assert.That(simpleCopy.Graph.Nodes.OfType<FmNoiseNode>()
            .Single().Id, Is.EqualTo(17));
        FmSynthDefinition parentCopy = FmSynthDocumentEditor.ImportFromSong(
            target, source, [parentId]).Single();
        ObjectId nestedId = parentCopy.Graph.Nodes
            .OfType<FmSoundSourceNode>().Single().SourceId;
        FmSynthDefinition nested = (FmSynthDefinition)target.Document.Objects[nestedId];
        Assert.That(nested.Graph.Nodes.OfType<FmNoiseNode>()
            .Single().Id, Is.EqualTo(17));
        Assert.That(nestedId, Is.Not.EqualTo(simpleId));
    }
}
