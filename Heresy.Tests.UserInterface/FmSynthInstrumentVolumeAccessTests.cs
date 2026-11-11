using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;

using AwesomeAssertions;

using Heresy.Core.Envelopes;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class FmSynthInstrumentVolumeAccessTests
{
	[AvaloniaTest]
	public void SingleSourceInspectorEditsSharedInstrumentVolumeWithoutLeavingSynthView()
	{
		DocumentWorkspace workspace = new();
		NodeEnvelopeDefinition envelope =
			EnvelopeDocumentEditor.CreateNodeEnvelope(workspace, "Tone release");
		ObjectId instrumentId = workspace.Document.AllocateObjectId();
		InstrumentDefinition instrument = new(instrumentId, "Tone");
		instrument.ToneSpecifications.Add(new ToneSpecification
		{
			SourceId = ObjectId.None,
			VolumeEnvelopeId = envelope.Id,
		});
		instrument.ToneTable.Add(0);
		workspace.Document.Add(instrument);
		ObjectId synthId = workspace.Document.AllocateObjectId();
		FmSynthDefinition synth = new(synthId, "Source audition",
			new FmSynthGraph([
				new FmSoundSourceNode(0, instrumentId,
					FmMultiNode.NeutralFrequencyHz),
			], 0));
		workspace.Document.Add(synth);
		int changed = 0;
		FmSynthEditorControl editor = new(workspace, synth,
			() => { }, _ => changed++);
		Window host = new() { Width = 1200, Height = 850, Content = editor };
		host.Show();
		try
		{
			StackPanel inspector = Field<StackPanel>(editor, "_inspector");
			Button edit = inspector.Children.OfType<Button>().Single(button =>
				Equals(button.Content, "Edit shared Volume envelope…"));
			edit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			Dictionary<ObjectId, Window> windows =
				Field<Dictionary<ObjectId, Window>>(editor, "_sharedVolumeEditors");
			windows.Should().ContainKey(envelope.Id);
			Window popup = windows[envelope.Id];
			ScrollViewer scroll = popup.Content.Should()
				.BeOfType<ScrollViewer>().Subject;
			NodeEnvelopeEditorControl volume = scroll.Content.Should()
				.BeOfType<NodeEnvelopeEditorControl>().Subject;
			uint audioRevision = workspace.Document.AudioRevision;
			Field<TextBox>(volume, "_nodesInput").Text =
				"0, 0.4\\n0.01, 0.8\\n0.02, 0.2\\n0.09, 0";
			typeof(NodeEnvelopeEditorControl).GetMethod("Apply",
				BindingFlags.Instance | BindingFlags.NonPublic)!
				.Invoke(volume, []);
			workspace.Document.AudioRevision.Should().Be(audioRevision + 1);
			changed.Should().Be(1);
			host.Content.Should().BeSameAs(editor,
				"the Synth preview view must remain mounted during shared edits");
			editor.IsAttachedToVisualTree().Should().BeTrue();
		}
		finally { host.Close(); }
	}

	private static T Field<T>(object instance, string name)
		=> (T)instance.GetType().GetField(name,
			BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(instance)!;
}
