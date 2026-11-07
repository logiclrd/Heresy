using System;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;

using Heresy.Render.Realtime;
using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class OutputScopeDockTests
{
	[AvaloniaTest]
	public void ScopeCanToggleAndDockToEveryWindowEdgeWithSplitter()
	{
		AudioOutputWaveformCapture capture = new();
		Border content = new();
		OutputScopeDockHost host = new(content, capture);
		Assert.That(host.ScopeVisible, Is.False);
		Assert.That(host.Children, Has.Count.EqualTo(1));
		Assert.That(capture.Enabled, Is.False);
		int transitions = 0;
		host.ScopeVisibilityChanged += (_, _) => transitions++;

		host.SetScopeVisible(true);
		Assert.That(host.ScopeVisible, Is.True);
		Assert.That(capture.Enabled, Is.True);
		Assert.That(host.Children, Has.Count.EqualTo(3));
		foreach (OutputScopeDockEdge edge in Enum.GetValues<OutputScopeDockEdge>())
		{
			host.DockTo(edge);
			Assert.That(host.Edge, Is.EqualTo(edge));
			Assert.That(host.Children.OfType<GridSplitter>().Count(), Is.EqualTo(1));
			Assert.That(host.Children.Contains(content), Is.True);
			if (edge is OutputScopeDockEdge.Top or OutputScopeDockEdge.Bottom)
			{
				Assert.That(host.RowDefinitions.Count, Is.EqualTo(3));
				Assert.That(host.ColumnDefinitions.Count, Is.Zero);
				Assert.That(Grid.GetRow(host.ScopePane),
					Is.EqualTo(edge == OutputScopeDockEdge.Top ? 0 : 2));
			}
			else
			{
				Assert.That(host.ColumnDefinitions.Count, Is.EqualTo(3));
				Assert.That(host.RowDefinitions.Count, Is.Zero);
				Assert.That(Grid.GetColumn(host.ScopePane),
					Is.EqualTo(edge == OutputScopeDockEdge.Left ? 0 : 2));
			}
		}
		host.SetScopeVisible(false);
		Assert.That(host.ScopeVisible, Is.False);
		Assert.That(capture.Enabled, Is.False);
		Assert.That(host.Children, Has.Count.EqualTo(1));
		Assert.That(transitions, Is.EqualTo(2));
		host.Shutdown();
	}

	[Test]
	public void DragDropChoosesNearestOfAllFourEdges()
	{
		Size bounds = new(1200, 700);
		Assert.That(OutputScopeDockHost.NearestEdge(
			new Point(10, 350), bounds), Is.EqualTo(OutputScopeDockEdge.Left));
		Assert.That(OutputScopeDockHost.NearestEdge(
			new Point(600, 2), bounds), Is.EqualTo(OutputScopeDockEdge.Top));
		Assert.That(OutputScopeDockHost.NearestEdge(
			new Point(1199, 350), bounds), Is.EqualTo(OutputScopeDockEdge.Right));
		Assert.That(OutputScopeDockHost.NearestEdge(
			new Point(600, 695), bounds), Is.EqualTo(OutputScopeDockEdge.Bottom));
	}

	[AvaloniaTest]
	public void CloseButtonHidesScopeAndStopsAudioCapture()
	{
		AudioOutputWaveformCapture capture = new();
		OutputScopeDockHost host = new(new Border(), capture);
		host.SetScopeVisible(true);
		Grid pane = (Grid)host.ScopePane.Content!;
		DockPanel header = (DockPanel)pane.Children[0];
		Button close = header.Children.OfType<Button>().Single();
		close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		Assert.That(host.ScopeVisible, Is.False);
		Assert.That(capture.Enabled, Is.False);
		host.Shutdown();
	}

	[AvaloniaTest]
	public void SurroundDefaultsToFrontPairAndCanSelectEverySpeaker()
	{
		AudioOutputWaveformCapture capture = new();
		OutputScopePane pane = new(capture);
		var surround = capture.BeginSession(new AudioOutputFormat(48000, 8));
		pane.SetActive(true);
		Grid content = (Grid)pane.Content!;
		DockPanel header = (DockPanel)content.Children[0];
		ComboBox selector = header.Children.OfType<ComboBox>().Single();
		OutputScopeChannelChoice[] items = selector.Items
			.OfType<OutputScopeChannelChoice>().ToArray();
		Assert.That(items.Length, Is.EqualTo(9));
		Assert.That(items[0].Name, Is.EqualTo("Front L + R"));
		Assert.That(pane.Graph.FirstChannel, Is.EqualTo(0));
		Assert.That(pane.Graph.SecondChannel, Is.EqualTo(1));
		Assert.That(items[3].Name, Is.EqualTo("Center"));
		selector.SelectedItem = items[3];
		Assert.That(pane.Graph.FirstChannel, Is.EqualTo(2));
		Assert.That(pane.Graph.SecondChannel, Is.EqualTo(-1));
		Assert.That(items[8].Name, Is.EqualTo("Side R"));
		capture.EndSession(surround);

		capture.BeginSession(new AudioOutputFormat(44100, 1));
		pane.SetActive(true); // forces format-change refresh
		Assert.That(selector.Items.OfType<OutputScopeChannelChoice>().Single()
			.Name, Is.EqualTo("Mono"));
		Assert.That(pane.Graph.SecondChannel, Is.EqualTo(-1));
		pane.Shutdown();
	}
}
