using System;
using System.Linq;
using System.Reflection;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Headless.NUnit;

using AwesomeAssertions;

using Heresy.Core.Envelopes;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.EnvelopeEditing;
using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class NodeEnvelopeGraphControlTests
{
	[AvaloniaTest]
	public void GraphCommitUpdatesSharedEnvelopeAndSynchronizesNodeList()
	{
		DocumentWorkspace workspace = new();
		NodeEnvelopeDefinition envelope =
			EnvelopeDocumentEditor.CreateNodeEnvelope(workspace, "Shared");
		string? changed = null;
		NodeEnvelopeEditorControl editor = new(
			workspace, envelope, s => changed = s);
		NodeEnvelopeGraphControl graph =
			(NodeEnvelopeGraphControl)typeof(NodeEnvelopeEditorControl)
				.GetField("_graph",
					BindingFlags.Instance | BindingFlags.NonPublic)!
				.GetValue(editor)!;
		uint revision = workspace.Document.AudioRevision;
		EnvelopeNode[] nodes = envelope.Nodes.ToArray();
		nodes[1] = new EnvelopeNode(
			TimeSpan.FromMilliseconds(20), 2.5);
		NodeEnvelopeGraphEdit request =
			new(nodes, 2, 2, SelectedNodeIndex: 1);
		typeof(NodeEnvelopeGraphControl).GetMethod(
			"Commit", BindingFlags.Instance | BindingFlags.NonPublic)!
			.Invoke(graph, [request]);
		envelope.Nodes[1].Value.Should().Be(2.5);
		envelope.Nodes[1].Time.Should().Be(TimeSpan.FromMilliseconds(20));
		graph.SelectedNodeIndex.Should().Be(1);
		workspace.Document.AudioRevision.Should().Be(revision + 1);
		changed.Should().Contain("Updated envelope");
		TextBox text = (TextBox)typeof(NodeEnvelopeEditorControl)
			.GetField("_nodesInput",
				BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(editor)!;
		text.Text.Should().Contain("0.02, 2.5");

		typeof(NodeEnvelopeGraphControl).GetMethod(
			"Commit", BindingFlags.Instance | BindingFlags.NonPublic)!
			.Invoke(graph, [request]);
		workspace.Document.AudioRevision.Should().Be(revision + 1,
			"equivalent edits do not generate extra audio revisions");
	}

	[AvaloniaTest]
	public void MatchAppearsForSelectedDistinctEndpointAndCommitsOneValue()
	{
		DocumentWorkspace workspace = new();
		NodeEnvelopeDefinition envelope =
			EnvelopeDocumentEditor.CreateNodeEnvelope(workspace, "Match test");
		EnvelopeDocumentEditor.UpdateNodeEnvelope(
			workspace, envelope, envelope.Nodes,
			1, 2, EnvelopeSustainLoopMode.PingPong);
		NodeEnvelopeEditorControl editor =
			new(workspace, envelope, _ => { });
		NodeEnvelopeGraphControl graph =
			(NodeEnvelopeGraphControl)typeof(NodeEnvelopeEditorControl)
				.GetField("_graph", BindingFlags.Instance
					| BindingFlags.NonPublic)!.GetValue(editor)!;
		Button match = (Button)typeof(NodeEnvelopeGraphControl)
			.GetField("_match", BindingFlags.Instance
				| BindingFlags.NonPublic)!.GetValue(graph)!;
		object surface = typeof(NodeEnvelopeGraphControl)
			.GetField("_surface", BindingFlags.Instance
				| BindingFlags.NonPublic)!.GetValue(graph)!;
		MethodInfo select = surface.GetType().GetMethod("Select")!;
		MethodInfo action = typeof(NodeEnvelopeGraphControl)
			.GetMethod("MatchSelected", BindingFlags.Instance
				| BindingFlags.NonPublic)!;
		match.IsVisible.Should().BeFalse();
		match.IsEnabled.Should().BeFalse();

		select.Invoke(surface, [1]);
		match.IsVisible.Should().BeTrue();
		match.IsEnabled.Should().BeTrue();
		double oldOpposite = envelope.Nodes[2].Value;
		TimeSpan oldTime = envelope.Nodes[1].Time;
		uint revision = workspace.Document.AudioRevision;
		action.Invoke(graph, []);
		envelope.Nodes[1].Value.Should().Be(oldOpposite);
		envelope.Nodes[1].Time.Should().Be(oldTime);
		envelope.Nodes[2].Value.Should().Be(oldOpposite);
		envelope.SustainStartIndex.Should().Be(1);
		envelope.SustainEndIndex.Should().Be(2);
		envelope.SustainLoopMode.Should().Be(EnvelopeSustainLoopMode.PingPong);
		workspace.Document.AudioRevision.Should().Be(revision + 1);
		match.IsVisible.Should().BeTrue();
		match.IsEnabled.Should().BeFalse();
		action.Invoke(graph, []);
		workspace.Document.AudioRevision.Should().Be(revision + 1);

		select.Invoke(surface, [2]);
		match.IsVisible.Should().BeTrue();
		match.IsEnabled.Should().BeFalse();
		select.Invoke(surface, [0]);
		match.IsVisible.Should().BeFalse();
		match.IsEnabled.Should().BeFalse();
		select.Invoke(surface, [-1]);
		match.IsVisible.Should().BeFalse();
	}

	[AvaloniaTest]
	public void MatchCanCopyTheOtherDirectionAndHidesForSingleNodeSustain()
	{
		DocumentWorkspace workspace = new();
		NodeEnvelopeDefinition envelope =
			EnvelopeDocumentEditor.CreateNodeEnvelope(workspace, "Two ends");
		EnvelopeDocumentEditor.UpdateNodeEnvelope(
			workspace, envelope, envelope.Nodes,
			1, 2, EnvelopeSustainLoopMode.Forward);
		NodeEnvelopeGraphControl graph = new(workspace, envelope);
		Button match = (Button)typeof(NodeEnvelopeGraphControl)
			.GetField("_match", BindingFlags.Instance
				| BindingFlags.NonPublic)!.GetValue(graph)!;
		object surface = typeof(NodeEnvelopeGraphControl)
			.GetField("_surface", BindingFlags.Instance
				| BindingFlags.NonPublic)!.GetValue(graph)!;
		surface.GetType().GetMethod("Select")!.Invoke(surface, [2]);
		match.IsVisible.Should().BeTrue();
		match.IsEnabled.Should().BeTrue();
		uint revision = workspace.Document.AudioRevision;
		typeof(NodeEnvelopeGraphControl).GetMethod(
			"MatchSelected", BindingFlags.Instance
				| BindingFlags.NonPublic)!.Invoke(graph, []);
		envelope.Nodes[2].Value.Should().Be(1.0);
		envelope.Nodes[1].Value.Should().Be(1.0);
		workspace.Document.AudioRevision.Should().Be(revision + 1);

		EnvelopeDocumentEditor.UpdateNodeEnvelope(
			workspace, envelope, envelope.Nodes,
			2, 2, EnvelopeSustainLoopMode.Forward);
		graph.RefreshFromEnvelope();
		match.IsVisible.Should().BeFalse();
		match.IsEnabled.Should().BeFalse();
	}

	[AvaloniaTest]
	public void SustainBoundaryCommitUpdatesNumericFieldsAndOnlyOneAudioRevision()
	{
		DocumentWorkspace workspace = new();
		NodeEnvelopeDefinition envelope =
			EnvelopeDocumentEditor.CreateNodeEnvelope(workspace, "Sustain");
		EnvelopeDocumentEditor.UpdateNodeEnvelope(
			workspace, envelope, envelope.Nodes,
			1, 2, EnvelopeSustainLoopMode.PingPong);
		NodeEnvelopeEditorControl editor =
			new(workspace, envelope, _ => { });
		NodeEnvelopeGraphControl graph =
			(NodeEnvelopeGraphControl)typeof(NodeEnvelopeEditorControl)
				.GetField("_graph", BindingFlags.Instance
					| BindingFlags.NonPublic)!.GetValue(editor)!;
		MethodInfo commit = typeof(NodeEnvelopeGraphControl)
			.GetMethod("Commit", BindingFlags.Instance
				| BindingFlags.NonPublic)!;
		uint revision = workspace.Document.AudioRevision;
		NodeEnvelopeGraphEdit region = new(envelope.Nodes,
			SustainStartIndex: 0, SustainEndIndex: 3,
			SelectedNodeIndex: -1);
		commit.Invoke(graph, [region]);
		envelope.SustainStartIndex.Should().Be(0);
		envelope.SustainEndIndex.Should().Be(3);
		workspace.Document.AudioRevision.Should().Be(revision + 1);
		((TextBox)typeof(NodeEnvelopeEditorControl)
			.GetField("_sustainStart", BindingFlags.Instance
				| BindingFlags.NonPublic)!.GetValue(editor)!)
			.Text.Should().Be("0");
		((TextBox)typeof(NodeEnvelopeEditorControl)
			.GetField("_sustainEnd", BindingFlags.Instance
				| BindingFlags.NonPublic)!.GetValue(editor)!)
			.Text.Should().Be("3");
		commit.Invoke(graph, [region]);
		workspace.Document.AudioRevision.Should().Be(revision + 1);
	}

	[AvaloniaTest]
	public void GuideHoverUsesDistinctCursorsAndFreezesCursorDuringDrag()
	{
		DocumentWorkspace workspace = new();
		NodeEnvelopeDefinition envelope =
			EnvelopeDocumentEditor.CreateNodeEnvelope(workspace, "Hover");
		EnvelopeDocumentEditor.UpdateNodeEnvelope(workspace, envelope,
			envelope.Nodes, 1, 3, EnvelopeSustainLoopMode.Forward);
		NodeEnvelopeGraphControl graph = new(workspace, envelope);
		Control surface = (Control)typeof(NodeEnvelopeGraphControl)
			.GetField("_surface", BindingFlags.Instance
				| BindingFlags.NonPublic)!.GetValue(graph)!;
		surface.Measure(new Size(440, 250));
		surface.Arrange(new Rect(0, 0, 440, 250));
		NodeEnvelopeGraphGeometry geometry = new(envelope.Nodes,
			440, 250, envelope.Vmin, envelope.Vmax);
		Type type = surface.GetType();
		BindingFlags privateFlags = BindingFlags.NonPublic
			| BindingFlags.Instance;
		BindingFlags staticFlags = BindingFlags.NonPublic
			| BindingFlags.Static;
		object endpoint = type.GetField("EndpointCursor", staticFlags)!
			.GetValue(null)!;
		object range = type.GetField("RangeCursor", staticFlags)!
			.GetValue(null)!;
		object normal = type.GetField("NormalCursor", staticFlags)!
			.GetValue(null)!;
		MethodInfo hover = type.GetMethod(
			"UpdateHoverCursor", privateFlags)!;
		// Keep cursor tests away from node dots, which intentionally
		// take precedence over their vertical guide hit regions.
		double y = (geometry.Top + geometry.Bottom) / 2;

		hover.Invoke(surface, [new Point(geometry.Point(1).X, y)]);
		surface.Cursor.Should().BeSameAs(endpoint);
		hover.Invoke(surface, [new Point(geometry.Point(2).X, y)]);
		surface.Cursor.Should().BeSameAs(range);
		hover.Invoke(surface, [new Point(geometry.Point(2).X + 2.01, y)]);
		surface.Cursor.Should().BeSameAs(normal);
		hover.Invoke(surface, [new Point(geometry.Point(3).X, y)]);
		surface.Cursor.Should().BeSameAs(endpoint);

		type.GetField("_dragGuide", privateFlags)!.SetValue(surface, 3);
		hover.Invoke(surface, [new Point(geometry.Point(2).X, y)]);
		surface.Cursor.Should().BeSameAs(endpoint,
			"cursor remains as it was at drag start");
		type.GetMethod("CancelDrag")!.Invoke(surface, []);
		surface.Cursor.Should().BeSameAs(normal);
	}

	[AvaloniaTest]
	public void SustainPreviewMovesCrossingEndpointAndAcceptsInteriorRangeOrigin()
	{
		DocumentWorkspace workspace = new();
		NodeEnvelopeDefinition envelope =
			EnvelopeDocumentEditor.CreateNodeEnvelope(workspace, "Ranges");
		EnvelopeDocumentEditor.UpdateNodeEnvelope(workspace, envelope,
			envelope.Nodes, 1, 3, EnvelopeSustainLoopMode.Forward);
		NodeEnvelopeGraphControl graph = new(workspace, envelope);
		object surface = typeof(NodeEnvelopeGraphControl)
			.GetField("_surface", BindingFlags.Instance
				| BindingFlags.NonPublic)!.GetValue(graph)!;
		Type type = surface.GetType();
		BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
		NodeEnvelopeGraphGeometry geometry = new(envelope.Nodes,
			440, 250, envelope.Vmin, envelope.Vmax);
		uint revision = workspace.Document.AudioRevision;
		void Set(string field, object value) =>
			type.GetField(field, flags)!.SetValue(surface, value);
		int Preview(string field) =>
			(int)type.GetField(field, flags)!.GetValue(surface)!;
		void Start(int origin)
		{
			Set("_dragGuide", origin);
			Set("_dragNode", -1);
			Set("_originalSustainStart", 1);
			Set("_originalSustainEnd", 3);
			Set("_fixedLayout", geometry);
			Set("_press", new Point(geometry.Point(origin).X,
				geometry.Top + 10));
			Set("_dragMoved", false);
		}
		void Move(int target) => type.GetMethod(
			"UpdateDraggedPosition", flags)!.Invoke(surface,
				[new Point(geometry.Point(target).X, geometry.Top + 10)]);

		Start(1);
		Move(3);
		Preview("_previewSustainStart").Should().Be(3);
		Preview("_previewSustainEnd").Should().Be(3);
		type.GetMethod("CancelDrag")!.Invoke(surface, []);
		Start(1);
		Move(0);
		Preview("_previewSustainStart").Should().Be(0);
		Preview("_previewSustainEnd").Should().Be(3);
		type.GetMethod("CancelDrag")!.Invoke(surface, []);

		// Starting on guide 2 (inside [1,3]) must replace the range.
		Start(2);
		Move(0);
		Preview("_previewSustainStart").Should().Be(0);
		Preview("_previewSustainEnd").Should().Be(2);
		workspace.Document.AudioRevision.Should().Be(revision,
			"previews never commit audio changes");
		type.GetMethod("CancelDrag")!.Invoke(surface, []);
		Preview("_previewSustainStart").Should().Be(1);
		Preview("_previewSustainEnd").Should().Be(3);
	}

	[AvaloniaTest]
	public void CancelledSustainDragRestoresPreviewWithoutDocumentMutation()
	{
		DocumentWorkspace workspace = new();
		NodeEnvelopeDefinition envelope =
			EnvelopeDocumentEditor.CreateNodeEnvelope(workspace, "Cancel");
		NodeEnvelopeGraphControl graph = new(workspace, envelope);
		object surface = typeof(NodeEnvelopeGraphControl)
			.GetField("_surface", BindingFlags.Instance
				| BindingFlags.NonPublic)!.GetValue(graph)!;
		Type type = surface.GetType();
		BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
		uint revision = workspace.Document.AudioRevision;
		type.GetField("_dragGuide", flags)!.SetValue(surface, 0);
		type.GetField("_previewSustainStart", flags)!.SetValue(surface, 0);
		type.GetField("_previewSustainEnd", flags)!.SetValue(surface, 3);
		type.GetProperty("IsDragging")!.GetValue(surface)
			.Should().Be(true);
		type.GetMethod("CancelDrag")!.Invoke(surface, []);
		type.GetProperty("IsDragging")!.GetValue(surface)
			.Should().Be(false);
		type.GetField("_previewSustainStart", flags)!.GetValue(surface)
			.Should().Be(envelope.SustainStartIndex);
		type.GetField("_previewSustainEnd", flags)!.GetValue(surface)
			.Should().Be(envelope.SustainEndIndex);
		workspace.Document.AudioRevision.Should().Be(revision);
	}

	[AvaloniaTest]
	public void HeadlessPointerDragSelectsAndAdjustsSustainBounds()
	{
		DocumentWorkspace workspace = new();
		NodeEnvelopeDefinition envelope =
			EnvelopeDocumentEditor.CreateNodeEnvelope(workspace, "Pointer");
		NodeEnvelopeGraphControl graph = new(workspace, envelope);
		Control surface = (Control)typeof(NodeEnvelopeGraphControl)
			.GetField("_surface", BindingFlags.Instance
				| BindingFlags.NonPublic)!.GetValue(graph)!;
		Window host = new() { Width = 550, Height = 370, Content = graph };
		host.Show();
		try
		{
			NodeEnvelopeGraphGeometry geometry = new(
				envelope.Nodes, surface.Bounds.Width, surface.Bounds.Height);
			Point GuidePosition(int i)
			{
				var (x, dotY) = geometry.Point(i);
				double y = geometry.Top + 8;
				if (Math.Abs(y - dotY) < 10)
					y = geometry.Bottom - 8;
				return surface.TranslatePoint(new Point(x, y), host)!.Value;
			}
			void Drag(int from, int to)
			{
				Point a = GuidePosition(from);
				Point b = GuidePosition(to);
				host.MouseMove(a);
				host.MouseDown(a, MouseButton.Left);
				host.MouseMove(b);
				host.MouseUp(b, MouseButton.Left);
			}
			uint revision = workspace.Document.AudioRevision;
			Drag(0, 3); // Replace the original one-node sustain.
			envelope.SustainStartIndex.Should().Be(0);
			envelope.SustainEndIndex.Should().Be(3);
			workspace.Document.AudioRevision.Should().Be(revision + 1);
			Drag(3, 2); // Move right boundary first, using a new origin.
			envelope.SustainStartIndex.Should().Be(0);
			envelope.SustainEndIndex.Should().Be(2);
			Drag(0, 1); // Now move left boundary without a same-guide double-click.
			envelope.SustainStartIndex.Should().Be(1);
			envelope.SustainEndIndex.Should().Be(2);
			workspace.Document.AudioRevision.Should().Be(revision + 3);
			Drag(3, 3); // Self drag of an outside guide cannot replace region.
			envelope.SustainStartIndex.Should().Be(1);
			envelope.SustainEndIndex.Should().Be(2);
			workspace.Document.AudioRevision.Should().Be(revision + 3);
		}
		finally { host.Close(); }
	}

	[AvaloniaTest]
	public void KeyboardNavigationAndNudgesCommitThroughSharedEditor()
	{
		DocumentWorkspace workspace = new();
		NodeEnvelopeDefinition env =
			EnvelopeDocumentEditor.CreateNodeEnvelope(workspace, "Keyboard");
		NodeEnvelopeGraphControl graph = new(workspace, env);
		object surface = typeof(NodeEnvelopeGraphControl)
			.GetField("_surface", BindingFlags.Instance
				| BindingFlags.NonPublic)!.GetValue(graph)!;
		MethodInfo handle = surface.GetType().GetMethod(
			"HandleGraphKey", BindingFlags.Instance
				| BindingFlags.NonPublic)!;
		bool Press(Avalonia.Input.Key key, Avalonia.Input.KeyModifiers modifiers = 0)
			=> (bool)handle.Invoke(surface, [key, modifiers])!;
		Press(Avalonia.Input.Key.Tab).Should().BeTrue();
		graph.SelectedNodeIndex.Should().Be(0);
		Press(Avalonia.Input.Key.Tab).Should().BeTrue();
		graph.SelectedNodeIndex.Should().Be(1);
		Press(Avalonia.Input.Key.Tab,
			Avalonia.Input.KeyModifiers.Shift).Should().BeTrue();
		graph.SelectedNodeIndex.Should().Be(0);
		Press(Avalonia.Input.Key.Tab,
			Avalonia.Input.KeyModifiers.Control).Should().BeFalse();
		Press(Avalonia.Input.Key.Tab,
			Avalonia.Input.KeyModifiers.Control
				| Avalonia.Input.KeyModifiers.Shift).Should().BeFalse();
		Press(Avalonia.Input.Key.Tab).Should().BeTrue();
		graph.SelectedNodeIndex.Should().Be(1);
		uint initial = workspace.Document.AudioRevision;
		Press(Avalonia.Input.Key.Up).Should().BeTrue();
		env.Nodes[1].Value.Should().Be(1.01);
		Press(Avalonia.Input.Key.Right).Should().BeTrue();
		env.Nodes[1].Time.Should().Be(TimeSpan.FromMilliseconds(20));
		workspace.Document.AudioRevision.Should().Be(initial + 2);
	}

	[AvaloniaTest]
	public void ZoomOutTimeDomainPersistsUntilFitButtonIsInvoked()
	{
		DocumentWorkspace workspace = new();
		NodeEnvelopeDefinition envelope =
			EnvelopeDocumentEditor.CreateNodeEnvelope(workspace, "Sticky time");
		NodeEnvelopeGraphControl graph = new(workspace, envelope);
		graph.Measure(new Avalonia.Size(460, 310));
		graph.Arrange(new Avalonia.Rect(0, 0, 460, 310));
		Type type = typeof(NodeEnvelopeGraphControl);
		BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
		Button fit = (Button)type.GetField("_fit", flags)!.GetValue(graph)!;
		object surface = type.GetField("_surface", flags)!.GetValue(graph)!;
		Type surfaceType = surface.GetType();
		System.Reflection.FieldInfo domain = surfaceType.GetField(
			"_timeDomainTicks", flags)!;
		long initial = (long)domain.GetValue(surface)!;
		initial.Should().Be(envelope.Nodes[^1].Time.Ticks);
		fit.Content.Should().Be("↔");
		fit.IsVisible.Should().BeFalse();

		EnvelopeNode[] shortened = envelope.Nodes.ToArray();
		shortened[^1] = shortened[^1] with
		{
			Time = TimeSpan.FromMilliseconds(250),
		};
		NodeEnvelopeGraphEdit edit = new(
			shortened, envelope.SustainStartIndex,
			envelope.SustainEndIndex, -1);
		type.GetMethod("Commit", flags)!.Invoke(graph, [edit]);
		((long)domain.GetValue(surface)!).Should().Be(initial,
			"shrinking the final node must retain the zoomed-out time domain");
		fit.IsVisible.Should().BeTrue();
		uint audioBeforeFit = workspace.Document.AudioRevision;
		surfaceType.GetMethod("FitTimeDomain")!.Invoke(surface, []);
		((long)domain.GetValue(surface)!).Should()
			.Be(shortened[^1].Time.Ticks);
		fit.IsVisible.Should().BeFalse();
		workspace.Document.AudioRevision.Should().Be(audioBeforeFit,
			"zoom adjustments never change the actual envelope");

		shortened[^1] = shortened[^1] with
		{
			Time = TimeSpan.FromMilliseconds(750),
		};
		type.GetMethod("Commit", flags)!.Invoke(graph, [new NodeEnvelopeGraphEdit(
			shortened, envelope.SustainStartIndex,
			envelope.SustainEndIndex, -1)]);
		((long)domain.GetValue(surface)!).Should()
			.Be(shortened[^1].Time.Ticks,
				"the domain must expand when the final node passes its edge");
		fit.IsVisible.Should().BeFalse();
	}

	[AvaloniaTest]
	public void GraphDeletionOfSelectedNodePreservesSustainIndicesAndRevision()
	{
		DocumentWorkspace workspace = new();
		NodeEnvelopeDefinition envelope =
			EnvelopeDocumentEditor.CreateNodeEnvelope(workspace, "Curve");
		NodeEnvelopeGraphControl graph = new(workspace, envelope);
		uint revision = workspace.Document.AudioRevision;
		NodeEnvelopeGraphEdit deletion = NodeEnvelopeGraphGeometry.Delete(
			envelope.Nodes, 2, envelope.SustainStartIndex,
			envelope.SustainEndIndex)!;

		typeof(NodeEnvelopeGraphControl).GetMethod(
			"Commit", BindingFlags.Instance | BindingFlags.NonPublic)!
			.Invoke(graph, [deletion]);
		envelope.Nodes.Should().HaveCount(3);
		envelope.SustainStartIndex.Should().Be(2);
		envelope.SustainEndIndex.Should().Be(2);
		graph.SelectedNodeIndex.Should().Be(-1);
		workspace.Document.AudioRevision.Should().Be(revision + 1);
	}
}
