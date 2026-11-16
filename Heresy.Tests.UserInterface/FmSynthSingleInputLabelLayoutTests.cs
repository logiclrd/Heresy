using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Media;
using Avalonia.VisualTree;

using Heresy.Core.FmSynthesis;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.FmEditing;
using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class FmSynthSingleInputLabelLayoutTests
{
    [TestCase("Oscillator", "Pitch", "Sine")]
    [TestCase("Source", "Pitch", "Source <")]
    [TestCase("Multi", "In", "voices")]
    [AvaloniaTest]
    public void StatusIsPreservedAndSeparateBlueInputRowMatchesPortAndWire(
        string kind, string inputName, string statusSubstring)
    {
        DocumentWorkspace workspace = new();
        FmSynthDefinition synth =
            FmSynthDocumentEditor.CreateFmSynth(workspace, "Labelled input");
        const double x = 480, y = 195;
        int id = kind switch
        {
            "Oscillator" => FmSynthDocumentEditor.AddOscillatorNode(
                workspace, synth, FmOscillatorWaveform.Sine, 440, x, y),
            "Source" => FmSynthDocumentEditor.AddSoundSourceNode(
                workspace, synth, x, y),
            "Multi" => FmSynthDocumentEditor.AddMultiNode(
                workspace, synth, x, y),
            _ => throw new ArgumentException("Unknown test node kind", nameof(kind)),
        };
        FmSynthNode node = synth.Graph.Nodes.Single(candidate => candidate.Id == id);
        FmSynthGraphCanvas surface = new(synth,
            _ => { }, (_, _, _) => { }, (_, _, _) => { },
            (_, _, _, _) => { });
        Window window = new()
        {
            Width = 1450,
            Height = 1000,
            Content = surface,
        };
        window.Show();
        try
        {
            var nodeControls =
                (Dictionary<int, Border>)typeof(FmSynthGraphCanvas)
                    .GetField("_nodeControls", BindingFlags.Instance
                        | BindingFlags.NonPublic)!
                    .GetValue(surface)!;
            Canvas graph = (Canvas)typeof(FmSynthGraphCanvas)
                .GetField("_canvas", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(surface)!;
            Border outer = nodeControls[id];
            Grid body = (Grid)outer.Child!;
            StackPanel rows = (StackPanel)body.Children[0];
            TextBlock outputLabel = (TextBlock)body.Children[1];
            FmSynthLayoutRect rect = new(x, y, FmSynthGraphCanvas.NodeWidth,
                FmSynthConnectionPorts.GetNodeHeight(node));
            FmSynthConnectionPort[] ports =
                FmSynthConnectionPorts.GetPorts(node, rect);

            Assert.That(rect.Height, Is.EqualTo(80),
                "header, original status and named input must each fit");
            Assert.That(outer.Bounds.Height, Is.EqualTo(rect.Height).Within(0.1));
            Assert.That(rows.Children.Count, Is.EqualTo(3));
            Assert.That(rows.Children[0], Is.InstanceOf<Grid>());
            Grid title = (Grid)rows.Children[0];
            Assert.That(title.Bounds.Height,
                Is.EqualTo(FmSynthConnectionPorts.FilterHeaderHeight).Within(0.1));

            Border statusRow = (Border)rows.Children[1];
            TextBlock status = (TextBlock)statusRow.Child!;
            Assert.That(statusRow.Name, Is.EqualTo("SingleInputStatusRow"));
            Assert.That(statusRow.Bounds.Height,
                Is.EqualTo(FmSynthConnectionPorts.StatusRowHeight).Within(0.1));
            Assert.That(status.Text, Does.Contain(statusSubstring),
                "retain the old waveform, Source or Multi status information");

            Border inputRow = (Border)rows.Children[2];
            TextBlock label = (TextBlock)inputRow.Child!;
            Assert.That(inputRow.Name, Is.EqualTo("SingleInputPortLabelRow"));
            Assert.That(inputRow.Bounds.Height,
                Is.EqualTo(FmSynthConnectionPorts.FilterInputRowHeight).Within(0.1));
            Assert.That(label.Text, Is.EqualTo(inputName));
            Assert.That(label.Foreground, Is.EqualTo(Brushes.LightSkyBlue),
                "single inputs use the same blue typography as Filter/Operator");
            Assert.That(ports.Length, Is.EqualTo(2));
            Assert.That(ports[1].InputIndex, Is.EqualTo(0));
            Assert.That(ports[1].Kind, Is.EqualTo(FmSynthConnectionPortKind.Input));

            Point inputCenter = label.TranslatePoint(
                new Point(0, label.Bounds.Height / 2), graph)!.Value;
            Assert.That(inputCenter.Y, Is.EqualTo(ports[1].Center.Y).Within(0.6),
                "the blue socket must align with the named input, not the status");
            Point statusCenter = status.TranslatePoint(
                new Point(0, status.Bounds.Height / 2), graph)!.Value;
            Assert.That(inputCenter.Y - statusCenter.Y,
                Is.EqualTo(FmSynthConnectionPorts.StatusRowHeight).Within(0.6),
                "status and input occupy separate consecutive rows");
            Assert.That(FmSynthConnectionPorts.HitTest(ports, ports[1].Center),
                Is.EqualTo(ports[1]));

            FmSynthLayoutRect sourceRect = new(50, 70, 170, 72);
            FmSynthRoutePoint sourceOutput =
                new(sourceRect.Right, sourceRect.Top
                    + FmSynthConnectionPorts.OutputCenterOffset);
            FmSynthRoutePoint[] route = FmSynthConnectionRouter.Route(
                sourceRect, rect, [], null, sourceOutput, ports[1].Center);
            Assert.That(route[^1], Is.EqualTo(ports[1].Center));
            Assert.That(route[^2].Y, Is.EqualTo(ports[1].Center.Y));

            Assert.That(ports[0].Center.Y, Is.EqualTo(y + 36),
                "the green socket stays fixed at the base node midpoint");
            Assert.That(outputLabel.Text, Is.EqualTo("Out"));
            Assert.That(FmSynthConnectionPorts.OutputLabelTextDrop, Is.EqualTo(3),
                "Out text is halfway between its original and 6-DIP positions");
            Point outputTextCenter = outputLabel.TranslatePoint(
                new Point(0, outputLabel.Bounds.Height / 2), graph)!.Value;
            Assert.That(outputTextCenter.Y, Is.EqualTo(ports[0].Center.Y
                    + FmSynthConnectionPorts.OutputLabelTextDrop).Within(0.6),
                "only the word Out drops half a line, not its connector");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    public void NodesWithoutInputKeepTheirExistingStatusWithoutAnExtraBlueRow()
    {
        DocumentWorkspace workspace = new();
        FmSynthDefinition synth =
            FmSynthDocumentEditor.CreateFmSynth(workspace, "No input");
        FmConstantNode node = synth.Graph.Nodes.OfType<FmConstantNode>().Single();
        FmSynthGraphCanvas surface = new(synth,
            _ => { }, (_, _, _) => { }, (_, _, _) => { },
            (_, _, _, _) => { });
        Window window = new() { Width = 1450, Height = 1000, Content = surface };
        window.Show();
        try
        {
            var controls = (Dictionary<int, Border>)typeof(FmSynthGraphCanvas)
                .GetField("_nodeControls", BindingFlags.Instance
                    | BindingFlags.NonPublic)!.GetValue(surface)!;
            Grid body = (Grid)controls[node.Id].Child!;
            StackPanel content = (StackPanel)body.Children[0];
            Assert.That(content.Children.Count, Is.EqualTo(2));
            Assert.That(content.Children[1], Is.TypeOf<TextBlock>(),
                "existing status text is preserved");
            Assert.That(FmSynthConnectionPorts.GetNodeHeight(node), Is.EqualTo(72));
            Assert.That(FmSynthConnectionPorts.GetPorts(node,
                new FmSynthLayoutRect(80, 80, 170, 72)).Length, Is.EqualTo(1),
                "no synthetic input may be introduced for Constant");
        }
        finally
        {
            window.Close();
        }
    }
}
