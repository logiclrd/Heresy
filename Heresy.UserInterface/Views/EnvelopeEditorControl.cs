using System;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

using Heresy.Core.Envelopes;
using Heresy.UserInterface.Documents;

namespace Heresy.UserInterface.Views;

/// <summary>Standalone node-envelope editor. The shared, functional
/// node-list editor is temporary until the graphical editor is rebuilt.</summary>
public sealed class EnvelopeEditorControl : UserControl
{
	private readonly NodeEnvelopeEditorControl _editor;

	/// <summary>Commit independent parameter drafts before Save or leaving
	/// the document view; the raw node textbox remains explicitly staged.</summary>
	public bool TryCommitPendingFields() => _editor.TryCommitPendingFields();

	public EnvelopeEditorControl(
		DocumentWorkspace workspace,
		NodeEnvelopeDefinition envelope,
		Action close,
		Action<string> changed)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(envelope);
		ArgumentNullException.ThrowIfNull(close);
		ArgumentNullException.ThrowIfNull(changed);

		Button back = new() { Content = "← Document", MinWidth = 100 };
		back.Click += (_, _) => close();
		TextBlock title = new()
		{
			Text = envelope.Name,
			FontSize = 20,
			FontWeight = Avalonia.Media.FontWeight.SemiBold,
			VerticalAlignment = VerticalAlignment.Center,
		};
		DockPanel header = new()
		{
			Margin = new Thickness(10, 8),
		};
		DockPanel.SetDock(back, Dock.Left);
		header.Children.Add(back);
		header.Children.Add(title);

		_editor = new NodeEnvelopeEditorControl(workspace, envelope, changed);
		DockPanel root = new();
		DockPanel.SetDock(header, Dock.Top);
		root.Children.Add(header);
		root.Children.Add(new ScrollViewer { Content = _editor });
		Content = root;
	}
}
