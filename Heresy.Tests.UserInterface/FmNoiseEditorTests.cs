using System;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
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
        StackPanel range = inspector.Children.OfType<StackPanel>()
            .Single(x => x.Name == "ValueRangeFields");
        Assert.That(range.Children.OfType<StackPanel>()
            .SelectMany(x => x.Children.OfType<TextBox>()).Count(), Is.EqualTo(2),
            "only authored Vmin/Vmax are editable; noise has no frequency input");
        ValueRangeLinkToggle link = range.Children.OfType<ValueRangeLinkToggle>()
            .Single();
        Assert.That(link.IsChecked, Is.True,
            "fresh bipolar Noise starts linked");
        Assert.That(inspector.Children.OfType<TextBlock>()
            .Any(x => x.Text == "Frequency Hz"), Is.False);
        FmSynthDocumentEditor.SetOutputNode(workspace, synth, 0);
        uint revision = workspace.Document.AudioRevision;
        FmSynthDocumentEditor.RemoveNode(workspace, synth, noiseId);
        Assert.That(synth.Graph.Nodes.OfType<FmNoiseNode>(), Is.Empty);
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(revision + 1));
    }

    [AvaloniaTest]
    public void NoiseRangeInspectorCommitsIndependentlyAndRejectsInvalidInput()
    {
        DocumentWorkspace workspace = new();
        FmSynthDefinition synth =
            FmSynthDocumentEditor.CreateFmSynth(workspace, "Ranged noise");
        int id = FmSynthDocumentEditor.AddNoiseNode(workspace, synth, 100, 90);
        FmSynthDocumentEditor.SetOutputNode(workspace, synth, id);
        int changes = 0;
        FmSynthEditorControl editor = new(workspace, synth,
            () => {}, _ => changes++);
        StackPanel inspector = (StackPanel)typeof(FmSynthEditorControl)
            .GetField("_inspector", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(editor)!;
        StackPanel range = inspector.Children.OfType<StackPanel>()
            .Single(x => x.Name == "ValueRangeFields");
        TextBox Field(string label)
            => range.Children.OfType<StackPanel>().Single(x =>
                x.Children.OfType<TextBlock>().Any(t => t.Text == label))
                .Children.OfType<TextBox>().Single();
        void Enter(string label, string value)
        {
            TextBox box = Field(label);
            box.Text = value;
            box.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.Enter,
            });
        }
        FmNoiseNode Current() => synth.Graph.Nodes.OfType<FmNoiseNode>().Single();

        Assert.That(Field("Vmin").Text, Is.EqualTo("-1"));
        Assert.That(Field("Vmax").Text, Is.EqualTo("1"));
        ValueRangeLinkToggle link = range.Children.OfType<ValueRangeLinkToggle>()
            .Single();
        Assert.That(link.IsChecked, Is.True);
        link.IsChecked = false; // The existing independent-edit contract.
        uint audio = workspace.Document.AudioRevision;
        Enter("Vmin", "-0.25");
        Assert.That(Current().Minimum, Is.EqualTo(-0.25));
        Assert.That(Current().Maximum, Is.EqualTo(1));
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(audio + 1));
        Enter("Vmax", "0.75");
        Assert.That(Current().Minimum, Is.EqualTo(-0.25));
        Assert.That(Current().Maximum, Is.EqualTo(0.75));
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(audio + 2));
        Enter("Vmin", "-0.25");
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(audio + 2));
        Enter("Vmax", "NaN");
        Assert.That(Current().Maximum, Is.EqualTo(0.75));
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(audio + 2));
        Assert.That(changes, Is.EqualTo(2));
    }

    [Test]
    public void NoiseSurvivesBothSimpleAndSourceGraphCrossSongImports()
    {
        SongDocument original = new();
        ObjectId simpleId = original.AllocateObjectId();
        FmSynthDefinition simple = new(simpleId, "Noise",
            new FmSynthGraph([new FmNoiseNode(17, -0.4, 0.6)], 17));
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
        FmNoiseNode imported = simpleCopy.Graph.Nodes.OfType<FmNoiseNode>()
            .Single();
        Assert.That(imported.Id, Is.EqualTo(17));
        Assert.That(imported.Minimum, Is.EqualTo(-0.4));
        Assert.That(imported.Maximum, Is.EqualTo(0.6));
        FmSynthDefinition parentCopy = FmSynthDocumentEditor.ImportFromSong(
            target, source, [parentId]).Single();
        ObjectId nestedId = parentCopy.Graph.Nodes
            .OfType<FmSoundSourceNode>().Single().SourceId;
        FmSynthDefinition nested = (FmSynthDefinition)target.Document.Objects[nestedId];
        FmNoiseNode nestedNoise = nested.Graph.Nodes.OfType<FmNoiseNode>()
            .Single();
        Assert.That(nestedNoise.Id, Is.EqualTo(17));
        Assert.That(nestedNoise.Minimum, Is.EqualTo(-0.4));
        Assert.That(nestedNoise.Maximum, Is.EqualTo(0.6));
        Assert.That(nestedId, Is.Not.EqualTo(simpleId));
    }
}
