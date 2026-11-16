using System;
using System.Linq;
using System.Reflection;

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Headless.NUnit;

using AwesomeAssertions;

using Heresy.Core.Envelopes;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class NodeEnvelopeEditorControlTests
{
	[AvaloniaTest]
	public void SetNodesOnlyCommitsRawNodeListAndLeavesParameterDraftsUntouched()
	{
		DocumentWorkspace workspace = new();
		NodeEnvelopeDefinition envelope =
			EnvelopeDocumentEditor.CreateNodeEnvelope(workspace, "LFO");
		string? notification = null;
		NodeEnvelopeEditorControl editor = new(
			workspace, envelope, msg => notification = msg);
		uint initial = workspace.Document.AudioRevision;

		TextBox nodes = Field<TextBox>(editor, "_nodesInput");
		TextBox start = Field<TextBox>(editor, "_sustainStart");
		TextBox end = Field<TextBox>(editor, "_sustainEnd");
		ComboBox mode = Field<ComboBox>(editor, "_loopMode");
		nodes.Text = "0, -0.5\n0.25, 1.5\n0.5, 0.25\n0.8, 0";
		start.Text = "1";
		end.Text = "3";
		mode.SelectedItem = EnvelopeSustainLoopMode.PingPong;
		Invoke(editor, "SetNodes");

		workspace.Document.AudioRevision.Should().Be(initial + 1);
		envelope.Nodes.Should().HaveCount(4);
		envelope.Nodes[1].Time.Should().Be(TimeSpan.FromMilliseconds(250));
		envelope.Nodes[1].Value.Should().Be(1.5);
		envelope.Nodes[0].Value.Should().Be(-0.5);
		envelope.SustainStartIndex.Should().Be(2,
			"Set nodes must not commit pending sustain fields");
		envelope.SustainEndIndex.Should().Be(2);
		envelope.SustainLoopMode.Should().Be(EnvelopeSustainLoopMode.Forward);
		start.Text.Should().Be("1",
			"committing nodes in the same view leaves other drafts alone");
		end.Text.Should().Be("3");
		mode.SelectedItem.Should().Be(EnvelopeSustainLoopMode.PingPong);
		notification.Should().Contain("Updated envelope");

		FocusLoss(start);
		FocusLoss(end);
		FocusLoss(mode);
		envelope.SustainStartIndex.Should().Be(1);
		envelope.SustainEndIndex.Should().Be(3);
		envelope.SustainLoopMode.Should().Be(EnvelopeSustainLoopMode.PingPong);
		workspace.Document.AudioRevision.Should().Be(initial + 4);

		Invoke(editor, "SetNodes");
		workspace.Document.AudioRevision.Should().Be(initial + 4);
		nodes.Text = "0, 0\n0, 1\n0.1, 0.4\n0.4, 0";
		Invoke(editor, "SetNodes");
		workspace.Document.AudioRevision.Should().Be(initial + 4);
		envelope.Nodes[1].Value.Should().Be(1.5);
		Field<TextBlock>(editor, "_message").Text.Should()
			.Contain("increase strictly");
	}

	[AvaloniaTest]
	public void LiveIndependentBoundsApplyOnFocusLossWithoutAudioRevision()
	{
		DocumentWorkspace workspace = new();
		NodeEnvelopeDefinition env =
			EnvelopeDocumentEditor.CreateNodeEnvelope(workspace, "Scale");
		NodeEnvelopeEditorControl editor = new(workspace, env, _ => { });
		TextBox vmin = Field<TextBox>(editor, "_vmin");
		TextBox vmax = Field<TextBox>(editor, "_vmax");
		ComboBox loop = Field<ComboBox>(editor, "_loopMode");
		uint audio = workspace.Document.AudioRevision;
		uint document = workspace.Document.DocumentRevision;

		vmin.Text = "-10";
		FocusLoss(vmin);
		env.Vmin.Should().Be(-10);
		env.Vmax.Should().Be(1);
		workspace.Document.AudioRevision.Should().Be(audio);
		workspace.Document.DocumentRevision.Should().Be(document + 1);

		vmax.Text = "20";
		FocusLoss(vmax);
		env.Vmax.Should().Be(20);
		workspace.Document.AudioRevision.Should().Be(audio);
		workspace.Document.DocumentRevision.Should().Be(document + 2);

		vmax.Text = "-100";
		FocusLoss(vmax);
		env.Vmax.Should().Be(20);
		workspace.Document.DocumentRevision.Should().Be(document + 2);
		Field<TextBlock>(editor, "_message").Text.Should().Contain("less than");

		vmax.Text = "20";
		FocusLoss(vmax);
		workspace.Document.DocumentRevision.Should().Be(document + 2);
		loop.SelectedItem = EnvelopeSustainLoopMode.None;
		FocusLoss(loop);
		env.SustainLoopMode.Should().Be(EnvelopeSustainLoopMode.None);
		workspace.Document.AudioRevision.Should().Be(audio + 1);
		workspace.Document.DocumentRevision.Should().Be(document + 3);
	}

	[AvaloniaTest]
	public void EnvelopeRangeLinkStartsFromCurrentBoundsAndRecentersAtomically()
	{
		DocumentWorkspace workspace = new();
		NodeEnvelopeDefinition env =
			EnvelopeDocumentEditor.CreateNodeEnvelope(workspace, "Symmetry");
		NodeEnvelopeEditorControl editor = new(workspace, env, _ => { });
		ValueRangeLinkToggle toggle = Field<ValueRangeLinkToggle>(editor, "_rangeLink");
		TextBox min = Field<TextBox>(editor, "_vmin");
		TextBox max = Field<TextBox>(editor, "_vmax");
		Assert.That(toggle.IsChecked, Is.False,
			"the original Envelope display range 0..1 is asymmetric");
		uint audio = workspace.Document.AudioRevision;
		uint initial = workspace.Document.DocumentRevision;
		toggle.IsChecked = true;
		Assert.Multiple(() =>
		{
			Assert.That(env.Vmin, Is.EqualTo(-0.5));
			Assert.That(env.Vmax, Is.EqualTo(0.5));
			Assert.That(min.Text, Is.EqualTo("-0.5"));
			Assert.That(max.Text, Is.EqualTo("0.5"));
			Assert.That(workspace.Document.DocumentRevision,
				Is.EqualTo(initial + 1), "a recenter is one atomic update");
			Assert.That(workspace.Document.AudioRevision, Is.EqualTo(audio),
				"display limits do not change audio PCM");
		});
		min.Text = "-3";
		FocusLoss(min);
		Assert.That(env.Vmin, Is.EqualTo(-3));
		Assert.That(env.Vmax, Is.EqualTo(3));
		Assert.That(max.Text, Is.EqualTo("3"));
		Assert.That(workspace.Document.DocumentRevision, Is.EqualTo(initial + 2));

		max.Text = "2";
		FocusLoss(max);
		Assert.That(env.Vmin, Is.EqualTo(-2));
		Assert.That(env.Vmax, Is.EqualTo(2));
		Assert.That(min.Text, Is.EqualTo("-2"));
		Assert.That(workspace.Document.DocumentRevision, Is.EqualTo(initial + 3));
		toggle.IsChecked = false;
		Assert.That(workspace.Document.DocumentRevision, Is.EqualTo(initial + 3),
			"unlinking must be entirely UI-only");
		min.Text = "-7";
		FocusLoss(min);
		Assert.That(env.Vmin, Is.EqualTo(-7));
		Assert.That(env.Vmax, Is.EqualTo(2));
		Assert.That(workspace.Document.DocumentRevision, Is.EqualTo(initial + 4));
		toggle.IsChecked = true;
		Assert.That(env.Vmax, Is.EqualTo(4.5),
			"newVmax = (oldVmax - oldVmin) / 2");
		Assert.That(env.Vmin, Is.EqualTo(-4.5));
		Assert.That(workspace.Document.DocumentRevision, Is.EqualTo(initial + 5));
		Assert.That(workspace.Document.AudioRevision, Is.EqualTo(audio));
		NodeEnvelopeEditorControl reopened = new(workspace, env, _ => { });
		Assert.That(Field<ValueRangeLinkToggle>(reopened, "_rangeLink").IsChecked,
			Is.True);
	}

	[AvaloniaTest]
	public void EnvelopeLinkedInvalidSignOrPendingRawTextIsNeverPartiallyCommitted()
	{
		DocumentWorkspace workspace = new();
		NodeEnvelopeDefinition env =
			EnvelopeDocumentEditor.CreateNodeEnvelope(workspace, "Ranges");
		NodeEnvelopeEditorControl editor = new(workspace, env, _ => { });
		ValueRangeLinkToggle toggle = Field<ValueRangeLinkToggle>(editor, "_rangeLink");
		toggle.IsChecked = true;
		uint revision = workspace.Document.DocumentRevision;
		TextBox min = Field<TextBox>(editor, "_vmin");
		TextBox max = Field<TextBox>(editor, "_vmax");
		TextBox nodes = Field<TextBox>(editor, "_nodesInput");
		nodes.Text = "not a node";
		min.Text = "1"; // Linked positive Vmin would violate Vmin < Vmax.
		FocusLoss(min);
		Assert.That(workspace.Document.DocumentRevision, Is.EqualTo(revision));
		Assert.That(env.Vmin, Is.EqualTo(-0.5));
		Assert.That(env.Vmax, Is.EqualTo(0.5));
		Assert.That(max.Text, Is.EqualTo("0.5"));
		Assert.That(nodes.Text, Is.EqualTo("not a node"));
		Assert.That(Field<TextBlock>(editor, "_message").Text,
			Does.Contain("less than"));
		min.Text = "-2";
		FocusLoss(min);
		Assert.That(env.Vmin, Is.EqualTo(-2));
		Assert.That(env.Vmax, Is.EqualTo(2));
		Assert.That(nodes.Text, Is.EqualTo("not a node"),
			"the raw node draft remains separately staged");
		Assert.That(workspace.Document.DocumentRevision, Is.EqualTo(revision + 1));
	}

	[AvaloniaTest]
	public void InvalidNodeTextDoesNotBlockIndependentValidParameterCommit()
	{
		DocumentWorkspace workspace = new();
		NodeEnvelopeDefinition envelope =
			EnvelopeDocumentEditor.CreateNodeEnvelope(workspace, "Curve");
		NodeEnvelopeEditorControl editor = new(workspace, envelope, _ => { });
		TextBox nodes = Field<TextBox>(editor, "_nodesInput");
		TextBox vmin = Field<TextBox>(editor, "_vmin");
		nodes.Text = "not a time";
		uint audio = workspace.Document.AudioRevision;
		uint document = workspace.Document.DocumentRevision;
		vmin.Text = "-8";
		FocusLoss(vmin);
		envelope.Vmin.Should().Be(-8);
		nodes.Text.Should().Be("not a time",
			"committing a field here does not implicitly accept the raw text");
		workspace.Document.AudioRevision.Should().Be(audio);
		workspace.Document.DocumentRevision.Should().Be(document + 1);
		Invoke(editor, "SetNodes");
		workspace.Document.DocumentRevision.Should().Be(document + 1);
		Field<TextBlock>(editor, "_message").Text.Should()
			.Contain("Invalid node");
	}

	[AvaloniaTest]
	public void SharedEditorsUpdateFromObjectChangeAndIndependentFieldCommit()
	{
		DocumentWorkspace workspace = new();
		NodeEnvelopeDefinition envelope =
			EnvelopeDocumentEditor.CreateNodeEnvelope(workspace, "Shared");
		FmSynthDefinition synth =
			FmSynthDocumentEditor.CreateFmSynth(workspace, "FM shared");
		int fmNodeId = FmSynthDocumentEditor.AddEnvelopeNode(
			workspace, synth, envelope.Id, 200, 100);
		FmSynthDocumentEditor.SetOutputNode(workspace, synth, fmNodeId);

		NodeEnvelopeEditorControl standalone =
			new(workspace, envelope, _ => { });
		FmSynthEditorControl fm =
			new(workspace, synth, () => { }, _ => { });
		StackPanel inspector = (StackPanel)typeof(FmSynthEditorControl)
			.GetField("_inspector", BindingFlags.Instance
				| BindingFlags.NonPublic)!.GetValue(fm)!;
		NodeEnvelopeEditorControl sharedInspector =
			inspector.Children.OfType<NodeEnvelopeEditorControl>().Single();
		Window host = new()
		{
			Width = 1100, Height = 850,
			Content = new StackPanel { Children = { standalone, fm } },
		};
		host.Show();
		try
		{
			TextBox standaloneNodes = Field<TextBox>(standalone, "_nodesInput");
			TextBox fmNodes = Field<TextBox>(sharedInspector, "_nodesInput");
			int signals = 0;
			envelope.Changed += (_, _) => signals++;

			EnvelopeNode[] modified = envelope.Nodes.ToArray();
			modified[2] = modified[2] with { Value = 0.37 };
			EnvelopeDocumentEditor.UpdateNodeEnvelope(
				workspace, envelope, modified, 1, 2,
				EnvelopeSustainLoopMode.PingPong, -2, 4);
			standaloneNodes.Text.Should().Contain("0.37");
			fmNodes.Text.Should().Contain("0.37");
			Field<TextBox>(sharedInspector, "_sustainStart")
				.Text.Should().Be("1");
			Field<TextBox>(sharedInspector, "_sustainEnd")
				.Text.Should().Be("2");
			Field<ComboBox>(sharedInspector, "_loopMode")
				.SelectedItem.Should().Be(EnvelopeSustainLoopMode.PingPong);
			Field<TextBox>(standalone, "_vmin").Text.Should().Be("-2");
			signals.Should().Be(1,
				"compound accepted changes notify object observers only once");

			TextBox fmVmax = Field<TextBox>(sharedInspector, "_vmax");
			fmVmax.Text = "8";
			FocusLoss(fmVmax);
			Field<TextBox>(standalone, "_vmax").Text.Should().Be("8");
			signals.Should().Be(2);

			// A raw-node acceptance on one editor updates the other.
			fmNodes.Text = "0, 0\n0.01, 0.8\n0.11, 0.2\n0.41, 0";
			Invoke(sharedInspector, "SetNodes");
			standaloneNodes.Text.Should().Contain("0.8");
			envelope.Nodes[1].Value.Should().Be(0.8);
			signals.Should().Be(3);
		}
		finally { host.Close(); }
	}

	[AvaloniaTest]
	public void LastAcceptedNodeEditReplacesOtherViewsPendingText()
	{
		DocumentWorkspace workspace = new();
		NodeEnvelopeDefinition envelope =
			EnvelopeDocumentEditor.CreateNodeEnvelope(workspace, "Conflict");
		NodeEnvelopeEditorControl first = new(workspace, envelope, _ => { });
		NodeEnvelopeEditorControl second = new(workspace, envelope, _ => { });
		Window host = new()
		{
			Width = 900, Height = 600,
			Content = new StackPanel { Children = { first, second } },
		};
		host.Show();
		try
		{
			TextBox draft = Field<TextBox>(first, "_nodesInput");
			TextBox current = Field<TextBox>(second, "_nodesInput");
			draft.Text = "0, 0\n0.01, 1.25\n0.11, 0.7\n0.41, 0";
			current.Text = "0, 0\n0.01, 0.25\n0.11, 0.7\n0.41, 0";
			uint revision = workspace.Document.AudioRevision;

			Invoke(second, "SetNodes");
			workspace.Document.AudioRevision.Should().Be(revision + 1);
			draft.Text.Should().Contain("0.25");
			draft.Text.Should().NotContain("1.25",
				"the latest accepted edit replaces another view's pending draft");
			Field<TextBlock>(first, "_message").Text.Should()
				.Contain("replaced");

			Invoke(first, "SetNodes");
			envelope.Nodes[1].Value.Should().Be(0.25);
			workspace.Document.AudioRevision.Should().Be(revision + 1);

			// Reversing the winning view works too.
			draft.Text = "0, 0\n0.01, 0.9\n0.11, 0.7\n0.41, 0";
			current.Text = "0, 0\n0.01, 0.3\n0.11, 0.7\n0.41, 0";
			Invoke(first, "SetNodes");
			current.Text.Should().Contain("0.9");
			current.Text.Should().NotContain("0.3");
			envelope.Nodes[1].Value.Should().Be(0.9);
			workspace.Document.AudioRevision.Should().Be(revision + 2);
		}
		finally { host.Close(); }
	}

	[AvaloniaTest]
	public void DetachingEditorCommitsParameterFieldButNotRawNodeDraft()
	{
		DocumentWorkspace workspace = new();
		NodeEnvelopeDefinition envelope =
			EnvelopeDocumentEditor.CreateNodeEnvelope(workspace, "Leaving");
		NodeEnvelopeEditorControl editor = new(workspace, envelope, _ => { });
		Window host = new() { Width = 900, Height = 600, Content = editor };
		host.Show();
		try
		{
			Field<TextBox>(editor, "_nodesInput").Text =
				"0, 0\n0.01, 1.8\n0.11, 0.7\n0.41, 0";
			TextBox vmin = Field<TextBox>(editor, "_vmin");
			vmin.Text = "-3";
			uint audio = workspace.Document.AudioRevision;
			uint document = workspace.Document.DocumentRevision;
			host.Content = new TextBlock { Text = "Different view" };
			envelope.Vmin.Should().Be(-3);
			envelope.Nodes[1].Value.Should().Be(1.0);
			workspace.Document.AudioRevision.Should().Be(audio);
			workspace.Document.DocumentRevision.Should().Be(document + 1);
		}
		finally { host.Close(); }
	}

	[AvaloniaTest]
	public void NodeButtonIsDirectlyBelowTextareaAndGridHasRequestedColumns()
	{
		DocumentWorkspace workspace = new();
		NodeEnvelopeDefinition envelope =
			EnvelopeDocumentEditor.CreateNodeEnvelope(workspace, "Layout");
		NodeEnvelopeEditorControl editor = new(workspace, envelope, _ => { });
		StackPanel body = (StackPanel)editor.Content!;
		TextBox nodes = Field<TextBox>(editor, "_nodesInput");
		int index = body.Children.IndexOf(nodes);
		((Button)body.Children[index + 1]).Content.Should().Be("Set nodes");
		body.Children.OfType<Button>().Select(b => b.Content)
			.Should().NotContain("Reload shared envelope");
		Grid grid = body.Children.OfType<Grid>().Single();
		grid.MaxWidth.Should().Be(800);
		grid.ColumnDefinitions.Should().HaveCount(4);
		TextBox start = Field<TextBox>(editor, "_sustainStart");
		TextBox end = Field<TextBox>(editor, "_sustainEnd");
		Grid.GetColumn(start).Should().Be(1);
		Grid.GetColumn(end).Should().Be(3);
		StackPanel range = grid.Children.OfType<StackPanel>()
			.Single(x => x.Name == "EnvelopeValueRange");
		Grid.GetRow(range).Should().Be(2);
		Grid.GetColumn(range).Should().Be(0);
		Grid.GetColumnSpan(range).Should().Be(4);
		range.Children.OfType<StackPanel>().Should().HaveCount(2);
		range.Children.OfType<StackPanel>().Select(x =>
			x.Children.OfType<TextBlock>().Single().Text)
			.Should().Equal("Vmin", "Vmax");
		range.Children.OfType<ValueRangeLinkToggle>().Should().ContainSingle();
		range.Children.OfType<StackPanel>()
			.SelectMany(x => x.Children.OfType<TextBox>())
			.Should().Equal(Field<TextBox>(editor, "_vmin"),
				Field<TextBox>(editor, "_vmax"));
	}

	[AvaloniaTest]
	public void StandaloneEditorBackButtonReturnsToDocumentNavigation()
	{
		DocumentWorkspace workspace = new();
		NodeEnvelopeDefinition envelope =
			EnvelopeDocumentEditor.CreateNodeEnvelope(workspace, "Navigate");
		int backCount = 0;
		EnvelopeEditorControl editor = new(
			workspace, envelope, () => backCount++, _ => { });
		Window host = new() { Width = 800, Height = 700, Content = editor };
		host.Show();
		try
		{
			DockPanel root = (DockPanel)editor.Content!;
			DockPanel header = (DockPanel)root.Children[0];
			Button back = (Button)header.Children[0];
			back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			backCount.Should().Be(1);
			workspace.Document.Objects[envelope.Id].Should().BeSameAs(envelope);
		}
		finally { host.Close(); }
	}

	private static T Field<T>(NodeEnvelopeEditorControl editor, string name)
		=> (T)typeof(NodeEnvelopeEditorControl).GetField(
			name, BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(editor)!;

	private static void Invoke(NodeEnvelopeEditorControl editor, string name)
		=> typeof(NodeEnvelopeEditorControl).GetMethod(
			name, BindingFlags.Instance | BindingFlags.NonPublic)!
			.Invoke(editor, []);

	private static void FocusLoss(Control control)
		=> control.RaiseEvent(new FocusChangedEventArgs(InputElement.LostFocusEvent));
}
