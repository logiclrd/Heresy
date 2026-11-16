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
using Heresy.Core.Filters;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.FmEditing;
using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class FmSynthConnectionSelectionTests
{
    private static T Field<T>(object owner, string name)
        => (T)owner.GetType().GetField(name,
            BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;

    private static void SelectConnection(FmSynthEditorControl editor,
        FmSynthConnectionCandidate connection)
        => typeof(FmSynthEditorControl).GetMethod("SelectConnection",
            BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(editor, [connection]);

    private static void SelectNode(FmSynthEditorControl editor, int nodeId)
        => typeof(FmSynthEditorControl).GetMethod("SelectNode",
            BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(editor, [nodeId]);

    private static KeyEventArgs Key(Control target,
        Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        KeyEventArgs e = new()
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key, KeyModifiers = modifiers,
        };
        target.RaiseEvent(e);
        return e;
    }

    [Test]
    public void DisconnectionRemovesOnlySelectedDuplicateOperandAndReindexesRoutingHints()
    {
        DocumentWorkspace work = new();
        FmSynthDefinition synth =
            FmSynthDocumentEditor.CreateFmSynth(work, "Duplicate operands");
        int other = FmSynthDocumentEditor.AddConstantNode(
            work, synth, 2.0, 260, 80);
        int op = FmSynthDocumentEditor.AddOperatorNode(
            work, synth, FmOperatorKind.Add, [0, other, 0, other],
            500, 80);
        FmSynthDocumentEditor.SetRoutingHint(work, synth, 0, op, 0,
            [new FmSynthRoutePoint(300, 150)]);
        FmSynthDocumentEditor.SetRoutingHint(work, synth, other, op, 1,
            [new FmSynthRoutePoint(310, 160)]);
        FmSynthDocumentEditor.SetRoutingHint(work, synth, 0, op, 2,
            [new FmSynthRoutePoint(320, 170)]);
        FmSynthDocumentEditor.SetRoutingHint(work, synth, other, op, 3,
            [new FmSynthRoutePoint(330, 180)]);
        uint audio = work.Document.AudioRevision;
        uint document = work.Document.DocumentRevision;

        FmSynthDocumentEditor.DisconnectConnection(work, synth, other, op, 1);
        Assert.That(synth.Graph.Nodes.OfType<FmOperatorNode>().Single()
            .InputNodeIds, Is.EqualTo(new[] { 0, 0, other }));
        Assert.That(synth.ConnectionRoutingHints.Select(hint =>
                (hint.SourceNodeId, hint.TargetInputIndex)),
            Is.EquivalentTo(new[] { (0, 0), (0, 1), (other, 2) }));
        Assert.That(synth.ConnectionRoutingHints.Single(hint =>
                hint.TargetInputIndex == 1).RoutePoints.Single(),
            Is.EqualTo(new FmSynthRoutePoint(320, 170)),
            "surviving operand's waypoint follows it to its new index");
        Assert.That(work.Document.AudioRevision, Is.EqualTo(audio + 1));
        Assert.That(work.Document.DocumentRevision, Is.EqualTo(document + 1));
        Assert.That(synth.Graph.Nodes.Any(n => n.Id == other), Is.True);
        FmSynthGraph before = synth.Graph;
        uint revision = work.Document.DocumentRevision;
        Assert.Throws<InvalidOperationException>(() =>
            FmSynthDocumentEditor.DisconnectConnection(work, synth, other, op, 1),
            "the removed edge's original slot cannot affect its new occupant");
        Assert.That(synth.Graph, Is.SameAs(before));
        Assert.That(work.Document.DocumentRevision, Is.EqualTo(revision));
    }

    [Test]
    public void LastOperatorOperandBecomesSilentConstantButRetainsDownstreamNodeIdentity()
    {
        DocumentWorkspace work = new();
        FmSynthDefinition synth =
            FmSynthDocumentEditor.CreateFmSynth(work, "Last input");
        int op = FmSynthDocumentEditor.AddOperatorNode(
            work, synth, FmOperatorKind.Multiply, [0], 500, 100);
        FmSynthDocumentEditor.SetOutputNode(work, synth, op);
        FmSynthDocumentEditor.DisconnectConnection(work, synth, 0, op, 0);
        Assert.That(synth.Graph.OutputNodeId, Is.EqualTo(op));
        Assert.That(synth.Graph.Nodes.Single(n => n.Id == op),
            Is.TypeOf<FmConstantNode>());
        Assert.That(((FmConstantNode)synth.Graph.Nodes.Single(n => n.Id == op))
            .Value, Is.Zero);
        Assert.That(synth.Graph.Nodes.Any(n => n.Id == 0), Is.True);
    }

    [Test]
    public void DisconnectClearsOscillatorAndFilterPortWithoutChangingOtherParameters()
    {
        DocumentWorkspace work = new();
        FmSynthDefinition synth =
            FmSynthDocumentEditor.CreateFmSynth(work, "All ports");
        int oscillator = FmSynthDocumentEditor.AddOscillatorNode(work, synth,
            FmOscillatorWaveform.Square, 120, 270, 80,
            multiplierNodeId: 0, exponentialMultiplier: true,
            constant: true, curvature: 1.5);
        int filterId = FmSynthDocumentEditor.AddFilterNode(work, synth, 520, 80);
        FmSynthDocumentEditor.ConnectNodes(work, synth, oscillator, filterId, 0);

        FmSynthDocumentEditor.DisconnectConnection(work, synth, 0, oscillator, 0);
        FmOscillatorNode after = synth.Graph.Nodes.OfType<FmOscillatorNode>().Single();
        Assert.That(after.MultiplierNodeId, Is.Null);
        Assert.That(after.Waveform, Is.EqualTo(FmOscillatorWaveform.Square));
        Assert.That(after.ExponentialMultiplier, Is.True);
        Assert.That(after.Constant, Is.True);
        Assert.That(after.Curvature, Is.EqualTo(1.5));

        FmSynthDocumentEditor.DisconnectConnection(
            work, synth, oscillator, filterId, 0);
        Assert.That(synth.Graph.Nodes.OfType<FmFilterNode>()
            .Single().AudioInputNodeId, Is.Null);
        Assert.That(synth.Graph.Nodes.OfType<FmOscillatorNode>().ToArray(),
            Has.Length.EqualTo(1));
    }

    [Test]
    public void FilterModulationDisconnectionRetainsAudioInputAndOtherPortBindings()
    {
        DocumentWorkspace work = new();
        FilterDefinition definition = FilterDocumentEditor.CreateFilter(
            work, "Modulation");
        FmSynthDefinition synth =
            FmSynthDocumentEditor.CreateFmSynth(work, "Filter modulation");
        int modulator = FmSynthDocumentEditor.AddConstantNode(
            work, synth, 0.25, 240, 80);
        int filterId = FmSynthDocumentEditor.AddFilterNode(
            work, synth, 520, 80);
        FmSynthDocumentEditor.SetFilterConfiguration(
            work, synth, filterId, new FilterInstance(definition));
        FmSynthDocumentEditor.ConnectNodes(work, synth, 0, filterId, 0);
        FmSynthDocumentEditor.ConnectNodes(work, synth, modulator, filterId, 1);
        FmSynthDocumentEditor.SetRoutingHint(work, synth, 0, filterId, 0,
            [new FmSynthRoutePoint(290, 150)]);
        FmSynthDocumentEditor.SetRoutingHint(work, synth, modulator, filterId, 1,
            [new FmSynthRoutePoint(310, 190)]);

        uint audio = work.Document.AudioRevision;
        FmSynthDocumentEditor.DisconnectConnection(
            work, synth, modulator, filterId, 1);
        FmFilterNode after = synth.Graph.Nodes
            .OfType<FmFilterNode>().Single();
        Assert.That(after.GetPorts()[0].SourceNodeId, Is.EqualTo(0));
        Assert.That(after.GetPorts()[1].SourceNodeId, Is.Null);
        Assert.That(after.Connections, Is.Empty);
        Assert.That(synth.ConnectionRoutingHints,
            Has.Count.EqualTo(1));
        Assert.That(synth.ConnectionRoutingHints.Single().TargetInputIndex,
            Is.EqualTo(0));
        Assert.That(work.Document.AudioRevision, Is.EqualTo(audio + 1));
    }

    [Test]
    public void MultiAndSoundSourceDisconnectOnlyTheirOptionalInputs()
    {
        DocumentWorkspace work = new();
        FmSynthDefinition synth =
            FmSynthDocumentEditor.CreateFmSynth(work, "Optional inputs");
        int multiId = FmSynthDocumentEditor.AddMultiNode(
            work, synth, 350, 80);
        int sourceId = FmSynthDocumentEditor.AddSoundSourceNode(
            work, synth, 580, 80, constant: true);
        FmSynthDocumentEditor.ConnectNodes(work, synth, 0, multiId, 0);
        FmSynthDocumentEditor.ConnectNodes(work, synth, 0, sourceId, 0);

        uint audio = work.Document.AudioRevision;
        FmSynthDocumentEditor.DisconnectConnection(
            work, synth, 0, multiId, 0);
        FmSynthDocumentEditor.DisconnectConnection(
            work, synth, 0, sourceId, 0);
        Assert.That(synth.Graph.Nodes.OfType<FmMultiNode>()
            .Single().InputNodeId, Is.Null);
        FmSoundSourceNode source = synth.Graph.Nodes
            .OfType<FmSoundSourceNode>().Single();
        Assert.That(source.MultiplierNodeId, Is.Null);
        Assert.That(source.Constant, Is.True);
        Assert.That(synth.Graph.Nodes, Has.Count.EqualTo(3));
        Assert.That(work.Document.AudioRevision, Is.EqualTo(audio + 2));
    }

    [AvaloniaTest]
    public void WirePressSelectsConnectionWhileNodePressRestoresExclusiveNodeSelection()
    {
        DocumentWorkspace work = new();
        FmSynthDefinition synth =
            FmSynthDocumentEditor.CreateFmSynth(work, "Selection");
        int producer = FmSynthDocumentEditor.AddConstantNode(
            work, synth, 0.75, 300, 220);
        int op = FmSynthDocumentEditor.AddOperatorNode(
            work, synth, FmOperatorKind.Add, [producer], 620, 220);
        List<FmSynthConnectionCandidate> selected = [];
        FmSynthGraphCanvas canvas = new(synth, _ => { }, (_, _, _) => { },
            (_, _, _) => { }, (_, _, _, _) => { }, selected.Add);
        Window window = new()
        {
            Width = 1050, Height = 730,
            Content = canvas,
        };
        window.Show();
        try
        {
            window.UpdateLayout();
            Canvas graph = Field<Canvas>(canvas, "_canvas");
            FmSynthRenderedConnection[] connections =
                (FmSynthRenderedConnection[])typeof(FmSynthGraphCanvas)
                    .GetMethod("GetRenderedConnections",
                        BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(canvas, null)!;
            FmSynthRenderedConnection wire = connections.Single(
                c => c.Connection.SourceNodeId == producer);
            FmSynthRoutePoint a = wire.Route[0];
            FmSynthRoutePoint b = wire.Route[1];
            Point wirePoint = graph.TranslatePoint(
                new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2),
                window)!.Value;

            uint revision = work.Document.DocumentRevision;
            window.MouseMove(wirePoint);
            window.MouseDown(wirePoint, MouseButton.Left);
            window.MouseUp(wirePoint, MouseButton.Left);
            Assert.That(selected, Has.Count.EqualTo(1));
            Assert.That(Field<FmSynthConnectionCandidate?>(
                canvas, "_selectedConnection"),
                Is.EqualTo(wire.Connection));
            Assert.That(Field<int?>(canvas, "_selectedNodeId"), Is.Null);
            Assert.That(work.Document.DocumentRevision, Is.EqualTo(revision),
                "selecting a wire or clicking without moving is presentation-only");

            Point nodePoint = graph.TranslatePoint(
                new Point(300 + 40, 220 + 35), window)!.Value;
            window.MouseDown(nodePoint, MouseButton.Left);
            window.MouseUp(nodePoint, MouseButton.Left);
            Assert.That(Field<FmSynthConnectionCandidate?>(
                canvas, "_selectedConnection"), Is.Null);
            Assert.That(Field<int?>(canvas, "_selectedNodeId"), Is.EqualTo(producer));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void GraphFocusedDeleteDisconnectsOnlySelectedEdgeAndInspectedTextFieldIsSafe()
    {
        DocumentWorkspace work = new();
        FmSynthDefinition synth =
            FmSynthDocumentEditor.CreateFmSynth(work, "Delete edge");
        int additional = FmSynthDocumentEditor.AddConstantNode(
            work, synth, 0.7, 260, 90);
        int op = FmSynthDocumentEditor.AddOperatorNode(
            work, synth, FmOperatorKind.Add, [0, additional, 0], 510, 90);
        List<string> changes = [];
        FmSynthEditorControl editor = new(
            work, synth, () => { }, changes.Add);
        FmSynthGraphCanvas canvas = Field<FmSynthGraphCanvas>(editor, "_canvas");
        FmSynthConnectionCandidate edge = new(0, op, 2);
        uint audio = work.Document.AudioRevision;

        SelectConnection(editor, edge);
        Assert.That(Field<int?>(editor, "_selectedNodeId"), Is.Null);
        Assert.That(Field<FmSynthConnectionCandidate?>(
            editor, "_selectedConnection"), Is.EqualTo(edge));
        KeyEventArgs modified = Key(canvas, Avalonia.Input.Key.Delete,
            KeyModifiers.Control);
        Assert.That(modified.Handled, Is.False);
        Assert.That(work.Document.AudioRevision, Is.EqualTo(audio));

        KeyEventArgs pressed = Key(canvas, Avalonia.Input.Key.Delete);
        Assert.That(pressed.Handled, Is.True);
        Assert.That(synth.Graph.Nodes.OfType<FmOperatorNode>()
            .Single().InputNodeIds, Is.EqualTo(new[] { 0, additional }));
        Assert.That(work.Document.AudioRevision, Is.EqualTo(audio + 1));
        Assert.That(Field<FmSynthConnectionCandidate?>(
            editor, "_selectedConnection"), Is.Null);
        Assert.That(synth.Graph.Nodes.Any(n => n.Id == 0), Is.True);
        Assert.That(synth.Graph.Nodes.Any(n => n.Id == additional), Is.True);
        Assert.That(changes, Has.Count.EqualTo(1));

        SelectNode(editor, additional);
        StackPanel inspector = Field<StackPanel>(editor, "_inspector");
        TextBox field = inspector.Children.OfType<TextBox>().First();
        KeyEventArgs inField = Key(field, Avalonia.Input.Key.Delete);
        Assert.That(inField.Handled, Is.False);
        Assert.That(work.Document.AudioRevision, Is.EqualTo(audio + 1));
    }
}
