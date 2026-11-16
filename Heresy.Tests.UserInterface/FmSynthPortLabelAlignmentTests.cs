using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.VisualTree;

using Heresy.Core.Filters;
using Heresy.Core.FmSynthesis;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.FmEditing;
using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class FmSynthPortLabelAlignmentTests
{
    [TestCase(0, 230.0, 130.0)]
    [TestCase(1, 420.0, 180.0)]
    [TestCase(3, 520.0, 280.0)]
    [AvaloniaTest]
    public void FilterRowsShareSocketsAndOutputHasFixedBaseHeight(
        int stageCount, double nodeX, double nodeY)
    {
        DocumentWorkspace workspace = new();
        FmSynthDefinition synth =
            FmSynthDocumentEditor.CreateFmSynth(workspace, "Align");
        int filterId = FmSynthDocumentEditor.AddFilterNode(
            workspace, synth, nodeX, nodeY);
        if (stageCount > 0)
        {
            FilterDefinition definition = FilterDocumentEditor.CreateFilter(
                workspace, "Shared");
            for (int i = 1; i < stageCount; i++)
                FilterDocumentEditor.AddStage(
                    workspace, definition, FilterKind.BiquadPeaking);
            FmSynthDocumentEditor.SetFilterConfiguration(
                workspace, synth, filterId, new FilterInstance(definition));
        }

        FmFilterNode node = synth.Graph.Nodes
            .OfType<FmFilterNode>().Single();
        FmSynthGraphCanvas surface = new(synth,
            _ => { }, (_, _, _) => { }, (_, _, _) => { },
            (_, _, _, _) => { });
        Window window = new()
        {
            Width = 1450,
            Height = 1040,
            Content = surface,
        };
        window.Show();
        try
        {
            var visualNodes = (Dictionary<int, Border>)typeof(FmSynthGraphCanvas)
                .GetField("_nodeControls", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(surface)!;
            Canvas graph = (Canvas)typeof(FmSynthGraphCanvas)
                .GetField("_canvas", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(surface)!;
            Border outer = visualNodes[filterId];
            Grid body = (Grid)outer.Child!;
            StackPanel content = (StackPanel)body.Children[0];
            Grid header = (Grid)content.Children[0];
            TextBlock outputLabel = (TextBlock)body.Children[1];
            FmSynthLayoutRect rect = new(nodeX, nodeY,
                FmSynthGraphCanvas.NodeWidth,
                FmSynthConnectionPorts.GetNodeHeight(node));
            FmSynthConnectionPort[] ports =
                FmSynthConnectionPorts.GetPorts(node, rect);

            Assert.That(content.Children.Count, Is.EqualTo(node.GetPorts().Count + 1),
                "one explicit row for each semantic Filter port");
            Assert.That(outer.Bounds.Height, Is.EqualTo(rect.Height).Within(0.1),
                "the real node uses the geometric node height");
            Assert.That(header.Bounds.Height,
                Is.EqualTo(FmSynthConnectionPorts.FilterHeaderHeight).Within(0.1));

            Point actualOutput = outputLabel.TranslatePoint(
                new Point(0, outputLabel.Bounds.Height / 2), graph)!.Value;
            Assert.That(actualOutput.Y, Is.EqualTo(
                    ports[0].Center.Y + FmSynthConnectionPorts.OutputLabelTextDrop)
                .Within(0.6),
                "the Out text should sit a half-line below its fixed-height connector");

            for (int index = 0; index < node.GetPorts().Count; index++)
            {
                Border row = (Border)content.Children[index + 1];
                TextBlock label = (TextBlock)row.Child!;
                FmSynthConnectionPort port = ports[index + 1];
                Assert.That(row.Bounds.Height,
                    Is.EqualTo(FmSynthConnectionPorts.FilterInputRowHeight).Within(0.1));
                Point actualLabel = label.TranslatePoint(
                    new Point(0, label.Bounds.Height / 2), graph)!.Value;
                Assert.That(actualLabel.Y, Is.EqualTo(port.Center.Y).Within(0.6),
                    $"input #{index} socket must align with its actual rendered label");
                Assert.That(FmSynthConnectionPorts.HitTest(ports, port.Center),
                    Is.EqualTo(port),
                    $"input #{index} click target uses the same center");

                FmSynthLayoutRect upstream = new(30, 80, 170, 72);
                FmSynthRoutePoint producer = new(upstream.Right, upstream.CenterY);
                FmSynthRoutePoint[] route = FmSynthConnectionRouter.Route(
                    upstream, rect, [], null, producer, port.Center);
                Assert.That(route[0], Is.EqualTo(producer));
                Assert.That(route[^1], Is.EqualTo(port.Center),
                    $"wire to input #{index} must end at the aligned socket");
                Assert.That(route[^2].Y, Is.EqualTo(port.Center.Y),
                    "the wire must approach the correct label horizontally");
            }

            double bottomMargin = outer.Bounds.Height -
                (ports[^1].Center.Y - nodeY);
            Assert.That(bottomMargin, Is.GreaterThanOrEqualTo(9),
                "last input must not clip into the lower node edge");
        }
        finally
        {
            window.Close();
        }
    }

    [Test]
    public void OscillatorOutputRemainsAtBaseHeightWhilePitchInputGetsOwnRow()
    {
        FmOscillatorNode oscillator =
            new(5, FmOscillatorWaveform.Sine, 440);
        double height = FmSynthConnectionPorts.GetNodeHeight(oscillator);
        FmSynthLayoutRect rect = new(100, 200, 170, height);
        FmSynthConnectionPort[] ports =
            FmSynthConnectionPorts.GetPorts(oscillator, rect);
        Assert.That(height, Is.EqualTo(80),
            "the original status and new blue Pitch row must both fit");
        Assert.That(ports[0].Center.Y, Is.EqualTo(200 + 36),
            "the output remains fixed to the original 72-DIP node midpoint");
        Assert.That(ports[1].Center.Y, Is.EqualTo(
            200 + FmSynthConnectionPorts.SingleInputCenterOffset),
            "the Pitch socket must align with its own dedicated label row");
    }
}
