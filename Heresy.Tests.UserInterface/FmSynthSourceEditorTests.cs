using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using AwesomeAssertions;

using Heresy.Core.Assets;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
using Heresy.Core.Persistence;
using Heresy.Core.Samples;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.FmEditing;
using Heresy.UserInterface.Views;
using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class FmSynthSourceEditorTests
{
    [Test]
    public void SourceNodeSupportsWiredModulationDeleteAndPersistedConstant()
    {
        DocumentWorkspace workspace = new();
        FmSynthDefinition synth = FmSynthDocumentEditor.CreateFmSynth(workspace, "Sources");
        int sourceId = FmSynthDocumentEditor.AddSoundSourceNode(
            workspace, synth, 300, 100);
        FmSoundSourceNode source = synth.Graph.Nodes.OfType<FmSoundSourceNode>().Single();
        source.SourceId.Should().Be(ObjectId.None);
        source.Constant.Should().BeFalse();
        source.MultiplierNodeId.Should().BeNull();
        FmSynthConnectionPorts.GetPorts(source,
            new FmSynthLayoutRect(0, 0, 170, 72)).Should().HaveCount(2);

        int modulatorId = FmSynthDocumentEditor.AddConstantNode(
            workspace, synth, 2, 100, 100);
        FmSynthDocumentEditor.ConnectNodes(workspace, synth, modulatorId, sourceId, 0);
        source = synth.Graph.Nodes.OfType<FmSoundSourceNode>().Single();
        source.MultiplierNodeId.Should().Be(modulatorId);
        FmSynthDocumentEditor.UpdateNode(workspace, synth,
            new FmSoundSourceNode(sourceId, ObjectId.None, source.FrequencyHz,
                source.MultiplierNodeId, exponentialMultiplier: true,
                constant: true));
        FmSynthDocumentEditor.SetOutputNode(workspace, synth, sourceId);

        FmSynthDocumentEditor.RemoveNode(workspace, synth, modulatorId);
        source = synth.Graph.Nodes.OfType<FmSoundSourceNode>().Single();
        source.MultiplierNodeId.Should().BeNull();
        source.ExponentialMultiplier.Should().BeTrue();
        source.Constant.Should().BeTrue();
        FmSynthDocumentEditor.SetOutputNode(workspace, synth, 0);
        FmSynthDocumentEditor.RemoveNode(workspace, synth, sourceId);
        synth.Graph.Nodes.OfType<FmSoundSourceNode>().Should().BeEmpty();
    }

    [AvaloniaTest]
    public void SourceInspectorPlacesConstantDirectlyUnderFrequencyAndSelectsSample()
    {
        DocumentWorkspace workspace = new();
        ObjectId sampleId = workspace.Document.AllocateObjectId();
        workspace.Document.Add(new SampleDefinition(sampleId, "Kick",
            new ExternalAssetReference("kick.wav")));
        FmSynthDefinition synth = FmSynthDocumentEditor.CreateFmSynth(
            workspace, "Player");
        int sourceId = FmSynthDocumentEditor.AddSoundSourceNode(
            workspace, synth, 230, 90);
        FmSynthDocumentEditor.SetOutputNode(workspace, synth, sourceId);
        FmSynthEditorControl editor = new(workspace, synth, () => {}, _ => {});
        StackPanel inspector = (StackPanel)typeof(FmSynthEditorControl)
            .GetField("_inspector", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(editor)!;
        int index = inspector.Children
            .Select((child, i) => (child, i))
            .Single(x => x.child is TextBlock label
                && label.Text == "Frequency Hz").i;
        inspector.Children[index + 1].Should().BeOfType<TextBox>();
        CheckBox constant = inspector.Children[index + 2]
            .Should().BeOfType<CheckBox>().Which;
        constant.Content.Should().Be("Constant");
        constant.IsChecked.Should().BeFalse();

        uint revision = workspace.Document.AudioRevision;
        constant.IsChecked = true;
        synth.Graph.Nodes.OfType<FmSoundSourceNode>()
            .Single().Constant.Should().BeTrue();
        workspace.Document.AudioRevision.Should().Be(revision + 1);
        constant.IsChecked = true;
        workspace.Document.AudioRevision.Should().Be(revision + 1);

        ComboBox sourceSelection = inspector.Children.OfType<ComboBox>()
            .First(c => c.Items.OfType<object>().Any(x =>
                x.ToString()!.Contains("Kick")));
        object choice = sourceSelection.Items.OfType<object>()
            .Single(x => x.ToString()!.Contains("Kick"));
        sourceSelection.SelectedItem = choice;
        synth.Graph.Nodes.OfType<FmSoundSourceNode>()
            .Single().SourceId.Should().Be(sampleId);
        workspace.Document.AudioRevision.Should().Be(revision + 2);

        string json = SongDocumentJson.Serialize(workspace.Document, "/tmp/heresy-source-editor.hm.json",
            Heresy.Core.Persistence.JsonAssetPathMode.Absolute);
        json.Should().Contain("\\"constant\\":true");
        json.Should().Contain("\\"sourceId\\":");
    }
}
