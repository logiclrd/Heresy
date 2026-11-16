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

            FmSynthDocumentEditor.MoveNode(work, synth, far, 230, 125);
            canvas.Refresh();
            window.UpdateLayout();
            b = Field<FmSynthGraphBounds>(canvas, "_contentBounds");
            Assert.That(b.Left, Is.EqualTo(60));
            Assert.That(b.Top, Is.EqualTo(60));
            Assert.That(b.Right, Is.EqualTo(420));
            Assert.That(b.Bottom, Is.EqualTo(217));
            Assert.That(scroll.Extent.Width, Is.LessThanOrEqualTo(
                scroll.Viewport.Width + 1),
                "horizontal scrolling hides when node bounds plus 20%-per-side overscan fit");

            int osc = FmSynthDocumentEditor.AddOscillatorNode(
                work, synth, FmOscillatorWaveform.Sine, 440, 350, 150);
            FmSynthDocumentEditor.ConnectNodes(work, synth, far, osc, 0);
            FmSynthDocumentEditor.SetRoutingHint(
                work, synth, far, osc, 0,
                [new FmSynthRoutePoint(1300, 125),
                 new FmSynthRoutePoint(1300, 600)]);
            canvas.Refresh();
            window.UpdateLayout();
            b = Field<FmSynthGraphBounds>(canvas, "_contentBounds");
            Assert.That(b.Right, Is.GreaterThanOrEqualTo(1320),
                "a routed wire beyond all node bodies still expands the extent");
            Assert.That(b.Bottom, Is.GreaterThanOrEqualTo(620),
                "the edge's lowest detour must have its own 20-DIP margin");

            FmSynthDocumentEditor.RemoveNode(work, synth, osc);
            canvas.Refresh();
            window.UpdateLayout();
            b = Field<FmSynthGraphBounds>(canvas, "_contentBounds");
            Assert.That(b.Right, Is.EqualTo(420),
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
            window.UpdateLayout();
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

    [Test]
    public void ScrollbarRangesExpandByTwentyPercentAndCameraProjectsWithoutClampingItself()
    {
        FmSynthGraphBounds bounds = new(-300, -150, 900, 750);
        FmSynthGraphScrollRange range =
            FmSynthGraphScrollRange.FromBounds(bounds, 500, 300);
        Assert.That(range.Left, Is.EqualTo(-400));
        Assert.That(range.Top, Is.EqualTo(-210));
        Assert.That(range.Right, Is.EqualTo(1000));
        Assert.That(range.Bottom, Is.EqualTo(810));
        Assert.That(range.Width, Is.EqualTo(1400));
        Assert.That(range.Height, Is.EqualTo(1020));
        Assert.That(range.MaximumOffsetX, Is.EqualTo(900));
        Assert.That(range.MaximumOffsetY, Is.EqualTo(720));

        FmSynthRoutePoint outside = new(2000, -1000);
        Assert.That(range.Project(outside),
            Is.EqualTo(new FmSynthRoutePoint(900, 0)),
            "scrollbar thumbs clamp independently while camera remains unbounded");
        Assert.That(outside, Is.EqualTo(new FmSynthRoutePoint(2000, -1000)),
            "projecting a camera onto scrollbars must not alter the camera");

        Assert.That(range.CameraAt(new FmSynthRoutePoint(840, 40)),
            Is.EqualTo(new FmSynthRoutePoint(440, -170)),
            "editing a scrollbar adopts the in-range camera coordinate");
        Assert.That(range.CameraAt(new FmSynthRoutePoint(9999, -5)),
            Is.EqualTo(new FmSynthRoutePoint(500, -210)),
            "the scrollbar cannot report an unrepresentable camera position");
    }

    [AvaloniaTest]
    public void BackgroundPanMayPassTheScrollbarStopAndThumbEditSnapsBackIntoRange()
    {
        DocumentWorkspace work = new();
        FmSynthDefinition synth =
            FmSynthDocumentEditor.CreateFmSynth(work, "Overscroll");
        int remote = FmSynthDocumentEditor.AddConstantNode(
            work, synth, 0.5, 1400, 80);
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
            window.UpdateLayout();
            FmSynthGraphScrollRange range =
                Field<FmSynthGraphScrollRange>(canvas, "_scrollRange");
            FmSynthRoutePoint initial =
                Field<FmSynthRoutePoint>(canvas, "_camera");
            Assert.That(range.MaximumOffsetX, Is.GreaterThan(500));
            uint revision = work.Document.DocumentRevision;

            for (int i = 0; i < 5; i++)
            {
                // Pick empty background well below the node bodies.
                Point start = scroll.TranslatePoint(
                    new Point(500, 320), window)!.Value;
                Point end = scroll.TranslatePoint(
                    new Point(130, 320), window)!.Value;
                window.MouseMove(start);
                window.MouseDown(start, MouseButton.Left);
                window.MouseMove(end);
                window.MouseUp(end, MouseButton.Left);
            }

            FmSynthRoutePoint camera =
                Field<FmSynthRoutePoint>(canvas, "_camera");
            range = Field<FmSynthGraphScrollRange>(canvas, "_scrollRange");
            Assert.That(camera.X, Is.GreaterThan(initial.X + 1700),
                "background dragging continues beyond the thumb's maximum");
            Assert.That(camera.X, Is.GreaterThan(
                range.Left + range.MaximumOffsetX));
            Assert.That(scroll.Offset.X,
                Is.EqualTo(range.MaximumOffsetX).Within(1),
                "the thumb clamps at its maximum while the graph keeps moving");
            Assert.That(work.Document.DocumentRevision, Is.EqualTo(revision));
            Assert.That(synth.NodePositions.Single(p => p.NodeId == remote).X,
                Is.EqualTo(1400));

            double editedThumb = range.MaximumOffsetX - 50;
            scroll.Offset = new Vector(editedThumb, scroll.Offset.Y);
            FmSynthRoutePoint snapped =
                Field<FmSynthRoutePoint>(canvas, "_camera");
            Assert.That(snapped.X, Is.EqualTo(range.Left + editedThumb).Within(1),
                "manual scrollbar editing discards unrepresentable overscroll");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    public void NegativeNodeCoordinatesChangeBothRangeEndsButNeverMoveTheCamera()
    {
        DocumentWorkspace work = new();
        FmSynthDefinition synth =
            FmSynthDocumentEditor.CreateFmSynth(work, "Negative coordinates");
        int nodeId = FmSynthDocumentEditor.AddConstantNode(
            work, synth, 0.5, 700, 500);
        FmSynthGraphCanvas canvas = Canvas(synth);
        ScrollViewer scroll = new()
        {
            Content = canvas,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        canvas.AttachScrollViewer(scroll);
        Window window = new() { Width = 580, Height = 400, Content = scroll };
        window.Show();
        try
        {
            window.UpdateLayout();
            FmSynthRoutePoint cameraBefore =
                Field<FmSynthRoutePoint>(canvas, "_camera");
            FmSynthGraphScrollRange oldRange =
                Field<FmSynthGraphScrollRange>(canvas, "_scrollRange");

            FmSynthDocumentEditor.MoveNode(work, synth, nodeId, -260, -340);
            canvas.Refresh();
            window.UpdateLayout();

            FmSynthGraphBounds bounds =
                Field<FmSynthGraphBounds>(canvas, "_contentBounds");
            FmSynthGraphScrollRange current =
                Field<FmSynthGraphScrollRange>(canvas, "_scrollRange");
            FmSynthRoutePoint cameraAfter =
                Field<FmSynthRoutePoint>(canvas, "_camera");
            Assert.That(bounds.Left, Is.EqualTo(-280));
            Assert.That(bounds.Top, Is.EqualTo(-360));
            Assert.That(bounds.Right, Is.EqualTo(270));
            Assert.That(bounds.Bottom, Is.EqualTo(172));
            Assert.That(current.Left, Is.LessThan(oldRange.Left));
            Assert.That(current.Top, Is.LessThan(oldRange.Top));
            Assert.That(current.Right, Is.LessThan(oldRange.Right));
            Assert.That(current.Bottom, Is.LessThan(oldRange.Bottom));
            Assert.That(cameraAfter, Is.EqualTo(cameraBefore),
                "recalculating a scrollbar range must NOT shift graph position");
            FmSynthRoutePoint projection = current.Project(cameraAfter);
            Assert.That(scroll.Offset.X, Is.EqualTo(projection.X).Within(1));
            Assert.That(scroll.Offset.Y, Is.EqualTo(projection.Y).Within(1));
            Assert.That(synth.NodePositions.Single(p => p.NodeId == nodeId).X,
                Is.EqualTo(-260), "negative coordinates must persist in model");
        }
        finally
        {
            window.Close();
        }
    }
}
