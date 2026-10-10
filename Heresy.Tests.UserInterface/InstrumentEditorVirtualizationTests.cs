using System;
using System.Reflection;

using Avalonia.Controls;
using AwesomeAssertions;

using Heresy.Core.Instruments;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.InstrumentEditing;
using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class InstrumentEditorVirtualizationTests
{
	[Test]
	public void RecycledNullItemDoesNotCrashOrPreventRenderingSubsequentRows()
	{
		DocumentWorkspace workspace = new();
		InstrumentDefinition instrument =
			InstrumentDocumentEditor.CreateInstrument(workspace, "Lead");
		InstrumentEditorControl editor = new(
			workspace, instrument, () => { }, _ => { });

		// Exercise the actual FuncDataTemplate installed on the ListBox.
		// Avalonia can call it with null when a recycled item container's
		// ContentPresenter clears its content during scrolling.
		FieldInfo field = typeof(InstrumentEditorControl).GetField(
			"_rows", BindingFlags.Instance | BindingFlags.NonPublic)!;
		ListBox rows = (ListBox)field.GetValue(editor)!;
		Action recycle = () => { _ = rows.ItemTemplate!.Build(null); };
		recycle.Should().NotThrow();
		rows.ItemTemplate!.Build(null).Should().BeNull();

		var projected = new InstrumentToneGridModel(workspace, instrument).Rows;
		rows.ItemTemplate.Build(projected[0]).Should().BeOfType<Border>();
		rows.ItemTemplate.Build(projected[^1]).Should().BeOfType<Border>();
	}
}
