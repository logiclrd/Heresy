using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Avalonia;
using Avalonia.Controls;
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
public sealed class FmSynthConnectionHitAreaTests
{
    private static T Field<T>(object owner, string fieldName)
        => (T)owner.GetType().GetField(fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;

    [AvaloniaTest]
    public void FourDipBufferAroundWireSelectsWithoutInsertingOrPanning()
    {
        DocumentWorkspace work = new();
        FmSynthDefinition synth =
            FmSynthDocumentEditor.CreateFmSynth(work, "Wide wire hit");
        int source = FmSynthDocumentEditor.AddConstantNode(
            work, synth, 1, 280, 210);
        int target = FmSynthDocumentEditor.AddOperatorNode(
            work, synth, FmOperatorKind.Add, [source], 660, 210);
        List<FmSynthConnectionCandidate> selected = [];
        List<IReadOnlyList<FmSynthRoutePoint>> changedWaypoints = [];

        FmSynthGraphCanvas canvas = new(synth, _ => { }, (_, _, _) => { },
            (_, _, _) => { },
            (_, _, _, points) => changedWaypoints.Add(points), selected.Add);
        Window window = new() { Width = 1120, Height = 800, Content = canvas };
        window.Show();
        try
        {
            window.UpdateLayout();
            Canvas graph = Field<Canvas>(canvas, "_canvas");
            FmSynthRenderedConnection wire =
                ((FmSynthRenderedConnection[])typeof(FmSynthGraphCanvas)
                    .GetMethod("GetRenderedConnections",
                        BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(canvas, null)!).Single();
            // Pick a long, unobstructed segment, then click 4.5 DIPs
            // from its centerline: outside the previous 1-DIP half-stroke
            // + 3-physical-pixel buffer at 100% display scaling, but inside
            // the new 1-DIP half-stroke + 4-DIP buffer at any scaling.
            (FmSynthRoutePoint a, FmSynthRoutePoint b) = wire.Route
                .Zip(wire.Route.Skip(1), (a, b) => (a, b))
                .OrderByDescending(pair => Math.Pow(pair.b.X - pair.a.X, 2)
                    + Math.Pow(pair.b.Y - pair.a.Y, 2)).First();
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            double length = Math.Sqrt(dx * dx + dy * dy);
            Assert.That(length, Is.GreaterThan(60.0));
            FmSynthRoutePoint offLine = new(
                (a.X + b.X) / 2.0 - dy / length * 4.5,
                (a.Y + b.Y) / 2.0 + dx / length * 4.5);
            Point click = graph.TranslatePoint(
                new Point(offLine.X, offLine.Y), window)!.Value;
            uint document = work.Document.DocumentRevision;

            window.MouseMove(click);
            window.MouseDown(click, MouseButton.Left);
            Assert.That(Field<bool>(canvas, "_isBackgroundPanning"), Is.False,
                "the padded line hit must win before background panning");
            Assert.That(selected, Has.Count.EqualTo(1));
            Assert.That(selected[0], Is.EqualTo(wire.Connection));
            window.MouseUp(click, MouseButton.Left);
            Assert.That(changedWaypoints, Is.Empty,
                "a click without dragging must not introduce a waypoint");
            Assert.That(work.Document.DocumentRevision, Is.EqualTo(document));
            Assert.That(Field<FmSynthConnectionCandidate?>(
                canvas, "_selectedConnection"), Is.EqualTo(wire.Connection));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void EightPixelCircleAroundWaypointSelectsAndDragsItsExistingNode()
    {
        DocumentWorkspace work = new();
        FmSynthDefinition synth =
            FmSynthDocumentEditor.CreateFmSynth(work, "Waypoint handle");
        int source = FmSynthDocumentEditor.AddConstantNode(
            work, synth, 1, 280, 210);
        int target = FmSynthDocumentEditor.AddOperatorNode(
            work, synth, FmOperatorKind.Add, [source], 660, 210);
        FmSynthRoutePoint waypoint = new(540, 325);
        FmSynthDocumentEditor.SetRoutingHint(work, synth, source, target, 0,
            [waypoint]);
        List<FmSynthConnectionCandidate> selected = [];
        List<FmSynthRoutePoint[]> edits = [];
        FmSynthGraphCanvas canvas = new(synth, _ => { }, (_, _, _) => { },
            (_, _, _) => { },
            (_, _, _, points) => edits.Add(points.ToArray()), selected.Add);
        Window window = new() { Width = 1120, Height = 800, Content = canvas };
        window.Show();
        try
        {
            window.UpdateLayout();
            Canvas graph = Field<Canvas>(canvas, "_canvas");
            // Distance 7.5 from waypoint center: outside the 4.5-DIP
            // visible gold disc, but inside the new eight-pixel circle.
            FmSynthRoutePoint near = new(waypoint.X + 6, waypoint.Y + 4.5);
            Point start = graph.TranslatePoint(
                new Point(near.X, near.Y), window)!.Value;
            Point end = start + new Vector(22, 11);
            uint document = work.Document.DocumentRevision;

            window.MouseMove(start);
            window.MouseDown(start, MouseButton.Left);
            Assert.That(selected, Has.Count.EqualTo(1));
            Assert.That(selected[0], Is.EqualTo(
                new FmSynthConnectionCandidate(source, target, 0)));
            Assert.That(Field<object?>(canvas, "_waypointDrag"), Is.Not.Null,
                "off-center press should pick an existing waypoint, not create one");
            window.MouseMove(end);
            window.MouseUp(end, MouseButton.Left);
            Assert.That(edits, Has.Count.EqualTo(1));
            Assert.That(edits[0], Has.Length.EqualTo(1));
            Assert.That(edits[0][0],
                Is.EqualTo(new FmSynthRoutePoint(waypoint.X + 22, waypoint.Y + 11)),
                "the waypoint follows pointer delta regardless of hit offset");
            Assert.That(work.Document.DocumentRevision, Is.EqualTo(document),
                "canvas reports the drag; the document mutation belongs to its owner");
        }
        finally { window.Close(); }
    }
}
