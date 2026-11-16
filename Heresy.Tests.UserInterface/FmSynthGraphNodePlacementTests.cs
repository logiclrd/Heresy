using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;

using Heresy.Core.FmSynthesis;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.FmEditing;
using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class FmSynthGraphNodePlacementTests
{
    private static T Field<T>(FmSynthGraphCanvas canvas, string name)
        => (T)typeof(FmSynthGraphCanvas)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(canvas)!;

    private static FmSynthGraphCanvas Canvas(FmSynthDefinition synth)
        => new(synth, _ => { }, (_, _, _) => { }, (_, _, _) => { },
            (_, _, _, _) => { });

    [Test]
    public void PlacementAvoidsNodeBodiesAndRenderedWireSegmentsWithTwentyDipClearance()
    {
        FmSynthLayoutRect[] bodies =
        [
            new(80, 80, 170, 72),
            new(460, 80, 170, 80),
        ];
        IReadOnlyList<FmSynthRoutePoint>[] edges =
        [
            new FmSynthRoutePoint[]
            {
                new(250, 116), new(900, 116), new(900, 350),
                new(65, 350), new(65, 210),
            },
        ];

        FmSynthRoutePoint placement =
            FmSynthGraphNodePlacement.FindUnobstructedPosition(
                bodies, edges, new FmSynthRoutePoint(60, 60),
                700, 500, 170, FmSynthGraphNodePlacement.InitialNodeHeight);
        FmSynthLayoutRect candidate = new(placement.X, placement.Y, 170, 80);
        Assert.That(FmSynthGraphNodePlacement.IsUnobstructed(
            candidate, bodies, edges), Is.True);
        Assert.That(placement, Is.Not.EqualTo(new FmSynthRoutePoint(80, 80)),
            "the initial visible cell overlaps the output node");
        Assert.That(FmSynthGraphNodePlacement.InitialNodeHeight, Is.EqualTo(80));
    }

    [Test]
    public void LongCrossingSegmentsAndDiagonalRoutesBlockCandidatesWithoutInsideVertices()
    {
        FmSynthLayoutRect candidate = new(100, 100, 170, 80);
        FmSynthRoutePoint[] across =
        [
            new(-100, 150), new(1000, 150),
        ];
        FmSynthRoutePoint[] diagonal =
        [
            new(-100, -100), new(600, 600),
        ];
        Assert.That(FmSynthGraphNodePlacement.IsUnobstructed(
            candidate, Array.Empty<FmSynthLayoutRect>(),
            [across]), Is.False);
        Assert.That(FmSynthGraphNodePlacement.IsUnobstructed(
            candidate, Array.Empty<FmSynthLayoutRect>(),
            [diagonal]), Is.False);
        Assert.That(FmSynthGraphNodePlacement.IsUnobstructed(
            candidate, Array.Empty<FmSynthLayoutRect>(),
            [new FmSynthRoutePoint[] { new(-100, 400), new(1000, 400) }]),
            Is.True, "distant wires must not disqualify empty space");
    }

    [Test]
    public void RepeatedDenseAdditionsFindDistinctUnobstructedSpace()
    {
        List<FmSynthLayoutRect> bodies = [];
        for (int row = 0; row < 5; row++)
            for (int column = 0; column < 4; column++)
                bodies.Add(new FmSynthLayoutRect(
                    80 + column * 190, 80 + row * 100, 170, 80));
        IReadOnlyList<FmSynthRoutePoint>[] edges =
        [
            new FmSynthRoutePoint[] { new(10, 620), new(2400, 620) },
            new FmSynthRoutePoint[] { new(2400, 620), new(2400, -600) },
        ];
        FmSynthRoutePoint camera = new(60, 60);

        FmSynthRoutePoint first =
            FmSynthGraphNodePlacement.FindUnobstructedPosition(
                bodies, edges, camera, 700, 500, 170, 80);
        Assert.That(FmSynthGraphNodePlacement.IsUnobstructed(
            new FmSynthLayoutRect(first.X, first.Y, 170, 80),
            bodies, edges), Is.True);

        bodies.Add(new FmSynthLayoutRect(first.X, first.Y, 170, 80));
        FmSynthRoutePoint second =
            FmSynthGraphNodePlacement.FindUnobstructedPosition(
                bodies, edges, camera, 700, 500, 170, 80);
        Assert.That(second, Is.Not.EqualTo(first));
        Assert.That(FmSynthGraphNodePlacement.IsUnobstructed(
            new FmSynthLayoutRect(second.X, second.Y, 170, 80),
            bodies, edges), Is.True);
    }

    [Test]
    public void BufferedRevealPreservesVisibleCameraAndMovesOnlyRequiredAxes()
    {
        FmSynthRoutePoint camera = new(0, 0);
        Assert.That(FmSynthGraphNodePlacement.CameraToReveal(
            camera, new FmSynthLayoutRect(200, 150, 170, 80), 500, 400),
            Is.EqualTo(camera));
        Assert.That(FmSynthGraphNodePlacement.CameraToReveal(
            camera, new FmSynthLayoutRect(480, 420, 170, 80), 500, 400),
            Is.EqualTo(new FmSynthRoutePoint(170, 120)));
        Assert.That(FmSynthGraphNodePlacement.CameraToReveal(
            camera, new FmSynthLayoutRect(-300, -140, 170, 80), 500, 400),
            Is.EqualTo(new FmSynthRoutePoint(-320, -160)));
        Assert.That(FmSynthGraphNodePlacement.CameraToReveal(
            camera, new FmSynthLayoutRect(480, 150, 170, 80), 180, 400),
            Is.EqualTo(new FmSynthRoutePoint(460, 0)),
            "an undersized axis anchors the padded near edge");
    }

    [AvaloniaTest]
    public void DenseGraphInsertionRevealsFullBufferWithoutExtraDocumentOrAudioMutation()
    {
        DocumentWorkspace work = new();
        FmSynthDefinition synth =
            FmSynthDocumentEditor.CreateFmSynth(work, "Spawn geometry");
        // The output is already at (80,80). Fill the remaining cells in a
        // viewport to force insertion beyond the initially visible area.
        for (int row = 0; row < 4; row++)
        {
            for (int column = 0; column < 3; column++)
            {
                if (row == 0 && column == 0)
                    continue;
                FmSynthDocumentEditor.AddConstantNode(
                    work, synth, 0.5, 80 + column * 190, 80 + row * 100);
            }
        }
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
            FmSynthRoutePoint oldCamera =
                Field<FmSynthRoutePoint>(canvas, "_camera");
            FmSynthRoutePoint placement = canvas.FindUnobstructedSpawnPosition();
            int id = FmSynthDocumentEditor.AddConstantNode(
                work, synth, 0.5, placement.X, placement.Y);
            uint revision = work.Document.DocumentRevision;
            uint audioRevision = work.Document.AudioRevision;

            canvas.Refresh();
            canvas.RevealNewNode(id);
            window.UpdateLayout();
            FmSynthRoutePoint newCamera =
                Field<FmSynthRoutePoint>(canvas, "_camera");
            Assert.That(newCamera, Is.Not.EqualTo(oldCamera),
                "a fully occupied visible region must reveal the new node");
            double margin = FmSynthGraphExtentGeometry.Margin;
            double epsilon = 1.1;
            Assert.That(placement.X - margin,
                Is.GreaterThanOrEqualTo(newCamera.X - epsilon));
            Assert.That(placement.X + FmSynthGraphCanvas.NodeWidth + margin,
                Is.LessThanOrEqualTo(newCamera.X + scroll.Viewport.Width + epsilon));
            Assert.That(placement.Y - margin,
                Is.GreaterThanOrEqualTo(newCamera.Y - epsilon));
            Assert.That(placement.Y + FmSynthGraphCanvas.NodeHeight + margin,
                Is.LessThanOrEqualTo(newCamera.Y + scroll.Viewport.Height + epsilon));
            Assert.That(work.Document.DocumentRevision, Is.EqualTo(revision),
                "revealing a node must not create a document edit");
            Assert.That(work.Document.AudioRevision, Is.EqualTo(audioRevision),
                "reveal is camera-only and cannot publish sound");

            window.Width = 540;
            window.Height = 340;
            window.UpdateLayout();
            FmSynthRoutePoint second = canvas.FindUnobstructedSpawnPosition();
            int secondId = FmSynthDocumentEditor.AddConstantNode(
                work, synth, 0.6, second.X, second.Y);
            canvas.Refresh();
            canvas.RevealNewNode(secondId);
            window.UpdateLayout();
            FmSynthRoutePoint resizedCamera =
                Field<FmSynthRoutePoint>(canvas, "_camera");
            Assert.That(second.X - margin,
                Is.GreaterThanOrEqualTo(resizedCamera.X - epsilon));
            Assert.That(second.X + FmSynthGraphCanvas.NodeWidth + margin,
                Is.LessThanOrEqualTo(resizedCamera.X + scroll.Viewport.Width + epsilon));
        }
        finally
        {
            window.Close();
        }
    }
}
