using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.VisualTree;

using Heresy.Core.FmSynthesis;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.FmEditing;
using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class FmSynthGraphScrollExtentTests
{
    private static T Field<T>(FmSynthGraphCanvas canvas, string field)
        => (T)typeof(FmSynthGraphCanvas)
            .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(canvas)!;

    private static FmSynthGraphCanvas Canvas(FmSynthDefinition synth)
        => new(synth, _ => { }, (_, _, _) => { }, (_, _, _) => { },
            (_, _, _, _) => { });

    [Test]
    public void FourIndependentMarginsCoverNodesAndAllVerticesOfRenderedEdges()
    {
        FmSynthGraphBounds actual = FmSynthGraphExtentGeometry.Calculate(
            [
                new FmSynthLayoutRect(100, 200, 170, 72),
                new FmSynthLayoutRect(380, 80, 170, 100),
            ],
            [
                new FmSynthRoutePoint[]
                {
                    new(270, 236), new(950, 236), new(950, -75),
                    new(-130, -75), new(-130, 140), new(380, 140),
                },
            ]);
        Assert.That(actual.Left, Is.EqualTo(-150));
        Assert.That(actual.Top, Is.EqualTo(-95));
        Assert.That(actual.Right, Is.EqualTo(970));
        Assert.That(actual.Bottom, Is.EqualTo(292));
        Assert.That(actual.Width, Is.EqualTo(1120));
        Assert.That(actual.Height, Is.EqualTo(387));
        Assert.That(actual.OriginX, Is.EqualTo(150));
        Assert.That(actual.OriginY, Is.EqualTo(95));
    }

    [Test]
    public void EmptyAndUnconnectedGraphsGetExactlyTwentyDipMargins()
    {
        FmSynthGraphBounds empty = FmSynthGraphExtentGeometry.Calculate(
            Array.Empty<FmSynthLayoutRect>(),
            Array.Empty<IReadOnlyList<FmSynthRoutePoint>>());
        Assert.That(empty, Is.EqualTo(
            new FmSynthGraphBounds(0, 0, 40, 40)));
        FmSynthGraphBounds one = FmSynthGraphExtentGeometry.Calculate(
            [new FmSynthLayoutRect(350, 450, 170, 80)],
            Array.Empty<IReadOnlyList<FmSynthRoutePoint>>());
        Assert.That(one, Is.EqualTo(
            new FmSynthGraphBounds(330, 430, 540, 550)));
    }

    [AvaloniaTest]
    public void ExtentsFollowMovesAdditionsDeletionAndRemoteWireWaypoints()
    {
        DocumentWorkspace work = new();
        FmSynthDefinition synth =
            FmSynthDocumentEditor.CreateFmSynth(work, "Scroll bounds");
        int far = FmSynthDocumentEditor.AddConstantNode(
            work, synth, 0.5, 1200, 1000);
        FmSynthGraphCanvas canvas = Canvas(synth);
        ScrollViewer scroll = new()
        {
            Content = canvas,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        canvas.AttachScrollViewer(scroll);
        Window window = new() { Width = 700, Height = 500, Content = scroll };
        window.Show();
        try
        {
            FmSynthGraphBounds b = Field<FmSynthGraphBounds>(canvas, "_contentBounds");
            Assert.That(b.Right, Is.EqualTo(1390));
            Assert.That(b.Bottom, Is.EqualTo(1092));
            Assert.That(b.Left, Is.EqualTo(60));
            Assert.That(b.Top, Is.EqualTo(60));
            Assert.That(scroll.Extent.Width, Is.GreaterThan(scroll.Viewport.Width),
                "horizontal scrollbar must be available for remote node");
            Assert.That(scroll.Extent.Height, Is.GreaterThan(scroll.Viewport.Height),
                "vertical scrollbar must be available for remote node");

            FmSynthDocumentEditor.MoveNode(work, synth, far, 300, 125);
            canvas.Refresh();
            b = Field<FmSynthGraphBounds>(canvas, "_contentBounds");
            Assert.That(b.Left, Is.EqualTo(60));
            Assert.That(b.Top, Is.EqualTo(60));
            Assert.That(b.Right, Is.EqualTo(490));
            Assert.That(b.Bottom, Is.EqualTo(217));
            Assert.That(scroll.Extent.Width, Is.LessThanOrEqualTo(
                scroll.Viewport.Width + 1),
                "horizontal scrolling hides independently when content fits");

            int osc = FmSynthDocumentEditor.AddOscillatorNode(
                work, synth, FmOscillatorWaveform.Sine, 440, 350, 150);
            FmSynthDocumentEditor.ConnectNodes(work, synth, far, osc, 0);
            FmSynthDocumentEditor.SetRoutingHint(
                work, synth, far, osc, 0,
                [new FmSynthRoutePoint(1300, 125),
                 new FmSynthRoutePoint(1300, 600)]);
            canvas.Refresh();
            b = Field<FmSynthGraphBounds>(canvas, "_contentBounds");
            Assert.That(b.Right, Is.GreaterThanOrEqualTo(1320),
                "a routed wire beyond all node bodies still expands the extent");
            Assert.That(b.Bottom, Is.GreaterThanOrEqualTo(620),
                "the edge's lowest detour must have its own 20-DIP margin");

            FmSynthDocumentEditor.RemoveNode(work, synth, osc);
            canvas.Refresh();
            b = Field<FmSynthGraphBounds>(canvas, "_contentBounds");
            Assert.That(b.Right, Is.EqualTo(490),
                "removing the routed edge must shrink extents on refresh");
            Assert.That(b.Bottom, Is.EqualTo(217));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    public void VerticalOverflowDoesNotRequireHorizontalScrollAndBackgroundDragPans()
    {
        DocumentWorkspace work = new();
        FmSynthDefinition synth =
            FmSynthDocumentEditor.CreateFmSynth(work, "Pan");
        int remote = FmSynthDocumentEditor.AddConstantNode(
            work, synth, 0.5, 200, 1350);
        FmSynthGraphCanvas canvas = Canvas(synth);
        ScrollViewer scroll = new()
        {
            Content = canvas,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        canvas.AttachScrollViewer(scroll);
        Window window = new() { Width = 720, Height = 550, Content = scroll };
        window.Show();
        try
        {
            Assert.That(scroll.Extent.Width, Is.LessThanOrEqualTo(
                scroll.Viewport.Width + 1),
                "only the vertical scrollbar should be needed");
            Assert.That(scroll.Extent.Height, Is.GreaterThan(
                scroll.Viewport.Height));
            scroll.Offset = new Vector(0, 180);

            // The blank background, not a node body or wire, starts panning.
            Point start = scroll.TranslatePoint(new Point(500, 270), window)!.Value;
            Point finish = scroll.TranslatePoint(new Point(500, 160), window)!.Value;
            uint revision = work.Document.DocumentRevision;
            window.MouseMove(start);
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(finish);
            window.MouseUp(finish, MouseButton.Left);

            Assert.That(scroll.Offset.Y, Is.GreaterThan(260),
                "dragging the background upward must pan the visible graph downward");
            Assert.That(scroll.Offset.X, Is.EqualTo(0).Within(0.5),
                "panning must not manufacture horizontal scrolling");
            Assert.That(work.Document.DocumentRevision, Is.EqualTo(revision),
                "panning is purely presentational and must not edit the song");
            Assert.That(synth.NodePositions.Single(p => p.NodeId == remote).Y,
                Is.EqualTo(1350), "panning must not mutate stored positions");

            FmSynthDocumentEditor.MoveNode(work, synth, remote, 1400, 125);
            canvas.Refresh();
            Assert.That(scroll.Extent.Width, Is.GreaterThan(scroll.Viewport.Width));
            Assert.That(scroll.Extent.Height,
                Is.LessThanOrEqualTo(scroll.Viewport.Height + 1),
                "the now-fitting vertical axis must hide independently");
        }
        finally
        {
            window.Close();
        }
    }
}
