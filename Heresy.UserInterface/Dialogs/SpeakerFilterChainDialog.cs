using System;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

using Heresy.Core.Filters;

namespace Heresy.UserInterface.Dialogs;

/// <summary>Standalone inline speaker Filter editor. Each serial stage is a
/// normal reusable FilterKind and typed parameter set, but the result is a
/// frozen audio preference independent of a SongDocument or its revisions.</summary>
public sealed class SpeakerFilterChainDialog : Window
{
    private readonly TextBox _editor;
    private readonly TextBlock _error = new()
    {
        Foreground = Brushes.IndianRed,
        TextWrapping = TextWrapping.Wrap,
    };
    private readonly int _sampleRate;
    private readonly int _speaker;

    public SpeakerFilterChainDialog(FilterInstance? initial, int sampleRate, int speaker)
    {
        _sampleRate = sampleRate;
        _speaker = speaker;
        Title = $"Speaker {speaker + 1} — reusable Filter chain";
        Width = 790;
        Height = 595;
        MinWidth = 600;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _editor = new TextBox
        {
            Text = initial is null ? SpeakerFilterChainText.Default
                : SpeakerFilterChainText.Format(initial),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        string kinds = string.Join(", ", FilterCatalogue.MenuItems
            .Where(item => item.IsSelectable)
            .Select(item => item.Kind!.Value.ToString()));
        TextBlock help = new()
        {
            Text = "One serial stage per line: Kind Parameter=Value ... "
                + "Numeric values are in invariant units (Hz, seconds, etc.). "
                + "Missing parameters use catalogue defaults. Lines beginning with # are comments. "
                + "Stage order is processing order. Example: OnePole CutoffHz=1000 Mode=LowPass "
                + "(TPT); SpeakerLegacyRc CutoffHz=1000 Mode=LowPass keeps old audio output behavior. "
                + "Available kinds: " + kinds,
            TextWrapping = TextWrapping.Wrap,
        };
        Button accept = new() { Content = "Apply chain", MinWidth = 110 };
        Button cancel = new() { Content = "Cancel", MinWidth = 100 };
        accept.Click += (_, _) =>
        {
            try
            {
                FilterInstance choice = SpeakerFilterChainText.Parse(
                    _editor.Text ?? "", _sampleRate, _speaker);
                Close(choice);
            }
            catch (Exception ex) when (ex is ArgumentException
                or InvalidOperationException or OverflowException)
            {
                _error.Text = ex.Message;
            }
        };
        cancel.Click += (_, _) => Close((FilterInstance?)null);
        StackPanel actions = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { cancel, accept },
        };
        Grid layout = new()
        {
            Margin = new Thickness(15),
            RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"),
            RowSpacing = 12,
        };
        Grid.SetRow(help, 0);
        Grid.SetRow(_editor, 1);
        Grid.SetRow(_error, 2);
        Grid.SetRow(actions, 3);
        layout.Children.Add(help);
        layout.Children.Add(_editor);
        layout.Children.Add(_error);
        layout.Children.Add(actions);
        Content = layout;
    }
}
