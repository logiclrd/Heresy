using System;
using System.Globalization;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

using Heresy.Core.Envelopes;
using Heresy.UserInterface.Documents;

namespace Heresy.UserInterface.Views;

/// <summary>
/// Expression authoring for script Envelopes. Expression analysis happens
/// before a workspace edit; failures preserve both the old revision and the
/// active renderer snapshot. Blank release means genuinely unbounded.
/// </summary>
public sealed class ScriptEnvelopeEditorControl : UserControl
{
	private readonly DocumentWorkspace _workspace;
	private readonly ScriptEnvelopeDefinition _envelope;
	private readonly Action _close;
	private readonly Action<string> _changed;
	private readonly TextBox _source;
	private readonly TextBox _release;
	private readonly TextBlock _message = new()
	{
		TextWrapping = TextWrapping.Wrap,
	};

	public ScriptEnvelopeEditorControl(
		DocumentWorkspace workspace, ScriptEnvelopeDefinition envelope,
		Action close, Action<string> changed)
	{
		_workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
		_envelope = envelope ?? throw new ArgumentNullException(nameof(envelope));
		_close = close ?? throw new ArgumentNullException(nameof(close));
		_changed = changed ?? throw new ArgumentNullException(nameof(changed));
		_source = new TextBox
		{
			Text = envelope.Source,
			AcceptsReturn = true,
			TextWrapping = TextWrapping.Wrap,
			MinHeight = 110,
			MinWidth = 350,
		};
		_release = new TextBox
		{
			Text = envelope.ReleaseDuration?.TotalSeconds.ToString(
				"R", CultureInfo.InvariantCulture) ?? string.Empty,
			PlaceholderText = "Blank = unbounded",
			MinWidth = 160,
		};
		Content = Build();
	}

	private Control Build()
	{
		Button back = new() { Content = "← Document" };
		back.Click += (_, _) => _close();
		Button apply = new() { Content = "Apply expression" };
		apply.Click += (_, _) => Apply();
		StackPanel fields = new()
		{
			Margin = new Thickness(18, 14),
			Spacing = 9,
			MaxWidth = 760,
			HorizontalAlignment = HorizontalAlignment.Left,
		};
		fields.Children.Add(back);
		fields.Children.Add(new TextBlock
		{
			Text = _envelope.Name + " — Script Envelope",
			FontSize = 20,
			FontWeight = FontWeight.SemiBold,
		});
		fields.Children.Add(new TextBlock
		{
			Text = "Scalar C# expression: elapsedSeconds is active-envelope time. " +
				"releaseSeconds is -1 before Note Off and elapsed release time afterwards.",
			TextWrapping = TextWrapping.Wrap,
		});
		fields.Children.Add(_source);
		fields.Children.Add(new TextBlock
		{
			Text = "Release limit (seconds). A finite limit forces silence " +
				"at its active-time endpoint.",
			TextWrapping = TextWrapping.Wrap,
		});
		fields.Children.Add(_release);
		fields.Children.Add(apply);
		fields.Children.Add(_message);
		return new ScrollViewer { Content = fields };
	}

	private void Apply()
	{
		try
		{
			string text = _release.Text?.Trim() ?? string.Empty;
			TimeSpan? release = text.Length == 0
				? null : TimeSpan.FromSeconds(
					double.Parse(text, CultureInfo.InvariantCulture));
			EnvelopeDocumentEditor.UpdateScriptEnvelope(
				_workspace, _envelope, _source.Text ?? string.Empty, release);
			_message.Text = "Expression updated.";
			_changed("Updated script Envelope " + _envelope.Name);
		}
		catch (Exception ex)
		{
			_message.Text = "Cannot apply Envelope: " + ex.Message;
		}
	}
}
