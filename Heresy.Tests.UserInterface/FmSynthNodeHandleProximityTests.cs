using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;

using Heresy.Core.FmSynthesis;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.FmEditing;
using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class FmSynthNodeHandleProximityTests
{
    private static T Field<T>(FmSynthGraphCanvas canvas, string name)
        => (T)typeof(FmSynthGraphCanvas)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(canvas)!;

    [Test]
    public void ZoneMeasuresPhysicalPixelsInsideOutsideAndAroundTrueCorners()
    {
        FmSynthLayoutRect rect = new(100, 200, 170, 72);
        static FmSynthRoutePoint Point(double x, double y) => new(x, y);
        Assert.That(FmSynthNodeHandleGeometry.ProximityPixels, Is.EqualTo(8));

        Assert.That(FmSynthNodeHandleGeometry.IsNearBoundary(
            rect, Point(92, 236), 1.0), Is.True);
        Assert.That(FmSynthNodeHandleGeometry.IsNearBoundary(
            rect, Point(91.99, 236), 1.0), Is.False);
        Assert.That(FmSynthNodeHandleGeometry.IsNearBoundary(
            rect, Point(108, 236), 1.0), Is.True,
            "8px inside the body also exposes its ports");
        Assert.That(FmSynthNodeHandleGeometry.IsNearBoundary(
            rect, Point(109, 236), 1.0), Is.False);
        Assert.That(FmSynthNodeHandleGeometry.IsNearBoundary(
            rect, Point(94, 195), 1.0), Is.True,
            "6-by-5 outside the top-left corner is under 8px");
        Assert.That(FmSynthNodeHandleGeometry.IsNearBoundary(
            rect, Point(94, 194), 1.0), Is.False,
            "the diagonal distance is not the minimum infinite-line distance");
        Assert.That(FmSynthNodeHandleGeometry.IsNearBoundary(
            rect, Point(185, 235), 1.0), Is.False,
            "the center is not a port proximity hit target");

        // The specification is physical pixels; scale 2 halves the DIP zone.
        Assert.That(FmSynthNodeHandleGeometry.IsNearBoundary(
            rect, Point(96, 240), 2.0), Is.True);
        Assert.That(FmSynthNodeHandleGeometry.IsNearBoundary(
            rect, Point(95.9, 240), 2.0), Is.False);
        Assert.That(FmSynthNodeHandleGeometry.IsNearBoundary(
            rect, Point(90, 240), 0.75), Is.True);
        Assert.That(FmSynthNodeHandleGeometry.IsNearBoundary(
            rect, Point(89, 240), 0.75), Is.False);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FmSynthNodeHandleGeometry.IsNearBoundary(rect, Point(100, 200), 0));
    }

    [Test]
    public void NearbyButNotAdjacentNodesDoNotGetFalseHoverAtCorners()
    {
        FmSynthLayoutRect left = new(100, 100, 170, 72);
        FmSynthLayoutRect right = new(290, 100, 170, 72);
        FmSynthRoutePoint between = new(280, 136);
        Assert.That(FmSynthNodeHandleGeometry.DistanceToBoundary(
            left, between), Is.EqualTo(10));
        Assert.That(FmSynthNodeHandleGeometry.DistanceToBoundary(
            right, between), Is.EqualTo(10));
        Assert.That(FmSynthNodeHandleGeometry.IsNearBoundary(
            left, between, 1), Is.False);
        Assert.That(FmSynthNodeHandleGeometry.IsNearBoundary(
            right, between, 1), Is.False);
        FmSynthRoutePoint nearLeft = new(277, 136);
        Assert.That(FmSynthNodeHandleGeometry.IsNearBoundary(
            left, nearLeft, 1), Is.True);
        Assert.That(FmSynthNodeHandleGeometry.IsNearBoundary(
            right, nearLeft, 1), Is.False);
    }

    [AvaloniaTest]
    public void OutsideAppendHandleRevealsAndDragsWithoutMovingNode()
    {
        DocumentWorkspace workspace = new();
        FmSynthDefinition synth =
            FmSynthDocumentEditor.CreateFmSynth(workspace, "Proximity");
        const double x = 550, y = 220;
        int operatorId = FmSynthDocumentEditor.AddOperatorNode(
            workspace, synth, FmOperatorKind.Add, [0], x, y);
        List<FmSynthConnectionCandidate> connections = [];
        List<int> moved = [];
        FmSynthGraphCanvas canvas = new(synth, _ => { },
            (id, _, _) => moved.Add(id),
            (source, target, index) =>
                connections.Add(new FmSynthConnectionCandidate(source, target, index)),
            (_, _, _, _) => { });
        ScrollViewer scroll = new()
        {
            Content = canvas,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        canvas.AttachScrollViewer(scroll);
        Window window = new() { Width = 1400, Height = 900, Content = scroll };
        window.Show();
        try
        {
            window.UpdateLayout();
            Canvas graph = Field<Canvas>(canvas, "_canvas");
            FmOperatorNode operation = synth.Graph.Nodes
                .OfType<FmOperatorNode>().Single();
            double bottom = y + FmSynthConnectionPorts.GetNodeHeight(operation);
            Point outsideCorner = graph.TranslatePoint(
                new Point(x - 6, bottom + 5), window)!.Value;
            Point away = graph.TranslatePoint(
                new Point(x - 14, bottom + 14), window)!.Value;
            Point output = graph.TranslatePoint(
                new Point(80 + FmSynthGraphCanvas.NodeWidth,
                    80 + FmSynthConnectionPorts.OutputCenterOffset),
                window)!.Value;

            window.MouseMove(outsideCorner);
            Assert.That(Field<int?>(canvas, "_hoverNodeId"), Is.EqualTo(operatorId),
                "the diagonal outside corner should reveal the operator handles");
            window.MouseMove(away);
            Assert.That(Field<int?>(canvas, "_hoverNodeId"), Is.Not.EqualTo(operatorId),
                "moving beyond the 8px boundary clears the handles");

            window.MouseMove(outsideCorner);
            window.MouseDown(outsideCorner, MouseButton.Left);
            Assert.That(Field<FmSynthConnectionPort?>(canvas, "_dragPort"),
                Is.Not.Null, "an outside-corner handle begins a port drag");
            window.MouseMove(output);
            window.MouseUp(output, MouseButton.Left);
            Assert.That(connections, Has.Count.EqualTo(1));
            Assert.That(connections[0], Is.EqualTo(
                new FmSynthConnectionCandidate(0, operatorId, 1)));
            Assert.That(moved, Is.Empty,
                "outside the node may initiate a port drag, never a body move");
            Assert.That(workspace.Document.DocumentRevision,
                Is.GreaterThan(0), "the only document mutations are the explicit additions");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    public void ScrollAndOutsideHandleHoverPreserveSelectionAndNodePositions()
    {
        DocumentWorkspace workspace = new();
        FmSynthDefinition synth =
            FmSynthDocumentEditor.CreateFmSynth(workspace, "Scroll handles");
        int oscillator = FmSynthDocumentEditor.AddOscillatorNode(
            workspace, synth, FmOscillatorWaveform.Sine, 440, 620, 100);
        List<int> moved = [];
        FmSynthGraphCanvas canvas = new(synth, _ => { },
            (id, _, _) => moved.Add(id), (_, _, _) => { },
            (_, _, _, _) => { });
        ScrollViewer scroll = new()
        {
            Content = canvas,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        canvas.AttachScrollViewer(scroll);
        Window window = new() { Width = 530, Height = 460, Content = scroll };
        window.Show();
        try
        {
            window.UpdateLayout();
            scroll.Offset = new Vector(350, scroll.Offset.Y);
            window.UpdateLayout();
            Canvas graph = Field<Canvas>(canvas, "_canvas");
            Point outsideOutput = graph.TranslatePoint(
                new Point(620 + FmSynthGraphCanvas.NodeWidth + 7,
                    100 + FmSynthConnectionPorts.OutputCenterOffset),
                window)!.Value;
            window.MouseMove(outsideOutput);
            Assert.That(Field<int?>(canvas, "_hoverNodeId"), Is.EqualTo(oscillator));

            FmSynthRoutePoint beforeCamera = Field<FmSynthRoutePoint>(canvas, "_camera");
            FmSynthNodePosition beforePosition =
                synth.NodePositions.Single(p => p.NodeId == oscillator);
            Point finish = outsideOutput + new Vector(0, 15);
            window.MouseDown(outsideOutput, MouseButton.Left);
            Assert.That(Field<FmSynthConnectionPort?>(canvas, "_dragPort")?.NodeId,
                Is.EqualTo(oscillator), "outside output must capture a port drag");
            window.MouseMove(finish);
            window.MouseUp(finish, MouseButton.Left);
            Assert.That(moved, Is.Empty);
            Assert.That(synth.NodePositions.Single(p => p.NodeId == oscillator),
                Is.EqualTo(beforePosition));
            Assert.That(Field<FmSynthRoutePoint>(canvas, "_camera"),
                Is.EqualTo(beforeCamera),
                "wiring handles should not pan a scrolled graph");
        }
        finally
        {
            window.Close();
        }
    }
}
