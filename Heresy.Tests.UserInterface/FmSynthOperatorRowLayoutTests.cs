using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Headless.NUnit;
using Avalonia.VisualTree;

using Heresy.Core.FmSynthesis;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.FmEditing;
using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class FmSynthOperatorRowLayoutTests
{
    private static T Field<T>(FmSynthGraphCanvas surface, string name)
        => (T)typeof(FmSynthGraphCanvas)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(surface)!;

    private static FmSynthGraphCanvas CreateCanvas(FmSynthDefinition synth)
        => new(synth, _ => { }, (_, _, _) => { }, (_, _, _) => { },
            (_, _, _, _) => { });

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(5)]
    [AvaloniaTest]
    public void OperatorReservesOneModeRowAndOneRowPerConnectedSourceButNoAppendRow(
        int inputCount)
    {
        DocumentWorkspace work = new();
        FmSynthDefinition synth = FmSynthDocumentEditor.CreateFmSynth(work, "Rows");
        const double left = 450, top = 155;
        int id = FmSynthDocumentEditor.AddOperatorNode(
            work, synth, FmOperatorKind.Add,
            Enumerable.Repeat(0, inputCount).ToArray(), left, top);
        FmOperatorNode op = synth.Graph.Nodes.OfType<FmOperatorNode>().Single();
        FmSynthGraphCanvas surface = CreateCanvas(synth);
        Window window = new() { Width = 1450, Height = 950, Content = surface };
        window.Show();
        try
        {
            Border border = Field<Dictionary<int, Border>>(
                surface, "_nodeControls")[id];
            Grid body = (Grid)border.Child!;
            StackPanel rows = (StackPanel)body.Children[0];
            TextBlock outputLabel = (TextBlock)body.Children[1];
            Grid title = (Grid)rows.Children[0];
            Border mode = (Border)rows.Children[1];
            Canvas graph = Field<Canvas>(surface, "_canvas");
            FmSynthLayoutRect rect = new(left, top,
                FmSynthGraphCanvas.NodeWidth,
                FmSynthConnectionPorts.GetNodeHeight(op));
            FmSynthConnectionPort[] ports =
                FmSynthConnectionPorts.GetPorts(op, rect);

            Assert.That(rows.Children.Count, Is.EqualTo(inputCount + 2),
                "header + one mode row + ONLY the connected inputs");
            Assert.That(title.Bounds.Height,
                Is.EqualTo(FmSynthConnectionPorts.FilterHeaderHeight).Within(0.1));
            Assert.That(mode.Name, Is.EqualTo("OperatorModeRow"));
            Assert.That(((TextBlock)mode.Child!).Text, Is.EqualTo("Mode: Add"));
            Assert.That(mode.Bounds.Height,
                Is.EqualTo(FmSynthConnectionPorts.OperatorModeRowHeight).Within(0.1));
            Assert.That(border.Bounds.Height, Is.EqualTo(rect.Height).Within(0.1));
            Assert.That(rect.Height,
                Is.EqualTo(2 * (FmSynthConnectionPorts.FilterBorderThickness
                    + FmSynthConnectionPorts.FilterVerticalMargin)
                    + FmSynthConnectionPorts.FilterHeaderHeight
                    + FmSynthConnectionPorts.OperatorModeRowHeight
                    + inputCount * FmSynthConnectionPorts.FilterInputRowHeight)
                    .Within(0.01));

            Point outCentre = outputLabel.TranslatePoint(
                new Point(0, outputLabel.Bounds.Height / 2), graph)!.Value;
            Assert.That(outCentre.Y, Is.EqualTo(top + 36).Within(0.6),
                "output is fixed at the midpoint of the 72 DIP BASE height");
            Assert.That(ports[0].Center.Y, Is.EqualTo(top + 36));

            for (int i = 0; i < inputCount; i++)
            {
                Border inputRow = (Border)rows.Children[i + 2];
                TextBlock inputText = (TextBlock)inputRow.Child!;
                Assert.That(inputRow.Name, Is.EqualTo("OperatorInputPortLabelRow"));
                Assert.That(inputRow.Bounds.Height,
                    Is.EqualTo(FmSynthConnectionPorts.FilterInputRowHeight).Within(0.1));
                Assert.That(inputText.Text, Is.EqualTo("#0 Constant  [OUTPUT]"),
                    "connected node's full title includes its graph-output marker");

                FmSynthConnectionPort inputPort = ports[i + 1];
                Point labelCentre = inputText.TranslatePoint(
                    new Point(0, inputText.Bounds.Height / 2), graph)!.Value;
                Assert.That(inputPort.InputIndex, Is.EqualTo(i));
                Assert.That(labelCentre.Y, Is.EqualTo(inputPort.Center.Y).Within(0.6));
                Assert.That(FmSynthConnectionPorts.HitTest(ports, inputPort.Center),
                    Is.EqualTo(inputPort));

                FmSynthLayoutRect upstream = new(50, 75, 170, 72);
                FmSynthRoutePoint source = new(upstream.Right,
                    upstream.Top + FmSynthConnectionPorts.OutputCenterOffset);
                FmSynthRoutePoint[] wire = FmSynthConnectionRouter.Route(
                    upstream, rect, [], null, source, inputPort.Center);
                Assert.That(wire[0], Is.EqualTo(source));
                Assert.That(wire[^1], Is.EqualTo(inputPort.Center),
                    "wires must follow the same label-aligned port centre");
                Assert.That(wire[^2].Y, Is.EqualTo(inputPort.Center.Y));
            }

            FmSynthConnectionPort append = ports[^1];
            Assert.That(ports.Length, Is.EqualTo(inputCount + 2));
            Assert.That(append.Kind, Is.EqualTo(FmSynthConnectionPortKind.Input));
            Assert.That(append.InputIndex, Is.EqualTo(inputCount));
            Assert.That(append.Center, Is.EqualTo(
                new FmSynthRoutePoint(rect.Left, rect.Bottom)),
                "gold append port sits ON the bottom-left node corner");
            Assert.That(FmSynthConnectionPorts.HitTest(ports,
                new FmSynthRoutePoint(rect.Left - 4, rect.Bottom + 3)),
                Is.EqualTo(append),
                "append handle can be hit from just outside the node body");
            Assert.That(append.Center.Y - ports[^2].Center.Y,
                Is.GreaterThan(2 * FmSynthConnectionPorts.HitRadius),
                "append circle must not overlap the last existing input's hit target");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    public void ConnectedOperatorRowsUseSourceTitlesIncludingOutputMarker()
    {
        DocumentWorkspace work = new();
        FmSynthDefinition synth = FmSynthDocumentEditor.CreateFmSynth(work, "Titles");
        int osc = FmSynthDocumentEditor.AddOscillatorNode(work, synth,
            FmOscillatorWaveform.Sine, 440, 245, 70);
        int constant = FmSynthDocumentEditor.AddConstantNode(
            work, synth, 0.75, 245, 180);
        int id = FmSynthDocumentEditor.AddOperatorNode(work, synth,
            FmOperatorKind.Multiply, [0, osc, constant, osc], 510, 180);
        FmSynthDocumentEditor.SetOutputNode(work, synth, osc);
        FmSynthGraphCanvas surface = CreateCanvas(synth);
        Window window = new() { Width = 1450, Height = 950, Content = surface };
        window.Show();
        try
        {
            Dictionary<int, Border> controls =
                Field<Dictionary<int, Border>>(surface, "_nodeControls");
            Grid body = (Grid)controls[id].Child!;
            StackPanel rows = (StackPanel)body.Children[0];
            Assert.That(((TextBlock)((Border)rows.Children[1]).Child!).Text,
                Is.EqualTo("Mode: Multiply"));
            string?[] labels = rows.Children.Skip(2)
                .Cast<Border>()
                .Select(row => ((TextBlock)row.Child!).Text)
                .ToArray();
            Assert.That(labels, Is.EqualTo(new[]
            {
                "#0 Constant",
                $"#{osc} Oscillator  [OUTPUT]",
                $"#{constant} Constant",
                $"#{osc} Oscillator  [OUTPUT]",
            }));
            Assert.That(rows.Children.Count, Is.EqualTo(6),
                "repeated input is still a separate, named connection row");

            // Every node kind gets the same green output text at +36 DIP;
            // taller Operator instances must not shift their output.
            foreach ((int nodeId, Border node) in controls)
            {
                Grid nodeBody = (Grid)node.Child!;
                TextBlock output = (TextBlock)nodeBody.Children[1];
                Assert.That(output.Name, Is.EqualTo("NodeOutputPortLabel"));
                Assert.That(output.Text, Is.EqualTo("Out"));
                Canvas graph = Field<Canvas>(surface, "_canvas");
                Point actual = output.TranslatePoint(
                    new Point(0, output.Bounds.Height / 2), graph)!.Value;
                FmSynthNode model = synth.Graph.Nodes.Single(n => n.Id == nodeId);
                FmSynthNodePosition position =
                    Field<Dictionary<int, FmSynthNodePosition>>(surface, "_positions")[nodeId];
                FmSynthLayoutRect rect = new(position.X, position.Y, 170,
                    FmSynthConnectionPorts.GetNodeHeight(model));
                Assert.That(actual.Y, Is.EqualTo(rect.Top + 36).Within(0.6));
                Assert.That(FmSynthConnectionPorts.GetPorts(model, rect)[0].Center.Y,
                    Is.EqualTo(rect.Top + 36));
            }
        }
        finally
        {
            window.Close();
        }
    }


    [AvaloniaTest]
    public void OutsideHalfOfBottomCornerAppendHandleCanStartConnectionDrag()
    {
        DocumentWorkspace work = new();
        FmSynthDefinition synth = FmSynthDocumentEditor.CreateFmSynth(
            work, "Append drag");
        const double x = 550, y = 220;
        int operatorId = FmSynthDocumentEditor.AddOperatorNode(
            work, synth, FmOperatorKind.Add, [0], x, y);
        List<FmSynthConnectionCandidate> connected = [];
        FmSynthGraphCanvas surface = new(synth, _ => { }, (_, _, _) => { },
            (source, target, index) =>
                connected.Add(new FmSynthConnectionCandidate(source, target, index)),
            (_, _, _, _) => { });
        Window window = new() { Width = 1450, Height = 1000, Content = surface };
        window.Show();
        try
        {
            Canvas graph = Field<Canvas>(surface, "_canvas");
            Dictionary<int, FmSynthNodePosition> positions =
                Field<Dictionary<int, FmSynthNodePosition>>(surface, "_positions");
            FmOperatorNode op = synth.Graph.Nodes.OfType<FmOperatorNode>().Single();
            double bottom = y + FmSynthConnectionPorts.GetNodeHeight(op);

            Point start = graph.TranslatePoint(
                new Point(x - 4, bottom + 3), window)!.Value;
            FmSynthNodePosition producer = positions[0];
            Point finish = graph.TranslatePoint(
                new Point(producer.X + FmSynthGraphCanvas.NodeWidth,
                    producer.Y + FmSynthConnectionPorts.OutputCenterOffset),
                window)!.Value;
            window.MouseMove(start);
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(finish);
            window.MouseUp(finish, MouseButton.Left);

            Assert.That(connected, Has.Count.EqualTo(1),
                "pressing the outside half of the gold border dot must initiate a drag");
            Assert.That(connected[0], Is.EqualTo(
                new FmSynthConnectionCandidate(0, operatorId, 1)),
                "the append port accepts a source output and retains the next input index");
        }
        finally
        {
            window.Close();
        }
    }

    [Test]
    public void AppendingAnOperandConvertsOldGoldPortIntoLabeledBlueAndGrowsHeight()
    {
        FmSynthLayoutRect beforeBounds = new(100, 200, 170,
            FmSynthConnectionPorts.GetNodeHeight(
                new FmOperatorNode(7, FmOperatorKind.Add, [2, 3])));
        FmOperatorNode before = new(7, FmOperatorKind.Add, [2, 3]);
        FmOperatorNode after = new(7, FmOperatorKind.Add, [2, 3, 4]);
        double beforeHeight = FmSynthConnectionPorts.GetNodeHeight(before);
        double afterHeight = FmSynthConnectionPorts.GetNodeHeight(after);
        Assert.That(afterHeight - beforeHeight,
            Is.EqualTo(FmSynthConnectionPorts.FilterInputRowHeight),
            "one new source creates exactly one new row, no append-only row");
        FmSynthConnectionPort oldGold =
            FmSynthConnectionPorts.GetPorts(before, beforeBounds)[^1];
        FmSynthLayoutRect afterBounds = beforeBounds with { Height = afterHeight };
        FmSynthConnectionPort[] afterPorts =
            FmSynthConnectionPorts.GetPorts(after, afterBounds);
        Assert.That(oldGold.InputIndex, Is.EqualTo(2));
        Assert.That(afterPorts[3].InputIndex, Is.EqualTo(2),
            "appended source must be connected to the old append index");
        Assert.That(afterPorts[^1].InputIndex, Is.EqualTo(3));
        Assert.That(afterPorts[^1].Center.Y, Is.EqualTo(afterBounds.Bottom));
    }
}
